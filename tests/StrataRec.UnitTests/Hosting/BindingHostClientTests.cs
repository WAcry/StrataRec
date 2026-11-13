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
        await using var host = new BindingHost(new BindingHostOptions { LoadTimeout = TimeSpan.FromMilliseconds(50) });
        var factory = new Factory { SignatureNeverAnswers = true };
        using var lease = host.OpenLease();

        var lookup = await lease.CompileAsync(bindings =>
        {
            var client = bindings.Acquire(Spec(factory), factory.Create);
            var lookup = bindings.Signature(client, "ctr_model", "2");
            client.FinishLoad();
            return lookup;
        }).WaitAsync(Wait);

        Assert.Equal(SignatureStatus.Failed, lookup.Status);
        Assert.Contains("within 50 ms", lookup.Error, StringComparison.Ordinal);
        Assert.Contains("LoadTimeout", lookup.Hint, StringComparison.Ordinal);
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

    private static Instance Loaded(BindingLease lease, Factory factory, string settings = """{ "uri": "m.bin" }""")
    {
        var instance = lease.Acquire(Spec(factory, settings), factory.Create);
        instance.FinishLoad();
        Assert.Empty(lease.WaitLoadedAsync().WaitAsync(Wait).GetAwaiter().GetResult());
        return instance;
    }

    private static BindingSpec Spec(Factory factory, string settings = """{ "uri": "m.bin" }""")
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

        public bool SignatureNeverAnswers { get; init; }

        public Instance Create(BindingContext context)
        {
            var instance = new Instance(context, SignatureNeverAnswers);
            CreatedQueue.Enqueue(instance);
            return instance;
        }
    }

    /// <summary>A loadable, disposable model client whose load the test finishes or fails.</summary>
    private sealed class Instance(BindingContext context, bool signatureNeverAnswers) : IModelClient, ILoadable, IAsyncDisposable
    {
        private readonly TaskCompletionSource _load = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _loads;
        private int _signatureCalls;
        private int _disposeCount;

        public BindingContext Context { get; } = context;

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

        public async ValueTask LoadAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _loads);
            LoadToken = cancellationToken;
            LoadStarted.TrySetResult();
            await (IgnoreCancellation ? _load.Task : _load.Task.WaitAsync(cancellationToken));
            if (OnLoad is not null)
            {
                await OnLoad(cancellationToken);
            }
        }

        public ValueTask<ModelSignature> GetSignatureAsync(string model, string? version, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _signatureCalls);
            return signatureNeverAnswers
                ? new ValueTask<ModelSignature>(Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => Signature(), TaskScheduler.Default))
                : ValueTask.FromResult(Signature());
        }

        public ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ModelStatus.Ok);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            Disposed.TrySetResult();
            return ThrowOnDispose ? ValueTask.FromException(new InvalidOperationException("dispose failed")) : ValueTask.CompletedTask;
        }

        private static ModelSignature Signature() =>
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
