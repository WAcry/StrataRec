using System.Diagnostics.Metrics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Features.Cache;
using StrataRec.Features.Local;

namespace StrataRec.Benchmarks;

internal abstract class ComparisonStore(CacheComparisonData data) : IDisposable
{
    protected CacheComparisonData Data { get; } = data;
    public long Attempts { get; protected set; }
    public long QueuePeak { get; protected set; }
    public virtual void Flush() { }
    protected long AdmissionCount;
    public long Admitted => Volatile.Read(ref AdmissionCount);
    public abstract int Count { get; }
    public abstract void Write(ReadOnlySpan<long> keys);
    public abstract int Read(ReadOnlySpan<long> keys, long[] misses, int[] rows, float[] output);
    public abstract void Dispose();
}

internal sealed class ComparisonLocal : ComparisonStore
{
    private readonly LocalTableCache _cache;
    private readonly LocalRecordRef[] _records = new LocalRecordRef[CacheComparison.Batch];
    private readonly MeterListener _listener = new();
    private CancellationTokenSource? _stop;
    private Task? _worker;
    private readonly ManualResetEventSlim _drained = new();
    private long _accepted, _waitFor = long.MaxValue;
    internal LocalFeatureTable Table { get; }

    public ComparisonLocal(CacheComparisonData data, long limit) : base(data)
    {
        Table = data.MakeTable(limit);
        _cache = Table.Cache!;
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument == StrataRecMetrics.CacheWrites) listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == StrataRecTags.Outcome && Equals(tag.Value, "written"))
                {
                    if (Interlocked.Add(ref AdmissionCount, value) >= Volatile.Read(ref _waitFor)) _drained.Set();
                }
            }
        });
        _listener.Start();
    }

    public override int Count => Table.Pin().RecordCount;

    public override void Write(ReadOnlySpan<long> keys)
    {
        Attempts += keys.Length;
        var entities = new EntityKey[keys.Length];
        var records = new ReadOnlyMemory<byte>[keys.Length];
        var now = _cache.NowMs();
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            var bytes = Data.Records[key];
            if (_worker is null) CacheRecord.WriteHeader(bytes.Span, 1, now, Data.Fingerprint, false);
            entities[i] = EntityKey.FromInt64(key);
            records[i] = bytes;
        }

        var batch = new CacheWriteBatch(entities, records, keys.Length);
        if (_cache.TryWriteBack(batch))
        {
            if (_worker is null) _cache.WriteQueued(Table.Writer);
            else
            {
                _accepted += keys.Length;
                QueuePeak = Math.Max(QueuePeak, _cache.QueuedBytes);
            }
        }
    }

    public override int Read(ReadOnlySpan<long> keys, long[] misses, int[] rows, float[] output)
    {
        var version = Table.Pin();
        version.Lookup(keys, _records);
        long now = _cache.NowMs(), freshSince = now - _cache.MinLifetimeMs;
        var n = 0;
        for (var i = 0; i < keys.Length; i++)
        {
            ref readonly var record = ref _records[i];
            if (record.Found)
            {
                var written = version.VersionOf(record);
                if (written >= freshSince || _cache.Classify(written, EntityKey.FromInt64(keys[i]), now) == CacheFreshness.Fresh)
                {
                    record.MarkCacheHit();
                    if (Data.Width == 1) output[i] = (float)version.Read(0, record).AsFloat64();
                    else version.List<float>(0, record).CopyTo(output.AsSpan(i * Data.Width, Data.Width));
                    continue;
                }
            }

            rows[n] = i;
            misses[n++] = keys[i];
        }

        return n;
    }

    public void StartWriter()
    {
        // Each key is submitted once in this burst. Freeze the shared fixture before handing it to another thread.
        var now = _cache.NowMs();
        foreach (var bytes in Data.Records) CacheRecord.WriteHeader(bytes.Span, 1, now, Data.Fingerprint, false);
        _stop = new CancellationTokenSource();
        _worker = _cache.RunAsync(Table.Writer, _stop.Token);
    }

    public override void Flush()
    {
        if (_worker is not { } worker) return;
        Interlocked.Exchange(ref _waitFor, _accepted);
        if (Admitted < _accepted && !_drained.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException($"The cache writer admitted {Admitted} of {_accepted} queued records.");
        }

        _stop!.Cancel();
        try { worker.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        _worker = null;
        _stop.Dispose();
    }

    public override void Dispose()
    {
        Flush();
        _listener.Dispose();
        _drained.Dispose();
    }
}

internal sealed class ComparisonMemory : ComparisonStore
{
    private readonly MemoryCache _cache;
    private readonly CompactionNotice _notice;
    private readonly bool _retry;
    private readonly long[] _pending = new long[CacheComparison.Batch];

    // IMemoryCache.Set returns the supplied value on a capacity rejection too. Inspecting the disposed entry's
    // outcome on .NET 10 avoids a second lookup changing recency or an eviction callback allocating per entry.
    private static readonly Func<ICacheEntry, EvictionReason> Reason = ReadReason();

    public ComparisonMemory(CacheComparisonData data, int limit, bool retry, double compact) : base(data)
    {
        var options = new MemoryCacheOptions { SizeLimit = limit, CompactionPercentage = compact };
        _retry = retry;
        _notice = new CompactionNotice(retry);
        _cache = new MemoryCache(options, _notice);
    }

    public override int Count => _cache.Count;

    public override void Write(ReadOnlySpan<long> keys)
    {
        var now = DateTimeOffset.UtcNow;
        do
        {
            var n = 0;
            Attempts += keys.Length;
            foreach (long key in keys)
            {
                var entry = _cache.CreateEntry(Data.BoxedKeys[key]);
                entry.Size = 1;
                entry.Value = Data.Values[key];
                entry.AbsoluteExpiration = now.AddMilliseconds(Data.Lifetime[key]);
                entry.Dispose();
                if (Reason(entry) == EvictionReason.None) AdmissionCount++;
                else if (_retry) _pending[n++] = key;
            }

            if (n == 0) return;
            // Retry the same rejected keys only after the automatically scheduled worker has finished.
            // This measures a caller requiring every admission, including its waiting and retries.
            Flush();
            keys = _pending.AsSpan(0, n);
        }
        while (true);
    }

    public override int Read(ReadOnlySpan<long> keys, long[] misses, int[] rows, float[] output)
    {
        int n = 0;
        for (var i = 0; i < keys.Length; i++)
        {
            if (_cache.TryGetValue(Data.BoxedKeys[keys[i]], out object? value))
            {
                CacheComparisonData.CopyValue(value!, output.AsSpan(i * Data.Width, Data.Width));
            }
            else
            {
                rows[n] = i;
                misses[n++] = keys[i];
            }
        }

        return n;
    }

    // This .NET 10 field covers the actual interval from scheduling to completion for this cache.
    // The final passive poll is outside the foreground and per-batch timings.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "lockFlag")]
    private static extern ref int CompactionPending(MemoryCache cache);

    public override void Flush()
    {
        if (Volatile.Read(ref CompactionPending(_cache)) == 0) return;
        _notice.Observe = true;
        // A preceding retry can have consumed this notification, or the worker may already have logged
        // completion when observation starts. The instance flag decides when to proceed in both cases.
        while (Volatile.Read(ref CompactionPending(_cache)) != 0) _notice.WaitForFinalNotification();
        _notice.Observe = _retry;
    }

    public override void Dispose()
    {
        _cache.Dispose();
        _notice.Dispose();
    }

    // Only the equal-admission producer and the final drain enable the official worker's debug notification.
    // Blocking on that event excludes busy-wait CPU and the timer granularity of polling each compaction.
    private sealed class CompactionNotice(bool observe) : ILoggerFactory, ILogger
    {
        private readonly AutoResetEvent _completed = new(false);
        public volatile bool Observe = observe;

        public void WaitForFinalNotification() => _completed.WaitOne(1);

        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => Observe && level == LogLevel.Debug && Thread.CurrentThread.IsThreadPoolThread;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        {
            if (format(state, error).StartsWith("Overcapacity compaction executed.", StringComparison.Ordinal)) _completed.Set();
        }

        public void Dispose() => _completed.Dispose();
    }

    private static Func<ICacheEntry, EvictionReason> ReadReason()
    {
        var assembly = typeof(MemoryCache).Assembly;
        var type = assembly.GetType("Microsoft.Extensions.Caching.Memory.CacheEntry");
        var property = type?.GetProperty("EvictionReason", BindingFlags.Instance | BindingFlags.NonPublic);
        var pending = typeof(MemoryCache).GetField("lockFlag", BindingFlags.Instance | BindingFlags.NonPublic);
        if (property?.PropertyType != typeof(EvictionReason) || pending?.FieldType != typeof(int))
        {
            throw new NotSupportedException($"The admission/compaction diagnostics expect .NET 10 MemoryCache internals. Check {assembly.FullName} before measuring.");
        }

        var entry = Expression.Parameter(typeof(ICacheEntry));
        var reason = Expression.Property(Expression.Convert(entry, type!), property);
        return Expression.Lambda<Func<ICacheEntry, EvictionReason>>(reason, entry).Compile();
    }
}
