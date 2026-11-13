using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StrataRec.Core.Bindings;
using StrataRec.Features.Bindings;
using StrataRec.Hosting.Bindings;
using StrataRec.Models;
using StrataRec.Pipeline.Planning;

namespace StrataRec.UnitTests.Hosting;

/// <summary>
/// The lifetime of the instances bindings name (design 6.4): shared by identity and factory, loaded once before a plan is
/// served, never handed out again after a failed load, and disposed only once their lease is retired and its last user
/// (a request, a run, a call the run abandoned) has left, never while their load runs. Loads and disposals signal through
/// task completion sources, so nothing here depends on timing except the timeouts under test.
/// </summary>
public sealed class BindingHostClientTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "stratarec-config");

    [Fact]
    public async Task InstancesAreSharedByIdentityAndFactory()
    {
        await using var host = new BindingHost();
        var factory = new Factory();
        using var a = host.OpenLease();
        using var b = host.OpenLease();

        var first = a.Acquire(Spec(factory), factory.Create);
        Assert.Same(first, a.Acquire(Spec(factory), factory.Create)); // a lease takes each identity once
        Assert.Same(first, b.Acquire(Spec(factory, """{ "uri": "m.bin" }"""), factory.Create));
        Assert.NotSame(first, b.Acquire(Spec(factory, """{ "uri": "other.bin" }"""), factory.Create));
        var other = new Factory();
        Assert.NotSame(first, b.Acquire(Spec(other), other.Create)); // another registry never shares

        Assert.Equal((2, 1), (factory.Created.Count, other.Created.Count));
        Assert.Equal(3, host.InstanceCount);
        Assert.Equal(new object[] { first }, a.Instances);
    }

    [Fact]
    public async Task AnInstanceLoadsOnce_AndAPlanIsReadyOnlyAfterItsLoad()
    {
        await using var host = new BindingHost();
        var factory = new Factory();
        using var a = host.OpenLease();
        using var b = host.OpenLease();
        var instance = a.Acquire(Spec(factory), factory.Create);
        b.Acquire(Spec(factory), factory.Create);

        var waiting = a.WaitLoadedAsync();
        await instance.LoadStarted.Task.WaitAsync(Wait);
        Assert.False(waiting.IsCompleted);

        instance.FinishLoad();
        Assert.Empty(await waiting.WaitAsync(Wait));
        Assert.Empty(await b.WaitLoadedAsync().WaitAsync(Wait)); // taken over loaded
        Assert.Equal(1, instance.Loads);
    }

    [Fact]
    public async Task AFailedLoad_IsReportedAtTheSetting_AndTheNextAcquisitionGetsAFreshInstance()
    {
        var logs = new ListLoggerFactory();
        await using var host = new BindingHost(loggerFactory: logs);
        var factory = new Factory();
        var failed = host.OpenLease();
        var broken = failed.Acquire(Spec(factory), factory.Create);
        broken.FailLoad(new BindingException("/uri", "The model file 'm.bin' does not exist.", "check the path"));

        var failure = Assert.Single(await failed.WaitLoadedAsync().WaitAsync(Wait));

        Assert.Equal(("BINDING_LOAD_FAILED", "bindings:test", "/models/ctr_model/uri", "check the path"),
            (failure.Code, failure.Artifact, failure.Pointer, failure.Hint));
        Assert.Equal("The model 'ctr_model' (test@1) failed to load: The model file 'm.bin' does not exist.", failure.Message);
        Assert.Contains(logs.Entries, e => e.EventId == 1208);

        using var retry = host.OpenLease();
        var fresh = retry.Acquire(Spec(factory), factory.Create);
        Assert.NotSame(broken, fresh); // the next load really retries
        fresh.FinishLoad();
        Assert.Empty(await retry.WaitLoadedAsync().WaitAsync(Wait));

        failed.Dispose(); // the failed plan returns its lease: its instance goes
        await broken.Disposed.Task.WaitAsync(Wait);
        Assert.False(fresh.Disposed.Task.IsCompleted);
        Assert.Equal(1, host.InstanceCount);
    }

    [Fact]
    public async Task ALoadThatOutlastsItsTimeout_FailsThePlan_AndTheInstanceIsDisposedOnlyAfterTheLoadReturns()
    {
        await using var host = new BindingHost(new BindingHostOptions { LoadTimeout = TimeSpan.FromMilliseconds(50) });
        var factory = new Factory();
        var lease = host.OpenLease();
        var slow = lease.Acquire(Spec(factory), factory.Create);
        slow.IgnoreCancellation = true;

        var failure = Assert.Single(await lease.WaitLoadedAsync().WaitAsync(Wait));
        Assert.Contains("did not finish loading within 50 ms", failure.Message, StringComparison.Ordinal);
        Assert.Contains("LoadTimeout", failure.Hint, StringComparison.Ordinal);

        lease.Dispose();
        Assert.True(SpinWait.SpinUntil(() => slow.LoadToken.IsCancellationRequested, Wait)); // the release cancels the load...
        await Task.Delay(50);
        Assert.False(slow.Disposed.Task.IsCompleted); // ...and does not dispose under it

        slow.FinishLoad();
        await slow.Disposed.Task.WaitAsync(Wait);
        Assert.Equal(1, slow.DisposeCount);
    }

    [Fact]
    public async Task AnInstanceIsDisposedOnceItsLeaseIsRetiredAndItsLastUserHasLeft()
    {
        await using var host = new BindingHost();
        var factory = new Factory();
        var lease = host.OpenLease();
        var instance = Loaded(lease, factory);
        Assert.True(lease.TryEnter()); // a request
        var run = lease.Join(); // its run, ended by its request memory

        lease.Dispose(); // the plan is replaced
        Assert.False(lease.TryEnter()); // no new request enters a retired lease
        lease.RequestEnded(); // the request returned...
        await Task.Delay(20);
        Assert.False(instance.Disposed.Task.IsCompleted); // ...but a call its run abandoned still runs

        run.RequestEnded(); // that call returned
        await instance.Disposed.Task.WaitAsync(Wait);
        Assert.Equal(0, host.InstanceCount);
    }

    [Fact]
    public async Task AnInstanceTakenOverByANewPlan_OutlivesTheOldPlan()
    {
        await using var host = new BindingHost();
        var factory = new Factory();
        var old = host.OpenLease();
        var instance = Loaded(old, factory);
        var current = host.OpenLease();
        Assert.Same(instance, current.Acquire(Spec(factory), factory.Create));

        old.Dispose();
        await Task.Delay(20);
        Assert.False(instance.Disposed.Task.IsCompleted);

        current.Dispose();
        await instance.Disposed.Task.WaitAsync(Wait);
    }

    [Fact]
    public async Task AFactoryThatRejectsItsSettings_HoldsNothing()
    {
        await using var host = new BindingHost();
        using var lease = host.OpenLease();
        var spec = Spec(new Factory());

        var error = Assert.Throws<BindingException>(() => lease.Acquire<Instance>(spec, _ => throw new BindingException("/uri", "bad", "fix it")));

        Assert.Equal("/uri", error.Pointer);
        Assert.Equal(0, host.InstanceCount);
        Assert.Empty(lease.Instances);
    }

    [Fact]
    public async Task TheLoserOfACreationRace_IsDisposedWithoutLoading()
    {
        await using var host = new BindingHost();
        var factory = new Factory();
        using var a = host.OpenLease();
        using var b = host.OpenLease();
        var creating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new ManualResetEventSlim();
        var loser = Task.Run(() => a.Acquire(Spec(factory), context =>
        {
            var created = factory.Create(context);
            creating.SetResult();
            proceed.Wait(Wait);
            return created;
        }));
        await creating.Task.WaitAsync(Wait);

        var winner = b.Acquire(Spec(factory), factory.Create);
        proceed.Set();

        Assert.Same(winner, await loser.WaitAsync(Wait));
        var dropped = Assert.Single(factory.Created, i => !ReferenceEquals(i, winner));
        await dropped.Disposed.Task.WaitAsync(Wait);
        Assert.Equal(0, dropped.Loads);
        Assert.Equal(1, host.InstanceCount);
    }

    [Fact]
    public async Task ShutdownCancelsLoads_AndWaitsForDisposals_WithinItsTimeout()
    {
        var logs = new ListLoggerFactory();
        var host = new BindingHost(new BindingHostOptions { ShutdownTimeout = TimeSpan.FromMilliseconds(100) }, logs);
        var factory = new Factory();
        var loading = host.OpenLease();
        var held = host.OpenLease();
        var instance = loading.Acquire(Spec(factory), factory.Create);
        var waiting = loading.WaitLoadedAsync();
        await instance.LoadStarted.Task.WaitAsync(Wait);
        var used = Loaded(held, factory, """{ "uri": "used.bin" }""");
        Assert.True(held.TryEnter()); // a request still runs on the plan

        loading.Dispose();
        held.Dispose();
        await host.DisposeAsync().AsTask().WaitAsync(Wait); // bounded: the request is still running

        Assert.True(instance.LoadToken.IsCancellationRequested);
        await instance.Disposed.Task.WaitAsync(Wait);
        Assert.False(used.Disposed.Task.IsCompleted);
        Assert.Contains(logs.Entries, e => e.EventId == 1210); // the instance the request holds was not disposed in time
        var failure = Assert.Single(await waiting.WaitAsync(Wait));
        Assert.Contains("stopped before the load completed", failure.Message, StringComparison.Ordinal);
        Assert.Throws<ObjectDisposedException>(() => host.OpenLease());

        held.RequestEnded(); // the request returns after shutdown: its instance is still disposed
        await used.Disposed.Task.WaitAsync(Wait);
    }

    [Theory]
    [InlineData(true)] // the lease is released, then the host shuts down
    [InlineData(false)] // the host shuts down, then the lease is released
    public async Task ALoadsCancellationCallbackStillCleaningUp_DelaysTheDisposal_AndShutdownReportsIt(bool releasedFirst)
    {
        var logs = new ListLoggerFactory();
        var host = new BindingHost(new BindingHostOptions { ShutdownTimeout = TimeSpan.FromMilliseconds(100) }, logs);
        var factory = new Factory();
        var lease = host.OpenLease();
        var instance = lease.Acquire(Spec(factory), factory.Create);
        using var cleanup = new ManualResetEventSlim();
        instance.CleanupGate = cleanup;
        _ = lease.WaitLoadedAsync();
        await instance.LoadStarted.Task.WaitAsync(Wait);

        if (releasedFirst)
        {
            lease.Dispose();
        }

        var shutdown = host.DisposeAsync().AsTask();
        await instance.CleanupStarted.Task.WaitAsync(Wait);
        if (!releasedFirst)
        {
            lease.Dispose(); // a second cancellation must still wait for the first one's callbacks
        }

        await shutdown.WaitAsync(Wait); // bounded by its timeout
        Assert.Contains(logs.Entries, e => e.EventId == 1210);
        Assert.False(instance.Disposed.Task.IsCompleted); // the load returned, its cancellation callback has not

        cleanup.Set();
        await instance.Disposed.Task.WaitAsync(Wait);
        Assert.Equal((1, 0), (instance.DisposeCount, instance.DisposalsDuringCleanup));
        Assert.True(SpinWait.SpinUntil(() => host.InstanceCount == 0, Wait));
    }

    [Fact]
    public async Task ALoadsCancellationCallbackThatThrows_IsLogged_AndTheInstanceIsStillDisposed()
    {
        var logs = new ListLoggerFactory();
        await using var host = new BindingHost(loggerFactory: logs);
        var factory = new Factory();
        var lease = host.OpenLease();
        var instance = lease.Acquire(Spec(factory), factory.Create);
        using var cleanup = new ManualResetEventSlim(initialState: true);
        instance.CleanupGate = cleanup;
        instance.ThrowingCleanup = true;
        _ = lease.WaitLoadedAsync();
        await instance.LoadStarted.Task.WaitAsync(Wait);

        lease.Dispose();

        await instance.Disposed.Task.WaitAsync(Wait);
        Assert.Equal((1, 0), (instance.DisposeCount, instance.DisposalsDuringCleanup));
        Assert.Contains(logs.Entries, e => e.EventId == 1212); // logged before the disposal, which waited for the callback
    }

    [Fact]
    public async Task AThrowingDisposalOrLogger_BreaksNeitherTheReleaseNorShutdown()
    {
        var logs = new ListLoggerFactory(throwOn: 1207); // "loaded"
        await using var host = new BindingHost(new BindingHostOptions { ShutdownTimeout = Wait }, logs);
        var factory = new Factory();
        var lease = host.OpenLease();
        var instance = Loaded(lease, factory);
        instance.ThrowOnDispose = true;
        await logs.Thrown.Task.WaitAsync(Wait);

        lease.Dispose();
        await instance.Disposed.Task.WaitAsync(Wait);
        Assert.True(SpinWait.SpinUntil(() => logs.Entries.Any(e => e.EventId == 1211), Wait));
        await host.DisposeAsync().AsTask().WaitAsync(Wait);
        Assert.DoesNotContain(logs.Entries, e => e.EventId == 1210);
    }

    [Fact]
    public async Task ASignatureThatNeedsTheLoad_IsPendingInTheFirstCompile_AndKnownInTheSecond()
    {
        await using var host = new BindingHost();
        var factory = new Factory();
        using var lease = host.OpenLease();
        var statuses = new List<SignatureStatus>();

        var compiled = await lease.CompileAsync(bindings =>
        {
            var client = bindings.Acquire(Spec(factory), factory.Create);
            var lookup = bindings.Signature(client, "ctr_model", null); // starts the load, which waits for the test
            statuses.Add(lookup.Status);
            client.FinishLoad();
            return lookup;
        }).WaitAsync(Wait);

        Assert.Equal([SignatureStatus.Pending, SignatureStatus.Known], statuses);
        Assert.Equal("p_click", Assert.Single(compiled.Signature!.Outputs).Name);
        var instance = Assert.Single(factory.Created);
        Assert.Equal((1, 1), (instance.Loads, instance.SignatureCalls)); // asked once per (model, version)
    }

    [Fact]
    public async Task ASignatureThatDoesNotArriveInTime_FailsTheSecondCompile_WithAHint()
    {
        // A client without a load, so the timeout under test bounds only the lookup, however slow the machine is.
        await using var host = new BindingHost(new BindingHostOptions { LoadTimeout = TimeSpan.FromMilliseconds(50) });
        var server = new ModelServer(new TaskCompletionSource().Task); // never answers; honours the cancellation
        using var lease = host.OpenLease();

        var lookup = await lease.CompileAsync(bindings =>
            bindings.Signature(bindings.Acquire(Spec(server), server.Create), "ctr_model", "2")).WaitAsync(Wait);

        Assert.Equal(SignatureStatus.Failed, lookup.Status);
        Assert.Contains("within 50 ms", lookup.Error, StringComparison.Ordinal);
        Assert.Contains("LoadTimeout", lookup.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALookupThatAnswersDuringTheFirstCompile_StillGetsASecondCompile()
    {
        // The baseline pass is answered Pending and an arm pass of the same compile finds the answer: that compile checked
        // some passes and not others, so it must not count, although nothing is pending once it returns.
        await using var host = new BindingHost();
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new ModelServer(answer.Task);
        using var lease = host.OpenLease();
        var compiles = new List<SignatureStatus>();

        var lookup = await lease.CompileAsync(bindings =>
        {
            var client = bindings.Acquire(Spec(server), server.Create);
            var baseline = bindings.Signature(client, "ctr_model", null);
            compiles.Add(baseline.Status);
            answer.TrySetResult();
            Assert.True(SpinWait.SpinUntil(() => bindings.Signature(client, "ctr_model", null).Status == SignatureStatus.Known, Wait)); // an arm
            return baseline;
        }).WaitAsync(Wait);

        Assert.Equal([SignatureStatus.Pending, SignatureStatus.Known], compiles);
        Assert.Equal(SignatureStatus.Known, lookup.Status);
        Assert.Equal(1, server.Lookups);
    }

    [Fact]
    public async Task ASignatureLookupStillRunningWhenItsLeaseRetires_KeepsTheInstanceUntilTheClientAnswers()
    {
        await using var host = new BindingHost();
        var factory = new Factory();
        var lease = host.OpenLease();
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        Instance? client = null;

        var compiling = lease.CompileAsync(bindings =>
        {
            client = bindings.Acquire(Spec(factory), factory.Create);
            client.SignatureGate = answer.Task; // a model server that ignores the cancellation
            client.FinishLoad();
            return bindings.Signature(client, "ctr_model", null);
        }, cancel.Token);
        Assert.True(SpinWait.SpinUntil(() => client?.SignatureCalls == 1, Wait));

        cancel.Cancel(); // the engine's load is cancelled...
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compiling.WaitAsync(Wait));
        lease.Dispose(); // ...and returns its lease
        Assert.True(SpinWait.SpinUntil(() => client!.SignatureToken.IsCancellationRequested, Wait)); // the lookup is cancelled...
        await Task.Delay(50);
        Assert.False(client!.Disposed.Task.IsCompleted); // ...and the client is not disposed under it

        answer.SetResult();
        await client.Disposed.Task.WaitAsync(Wait);
        Assert.Equal((1, 0), (client.DisposeCount, client.CallsAtOrAfterDispose));
        Assert.Equal(0, host.InstanceCount);
    }

    [Fact]
    public async Task TheSignatureOfAClientWhoseLoadFailed_IsUnchecked_TheLoadFailureIsTheError()
    {
        await using var host = new BindingHost();
        var factory = new Factory();
        using var lease = host.OpenLease();

        var lookup = await lease.CompileAsync(bindings =>
        {
            var client = bindings.Acquire(Spec(factory), factory.Create);
            var lookup = bindings.Signature(client, "ctr_model", null);
            client.FailLoad(new InvalidOperationException("corrupt model"));
            return lookup;
        }).WaitAsync(Wait);

        Assert.Equal(SignatureStatus.Unchecked, lookup.Status);
        var failure = Assert.Single(await lease.WaitLoadedAsync().WaitAsync(Wait));
        Assert.Contains("corrupt model", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostThatLoadsNothing_NeverLoads_AndLeavesSignaturesUnchecked()
    {
        await using var host = new BindingHost(new BindingHostOptions { Load = false });
        var factory = new Factory();
        using var lease = host.OpenLease();

        var lookup = await lease.CompileAsync(bindings =>
            bindings.Signature(bindings.Acquire(Spec(factory), factory.Create), "ctr_model", null)).WaitAsync(Wait);

        Assert.Equal(SignatureStatus.Unchecked, lookup.Status);
        Assert.Empty(await lease.WaitLoadedAsync().WaitAsync(Wait));
        Assert.Equal((0, 0), (factory.Created[0].Loads, factory.Created[0].SignatureCalls));
    }

    [Fact]
    public async Task TheSecretStoreReachesTheFactoryContext()
    {
        var store = new Store();
        await using var host = new BindingHost(new BindingHostOptions { Secrets = store });
        var factory = new Factory();
        using var lease = host.OpenLease();
        var instance = lease.Acquire(Spec(factory, """{ "uri": "m.bin", "credentials": "secret://ctr-token" }"""), factory.Create);
        instance.OnLoad = async token => instance.Credential = await instance.Context.SecretReference("credentials")!.ResolveAsync(token);
        instance.FinishLoad();

        Assert.Empty(await lease.WaitLoadedAsync().WaitAsync(Wait));
        Assert.Equal("value-of-ctr-token", instance.Credential!.Reveal());
    }

    [Fact]
    public async Task ALoadFailureWhoseMessageCannotBeRead_StillFailsThePlan_AtTheBinding()
    {
        await using var host = new BindingHost();
        var factory = new Factory();
        using var lease = host.OpenLease();
        var broken = lease.Acquire(Spec(factory), factory.Create);
        lease.Acquire(Spec(factory, """{ "uri": "slow.bin" }"""), factory.Create); // still loading when the other fails
        broken.FailLoad(new UnreadableException());

        var failure = Assert.Single(await lease.WaitLoadedAsync().WaitAsync(Wait)); // stops early only with a failure to show

        Assert.Equal(("BINDING_LOAD_FAILED", "bindings:test", "/models/ctr_model"), (failure.Code, failure.Artifact, failure.Pointer));
        Assert.Contains(typeof(UnreadableException).FullName!, failure.Message, StringComparison.Ordinal);
        Assert.IsType<UnreadableException>(failure.Exception.InnerException);
    }

    [Fact]
    public async Task ASignatureFailureWhoseMessageCannotBeRead_IsAFailedLookup()
    {
        await using var host = new BindingHost();
        var server = new ModelServer(Task.FromException(new UnreadableException()));
        using var lease = host.OpenLease();

        var lookup = await lease.CompileAsync(bindings =>
            bindings.Signature(bindings.Acquire(Spec(server), server.Create), "ctr_model", null)).WaitAsync(Wait);

        Assert.Equal(SignatureStatus.Failed, lookup.Status);
        Assert.Contains(typeof(UnreadableException).FullName!, lookup.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FactoriesThatAreEqualButDistinct_NeverShareAnInstance()
    {
        await using var host = new BindingHost();
        var a = new ValueFactory("test");
        var b = new ValueFactory("test");
        Assert.Equal(a, b); // a record: equal by value
        using var first = host.OpenLease();
        using var second = host.OpenLease();

        var fromA = first.Acquire(Spec(a), a.Create);
        var fromB = second.Acquire(Spec(b), b.Create);

        Assert.NotSame(fromA, fromB); // instances are shared only by the same factory object
        Assert.Equal(2, host.InstanceCount);
    }

    [Fact]
    public async Task AFactoryWhoseHashChangesAsItCreates_StillReleasesWhatItCreated()
    {
        await using var host = new BindingHost();
        var factory = new CountingRecordFactory(); // a record whose Created count is part of its equality and hash
        var lease = host.OpenLease();
        var first = lease.Acquire(Spec(factory), factory.Create);
        var second = lease.Acquire(Spec(factory, """{ "uri": "other.bin" }"""), factory.Create);
        Assert.Equal((2, 2), (factory.Created, host.InstanceCount));

        lease.Dispose();

        await Task.WhenAll(first.Disposed.Task, second.Disposed.Task).WaitAsync(Wait);
        Assert.Equal(0, host.InstanceCount); // both entries were found again, although the factory's hash changed
    }

    private static Instance Loaded(BindingLease lease, Factory factory, string settings = """{ "uri": "m.bin" }""")
    {
        var instance = lease.Acquire(Spec(factory, settings), factory.Create);
        instance.FinishLoad();
        Assert.Empty(lease.WaitLoadedAsync().WaitAsync(Wait).GetAwaiter().GetResult());
        return instance;
    }

    private static BindingSpec Spec(object factory, string settings = """{ "uri": "m.bin" }""")
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in JsonDocument.Parse(settings).RootElement.EnumerateObject())
        {
            values[property.Name] = property.Value.Clone();
        }

        return BindingSpec.Create(BindingSection.Models, "ctr_model", "test@1", values, Root, factory, "bindings:test");
    }

    private sealed class Factory
    {
        public ConcurrentQueue<Instance> CreatedQueue { get; } = new();

        public List<Instance> Created => CreatedQueue.ToList();

        public Instance Create(BindingContext context)
        {
            var instance = new Instance(context);
            CreatedQueue.Enqueue(instance);
            return instance;
        }
    }

    /// <summary>An implementation's exception whose message cannot be read: describing it throws.</summary>
    private sealed class UnreadableException : Exception
    {
        public override string Message => throw new InvalidOperationException("the message failed");
    }

    /// <summary>A factory with value equality: two with the same kind are equal, and neither may get the other's instances.</summary>
    private sealed record ValueFactory(string Kind)
    {
        public Instance Create(BindingContext context) => new(context);
    }

    /// <summary>A record factory with mutable state: every creation changes its equality and its hash.</summary>
    private sealed record CountingRecordFactory
    {
        public int Created { get; private set; }

        public Instance Create(BindingContext context)
        {
            Created++;
            return new Instance(context);
        }
    }

    /// <summary>
    /// A client without a load whose signature only its model server knows: each lookup answers once
    /// <paramref name="answer"/> completes, or is cancelled. Its own factory (one instance).
    /// </summary>
    private sealed class ModelServer(Task answer) : IModelClient
    {
        private int _lookups;

        public string ClientType => "server@1";

        public int Lookups => Volatile.Read(ref _lookups);

        public ModelServer Create(BindingContext context) => this;

        public async ValueTask<ModelSignature> GetSignatureAsync(string model, string? version, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _lookups);
            await answer.WaitAsync(cancellationToken);
            return Instance.Signature();
        }

        public ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ModelStatus.Ok);
    }

    /// <summary>A loadable, disposable model client whose load the test finishes or fails.</summary>
    private sealed class Instance(BindingContext context) : IModelClient, ILoadable, IAsyncDisposable
    {
        private readonly TaskCompletionSource _load = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _loads;
        private int _signatureCalls;
        private int _disposeCount;
        private int _inFlight;
        private int _callsAtOrAfterDispose;
        private int _cleaningUp;
        private int _disposalsDuringCleanup;

        public BindingContext Context { get; } = context;

        /// <summary>When set, a signature lookup answers once this completes, whatever its token says.</summary>
        public Task? SignatureGate { get; set; }

        public CancellationToken SignatureToken { get; private set; }

        /// <summary>Signature lookups that were running when the disposal began, or started after it: always 0.</summary>
        public int CallsAtOrAfterDispose => Volatile.Read(ref _callsAtOrAfterDispose);

        public string ClientType => "test@1";

        public TaskCompletionSource LoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken LoadToken { get; private set; }

        public bool IgnoreCancellation { get; set; }

        public bool ThrowOnDispose { get; set; }

        public Func<CancellationToken, Task>? OnLoad { get; set; }

        public Secret? Credential { get; set; }

        public int Loads => Volatile.Read(ref _loads);

        public int SignatureCalls => Volatile.Read(ref _signatureCalls);

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void FinishLoad() => _load.TrySetResult();

        public void FailLoad(Exception error) => _load.TrySetException(error);

        /// <summary>
        /// When set, the load registers a cancellation callback that ends the load at once and then goes on cleaning up
        /// until this is set (and then throws when <see cref="ThrowingCleanup"/>), as a connection's cancellation would.
        /// </summary>
        public ManualResetEventSlim? CleanupGate { get; set; }

        public bool ThrowingCleanup { get; set; }

        public TaskCompletionSource CleanupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Disposals that began while a cancellation callback was still cleaning up: always 0.</summary>
        public int DisposalsDuringCleanup => Volatile.Read(ref _disposalsDuringCleanup);

        public async ValueTask LoadAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _loads);
            LoadToken = cancellationToken;
            if (CleanupGate is { } gate)
            {
                cancellationToken.Register(() =>
                {
                    Volatile.Write(ref _cleaningUp, 1);
                    CleanupStarted.TrySetResult();
                    _load.TrySetCanceled(); // the load returns first...
                    try
                    {
                        gate.Wait(); // ...while the cleanup goes on using the instance
                        if (ThrowingCleanup)
                        {
                            throw new InvalidOperationException("cleanup failed");
                        }
                    }
                    finally
                    {
                        Volatile.Write(ref _cleaningUp, 0);
                    }
                });
            }

            LoadStarted.TrySetResult();
            await (IgnoreCancellation || CleanupGate is not null ? _load.Task : _load.Task.WaitAsync(cancellationToken));
            if (OnLoad is not null)
            {
                await OnLoad(cancellationToken);
            }
        }

        public async ValueTask<ModelSignature> GetSignatureAsync(string model, string? version, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _inFlight);
            try
            {
                if (DisposeCount > 0)
                {
                    Interlocked.Increment(ref _callsAtOrAfterDispose);
                }

                SignatureToken = cancellationToken;
                Interlocked.Increment(ref _signatureCalls);
                if (SignatureGate is not null)
                {
                    await SignatureGate; // ignores the cancellation
                }

                return Signature();
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ModelStatus.Ok);

        public ValueTask DisposeAsync()
        {
            if (Volatile.Read(ref _inFlight) > 0)
            {
                Interlocked.Increment(ref _callsAtOrAfterDispose);
            }

            if (Volatile.Read(ref _cleaningUp) != 0)
            {
                Interlocked.Increment(ref _disposalsDuringCleanup);
            }

            Interlocked.Increment(ref _disposeCount);
            Disposed.TrySetResult();
            return ThrowOnDispose ? ValueTask.FromException(new InvalidOperationException("dispose failed")) : ValueTask.CompletedTask;
        }

        public static ModelSignature Signature() =>
            new([new TensorSpec("x", TensorElementType.Float32, [-1])], [new TensorSpec("p_click", TensorElementType.Float32, [-1])]);
    }

    private sealed class Store : ISecretResolver
    {
        public ValueTask<string?> ResolveAsync(string name, CancellationToken cancellationToken) => ValueTask.FromResult<string?>("value-of-" + name);
    }

    /// <summary>Records every log entry; with <paramref name="throwOn"/>, the log call for that event id throws instead.</summary>
    private sealed class ListLoggerFactory(int? throwOn = null) : ILoggerFactory
    {
        public int? ThrowOn { get; } = throwOn;

        public ConcurrentQueue<(int EventId, string Message)> Entries { get; } = new();

        public TaskCompletionSource Thrown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class Logger(ListLoggerFactory factory) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (eventId.Id == factory.ThrowOn)
                {
                    factory.Thrown.TrySetResult();
                    throw new InvalidOperationException("logger failed");
                }

                factory.Entries.Enqueue((eventId.Id, formatter(state, exception)));
            }
        }
    }
}
