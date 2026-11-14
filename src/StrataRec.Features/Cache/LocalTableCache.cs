using System.Text;
using System.Threading.Channels;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Hashing;
using StrataRec.Core.Keys;
using StrataRec.Features.Local;

namespace StrataRec.Features.Cache;

/// <summary>The cache policy of a <c>cache</c> table (design 6.6), from the binding's <c>cache</c> settings.</summary>
public sealed class CacheTableOptions
{
    /// <summary><c>cache.ttl_ms</c>: how long a value is served. Each key's lifetime is this ±10%, by the key's hash.</summary>
    public required long TtlMs { get; init; }

    /// <summary><c>cache.stale_if_error_ms</c>: how much longer an expired value may be served when the connector fails.</summary>
    public long StaleIfErrorMs { get; init; }

    /// <summary>
    /// The most record bytes waiting for the table's writer; a write-back past it is dropped and counted (the key is
    /// fetched again on its next miss). 0 takes a sixteenth of <c>max_bytes</c>, between 1 MiB and 64 MiB.
    /// </summary>
    public long WriteQueueBytes { get; init; }

    /// <summary>
    /// The clock of write times and expiry, and the timestamps the distributed tier's time is judged by; tests pass one
    /// they move by hand.
    /// </summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TtlMs);
        ArgumentOutOfRangeException.ThrowIfNegative(StaleIfErrorMs);
        ArgumentOutOfRangeException.ThrowIfNegative(WriteQueueBytes);
        ArgumentNullException.ThrowIfNull(Clock);
        if (TtlMs > long.MaxValue / 4 || StaleIfErrorMs > long.MaxValue / 4)
        {
            throw new ArgumentOutOfRangeException(nameof(TtlMs), "ttl_ms and stale_if_error_ms are at most 2^61 ms.");
        }
    }
}

/// <summary>How a cached record stands at a moment (design 6.6 <c>cache</c> items 1 and 4).</summary>
internal enum CacheFreshness : byte
{
    /// <summary>Within its key's lifetime: served.</summary>
    Fresh = 1,

    /// <summary>Past its lifetime, within <c>stale_if_error_ms</c> after it: served only when the connector fails.</summary>
    Stale,

    /// <summary>Past both: of no use.</summary>
    Expired,
}

/// <summary>Which tier of a <c>cache</c> source a key count is about (metric tag <c>stratarec.cache.tier</c>).</summary>
internal enum CacheTier : byte
{
    Local = 0,
    Distributed = 1,
}

/// <summary>What a tier had for a key (metric tag <c>stratarec.outcome</c> of <see cref="StrataRecMetrics.CacheKeys"/>).</summary>
internal enum CacheKeyOutcome : byte
{
    Hit = 0,
    Miss,
    Expired,
    Stale,
    Mismatch,
    Error,
}

/// <summary>What became of a written-back record (metric tag <c>stratarec.outcome</c> of <see cref="StrataRecMetrics.CacheWrites"/>).</summary>
internal enum CacheWriteOutcome : byte
{
    Written = 0,
    Dropped,
    Failed,
}

/// <summary>Records a read hands the table's writer: each key's encoded record (<see cref="CacheRecord"/>), whose header
/// carries its write time, the version the table writes it at.</summary>
internal sealed class CacheWriteBatch(EntityKey[] keys, ReadOnlyMemory<byte>[] records, int count)
{
    public EntityKey[] Keys { get; } = keys;

    public ReadOnlyMemory<byte>[] Records { get; } = records;

    public int Count { get; } = count;

    public long Bytes { get; } = SumBytes(records, count);

    private static long SumBytes(ReadOnlyMemory<byte>[] records, int count)
    {
        long bytes = 0;
        for (var i = 0; i < count; i++)
        {
            bytes += records[i].Length;
        }

        return bytes;
    }
}

/// <summary>
/// The in-process tier of a <c>cache</c> source (design 6.6 <c>cache</c>): one per table, and the one owner of what the
/// table's records mean as a cache. The request side asks it how fresh a record is (each key's lifetime is the TTL ±10%
/// by the key's hash); the write side is its single writer, which applies what reads hand back and keeps the table within
/// <c>max_bytes</c>.
/// </summary>
/// <remarks>
/// <para><b>Versions are write times.</b> A record's version is when the fetch that produced it started (Unix ms, from
/// <see cref="CacheTableOptions.Clock"/>), and the table ignores a write whose version is not newer than the key's record,
/// so a slow fetch that started early never replaces what a later one wrote. A record copied from the distributed tier
/// keeps the write time it was written with there.</para>
/// <para><b>Write-back is off the request path.</b> A read hands its records over with <see cref="TryWriteBack"/>, which
/// never waits: past <see cref="CacheTableOptions.WriteQueueBytes"/> waiting bytes the batch is dropped and counted. The
/// engine runs the writer as the table's update source (<see cref="CacheWriterSource"/>), so the table host starts,
/// restarts and stops it like any other; until it runs, the table counts as loaded at once, since an empty cache is a
/// complete one (every key misses).</para>
/// <para><b>Eviction.</b> A write that does not fit <c>max_bytes</c> (after the table compacted its superseded records)
/// drops the records that are of no use any more (past their lifetime and stale-if-error window), then, while the table
/// is still above three quarters of <c>max_bytes</c>, the oldest-written ones; then the write is tried again. Reads never
/// record anything, so the read path stays read-only: a hot key is fetched and written again after each expiry and stays,
/// a key written once and never again is the first to go.</para>
/// </remarks>
public sealed class LocalTableCache
{
    /// <summary>
    /// The longest time to live a distributed tier is given, in milliseconds: what a <see cref="TimeSpan"/> holds (about
    /// 29,000 years). A binding with a distributed tier whose <see cref="RetentionMs"/> exceeds it is rejected.
    /// </summary>
    internal const long MaxRetentionMs = long.MaxValue / TimeSpan.TicksPerMillisecond;

    private const double Jitter = 0.1;

    private readonly LocalFeatureTable _table;
    private readonly Channel<CacheWriteBatch> _queue = Channel.CreateUnbounded<CacheWriteBatch>(new UnboundedChannelOptions { SingleReader = true });
    private readonly long _queueLimit;
    private readonly KeyValuePair<string, object?>[][] _keyTags; // [tier * outcomes + outcome]
    private readonly KeyValuePair<string, object?>[][] _writeTags; // [tier * outcomes + outcome]
    private readonly KeyValuePair<string, object?>[] _evictedExpiredTags;
    private readonly KeyValuePair<string, object?>[] _evictedOldestTags;
    private long _queuedBytes;
    private char[] _chars = new char[256]; // the writer's UTF-8 decoding buffer

    internal LocalTableCache(LocalFeatureTable table, CacheTableOptions options)
    {
        options.Validate();
        _table = table;
        Options = options;
        Clock = options.Clock;
        Layout = new CacheRecordLayout(table.Schema);
        Keyspace = new CacheKeyspace(table.Schema.Source, table.Schema.Entity, table.Schema.KeyType);
        _queueLimit = options.WriteQueueBytes > 0 ? options.WriteQueueBytes : Math.Clamp(table.Options.MaxBytes / 16, 1L << 20, 64L << 20);
        MaxLifetimeMs = MaxLifetime(options.TtlMs);
        var tags = table.MetricTags.ToArray();
        _keyTags = TagMatrix(tags, Enum.GetValues<CacheKeyOutcome>().Length, static o => OutcomeName((CacheKeyOutcome)o));
        _writeTags = TagMatrix(tags, Enum.GetValues<CacheWriteOutcome>().Length, static o => OutcomeName((CacheWriteOutcome)o));
        _evictedExpiredTags = [tags[0], tags[1], new(StrataRecTags.Outcome, "expired")];
        _evictedOldestTags = [tags[0], tags[1], new(StrataRecTags.Outcome, "oldest")];
    }

    public CacheTableOptions Options { get; }

    public TimeProvider Clock { get; }

    /// <summary>The longest lifetime of any key: the TTL plus the most jitter.</summary>
    public long MaxLifetimeMs { get; }

    /// <summary>Record bytes handed to the writer and not written yet.</summary>
    public long QueuedBytes => Volatile.Read(ref _queuedBytes);

    internal CacheRecordLayout Layout { get; }

    internal CacheKeyspace Keyspace { get; }

    /// <summary>The fingerprint the table's records carry.</summary>
    internal ulong Fingerprint => Layout.Fingerprint;

    /// <summary>The longest lifetime of any key of a table with TTL <paramref name="ttlMs"/>: the TTL plus the most jitter.</summary>
    internal static long MaxLifetime(long ttlMs) => (long)Math.Ceiling(ttlMs * (1 + Jitter));

    /// <summary>The longest a distributed tier keeps a record: until no reader could use it, even stale.</summary>
    internal static long RetentionMs(long ttlMs, long staleIfErrorMs) => MaxLifetime(ttlMs) + staleIfErrorMs;

    /// <summary>Now, in Unix milliseconds: the version of a record a fetch starting now writes.</summary>
    internal long NowMs() => Clock.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>How long the record of <paramref name="key"/> is served: the TTL ±10% by the key's hash, the same on every instance.</summary>
    internal long LifetimeMs(in EntityKey key)
    {
        return (long)(Options.TtlMs * (1 - Jitter + (2 * Jitter * StableHash.ToUnit(KeyHash(key)))));
    }

    /// <summary>The canonical hash of a key (design 5.7 encoding): what its jitter derives from, so every instance agrees.</summary>
    private static ulong KeyHash(in EntityKey key)
    {
        if (key.StringValue is not { } s)
        {
            return StableHash.Hash(key.Int64Value);
        }

        using var builder = new StableHash.Builder();
        builder.Add(s);
        return builder.Finish();
    }

    /// <summary>How a record of <paramref name="key"/> written at <paramref name="writtenMs"/> stands at <paramref name="nowMs"/>.</summary>
    /// <remarks>A write time ahead of the clock (another instance's clock runs ahead) counts as written now.</remarks>
    internal CacheFreshness Classify(long writtenMs, in EntityKey key, long nowMs)
    {
        var age = Math.Max(0, nowMs - writtenMs);
        var lifetime = LifetimeMs(key);
        return age <= lifetime ? CacheFreshness.Fresh : age <= lifetime + Options.StaleIfErrorMs ? CacheFreshness.Stale : CacheFreshness.Expired;
    }

    /// <summary>How long the distributed tier should keep a record written at <paramref name="writtenMs"/>: until no reader
    /// could use it, even stale.</summary>
    /// <remarks>
    /// The retention less the record's age, at least 1 ms. The age is judged as <see cref="Classify"/> judges it: a write
    /// time ahead of the clock (the clock stepped back during the fetch, say) counts as written now, so the result is at
    /// most <see cref="RetentionMs"/>, which the binding checked against <see cref="MaxRetentionMs"/>; at that bound a
    /// write time 1 ms ahead used to overflow the ticks to a negative time to live (review 21 #1). In ticks, so no
    /// floating-point rounding takes the boundary past what a <see cref="TimeSpan"/> holds.
    /// </remarks>
    internal TimeSpan StoreTimeToLive(long writtenMs, long nowMs)
    {
        var retention = MaxLifetimeMs + Options.StaleIfErrorMs;
        var age = writtenMs >= nowMs ? 0 : nowMs - writtenMs;
        var remaining = age < 0 || age >= retention ? 1 : retention - age; // a negative age is one that overflowed: very old
        return TimeSpan.FromTicks(remaining * TimeSpan.TicksPerMillisecond);
    }

    /// <summary>Hands records to the writer without waiting; false (and counted) when the queue is full.</summary>
    internal bool TryWriteBack(CacheWriteBatch batch)
    {
        if (Interlocked.Add(ref _queuedBytes, batch.Bytes) > _queueLimit || !_queue.Writer.TryWrite(batch))
        {
            Interlocked.Add(ref _queuedBytes, -batch.Bytes);
            CountWrites(CacheTier.Local, CacheWriteOutcome.Dropped, batch.Count);
            return false;
        }

        return true;
    }

    internal void CountKeys(CacheTier tier, CacheKeyOutcome outcome, int keys)
    {
        if (keys > 0)
        {
            StrataRecMetrics.CacheKeys.Add(keys, _keyTags[((int)tier * _keyTags.Length / 2) + (int)outcome]);
        }
    }

    internal void CountWrites(CacheTier tier, CacheWriteOutcome outcome, int records)
    {
        if (records > 0)
        {
            StrataRecMetrics.CacheWrites.Add(records, _writeTags[((int)tier * _writeTags.Length / 2) + (int)outcome]);
        }
    }

    /// <summary>
    /// The writer's run (see <see cref="CacheWriterSource"/>): marks the table loaded, then applies batches as they come
    /// until cancelled. One run at a time, as for any update source.
    /// </summary>
    internal async Task RunAsync(LocalTableWriter writer, CancellationToken cancellationToken)
    {
        writer.Checkpoint(UpdatePosition.Empty); // an empty cache is complete: every key misses
        var reader = _queue.Reader;
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            WriteQueued(writer);
        }
    }

    /// <summary>Applies every batch waiting now, on the calling thread (the writer's; tests call it directly). Returns the batches applied.</summary>
    internal int WriteQueued(LocalTableWriter writer)
    {
        var batches = 0;
        while (_queue.Reader.TryRead(out var batch))
        {
            Interlocked.Add(ref _queuedBytes, -batch.Bytes);
            Write(writer, batch);
            batches++;
        }

        if (batches > 0)
        {
            _table.RecordSize();
        }

        return batches;
    }

    private void Write(LocalTableWriter writer, CacheWriteBatch batch)
    {
        int written = 0, failed = 0;
        for (var i = 0; i < batch.Count; i++)
        {
            var result = WriteOne(writer, batch.Keys[i], batch.Records[i].Span);
            if (result == WriteResult.Full)
            {
                Evict(writer);
                result = WriteOne(writer, batch.Keys[i], batch.Records[i].Span);
            }

            switch (result)
            {
                case WriteResult.Written:
                    written++;
                    break;
                case WriteResult.Full or WriteResult.Corrupt:
                    failed++;
                    break;
                case WriteResult.Ignored:
                    break; // the table holds a newer record of the key
            }
        }

        CountWrites(CacheTier.Local, CacheWriteOutcome.Written, written);
        CountWrites(CacheTier.Local, CacheWriteOutcome.Failed, failed);
    }

    private WriteResult WriteOne(LocalTableWriter writer, in EntityKey key, ReadOnlySpan<byte> record)
    {
        if (CacheRecord.ReadHeader(record, Layout.FieldCount, Layout.Fingerprint, out var writtenMs, out _) != CacheRecordHeader.Ok
            || !CacheRecord.IsWellFormed(record, Layout))
        {
            return WriteResult.Corrupt; // never produced by a read, which checks what it hands over; a defect
        }

        try
        {
            var builder = writer.Begin(key, writtenMs);
            if (builder.IsIgnored)
            {
                return WriteResult.Ignored;
            }

            var visitor = new BuilderVisitor(builder, this);
            CacheRecord.Read(record, Layout, ref visitor);
            return builder.Commit() ? WriteResult.Written : WriteResult.Ignored;
        }
        catch (LocalTableFullException)
        {
            return WriteResult.Full; // nothing written; the builder is discarded
        }
    }

    /// <summary>
    /// Makes room (design 6.6 <c>cache</c> item 5): drops the records no reader can use any more, then, while the table is
    /// still above three quarters of <c>max_bytes</c>, the oldest-written ones, so that eviction does not run again at the
    /// very next write.
    /// </summary>
    private void Evict(LocalTableWriter writer)
    {
        var maxBytes = _table.Options.MaxBytes;
        var before = _table.Pin().RecordCount;
        var deadBefore = NowMs() - MaxLifetimeMs - Options.StaleIfErrorMs;
        TryCompact(writer, deadBefore);
        var afterExpired = _table.Pin().RecordCount;
        if (before > afterExpired)
        {
            StrataRecMetrics.CacheEvictions.Add(before - afterExpired, _evictedExpiredTags);
        }

        var target = maxBytes / 4 * 3;
        var bytes = _table.Pin().Bytes;
        if (bytes <= target)
        {
            return;
        }

        TryCompact(writer, writer.WriteTimeCutoff(bytes - target));
        var afterOldest = _table.Pin().RecordCount;
        if (afterExpired > afterOldest)
        {
            StrataRecMetrics.CacheEvictions.Add(afterExpired - afterOldest, _evictedOldestTags);
        }
    }

    private static void TryCompact(LocalTableWriter writer, long dropWrittenBefore)
    {
        try
        {
            writer.Compact(dropWrittenBefore);
        }
        catch (LocalTableFullException)
        {
            // The compacted copy laid its pages out larger than max_bytes (see Compact); the next step drops more.
        }
    }

    private Span<char> Chars(int length)
    {
        if (_chars.Length < length)
        {
            _chars = new char[Math.Max(length, _chars.Length * 2)];
        }

        return _chars.AsSpan(0, length);
    }

    private static KeyValuePair<string, object?>[][] TagMatrix(KeyValuePair<string, object?>[] table, int outcomes, Func<int, string> outcomeName)
    {
        var matrix = new KeyValuePair<string, object?>[2 * outcomes][];
        for (var tier = 0; tier < 2; tier++)
        {
            for (var outcome = 0; outcome < outcomes; outcome++)
            {
                matrix[(tier * outcomes) + outcome] =
                [
                    table[0], table[1], new(StrataRecTags.CacheTier, tier == (int)CacheTier.Local ? "local" : "distributed"),
                    new(StrataRecTags.Outcome, outcomeName(outcome)),
                ];
            }
        }

        return matrix;
    }

    private static string OutcomeName(CacheKeyOutcome outcome) => outcome switch
    {
        CacheKeyOutcome.Hit => "hit",
        CacheKeyOutcome.Miss => "miss",
        CacheKeyOutcome.Expired => "expired",
        CacheKeyOutcome.Stale => "stale",
        CacheKeyOutcome.Mismatch => "mismatch",
        CacheKeyOutcome.Error => "error",
    };

    private static string OutcomeName(CacheWriteOutcome outcome) => outcome switch
    {
        CacheWriteOutcome.Written => "written",
        CacheWriteOutcome.Dropped => "dropped",
        CacheWriteOutcome.Failed => "failed",
    };

    private enum WriteResult : byte
    {
        Written,
        Ignored,
        Full,
        Corrupt,
    }

    /// <summary>Copies a record's fields into the table's record builder: every field, so the table's record is complete.</summary>
    private struct BuilderVisitor(RecordBuilder builder, LocalTableCache owner) : ICacheRecordVisitor
    {
        public void Missing(int field) => builder.SetMissing(field);

        public void Bool(int field, bool value) => builder.SetBool(field, value);

        public void Int32(int field, int value) => builder.SetInt32(field, value);

        public void Int64(int field, long value) => builder.SetInt64(field, value);

        public void Float32(int field, float value) => builder.SetFloat32(field, value);

        public void Float64(int field, double value) => builder.SetFloat64(field, value);

        public void Vector(int field, ReadOnlySpan<float> value) => builder.SetVector(field, value);

        public void String(int field, ReadOnlySpan<byte> utf8) => builder.SetString(field, Decode(utf8));

        public void Int32List(int field, ReadOnlySpan<int> value) => builder.SetInt32List(field, value);

        public void Int64List(int field, ReadOnlySpan<long> value) => builder.SetInt64List(field, value);

        public void StringListStart(int field, int count) => builder.StartList(field);

        public void StringListItem(int field, ReadOnlySpan<byte> utf8) => builder.AddStringItem(field, Decode(utf8));

        public void StringListEnd(int field)
        {
        }

        private readonly ReadOnlySpan<char> Decode(ReadOnlySpan<byte> utf8)
        {
            var chars = owner.Chars(Encoding.UTF8.GetMaxCharCount(utf8.Length));
            return chars[..Encoding.UTF8.GetChars(utf8, chars)];
        }
    }
}

/// <summary>
/// The writer of a <c>cache</c> table, run by the table host as the table's update source (design 6.6 item 3: one
/// background writer per table): it applies the records reads hand back until it is stopped. Stateless; the queue and
/// the policy belong to the table (<see cref="LocalFeatureTable.Cache"/>), so whichever plan's spec created the table,
/// its writer drains the queue every plan's requests write to.
/// </summary>
internal sealed class CacheWriterSource : IFeatureUpdateSource
{
    public const string Type = "cache_writer@1";

    public static CacheWriterSource Instance { get; } = new();

    public string SourceType => Type;

    public Task RunAsync(UpdatePosition from, IFeatureTableWriter writer, CancellationToken cancellationToken) =>
        writer is LocalTableWriter { Table.Cache: { } cache } table
            ? cache.RunAsync(table, cancellationToken)
            : throw new InvalidOperationException("The cache writer runs on the writer of a cache table.");
}
