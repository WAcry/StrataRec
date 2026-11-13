using System.Collections.Concurrent;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Types;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features;

/// <summary>
/// The fetcher's call-site hook (design 13.4): any connector is recorded with the values it wrote per key, and a
/// replaying hook answers calls without the connector being called.
/// </summary>
public class FeatureCallHookTests
{
    [Fact]
    public async Task RecordingHook_SeesEveryChunk_WithTheValuesPerKey()
    {
        var connector = ScriptedConnector.Values((key, _) => key == 2 ? null : key * 1.5);
        var rig = new FeatureTestRig().Source("item_store", connector, maxBatch: 2);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        var hook = new ListHook();
        var request = rig.NewRequest(new long[] { 1, 2, 3 }, hook: hook);

        await request.EnsureAsync(score);

        var calls = hook.Recorded.OrderBy(c => c.Sequence).ToArray();
        Assert.Equal(new[] { 0, 1 }, calls.Select(c => c.Sequence));
        Assert.All(calls, c => Assert.Equal(("item_store", true, IoOutcome.Ok, FetchOutcome.Ok), (c.Source, c.Sent, c.Outcome, c.Status.Outcome)));
        Assert.Equal(new long[] { 1, 2, 3 }, calls.SelectMany(c => c.Keys));
        Assert.Equal(new double?[] { 1.5, null, 4.5 }, calls.SelectMany(c => c.Values!)); // missing stays missing
        Assert.Equal(1.5, request.Value(score, 0).AsFloat64());
    }

    [Fact]
    public async Task RecordingHook_RecordsATimeout_WithoutReadingTheStaging()
    {
        var release = new TaskCompletionSource();
        var connector = new ScriptedConnector(async (keys, fields, writer, _) =>
        {
            await release.Task; // ignores cancellation, writes late
            ScriptedConnector.WriteAll(keys, fields, writer, (_, _) => 5.0);
            return FetchStatus.Ok;
        });
        var rig = new FeatureTestRig().Source("item_store", connector, timeoutMs: 50);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        var hook = new ListHook();
        var request = rig.NewRequest(new long[] { 1, 2 }, hook: hook);

        var result = await request.EnsureAsync(score);
        release.SetResult();
        await request.Fetcher.WhenIoCompleteAsync();

        Assert.True(result.Failed);
        var call = Assert.Single(hook.Recorded);
        Assert.Equal(IoOutcome.Timeout, call.Outcome);
        Assert.Equal(FetchOutcome.Failed, call.Status.Outcome);
        Assert.Null(call.Values); // the late write is never recorded
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAbandonedCall_HoldsTheRequestMemoryOnce_AndIsRecordedFromLiveBuffers(bool record)
    {
        // A prefetch nobody awaits is still running when the request releases its memory: one external operation is
        // abandoned, recorded or not. The recording reads the keys and staging after the request has let go of them.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new ScriptedConnector(async (keys, fields, writer, _) =>
        {
            await release.Task; // ignores cancellation
            ScriptedConnector.WriteAll(keys, fields, writer, (key, _) => key * 2.0);
            return FetchStatus.Ok;
        });
        var rig = new FeatureTestRig().Source("item_store", connector, timeoutMs: 600_000);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        var hook = record ? new ListHook() : null;
        var request = rig.NewRequest(new long[] { 1, 2 }, pool: new PoisoningPool(), deadlineMs: 600_000, hook: hook);

        request.Fetcher.Prefetch(new[] { score }, CancellationToken.None);
        var abandoned = request.Memory.Release(); // the request returns without awaiting the prefetch

        Assert.Equal(1, abandoned);
        release.SetResult();
        if (hook is not null)
        {
            var call = await hook.FirstRecorded.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(new long[] { 1, 2 }, call.Keys);
            Assert.Equal(new double?[] { 2.0, 4.0 }, call.Values);
        }

        await request.Fetcher.WhenIoCompleteAsync();
    }

    [Fact]
    public async Task ReplayingHook_AnswersCalls_AndTheConnectorIsNeverCalled()
    {
        var connector = ScriptedConnector.Values((_, _) => throw new InvalidOperationException("the connector must not be called"));
        var rig = new FeatureTestRig().Source("item_store", connector, timeoutMs: 1);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        var hook = new ListHook(replays: true);
        var request = rig.NewRequest(new long[] { 4, 5 }, hook: hook);

        var pending = request.EnsureAsync(score);

        Assert.True(pending.IsCompletedSuccessfully); // nothing to wait for, not even the 1 ms source timeout
        Assert.False((await pending).Failed);
        Assert.Empty(connector.Calls);
        Assert.Empty(hook.Recorded);
        Assert.Equal(40.0, request.Value(score, 0).AsFloat64());
        Assert.Equal(50.0, request.Value(score, 1).AsFloat64());
    }

    [Fact]
    public async Task AHookThatThrowsWhileRecording_SeesTheCallOnce()
    {
        // The call is completed once, after the wait decided it: a completion that throws (here the hook) is not run again
        // from the failure path, which would record a successful call a second time as an error (review 14 follow-up).
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new ScriptedConnector(async (keys, fields, writer, _) =>
        {
            await gate.Task; // pending when the fetch installs its wait: the awaited branch decides this call
            ScriptedConnector.WriteAll(keys, fields, writer, (key, _) => key * 1.5);
            return FetchStatus.Ok;
        });
        var rig = new FeatureTestRig().Source("item_store", connector);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        var hook = new ThrowingHook();
        var request = rig.NewRequest(new long[] { 1, 2 }, hook: hook);

        try
        {
            var pending = request.EnsureAsync(score); // returns once the one chunk is sent and its wait installed
            gate.SetResult();
            await pending;
        }
        catch (InvalidOperationException ex) when (ex.Message == "recorder failed")
        {
            // A failing recorder is a defect; it may surface. What matters is that the call is not completed twice.
        }

        await request.Fetcher.WhenIoCompleteAsync();
        var call = Assert.Single(hook.Recorded);
        Assert.Equal((IoOutcome.Ok, FetchOutcome.Ok), (call.Outcome, call.Status.Outcome));
    }

    [Fact]
    public async Task AFailureWhoseDescriptionThrows_StillReturnsTheMemory()
    {
        // The wait, the classification of a failure and the completion all end in the call site's one finally: an exception
        // whose Message throws fails the fetch, but never keeps the request memory held (review 15).
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new ScriptedConnector(async (_, _, _, _) =>
        {
            await gate.Task;
            throw new UndescribableException();
        });
        var rig = new FeatureTestRig().Source("item_store", connector);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        var request = rig.NewRequest(new long[] { 1, 2 });

        var pending = request.EnsureAsync(score);
        gate.SetResult();
        try
        {
            await pending;
        }
        catch (InvalidOperationException ex) when (ex.Message == "message formatting failed")
        {
            // The diagnostic failed; it may surface.
        }

        await request.Fetcher.WhenIoCompleteAsync();
        Assert.Equal(0, request.Memory.Release()); // the request's own hold was the last
        Assert.True(request.Memory.IsReturned);
    }

    private sealed class UndescribableException : Exception
    {
        public override string Message => throw new InvalidOperationException("message formatting failed");
    }

    /// <summary>Notes each recorded call, then throws.</summary>
    private sealed class ThrowingHook : IFeatureCallHook
    {
        public bool Replays => false;

        public ConcurrentQueue<FeatureCallInfo> Recorded { get; } = new();

        public int Begin(FetchGroup group, in EntityKeyBatch batch, CompiledFieldBinding[] fields) => 0;

        public void Record(in FeatureCallInfo call, IStagedFeatureValues? values)
        {
            Recorded.Enqueue(call);
            throw new InvalidOperationException("recorder failed");
        }

        public FetchStatus Replay(FetchGroup group, in EntityKeyBatch batch, CompiledFieldBinding[] fields, IFeatureBatchWriter writer) =>
            throw new NotSupportedException();
    }

    /// <summary>Records calls like a bundle recorder would; when replaying, answers 10 × key for every field.</summary>
    internal sealed class ListHook(bool replays = false) : IFeatureCallHook
    {
        private int _sequence;

        public bool Replays => replays;

        public ConcurrentQueue<Call> Recorded { get; } = new();

        public TaskCompletionSource<Call> FirstRecorded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Begin(FetchGroup group, in EntityKeyBatch batch, CompiledFieldBinding[] fields) => Interlocked.Increment(ref _sequence) - 1;

        public void Record(in FeatureCallInfo call, IStagedFeatureValues? values)
        {
            var keys = call.Batch.CandidateKeys.ToArray().Select(k => k.Int64Value).ToArray();
            double?[]? recorded = null;
            if (values is not null)
            {
                recorded = new double?[keys.Length];
                for (var k = 0; k < keys.Length; k++)
                {
                    var value = values.Get(0, k);
                    recorded[k] = value.IsMissing ? null : value.AsFloat64();
                }
            }

            var recordedCall = new Call(call.Sequence, call.Group.SourceName, keys, call.Sent, call.Status, call.Outcome, recorded);
            Recorded.Enqueue(recordedCall);
            FirstRecorded.TrySetResult(recordedCall);
        }

        public FetchStatus Replay(FetchGroup group, in EntityKeyBatch batch, CompiledFieldBinding[] fields, IFeatureBatchWriter writer)
        {
            ScriptedConnector.WriteAll(batch, fields, writer, (key, _) => key * 10.0);
            return FetchStatus.Ok;
        }
    }

    internal sealed record Call(int Sequence, string Source, long[] Keys, bool Sent, FetchStatus Status, IoOutcome Outcome, double?[]? Values);
}
