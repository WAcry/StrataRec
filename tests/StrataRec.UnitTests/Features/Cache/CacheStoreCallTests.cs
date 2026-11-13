using System.Diagnostics.Metrics;
using System.Threading.Tasks.Sources;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Features.Cache;
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

    /// <summary>An exception whose <see cref="Exception.Message"/> getter throws (review 19).</summary>
    private sealed class UnreadableException : Exception
    {
        public override string Message => throw new InvalidOperationException("reading the message failed");
    }

    /// <summary>A store whose every read is <paramref name="get"/>.</summary>
    private sealed class ScriptedStore(Func<ValueTask> get) : IFeatureCacheStore
    {
        public string StoreType => TestCacheStoreFactory.Type;

        public ValueTask GetAsync(CacheKeyspace keyspace, ReadOnlyMemory<EntityKey> keys, Memory<ReadOnlyMemory<byte>> records, CancellationToken cancellationToken) =>
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
                    && instrument.Name is StrataRecMetrics.Names.SourceDuration or StrataRecMetrics.Names.SourceErrors)
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
                if (tag.Key == StrataRecTags.SourceKind && Equals(tag.Value, "feature_cache"))
                {
                    Interlocked.Increment(ref instrument.Name == StrataRecMetrics.Names.SourceDuration ? ref _completed : ref _errors);
                }
            }
        }
    }
}
