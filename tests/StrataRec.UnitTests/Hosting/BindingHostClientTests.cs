using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Features.Bindings;
using StrataRec.Hosting.Bindings;
using StrataRec.Pipeline.Planning;
using StrataRec.Core.Services;

namespace StrataRec.UnitTests.Hosting;

/// <summary>
/// Loads and disposals signal through task completion sources, so nothing here depends on timing except the timeouts
/// under test.
/// </summary>
public sealed class BindingHostClientTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    /// <summary>For what must happen promptly: far below the default <see cref="BindingHostOptions.LoadTimeout"/>.</summary>
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(10);
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "stratarec-config");

    [Fact]
    public async Task InstancesAreSharedByIdentityAndFactory()
    {
        await using var host = new BindingHost();
        var factory = new Factory();
        using var a = host.OpenLease();
        using var b = host.OpenLease();

        var first = a.Acquire(Spec(factory), factory.Create);
        Assert.Same(first, a.Acquire(Spec(factory), factory.Create));
        Assert.Same(first, b.Acquire(Spec(factory, """{ "uri": "m.bin" }"""), factory.Create));
        Assert.NotSame(first, b.Acquire(Spec(factory, """{ "uri": "other.bin" }"""), factory.Create));
        var other = new Factory();
        Assert.NotSame(first, b.Acquire(Spec(other), other.Create));

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

        var waiting = a.WaitLoadedAsync(TestContext.Current.CancellationToken);
        await instance.LoadStarted.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);

        instance.FinishLoad();
        Assert.Empty(await waiting.WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.Empty(await b.WaitLoadedAsync(TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken));
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

        var failure = Assert.Single(await failed.WaitLoadedAsync(TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken));

        Assert.Equal(("BINDING_LOAD_FAILED", "bindings:test", "/services/ctr_model/uri", "check the path"),
            (failure.Code, failure.Artifact, failure.Pointer, failure.Hint));
        Assert.Equal("The service 'ctr_model' (test@1) failed to load: The model file 'm.bin' does not exist.", failure.Message);
        Assert.Contains(logs.Entries, e => e.EventId == 1208);

        using var retry = host.OpenLease();
        var fresh = retry.Acquire(Spec(factory), factory.Create);
        Assert.NotSame(broken, fresh);
        fresh.FinishLoad();
        Assert.Empty(await retry.WaitLoadedAsync(TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken));

        failed.Dispose();
        await broken.Disposed.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
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

        var failure = Assert.Single(await lease.WaitLoadedAsync(TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.Contains("did not finish loading within 50 ms", failure.Message, StringComparison.Ordinal);
        Assert.Contains("LoadTimeout", failure.Hint, StringComparison.Ordinal);

        lease.Dispose();
        Assert.True(SpinWait.SpinUntil(() => slow.LoadToken.IsCancellationRequested, Wait));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(slow.Disposed.Task.IsCompleted);

        slow.FinishLoad();
        await slow.Disposed.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.Equal(1, slow.DisposeCount);
    }

    [Fact]
    public async Task AnInstanceIsDisposedOnceItsLeaseIsRetiredAndItsLastUserHasLeft()
    {
        await using var host = new BindingHost();
        var factory = new Factory();
        var lease = host.OpenLease();
        var instance = Loaded(lease, factory);
        Assert.True(lease.TryEnter());
        var run = lease.Join(); // its run, ended by its request memory

        lease.Dispose();
        Assert.False(lease.TryEnter());
        lease.RequestEnded();
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.False(instance.Disposed.Task.IsCompleted); // ...but a call its run abandoned still runs

        run.RequestEnded();
        await instance.Disposed.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
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
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.False(instance.Disposed.Task.IsCompleted);

        current.Dispose();
        await instance.Disposed.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
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
        await creating.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

        var winner = b.Acquire(Spec(factory), factory.Create);
        proceed.Set();

        Assert.Same(winner, await loser.WaitAsync(Wait, TestContext.Current.CancellationToken));
        var dropped = Assert.Single(factory.Created, i => !ReferenceEquals(i, winner));
        await dropped.Disposed.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
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
        var waiting = loading.WaitLoadedAsync(TestContext.Current.CancellationToken);
        await instance.LoadStarted.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        var used = Loaded(held, factory, """{ "uri": "used.bin" }""");
        Assert.True(held.TryEnter());

        loading.Dispose();
        held.Dispose();
        await host.DisposeAsync().AsTask().WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.True(instance.LoadToken.IsCancellationRequested);
        await instance.Disposed.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.False(used.Disposed.Task.IsCompleted);
        Assert.Contains(logs.Entries, e => e.EventId == 1210); // the instance the request holds was not disposed in time
        var failure = Assert.Single(await waiting.WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.Contains("stopped before the load completed", failure.Message, StringComparison.Ordinal);
        Assert.Throws<ObjectDisposedException>(() => host.OpenLease());

        held.RequestEnded();
        await used.Disposed.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ALoadsCancellationCallbackStillCleaningUp_DelaysTheDisposal_AndShutdownReportsIt(bool releasedFirst)
    {
        var logs = new ListLoggerFactory();
        var host = new BindingHost(new BindingHostOptions { ShutdownTimeout = TimeSpan.FromMilliseconds(100) }, logs);
        var factory = new Factory();
        var lease = host.OpenLease();
        var instance = lease.Acquire(Spec(factory), factory.Create);
        using var cleanup = new ManualResetEventSlim();
        instance.CleanupGate = cleanup;
        _ = lease.WaitLoadedAsync(TestContext.Current.CancellationToken);
        await instance.LoadStarted.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

        if (releasedFirst)
        {
            lease.Dispose();
        }

        var shutdown = host.DisposeAsync().AsTask();
        await instance.CleanupStarted.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        if (!releasedFirst)
        {
            lease.Dispose(); // a second cancellation must still wait for the first one's callbacks
        }

        await shutdown.WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.Contains(logs.Entries, e => e.EventId == 1210);
        Assert.False(instance.Disposed.Task.IsCompleted);

        cleanup.Set();
        await instance.Disposed.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
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
        _ = lease.WaitLoadedAsync(TestContext.Current.CancellationToken);
        await instance.LoadStarted.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

        lease.Dispose();

        await instance.Disposed.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.Equal((1, 0), (instance.DisposeCount, instance.DisposalsDuringCleanup));
        Assert.Contains(logs.Entries, e => e.EventId == 1212);
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
        await logs.Thrown.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

        lease.Dispose();
        await instance.Disposed.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.True(SpinWait.SpinUntil(() => logs.Entries.Any(e => e.EventId == 1211), Wait));
        await host.DisposeAsync().AsTask().WaitAsync(Wait, TestContext.Current.CancellationToken);
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
            var lookup = bindings.Signature(client, "ctr_model", null);
            statuses.Add(lookup.Status);
            client.FinishLoad();
            return lookup;
        }, TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Equal([SignatureStatus.Pending, SignatureStatus.Known], statuses);
        Assert.Equal("p_click", Assert.Single(compiled.Signature!.Outputs).Name);
        var instance = Assert.Single(factory.Created);
        Assert.Equal((1, 1), (instance.Loads, instance.SignatureCalls));
    }

    [Fact]
    public async Task ASignatureThatDoesNotArriveInTime_FailsTheSecondCompile_WithAHint()
    {
        // A client without a load, so the timeout under test bounds only the lookup, however slow the machine is.
        await using var host = new BindingHost(new BindingHostOptions { LoadTimeout = TimeSpan.FromMilliseconds(50) });
        var server = new ModelServer(new TaskCompletionSource().Task); // never answers; honours the cancellation
        using var lease = host.OpenLease();

        var lookup = await lease.CompileAsync(bindings =>
            bindings.Signature(bindings.Acquire(Spec(server), server.Create), "ctr_model", "2"), TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken);

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
            Assert.True(SpinWait.SpinUntil(() => bindings.Signature(client, "ctr_model", null).Status == SignatureStatus.Known, Wait));
            return baseline;
        }, TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Equal([SignatureStatus.Pending, SignatureStatus.Known], compiles);
        Assert.Equal(SignatureStatus.Known, lookup.Status);
        Assert.Equal(1, server.Lookups);
    }

    [Fact]
    public async Task ASignatureLookupThatBlocksBeforeReturning_IsBoundedByTheLoadTimeout_AndKeepsItsInstanceUntilItReturns()
    {
        await using var host = new BindingHost(new BindingHostOptions { LoadTimeout = TimeSpan.FromMilliseconds(50) });
        var server = new BlockingServer();
        var lease = host.OpenLease();
        try
        {
            // On a thread of its own, so a compile that blocks fails the test instead of hanging it.
            var lookup = await Task.Run(() => lease.CompileAsync(bindings =>
                bindings.Signature(bindings.Acquire(Spec(server), server.Create), "ctr_model", null))).WaitAsync(Wait, TestContext.Current.CancellationToken);

            Assert.Equal(SignatureStatus.Failed, lookup.Status);
            Assert.Contains("within 50 ms", lookup.Error, StringComparison.Ordinal);

            // The bound may end the wait before the pool even started the client's method: what follows needs
            // the client inside it, so wait for that instead of assuming the pool started it within the 50 ms.
            await server.Entered.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
            Assert.True(server.Inside);
            lease.Dispose();
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.False(server.Disposed.Task.IsCompleted);
        }
        finally
        {
            server.Gate.Set();
        }

        await server.Disposed.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.Equal(0, server.DisposalsInside);
    }

    [Fact]
    public async Task ASignatureLookupThatBlocksBeforeReturning_DoesNotHoldUpACancelledCompile()
    {
        await using var host = new BindingHost(); // a bound of minutes: only the cancellation can end the wait
        var server = new BlockingServer();
        var lease = host.OpenLease();
        using var cancel = new CancellationTokenSource();
        try
        {
            var compiling = Task.Run(() => lease.CompileAsync(bindings =>
                bindings.Signature(bindings.Acquire(Spec(server), server.Create), "ctr_model", null), cancel.Token));
            await server.Entered.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

            cancel.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compiling.WaitAsync(Wait, TestContext.Current.CancellationToken));
            Assert.True(server.Inside);
            lease.Dispose();
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.False(server.Disposed.Task.IsCompleted);
        }
        finally
        {
            server.Gate.Set();
        }

        await server.Disposed.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.Equal(0, server.DisposalsInside);
    }

    [Fact]
    public async Task ASignatureLookupsCancellationCallbackStillCleaningUp_KeepsTheInstanceUntilItEnds()
    {
        await using var host = new BindingHost();
        var server = new CleaningServer();
        var lease = host.OpenLease();

        var compiling = lease.CompileAsync(bindings => bindings.Signature(bindings.Acquire(Spec(server), server.Create), "ctr_model", null), TestContext.Current.CancellationToken);
        await server.Entered.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        lease.Dispose(); // cancels the lookup: its callback ends the lookup, then goes on cleaning up with the instance
        await server.CleanupStarted.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => compiling.WaitAsync(Wait, TestContext.Current.CancellationToken));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(server.Disposed.Task.IsCompleted);

        server.Gate.Set();
        await server.Disposed.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.Equal(0, server.DisposalsDuringCleanup);
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

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compiling.WaitAsync(Wait, TestContext.Current.CancellationToken));
        lease.Dispose();
        Assert.True(SpinWait.SpinUntil(() => client!.SignatureToken.IsCancellationRequested, Wait));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(client!.Disposed.Task.IsCompleted);

        answer.SetResult();
        await client.Disposed.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.Equal((1, 0), (client.DisposeCount, client.CallsAtOrAfterDispose));
        Assert.Equal(0, host.InstanceCount);
    }

    [Fact]
    public async Task ACancelledPlansLookupWaitingForTheLoad_EndsAtOnce_SoItsLeaseEndsAndTheLoadIsCancelled()
    {
        // The lookup holds the lease, the lease holds the client, and the client's load is cancelled only once nobody
        // holds it.
        await using var host = new BindingHost();
        var factory = new Factory();
        var lease = host.OpenLease();
        using var cancel = new CancellationTokenSource();

        var compiling = lease.CompileAsync(bindings =>
            bindings.Signature(bindings.Acquire(Spec(factory), factory.Create), "ctr_model", null), cancel.Token);
        var client = Assert.Single(factory.Created);
        await client.LoadStarted.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compiling.WaitAsync(Wait, TestContext.Current.CancellationToken));
        lease.Dispose();

        await client.Disposed.Task.WaitAsync(Prompt, TestContext.Current.CancellationToken);
        Assert.True(client.LoadToken.IsCancellationRequested);
        Assert.Empty(lease.Instances);
        Assert.Equal(0, client.SignatureCalls);
    }

    [Fact]
    public async Task APlanSharingTheClient_KeepsItsLoadWhenAnotherPlanIsCancelled_AndGetsItsAnswer()
    {
        await using var host = new BindingHost();
        var factory = new Factory();
        var cancelled = host.OpenLease();
        using var surviving = host.OpenLease();
        using var cancel = new CancellationTokenSource();

        var first = cancelled.CompileAsync(bindings =>
            bindings.Signature(bindings.Acquire(Spec(factory), factory.Create), "ctr_model", null), cancel.Token);
        var second = surviving.CompileAsync(bindings =>
            bindings.Signature(bindings.Acquire(Spec(factory), factory.Create), "ctr_model", null), TestContext.Current.CancellationToken);
        var client = Assert.Single(factory.Created);
        await client.LoadStarted.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Wait, TestContext.Current.CancellationToken));
        cancelled.Dispose();

        Assert.True(SpinWait.SpinUntil(() => cancelled.Instances.Count == 0, Prompt));
        Assert.False(client.LoadToken.IsCancellationRequested);
        Assert.False(second.IsCompleted);

        client.FinishLoad();
        Assert.Equal(SignatureStatus.Known, (await second.WaitAsync(Wait, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, client.Loads);
        Assert.False(client.Disposed.Task.IsCompleted);
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
        }, TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Equal(SignatureStatus.Unchecked, lookup.Status);
        var failure = Assert.Single(await lease.WaitLoadedAsync(TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.Contains("corrupt model", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostThatLoadsNothing_NeverLoads_AndLeavesSignaturesUnchecked()
    {
        await using var host = new BindingHost(new BindingHostOptions { Load = false });
        var factory = new Factory();
        using var lease = host.OpenLease();

        var lookup = await lease.CompileAsync(bindings =>
            bindings.Signature(bindings.Acquire(Spec(factory), factory.Create), "ctr_model", null), TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Equal(SignatureStatus.Unchecked, lookup.Status);
        Assert.Empty(await lease.WaitLoadedAsync(TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken));
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
        instance.OnLoad = async token =>
            instance.Credential = await instance.Context.SecretReference(instance.Context.Settings["credentials"], "/credentials").ResolveAsync(token);
        instance.FinishLoad();

        Assert.Empty(await lease.WaitLoadedAsync(TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken));
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

        var failure = Assert.Single(await lease.WaitLoadedAsync(TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken));

        Assert.Equal(("BINDING_LOAD_FAILED", "bindings:test", "/services/ctr_model"), (failure.Code, failure.Artifact, failure.Pointer));
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
            bindings.Signature(bindings.Acquire(Spec(server), server.Create), "ctr_model", null), TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Equal(SignatureStatus.Failed, lookup.Status);
        Assert.Contains(typeof(UnreadableException).FullName!, lookup.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FactoriesThatAreEqualButDistinct_NeverShareAnInstance()
    {
        await using var host = new BindingHost();
        var a = new ValueFactory("test");
        var b = new ValueFactory("test");
        Assert.Equal(a, b);
        using var first = host.OpenLease();
        using var second = host.OpenLease();

        var fromA = first.Acquire(Spec(a), a.Create);
        var fromB = second.Acquire(Spec(b), b.Create);

        Assert.NotSame(fromA, fromB);
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

        await Task.WhenAll(first.Disposed.Task, second.Disposed.Task).WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.Equal(0, host.InstanceCount);
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

        return BindingSpec.Create(BindingKind.Service, "ctr_model", "test@1", values, Root, factory, "bindings:test");
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

    private sealed class UnreadableException : Exception
    {
        public override string Message => throw new InvalidOperationException("the message failed");
    }

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

    private sealed class ModelServer(Task answer) : IServiceClient
    {
        private int _lookups;

        public int Lookups => Volatile.Read(ref _lookups);

        public ModelServer Create(BindingContext context) => this;

        public async ValueTask<ServiceSignature> DescribeAsync(string model, string? version, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _lookups);
            await answer.WaitAsync(cancellationToken);
            return Instance.Signature();
        }

        public ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse response, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ServiceStatus.Ok);
    }

    private sealed class BlockingServer : IServiceClient, IDisposable
    {
        private int _inside;
        private int _disposalsInside;

        public ManualResetEventSlim Gate { get; } = new();

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Inside => Volatile.Read(ref _inside) > 0;

        public int DisposalsInside => Volatile.Read(ref _disposalsInside);

        public BlockingServer Create(BindingContext context) => this;

        public ValueTask<ServiceSignature> DescribeAsync(string model, string? version, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _inside);
            try
            {
                Entered.TrySetResult();
                Gate.Wait();
                return ValueTask.FromResult(Instance.Signature());
            }
            finally
            {
                Interlocked.Decrement(ref _inside);
            }
        }

        public ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse response, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ServiceStatus.Ok);

        public void Dispose()
        {
            if (Inside)
            {
                Interlocked.Increment(ref _disposalsInside);
            }

            Disposed.TrySetResult();
        }
    }

    /// <summary>
    /// A client without a load whose signature lookup waits for its server; when the lookup is cancelled, its callback
    /// ends the lookup at once and then goes on cleaning up until <see cref="Gate"/> is set.
    /// </summary>
    private sealed class CleaningServer : IServiceClient, IDisposable
    {
        private int _cleaningUp;
        private int _disposalsDuringCleanup;

        public ManualResetEventSlim Gate { get; } = new();

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CleanupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposalsDuringCleanup => Volatile.Read(ref _disposalsDuringCleanup);

        public CleaningServer Create(BindingContext context) => this;

        public ValueTask<ServiceSignature> DescribeAsync(string model, string? version, CancellationToken cancellationToken)
        {
            var answer = new TaskCompletionSource<ServiceSignature>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() =>
            {
                Volatile.Write(ref _cleaningUp, 1);
                CleanupStarted.TrySetResult();
                answer.TrySetCanceled();
                try
                {
                    Gate.Wait();
                }
                finally
                {
                    Volatile.Write(ref _cleaningUp, 0);
                }
            });
            Entered.TrySetResult();
            return new ValueTask<ServiceSignature>(answer.Task);
        }

        public ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse response, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ServiceStatus.Ok);

        public void Dispose()
        {
            if (Volatile.Read(ref _cleaningUp) != 0)
            {
                Interlocked.Increment(ref _disposalsDuringCleanup);
            }

            Disposed.TrySetResult();
        }
    }

    private sealed class Instance(BindingContext context) : IServiceClient, ILoadable, IAsyncDisposable
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
                    _load.TrySetCanceled();
                    try
                    {
                        gate.Wait();
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

        public async ValueTask<ServiceSignature> DescribeAsync(string model, string? version, CancellationToken cancellationToken)
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
                    await SignatureGate;
                }

                return Signature();
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse response, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ServiceStatus.Ok);

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

        public static ServiceSignature Signature() =>
            new([new TensorSpec("x", TensorElementType.Float32, [-1])], [new TensorSpec("p_click", TensorElementType.Float32, [-1])]);
    }

    private sealed class Store : ISecretResolver
    {
        public ValueTask<string?> ResolveAsync(string name, CancellationToken cancellationToken) => ValueTask.FromResult<string?>("value-of-" + name);
    }

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
