using System.Diagnostics.Metrics;
using System.Threading.Tasks.Sources;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Features.Cache;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features.Cache;

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
        if (answer == Answer.FailsLater)
        {
            // Only the answer that hands this task to the engine fails it: a failed task nobody waits for is reported
            // when it is collected, and whoever reports it reads the message this exception throws on.
            later.TrySetException(new UnreadableException());
        }

        var result = await reading;

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal((1.0, 2.0), (request.Value(clicks, 0).AsFloat64(), request.Value(clicks, 1).AsFloat64()));
        Assert.Single(connector.Calls);
        await request.Fetcher.WhenIoCompleteAsync();
        request.Memory.Release();
        Assert.True(request.Memory.IsReturned);
        Assert.Equal((1, 1), (calls.Completed, calls.Errors));
    }

    /// <summary>
    /// A store's synchronous part runs on the request's thread and the engine does not bound it; what it answers is
    /// judged when the engine first sees it complete.
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
            Assert.Equal(FeatureState.Failed, result.State);
            Assert.Empty(connector.Calls);
        }
        else
        {
            Assert.Equal(FeatureState.Ok, result.State);
            Assert.Equal((1.0, 2.0), (request.Value(clicks, 0).AsFloat64(), request.Value(clicks, 1).AsFloat64()));
            Assert.Single(connector.Calls);
        }

        rig.WriteBack("item_store");
        var version = rig.Tables["item_store"].Pin();
        var record = version.Find(1L);
        Assert.Equal(pastTheSourceDeadline ? (double?)null : 1.0, record.Found ? version.Read(0, record).AsFloat64() : null);
    }

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
        Assert.Equal(111.0, request.Value(clicks, 0).AsFloat64());
        Assert.Empty(connector.Calls);
        await request.Fetcher.WhenIoCompleteAsync();
        Assert.Equal(0, request.Memory.Release());
    }

    /// <summary>
    /// The tier's time is judged by the cache's clock, held by the test: the first chunk is sent with time left,
    /// whatever the machine does, and its call moves the clock past the tier's time.
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

        var request = rig.NewRequest([1, 2, 3, 4]);
        var result = await request.EnsureAsync(clicks);

        Assert.Equal(1, store.Calls);
        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(new[] { 1.0, 2, 3, 4 }, Enumerable.Range(0, 4).Select(r => request.Value(clicks, r).AsFloat64()));
        Assert.Equal(new long[] { 1, 2, 3, 4 }, connector.Calls.SelectMany(c => c.Keys).Select(k => k.Int64Value).Order());
    }

    /// <summary>
    /// The tier's time is up before the first chunk is sent (the request's thread was preempted, say; here every look
    /// at the clock moves it a second).
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

        var request = rig.NewRequest([1, 2]);
        var fill = request.EnsureAsync(clicks).AsTask();
        await store.SecondSent.WaitAsync(TestContext.Current.CancellationToken);
        rig.Clock.AdvanceMs(50);
        store.Answer();
        var result = await fill;

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(111.0, request.Value(clicks, 0).AsFloat64());
        Assert.Equal(2.0, request.Value(clicks, 1).AsFloat64());
        Assert.Equal(new long[] { 2 }, connector.Calls.SelectMany(c => c.Keys).Select(k => k.Int64Value));
    }

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

        var request = rig.NewRequest([1, 2]);
        var fill = request.EnsureAsync(clicks).AsTask();
        Assert.Equal(2, store.Calls);
        rig.Clock.AdvanceMs(50);
        store.Answer();
        var result = await fill;

        Assert.Equal(connectorFails ? FeatureState.Degraded : FeatureState.Ok, result.State);
        Assert.Equal(connectorFails ? null : 1.0, request.Value(clicks, 0).IsMissing ? (double?)null : request.Value(clicks, 0).AsFloat64());
        Assert.Equal(111.0, request.Value(clicks, 1).AsFloat64());
        Assert.Equal(new long[] { 1 }, connector.Calls.SelectMany(c => c.Keys).Select(k => k.Int64Value));
        await request.Fetcher.WhenIoCompleteAsync();
        request.Memory.Release();
    }

    /// <summary>
    /// A chunk is judged at the cache clock's time when it was seen to answer in time, taken before the call is
    /// completed.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AChunkIsJudgedWhenItAnswered_NotAfterTheListenersOfItsCompletionRan(bool synchronously)
    {
        var answers = new ClockedStore(() => { });
        var waits = new FirstChunkWaits();
        var connector = ScriptedConnector.Values((key, _) => key * 1.0);
        var rig = new FeatureTestRig().CacheSource("listener_cache", connector, ttlMs: 10, distributed: synchronously ? answers : waits);
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "listener_cache");
        rig.Build();
        answers.Prepare(rig.Tables["listener_cache"].Cache!, rig.Clock.NowMs, 111.0);
        waits.Prepare(rig.Tables["listener_cache"].Cache!, rig.Clock.NowMs, 111.0);
        rig.Clock.HoldTimestamps(); // the tier's time never runs out
        using var listener = new MeasurementHook(StrataRecMetrics.Names.IoDuration, () => rig.Clock.AdvanceMs(50),
            (StrataRecTags.IoTarget, "listener_cache"), (StrataRecTags.IoRole, IoRoles.Cache));

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

    [Theory]
    [InlineData(StrataRecMetrics.Names.IoDuration)]
    [InlineData(StrataRecMetrics.Names.CacheKeys)]
    public async Task AListenerThatThrowsWhileACacheReadResolves_LosesItsMeasurement_AndTheReadIsRecorded(string instrument)
    {
        var store = new ClockedStore(() => { });
        var connector = ScriptedConnector.Values((key, _) => key * 1.0);
        var rig = new FeatureTestRig().CacheSource("listener_cache", connector, distributed: store);
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "listener_cache");
        rig.Build();
        store.Prepare(rig.Tables["listener_cache"].Cache!, rig.Clock.NowMs, 111.0);
        using var listener = instrument == StrataRecMetrics.Names.CacheKeys
            ? MeasurementHook.Throwing(instrument, (StrataRecTags.Source, "listener_cache"), (StrataRecTags.CacheTier, "distributed"))
            : MeasurementHook.Throwing(instrument, (StrataRecTags.IoTarget, "listener_cache"), (StrataRecTags.IoRole, IoRoles.Cache));
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

    private sealed class UnreadableException : Exception
    {
        public override string Message => throw new InvalidOperationException("reading the message failed");
    }

    private sealed class ScriptedStore(Func<ValueTask> get) : ICacheStore
    {
        public string StoreType => TestCacheStoreFactory.TypeName;

        public ValueTask GetAsync(CacheKeyspace keyspace, ReadOnlyMemory<string> keys, Memory<ReadOnlyMemory<byte>> records, CancellationToken cancellationToken) =>
            get();

        public ValueTask SetAsync(CacheKeyspace keyspace, ReadOnlyMemory<CacheStoreEntry> entries, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class FailsOnSecondLook : IValueTaskSource
    {
        private int _looks;

        public ValueTaskSourceStatus GetStatus(short token) =>
            Interlocked.Increment(ref _looks) == 1 ? ValueTaskSourceStatus.Pending : ValueTaskSourceStatus.Faulted;

        public void GetResult(short token) => throw new UnreadableException();

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            continuation(state);
    }

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
