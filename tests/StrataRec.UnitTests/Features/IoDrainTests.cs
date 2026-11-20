using System.Collections.Immutable;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Features.Cache;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features;

/// <summary>
/// The request's I/O drain (<see cref="FeatureFetcher.WhenIoCompleteAsync"/>) promises that every operation the request
/// started has really finished, and the request memory is back once the request released it: here, operations that go on
/// after the request stopped reading them (review 78).
/// </summary>
public class IoDrainTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Review 78: a candidate cache read the request stopped reading (another source's call failed the fill first) sends
    /// its origin call once its distributed tier answered, after every call the request had sent completed. The drain
    /// tracked the calls only, so it returned while the origin call was being sent, the memory still held. The read's
    /// resolution, which starts it, is tracked now.
    /// </summary>
    [Fact]
    public async Task TheDrain_WaitsForACacheReadTheRequestStoppedReading_AndTheOriginCallItSendsLater()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FirstReadWaits();
        using var originResume = new ManualResetEventSlim();
        var originEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var originDone = new TaskCompletionSource<FetchStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingDone = new TaskCompletionSource<FetchStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var origin = new ScriptedConnector((_, _, _, _) =>
        {
            originEntered.TrySetResult();
            return originResume.Wait(Timeout) ? new ValueTask<FetchStatus>(originDone.Task) : throw new TimeoutException();
        });
        var sibling = new ScriptedConnector((_, _, _, _) => new ValueTask<FetchStatus>(siblingDone.Task));
        var rig = new FeatureTestRig().Source("sibling", sibling).CacheSource("cache", origin, distributed: store);
        var broken = rig.Physical("item.broken", FeatureTestRig.Item, ColumnType.Float64, "sibling");
        var cached = rig.Physical("item.cached", FeatureTestRig.Item, ColumnType.Float64, "cache");
        rig.Build();
        var request = rig.NewRequest([1], hook: new FailingRecord("sibling"));
        var fill = request.EnsureAsync(broken, cached).AsTask();
        try
        {
            Assert.Equal(1, store.Calls); // the read waits for its distributed tier
            siblingDone.SetResult(FetchStatus.Ok); // recording it throws: the fill ends before it reads the cache read
            await Assert.ThrowsAsync<IOException>(() => fill.WaitAsync(Timeout, ct));
            request.Fetcher.EndRequest();
            request.Memory.Release();

            var drain = request.Fetcher.WhenIoCompleteAsync();
            store.Answer(); // nothing for the key: the read sends it to the origin
            await originEntered.Task.WaitAsync(Timeout, ct);
            Assert.False(drain.IsCompleted); // the origin call is being sent
            originResume.Set();
            Assert.False(drain.IsCompleted);
            Assert.False(request.Memory.IsReturned);

            originDone.SetResult(FetchStatus.Ok);
            await drain.WaitAsync(Timeout, ct);
            Assert.True(request.Memory.IsReturned);
            Assert.Single(origin.Calls);
        }
        finally
        {
            originResume.Set();
            originDone.TrySetResult(FetchStatus.Failed("finished"));
            siblingDone.TrySetResult(FetchStatus.Ok);
            store.Answer();
        }
    }

    /// <summary>
    /// Review 78: the drain tracked a call's connector, not its call site. A prefetch nobody read is recorded where its
    /// connector's answer is observed; the drain returned once the connector completed, while the recording still read the
    /// call's keys and staging and the memory was held. A call is done when both are.
    /// </summary>
    [Fact]
    public async Task TheDrain_WaitsForAnUnreadPrefetch_ToBeRecorded_NotOnlyForItsConnector()
    {
        var ct = TestContext.Current.CancellationToken;
        var answer = new TaskCompletionSource<FetchStatus>(); // continuations inline: the call site runs where it answers
        var connector = new ScriptedConnector((_, _, _, _) => new ValueTask<FetchStatus>(answer.Task));
        var rig = new FeatureTestRig().Source("item_store", connector);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        var hook = new BlockingRecord();
        var request = rig.NewRequest([1, 2], hook: hook);
        request.Fetcher.Prefetch([score], ct); // never read
        request.Fetcher.EndRequest();
        request.Memory.Release();

        var answering = Task.Run(() => answer.SetResult(FetchStatus.Ok), ct);
        try
        {
            await hook.Entered.Task.WaitAsync(Timeout, ct); // the connector completed; its call site is recording it
            var drain = request.Fetcher.WhenIoCompleteAsync();
            Assert.False(drain.IsCompleted);
            Assert.False(request.Memory.IsReturned);

            hook.Resume.Set();
            await drain.WaitAsync(Timeout, ct);
            Assert.True(request.Memory.IsReturned);
        }
        finally
        {
            hook.Resume.Set();
            await answering;
        }
    }

    /// <summary>A store whose first read waits for <see cref="Answer"/>, then has nothing; later reads have nothing at once.</summary>
    private sealed class FirstReadWaits : IFeatureCacheStore
    {
        private readonly TaskCompletionSource _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public string StoreType => TestCacheStoreFactory.Type;

        public int Calls => Volatile.Read(ref _calls);

        public void Answer() => _answer.TrySetResult();

        public ValueTask GetAsync(CacheKeyspace keyspace, ReadOnlyMemory<string> keys, Memory<ReadOnlyMemory<byte>> records, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _calls) == 1 ? new ValueTask(_answer.Task) : ValueTask.CompletedTask;

        public ValueTask SetAsync(CacheKeyspace keyspace, ReadOnlyMemory<CacheStoreEntry> entries, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    /// <summary>Records nothing, and throws when it records a call of <paramref name="source"/>.</summary>
    private sealed class FailingRecord(string source) : IFeatureCallHook
    {
        public bool Replays => false;

        public int Begin(FetchGroup group, in EntityKeyBatch batch, ImmutableArray<CompiledFieldBinding> fields) => 0;

        public void Record(in FeatureCallInfo call, IStagedFeatureValues? values)
        {
            if (call.Group.SourceName == source)
            {
                throw new IOException("recording unavailable");
            }
        }

        public FetchStatus Replay(FetchGroup group, in EntityKeyBatch batch, ImmutableArray<CompiledFieldBinding> fields, IFeatureBatchWriter writer) =>
            throw new NotSupportedException();
    }

    /// <summary>Records nothing; each recording says it began and waits for <see cref="Resume"/>.</summary>
    private sealed class BlockingRecord : IFeatureCallHook
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Resume { get; } = new();

        public bool Replays => false;

        public int Begin(FetchGroup group, in EntityKeyBatch batch, ImmutableArray<CompiledFieldBinding> fields) => 0;

        public void Record(in FeatureCallInfo call, IStagedFeatureValues? values)
        {
            Entered.TrySetResult();
            Resume.Wait(Timeout);
        }

        public FetchStatus Replay(FetchGroup group, in EntityKeyBatch batch, ImmutableArray<CompiledFieldBinding> fields, IFeatureBatchWriter writer) =>
            throw new NotSupportedException();
    }
}
