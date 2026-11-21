using System.Diagnostics.Metrics;
using System.Threading.Tasks.Sources;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Features.Cache;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features.Cache;

/// <summary>
/// A read of the distributed tier is an optimisation (design 6.6 <c>cache</c>): however it fails, its keys go to the
/// connector, the request is not failed, its memory comes back and the call is completed exactly once. Each way the
/// engine can find the read finished is covered: the store throwing before it returns, a completed task, a pending one,
/// and one that completed between the engine's check and its wait.
/// </summary>
[Collection(MetricMeasurement.Name)]
public class CacheStoreCallTests
{
    public enum Answer
    {
        /// <summary><c>GetAsync</c> throws before returning a task.</summary>
        Throws,

        /// <summary><c>GetAsync</c> returns a task that failed already.</summary>
        Failed,

        /// <summary><c>GetAsync</c> returns a pending task, failed after the engine waits for it.</summary>
        FailsLater,

        /// <summary>Pending when the engine first looks, failed when it installs its wait.</summary>
        FailedBeforeTheWait,
    }

    /// <summary>Review 19: an exception whose message cannot be read must not decide what the read does.</summary>
    [Theory]
    [InlineData(Answer.Throws)]
    [InlineData(Answer.Failed)]
    [InlineData(Answer.FailsLater)]
    [InlineData(Answer.FailedBeforeTheWait)]
    public async Task AStoreFailureWhoseMessageThrows_SendsTheKeysToTheConnector(Answer answer)
    {
        var later = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ScriptedStore(() =>
        {
            entered.TrySetResult();
            return answer switch
            {
                Answer.Throws => throw new UnreadableException(),
                Answer.Failed => ValueTask.FromException(new UnreadableException()),
                Answer.FailsLater => new ValueTask(later.Task),
                Answer.FailedBeforeTheWait => new ValueTask(new FailsOnSecondLook(), 0),
                _ => throw new ArgumentOutOfRangeException(nameof(answer)),
            };
        });
        var connector = ScriptedConnector.Values((key, _) => key * 1.0);
        var rig = new FeatureTestRig().CacheSource("item_store", connector, distributed: store);
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        using var calls = new StoreCalls();

        var request = rig.NewRequest([1, 2]);
        var reading = request.EnsureAsync(clicks).AsTask();
        await entered.Task;
        later.TrySetException(new UnreadableException());
        var result = await reading;

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal((1.0, 2.0), (request.Value(clicks, 0).AsFloat64(), request.Value(clicks, 1).AsFloat64()));
        Assert.Single(connector.Calls);
        await request.Fetcher.WhenIoCompleteAsync();
        request.Memory.Release();
        Assert.True(request.Memory.IsReturned);
        Assert.Equal((1, 1), (calls.Completed, calls.Errors)); // completed once, as an error
    }

    /// <summary>
    /// Review 19: a store's synchronous part runs on the request's thread and the engine does not bound it; what it
    /// answers is judged when the engine first sees it complete. Here the store blocks before returning, past the tier's
    /// timeout (the source has time left: the connector answers) or past the source's deadline (no time left: the keys
    /// fail). Either way its fresh records (111) are never used.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStoreThatAnswersSynchronously_AfterItsTime_IsLate_AndItsRecordsAreNotUsed(bool pastTheSourceDeadline)
    {
        var store = new SlowStore(blockMs: 40);
        var connector = ScriptedConnector.Values((key, _) => key * 1.0);
        var rig = new FeatureTestRig().CacheSource("item_store", connector, distributed: store, distributedTimeoutMs: 20,
            timeoutMs: pastTheSourceDeadline ? 20 : FeatureTestRig.UntimedMs);
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        store.Prepare(rig.Tables["item_store"].Cache!, rig.Clock.NowMs, 111.0);

        var request = rig.NewRequest([1, 2]);
        var result = await request.EnsureAsync(clicks);

        if (pastTheSourceDeadline)
        {
            Assert.Equal(FeatureState.Failed, result.State); // no key got a value: not the store's, and no time for the connector
            Assert.Empty(connector.Calls);
        }
        else
        {
            Assert.Equal(FeatureState.Ok, result.State);
            Assert.Equal((1.0, 2.0), (request.Value(clicks, 0).AsFloat64(), request.Value(clicks, 1).AsFloat64()));
            Assert.Single(connector.Calls);
        }

        rig.WriteBack("item_store"); // nor written back: the table has the connector's value or nothing
        var version = rig.Tables["item_store"].Pin();
        var record = version.Find(1L);
        Assert.Equal(pastTheSourceDeadline ? (double?)null : 1.0, record.Found ? version.Read(0, record).AsFloat64() : null);
    }

    /// <summary>
    /// Review 85: a store that answers synchronously is seen answering before the flow gets its activity back. The host's
    /// <see cref="System.Diagnostics.Activity.CurrentChanged"/> handlers ran first (in <see cref="IoCall.Sent"/>), and one
    /// that took time (here it moves the cache's clock past the TTL) made a record fresh when the store answered expired,
    /// which went to the connector (the same as review 80's metric listener).
    /// </summary>
    [Fact]
    public async Task AStoreThatAnswersAtOnce_IsSeenAnswering_BeforeTheHostsCurrentChangedHandlersRun()
    {
        var target = "changed_" + Guid.NewGuid().ToString("N");
        var store = new ClockedStore(() => { });
        var connector = ScriptedConnector.Values((key, _) => key * 1.0);
        var rig = new FeatureTestRig().CacheSource(target, connector, ttlMs: 10, distributed: store);
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, target);
        rig.Build();
        store.Prepare(rig.Tables[target].Cache!, rig.Clock.NowMs, 111.0);
        rig.Clock.HoldTimestamps();
        using var flow = new TracedFlow();
        var changes = 0;
        void Changed(object? sender, System.Diagnostics.ActivityChangedEventArgs args)
        {
            if (ReferenceEquals(args.Current, flow.Request) && args.Previous?.OperationName == "cache " + target)
            {
                changes++;
                rig.Clock.AdvanceMs(50);
            }
        }

        var request = rig.NewRequest([1]);
        System.Diagnostics.Activity.CurrentChanged += Changed;
        try
        {
            Assert.Equal(FeatureState.Ok, (await request.EnsureAsync(clicks)).State);
        }
        finally
        {
            System.Diagnostics.Activity.CurrentChanged -= Changed;
        }

        Assert.Equal(1, changes);
        Assert.Equal(111.0, request.Value(clicks, 0).AsFloat64()); // the store's, fresh when it answered
        Assert.Empty(connector.Calls);
        await request.Fetcher.WhenIoCompleteAsync();
        Assert.Equal(0, request.Memory.Release());
    }

    /// <summary>
    /// Review 19: once the tier's time is up (here the first chunk's synchronous part took it), no further chunk is sent.
    /// The tier's time is judged by the cache's clock, held by the test (review 21 #2): the first chunk is sent with time
    /// left, whatever the machine does, and its call moves the clock past the tier's time.
    /// </summary>
    [Fact]
    public async Task OnceTheTiersTimeIsUp_NoFurtherChunkIsSent_AndTheConnectorAnswersEveryKey()
    {
        ManualClock? clock = null;
        var store = new ClockedStore(() => clock!.AdvanceTimestamps(TimeSpan.FromSeconds(1)));
        var connector = ScriptedConnector.Values((key, _) => key * 1.0);
        var rig = new FeatureTestRig().CacheSource("item_store", connector, distributed: store, distributedTimeoutMs: 20, maxBatch: 2);
        clock = rig.Clock;
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        store.Prepare(rig.Tables["item_store"].Cache!, rig.Clock.NowMs, 111.0);
        rig.Clock.HoldTimestamps();

        var request = rig.NewRequest([1, 2, 3, 4]); // two chunks
        var result = await request.EnsureAsync(clicks);

        Assert.Equal(1, store.Calls); // the first chunk, sent with time left; not the second
        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(new[] { 1.0, 2, 3, 4 }, Enumerable.Range(0, 4).Select(r => request.Value(clicks, r).AsFloat64())); // not 111
        Assert.Equal(new long[] { 1, 2, 3, 4 }, connector.Calls.SelectMany(c => c.Keys).Select(k => k.Int64Value).Order());
    }

    /// <summary>
    /// Review 21 #2: the tier's time is up before the first chunk is sent (the request's thread was preempted, say; here
    /// every look at the clock moves it a second). No chunk is sent, and the connector answers every key.
    /// </summary>
    [Fact]
    public async Task TheTiersTimeUpBeforeTheFirstChunk_SendsNone_AndTheConnectorAnswersEveryKey()
    {
        var store = new ClockedStore(() => { });
        var connector = ScriptedConnector.Values((key, _) => key * 1.0);
        var rig = new FeatureTestRig().CacheSource("item_store", connector, distributed: store, distributedTimeoutMs: 20, maxBatch: 2);
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        store.Prepare(rig.Tables["item_store"].Cache!, rig.Clock.NowMs, 111.0);
        rig.Clock.HoldTimestamps(stepPerRead: TimeSpan.FromSeconds(1));

        var request = rig.NewRequest([1, 2, 3, 4]);
        var result = await request.EnsureAsync(clicks);

        Assert.Equal(0, store.Calls);
        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(new[] { 1.0, 2, 3, 4 }, Enumerable.Range(0, 4).Select(r => request.Value(clicks, r).AsFloat64()));
        Assert.Equal(new long[] { 1, 2, 3, 4 }, connector.Calls.SelectMany(c => c.Keys).Select(k => k.Int64Value).Order());
    }

    /// <summary>
    /// Pre-release assessment: each chunk of the tier's read is judged when it answered. The first chunk answers at once
    /// and is fresh; the second answers after the records' TTL passed (50 ms against 10 ms) and goes to the connector. The
    /// time the first chunk was judged at made the second's record a fresh hit.
    /// </summary>
    [Fact]
    public async Task AChunkThatAnswersAfterItsRecordsExpired_SendsItsKeysToTheConnector_WhileAnEarlierChunkStaysAHit()
    {
        var store = new SecondChunkWaits();
        var connector = ScriptedConnector.Values((key, _) => key * 1.0);
        var rig = new FeatureTestRig().CacheSource("item_store", connector, ttlMs: 10, distributed: store, maxBatch: 1);
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        store.Prepare(rig.Tables["item_store"].Cache!, rig.Clock.NowMs, 111.0);
        rig.Clock.HoldTimestamps(); // the tier's time never runs out

        var request = rig.NewRequest([1, 2]); // two chunks
        var fill = request.EnsureAsync(clicks).AsTask();
        await store.SecondSent.WaitAsync(TestContext.Current.CancellationToken);
        rig.Clock.AdvanceMs(50);
        store.Answer();
        var result = await fill;

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(111.0, request.Value(clicks, 0).AsFloat64()); // the first chunk's hit
        Assert.Equal(2.0, request.Value(clicks, 1).AsFloat64()); // not 111: expired when it arrived
        Assert.Equal(new long[] { 2 }, connector.Calls.SelectMany(c => c.Keys).Select(k => k.Int64Value));
    }

    /// <summary>
    /// Review 78: the reverse. The second chunk answers at once and is fresh; the first answers after the records' TTL
    /// passed and goes to the connector. The chunks were judged as the read reached them, in index order, so the second,
    /// waiting behind the first, was judged at the first's time: expired, and sent to the connector too (with a failing
    /// connector, the feature failed though a fresh value existed).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AChunkThatAnsweredInTime_StaysAHit_WhileAnEarlierChunkAnswersAfterItsRecordsExpired(bool connectorFails)
    {
        var store = new FirstChunkWaits();
        var connector = connectorFails
            ? new ScriptedConnector((_, _, _, _) => ValueTask.FromResult(FetchStatus.Failed("origin unavailable")))
            : ScriptedConnector.Values((key, _) => key * 1.0);
        var rig = new FeatureTestRig().CacheSource("item_store", connector, ttlMs: 10, distributed: store, maxBatch: 1);
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        store.Prepare(rig.Tables["item_store"].Cache!, rig.Clock.NowMs, 111.0);
        rig.Clock.HoldTimestamps(); // the tier's time never runs out

        var request = rig.NewRequest([1, 2]); // two chunks
        var fill = request.EnsureAsync(clicks).AsTask();
        Assert.Equal(2, store.Calls); // both sent; the second answered
        rig.Clock.AdvanceMs(50);
        store.Answer();
        var result = await fill;

        Assert.Equal(connectorFails ? FeatureState.Degraded : FeatureState.Ok, result.State);
        Assert.Equal(connectorFails ? null : 1.0, request.Value(clicks, 0).IsMissing ? (double?)null : request.Value(clicks, 0).AsFloat64());
        Assert.Equal(111.0, request.Value(clicks, 1).AsFloat64()); // the second chunk's hit
        Assert.Equal(new long[] { 1 }, connector.Calls.SelectMany(c => c.Keys).Select(k => k.Int64Value));
        await request.Fetcher.WhenIoCompleteAsync();
        request.Memory.Release();
    }

    /// <summary>
    /// Review 80: a chunk is judged at the cache clock's time when it was seen to answer in time, taken before the call is
    /// completed. A host's metric listener runs at the completion; one that took time (here it moves the clock 50 ms, past
    /// the record's 10 ms TTL) made the record, fresh when the store answered, look expired, and its key went to the
    /// connector (the value 1, not 111). Both ways a chunk is found answered: at once, and after a wait.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AChunkIsJudgedWhenItAnswered_NotAfterTheListenersOfItsCompletionRan(bool synchronously)
    {
        var answers = new ClockedStore(() => { });
        var waits = new FirstChunkWaits();
        var connector = ScriptedConnector.Values((key, _) => key * 1.0);
        var rig = new FeatureTestRig().CacheSource("review80_cache", connector, ttlMs: 10, distributed: synchronously ? answers : waits);
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "review80_cache");
        rig.Build();
        answers.Prepare(rig.Tables["review80_cache"].Cache!, rig.Clock.NowMs, 111.0);
        waits.Prepare(rig.Tables["review80_cache"].Cache!, rig.Clock.NowMs, 111.0);
        rig.Clock.HoldTimestamps(); // the tier's time never runs out
        using var listener = new MeasurementHook(StrataRecMetrics.Names.IoDuration, () => rig.Clock.AdvanceMs(50),
            (StrataRecTags.IoTarget, "review80_cache"), (StrataRecTags.IoRole, IoRoles.Cache));

        var request = rig.NewRequest([1]);
        var fill = request.EnsureAsync(clicks).AsTask();
        waits.Answer();
        var result = await fill;

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(111.0, request.Value(clicks, 0).AsFloat64());
        Assert.Empty(connector.Calls);
        Assert.Equal(1, listener.Calls);
        await request.Fetcher.WhenIoCompleteAsync();
        request.Memory.Release();
    }

    /// <summary>
    /// Review 80: a host's metric listener that throws while a cache read resolves, where its store call completes or where
    /// it counts the distributed tier's keys, loses its measurement. The read serves the store's record and is recorded as
    /// it ended; the throw faulted the read's resolution, so the read was never recorded (outstanding in a recording) and
    /// its fill threw the listener's exception.
    /// </summary>
    [Theory]
    [InlineData(StrataRecMetrics.Names.IoDuration)]
    [InlineData(StrataRecMetrics.Names.CacheKeys)]
    public async Task AListenerThatThrowsWhileACacheReadResolves_LosesItsMeasurement_AndTheReadIsRecorded(string instrument)
    {
        var store = new ClockedStore(() => { });
        var connector = ScriptedConnector.Values((key, _) => key * 1.0);
        var rig = new FeatureTestRig().CacheSource("review80_cache", connector, distributed: store);
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "review80_cache");
        rig.Build();
        store.Prepare(rig.Tables["review80_cache"].Cache!, rig.Clock.NowMs, 111.0);
        using var listener = instrument == StrataRecMetrics.Names.CacheKeys
            ? MeasurementHook.Throwing(instrument, (StrataRecTags.Source, "review80_cache"), (StrataRecTags.CacheTier, "distributed"))
            : MeasurementHook.Throwing(instrument, (StrataRecTags.IoTarget, "review80_cache"), (StrataRecTags.IoRole, IoRoles.Cache));
        var hook = new FeatureCallHookTests.ListHook();
        var request = rig.NewRequest([1], hook: hook);

        var result = await request.EnsureAsync(clicks);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(111.0, request.Value(clicks, 0).AsFloat64());
        Assert.Empty(connector.Calls);
        var read = Assert.Single(hook.Recorded);
        Assert.Equal(IoOutcome.Ok, read.Outcome);
        Assert.Equal(new double?[] { 111.0 }, read.Values);
        Assert.Equal(1, listener.Calls);
        await request.Fetcher.WhenIoCompleteAsync();
        Assert.Equal(0, request.Memory.Release());
    }

    /// <summary>
    /// A store whose first read waits for <see cref="Answer"/> and whose later reads answer at once; each answers a record
    /// written at the time <see cref="Prepare"/> was given.
    /// </summary>
    private sealed class FirstChunkWaits : ICacheStore
    {
        private readonly TaskCompletionSource _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ReadOnlyMemory<byte> _record;
        private int _calls;

        public string StoreType => TestCacheStoreFactory.TypeName;

        public int Calls => Volatile.Read(ref _calls);

        public void Prepare(LocalTableCache cache, long writtenMs, double value)
        {
            var writer = new CacheRecordWriter(cache.Layout, 64);
            writer.Begin(writtenMs, cache.Fingerprint, absent: false);
            writer.Float64(0, value);
            var (offset, length) = writer.End();
            _record = writer.Bytes.AsMemory(offset, length);
        }

        public void Answer() => _answer.TrySetResult();

        public ValueTask GetAsync(CacheKeyspace keyspace, ReadOnlyMemory<string> keys, Memory<ReadOnlyMemory<byte>> records, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                return new ValueTask(AnswerLaterAsync(records));
            }

            records.Span.Fill(_record);
            return ValueTask.CompletedTask;
        }

        public ValueTask SetAsync(CacheKeyspace keyspace, ReadOnlyMemory<CacheStoreEntry> entries, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        private async Task AnswerLaterAsync(Memory<ReadOnlyMemory<byte>> records)
        {
            await _answer.Task;
            records.Span.Fill(_record);
        }
    }

    /// <summary>
    /// A store whose first read answers at once and whose later reads wait for <see cref="Answer"/>; each answers a record
    /// written at the time <see cref="Prepare"/> was given.
    /// </summary>
    private sealed class SecondChunkWaits : ICacheStore
    {
        private readonly TaskCompletionSource _sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ReadOnlyMemory<byte> _record;
        private int _calls;

        public string StoreType => TestCacheStoreFactory.TypeName;

        public Task SecondSent => _sent.Task;

        public void Prepare(LocalTableCache cache, long writtenMs, double value)
        {
            var writer = new CacheRecordWriter(cache.Layout, 64);
            writer.Begin(writtenMs, cache.Fingerprint, absent: false);
            writer.Float64(0, value);
            var (offset, length) = writer.End();
            _record = writer.Bytes.AsMemory(offset, length);
        }

        public void Answer() => _answer.TrySetResult();

        public ValueTask GetAsync(CacheKeyspace keyspace, ReadOnlyMemory<string> keys, Memory<ReadOnlyMemory<byte>> records, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                records.Span.Fill(_record);
                return ValueTask.CompletedTask;
            }

            _sent.TrySetResult();
            return new ValueTask(AnswerLaterAsync(records));
        }

        public ValueTask SetAsync(CacheKeyspace keyspace, ReadOnlyMemory<CacheStoreEntry> entries, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        private async Task AnswerLaterAsync(Memory<ReadOnlyMemory<byte>> records)
        {
            await _answer.Task;
            records.Span.Fill(_record);
        }
    }

    /// <summary>
    /// A store that runs <paramref name="whileSending"/> in its synchronous part (where the test moves the clock), then
    /// answers a fresh record for every key.
    /// </summary>
    private sealed class ClockedStore(Action whileSending) : ICacheStore
    {
        private ReadOnlyMemory<byte> _record;
        private int _calls;

        public string StoreType => TestCacheStoreFactory.TypeName;

        public int Calls => Volatile.Read(ref _calls);

        public void Prepare(LocalTableCache cache, long writtenMs, double value)
        {
            var writer = new CacheRecordWriter(cache.Layout, 64);
            writer.Begin(writtenMs, cache.Fingerprint, absent: false);
            writer.Float64(0, value);
            var (offset, length) = writer.End();
            _record = writer.Bytes.AsMemory(offset, length);
        }

        public ValueTask GetAsync(CacheKeyspace keyspace, ReadOnlyMemory<string> keys, Memory<ReadOnlyMemory<byte>> records, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            whileSending();
            records.Span.Fill(_record);
            return ValueTask.CompletedTask;
        }

        public ValueTask SetAsync(CacheKeyspace keyspace, ReadOnlyMemory<CacheStoreEntry> entries, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    /// <summary>
    /// A store that blocks for <c>blockMs</c> before it returns (the synchronous part a store should not have), then answers
    /// a fresh record for every key.
    /// </summary>
    private sealed class SlowStore(int blockMs) : ICacheStore
    {
        private ReadOnlyMemory<byte> _record;
        private int _calls;

        public string StoreType => TestCacheStoreFactory.TypeName;

        public int Calls => Volatile.Read(ref _calls);

        public void Prepare(LocalTableCache cache, long writtenMs, double value)
        {
            var writer = new CacheRecordWriter(cache.Layout, 64);
            writer.Begin(writtenMs, cache.Fingerprint, absent: false);
            writer.Float64(0, value);
            var (offset, length) = writer.End();
            _record = writer.Bytes.AsMemory(offset, length);
        }

        public ValueTask GetAsync(CacheKeyspace keyspace, ReadOnlyMemory<string> keys, Memory<ReadOnlyMemory<byte>> records, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var until = System.Diagnostics.Stopwatch.GetTimestamp() + (blockMs * System.Diagnostics.Stopwatch.Frequency / 1000);
            while (System.Diagnostics.Stopwatch.GetTimestamp() < until)
            {
                Thread.Sleep(1);
            }

            records.Span.Fill(_record);
            return ValueTask.CompletedTask;
        }

        public ValueTask SetAsync(CacheKeyspace keyspace, ReadOnlyMemory<CacheStoreEntry> entries, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    /// <summary>An exception whose <see cref="Exception.Message"/> getter throws (review 19).</summary>
    private sealed class UnreadableException : Exception
    {
        public override string Message => throw new InvalidOperationException("reading the message failed");
    }

    /// <summary>A store whose every read is <paramref name="get"/>.</summary>
    private sealed class ScriptedStore(Func<ValueTask> get) : ICacheStore
    {
        public string StoreType => TestCacheStoreFactory.TypeName;

        public ValueTask GetAsync(CacheKeyspace keyspace, ReadOnlyMemory<string> keys, Memory<ReadOnlyMemory<byte>> records, CancellationToken cancellationToken) =>
            get();

        public ValueTask SetAsync(CacheKeyspace keyspace, ReadOnlyMemory<CacheStoreEntry> entries, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    /// <summary>Pending at the first look (<see cref="ValueTask.IsCompleted"/>), failed from the second on.</summary>
    private sealed class FailsOnSecondLook : IValueTaskSource
    {
        private int _looks;

        public ValueTaskSourceStatus GetStatus(short token) =>
            Interlocked.Increment(ref _looks) == 1 ? ValueTaskSourceStatus.Pending : ValueTaskSourceStatus.Faulted;

        public void GetResult(short token) => throw new UnreadableException();

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            continuation(state);
    }

    /// <summary>Completed reads of distributed tiers (<c>stratarec.source.kind</c> = <c>feature_cache</c>), and those that failed.</summary>
    private sealed class StoreCalls : IDisposable
    {
        private readonly MeterListener _listener = new();
        private int _completed;
        private int _errors;

        public StoreCalls()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == StrataRecTelemetry.Name
                    && instrument.Name is StrataRecMetrics.Names.IoDuration or StrataRecMetrics.Names.IoErrors)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => Count(instrument, tags));
            _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => Count(instrument, tags));
            _listener.Start();
        }

        public int Completed => Volatile.Read(ref _completed);

        public int Errors => Volatile.Read(ref _errors);

        public void Dispose() => _listener.Dispose();

        private void Count(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (var tag in tags)
            {
                if (tag.Key == StrataRecTags.IoRole && Equals(tag.Value, IoRoles.Cache))
                {
                    Interlocked.Increment(ref instrument.Name == StrataRecMetrics.Names.IoDuration ? ref _completed : ref _errors);
                }
            }
        }
    }
}
