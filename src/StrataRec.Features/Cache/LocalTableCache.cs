using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Hashing;
using StrataRec.Core.Keys;
using StrataRec.Features.Connectors;
using StrataRec.Features.Local;

namespace StrataRec.Features.Cache;

/// <summary>What a <c>cache</c> table is created with, from the binding's <c>cache</c> settings.</summary>
public sealed class CacheTableOptions
{
    /// <summary><c>cache.ttl_ms</c> of the plan that takes the table: with <see cref="StaleIfErrorMs"/>, its
    /// <see cref="CachePolicy"/>.</summary>
    public required long TtlMs { get; init; }

    /// <summary><c>cache.stale_if_error_ms</c> of the plan that takes the table.</summary>
    public long StaleIfErrorMs { get; init; }

    /// <summary>
    /// Whether the table keeps records (<c>cache.memory</c>). A cache without an in-process tier still has its table:
    /// it stays empty, so every read misses it and goes on to the distributed tier.
    /// </summary>
    public bool Memory { get; init; } = true;

    /// <summary>
    /// Whether a tier keeps the cache's records: this table (<see cref="Memory"/>) or a distributed store. False for a
    /// cache with no tier (<c>"cache": {}</c>), which only has the reads of one request share what one of them got.
    /// </summary>
    public bool Kept { get; init; } = true;

    /// <summary>
    /// The most bytes waiting for the table's writer (records and keys); a write-back past it is dropped and counted
    /// (the key is fetched again on its next miss), but for one larger than the whole limit handed to an empty queue,
    /// which takes it. 0 takes a sixteenth of <c>max_bytes</c>, between 1 MiB and 64 MiB.
    /// </summary>
    public long WriteQueueBytes { get; init; }

    /// <summary>
    /// The clock of write times and expiry, and the timestamps the distributed tier's time is judged by.
    /// </summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>
    /// What the table's values come from, for a feature source's cache: the binding's <c>cache.version</c>, or without one
    /// a digest of its connector and the connector's settings; the same on every instance with the same configuration.
    /// Part of every key of the distributed tier (<see cref="CacheKeyspace.Prefix"/>), so a source bound to another
    /// connector, or to other data, does not read what the earlier binding left there; two releases that differ in it
    /// each keep their own. Null for a service's cache, whose keys carry the service's answer identity themselves.
    /// </summary>
    public string? Origin { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(WriteQueueBytes);
        ArgumentNullException.ThrowIfNull(Clock);
        if (Memory && !Kept)
        {
            throw new ArgumentException("A cache whose table keeps records is kept.", nameof(Kept));
        }
    }
}

internal enum CacheFreshness : byte
{
    Fresh = 1,

    Stale,

    Expired,
}

/// <summary>
/// Which tier of a cache a key count is about (metric tag <c>stratarec.cache.tier</c>: <c>memory</c>, <c>distributed</c>,
/// and <c>request</c> for a read answered by what another read of its request got, <see cref="Core.Threading.RequestReads"/>).
/// </summary>
internal enum CacheTier : byte
{
    Memory = 0,
    Distributed = 1,
    Request = 2,
}

internal enum CacheKeyOutcome : byte
{
    Hit = 0,
    Miss,
    Expired,
    Stale,
    Mismatch,
    Error,
}

internal enum CacheWriteOutcome : byte
{
    Written = 0,
    Dropped,
    Failed,
}

/// <summary>Records a read hands the table's writer: each key's encoded record (<see cref="CacheRecord"/>), whose header
/// carries its write time, the version the table writes it at.</summary>
/// <param name="keys">Key i's parts at <c>i * partCount</c>, in part order.</param>
internal sealed class CacheWriteBatch(EntityKey[] keys, ReadOnlyMemory<byte>[] records, int count, int partCount = 1)
{
    public EntityKey[] Keys { get; } = keys;

    public int PartCount { get; } = partCount;

    public ReadOnlySpan<EntityKey> Key(int index) => Keys.AsSpan(index * PartCount, PartCount);

    public ReadOnlyMemory<byte>[] Records { get; } = records;

    public int Count { get; } = count;

    /// <remarks>
    /// Comparing references allocates nothing; an equal string that is another object is another object held, and
    /// charged.
    /// </remarks>
    public long Bytes { get; } = Footprint(keys, records, count, partCount);

    private static long Footprint(EntityKey[] keys, ReadOnlyMemory<byte>[] records, int count, int partCount)
    {
        var bytes = ((long)keys.Length * Unsafe.SizeOf<EntityKey>()) + ((long)records.Length * Unsafe.SizeOf<ReadOnlyMemory<byte>>());
        for (var i = 0; i < count; i++)
        {
            bytes += records[i].Length;
        }

        for (var k = 0; k < count * partCount; k++)
        {
            if (keys[k].StringValue is { } text && (k < partCount || !ReferenceEquals(text, keys[k - partCount].StringValue)))
            {
                bytes += TableVersion.StringObjectBytes(text.Length);
            }
        }

        return bytes;
    }
}

/// <summary>
/// The in-process tier of a cache: one per table. Its write side is the table's single writer, which applies what reads
/// hand back and keeps the table within <c>max_bytes</c>. How fresh a record is, each reader asks its own plan's
/// <see cref="CachePolicy"/>.
/// </summary>
/// <remarks>
/// <para><b>Versions are write times.</b> A record's version is when the fetch that produced it started (Unix ms, from
/// <see cref="CacheTableOptions.Clock"/>), and the table ignores a write whose version is not newer than the key's record,
/// so a slow fetch that started early never replaces what a later one wrote. A record copied from the distributed tier
/// keeps the write time it was written with there.</para>
/// <para><b>Write-back is off the request path.</b> A read hands its records over with <see cref="TryWriteBack"/>,
/// which never waits: past <see cref="CacheTableOptions.WriteQueueBytes"/> waiting bytes the batch is dropped and
/// counted (an empty queue takes even a larger one). The engine runs the writer; until it runs, the table counts as
/// loaded at once, since an empty cache is a complete one (every key misses).</para>
/// <para><b>Eviction.</b> The writer reclaims records past the stale window, then aims for three quarters of
/// <c>max_bytes</c>. If all remaining candidates are visited, the writer can still admit a new key. Victims are removed
/// only when the pending record fits.</para>
/// </remarks>
public sealed class LocalTableCache
{
    private readonly LocalFeatureTable _table;
    private readonly Channel<CacheWriteBatch> _queue = Channel.CreateUnbounded<CacheWriteBatch>(new UnboundedChannelOptions { SingleReader = true });
    private readonly KeyValuePair<string, object?>[][] _keyTags; // [tier * outcomes + outcome]
    private readonly KeyValuePair<string, object?>[][] _writeTags; // [tier * outcomes + outcome]
    private readonly KeyValuePair<string, object?>[] _evictedExpiredTags;
    private readonly KeyValuePair<string, object?>[] _evictedCapacityTags;
    private CachePolicy _policy;
    private long _queuedBytes;
    private char[] _chars = new char[256]; // the writer's UTF-8 decoding buffer

    internal LocalTableCache(LocalFeatureTable table, CacheTableOptions options)
    {
        options.Validate();
        _table = table;
        _policy = new CachePolicy(options.TtlMs, options.StaleIfErrorMs);
        Options = options;
        Clock = options.Clock;
        Layout = new CacheRecordLayout(table.Schema);
        Keyspace = new CacheKeyspace(table.Schema.Source, table.Schema.Entity, table.Schema.Key, options.Origin);
        var tags = table.MetricTags.ToArray();
        _keyTags = TagMatrix(tags, Enum.GetValues<CacheKeyOutcome>().Length, static o => OutcomeName((CacheKeyOutcome)o));
        _writeTags = TagMatrix(tags, Enum.GetValues<CacheWriteOutcome>().Length, static o => OutcomeName((CacheWriteOutcome)o));
        _evictedExpiredTags = [tags[0], tags[1], new(StrataRecTags.Outcome, "expired")];
        _evictedCapacityTags = [tags[0], tags[1], new(StrataRecTags.Outcome, "capacity")];
    }

    public CacheTableOptions Options { get; }

    public TimeProvider Clock { get; }

    /// <summary>
    /// The policy of the plan that took the table last: the writer's, which reclaims records past its retention first. A
    /// reader uses its own plan's, which is this one unless two plans with different times share the table.
    /// </summary>
    public CachePolicy Policy => Volatile.Read(ref _policy);

    /// <summary>Whether the table keeps records (<see cref="CacheTableOptions.Memory"/>).</summary>
    public bool Memory => Options.Memory;

    /// <summary>Whether a tier keeps the cache's records (<see cref="CacheTableOptions.Kept"/>).</summary>
    public bool Kept => Options.Kept;

    private long QueueLimit => Options.WriteQueueBytes > 0 ? Options.WriteQueueBytes : Math.Clamp(_table.MaxBytes / 16, 1L << 20, 64L << 20);

    internal void Adopt(CachePolicy policy) => Volatile.Write(ref _policy, policy);

    /// <summary>
    /// Has the writer look at the table without a write to make: its limit was lowered under what it holds, and a cache
    /// whose reads all hit would keep the memory it was told to give back until its next miss.
    /// </summary>
    internal void Wake() => _queue.Writer.TryWrite(Nothing);

    private static readonly CacheWriteBatch Nothing = new([], [], 0);

    /// <summary>Bytes handed to the writer and not written yet, as charged.</summary>
    public long QueuedBytes => Volatile.Read(ref _queuedBytes);

    internal CacheRecordLayout Layout { get; }

    internal CacheKeyspace Keyspace { get; }

    internal ulong Fingerprint => Layout.Fingerprint;

    internal long NowMs() => Clock.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>The canonical hash of a one-part key: what its jitter derives from, so every instance agrees.</summary>
    internal static ulong KeyHash(in EntityKey key)
    {
        if (key.StringValue is not { } s)
        {
            return StableHash.Hash(key.Int64Value);
        }

        using var builder = new StableHash.Builder();
        builder.Add(s);
        return builder.Finish();
    }

    /// <summary>
    /// The canonical hash of key <paramref name="index"/> of <paramref name="keys"/>: a one-part key's as above,
    /// several parts each added in order to one tagged, length-prefixed encoding.
    /// </summary>
    internal static ulong KeyHash(in EntityKeyBatch keys, int index)
    {
        if (keys.PartCount == 1)
        {
            return KeyHash(keys.Key(index, 0));
        }

        using var builder = new StableHash.Builder();
        for (var p = 0; p < keys.PartCount; p++)
        {
            var part = keys.Key(index, p);
            if (part.StringValue is { } s)
            {
                builder.Add(s);
            }
            else
            {
                builder.Add(part.Int64Value);
            }
        }

        return builder.Finish();
    }

    /// <summary>
    /// What the reads of one request find key <paramref name="index"/>'s record of a table by (<see cref="Core.Threading.RequestReads"/>):
    /// 128 bits of every part, since two keys taken for one would give one's record to the other.
    /// </summary>
    internal static UInt128 RecordKey(in EntityKeyBatch keys, int index)
    {
        using var builder = new StableHash.Builder();
        for (var p = 0; p < keys.PartCount; p++)
        {
            var part = keys.Key(index, p);
            if (part.StringValue is { } s)
            {
                builder.Add(s);
            }
            else
            {
                builder.Add(part.Int64Value);
            }
        }

        return builder.Finish128();
    }

    internal bool TryWriteBack(CacheWriteBatch batch)
    {
        if (!Memory)
        {
            return true; // no in-process tier: nothing is kept, and nothing was dropped
        }

        if (!WriteQueueBudget.TryTake(ref _queuedBytes, batch.Bytes, QueueLimit))
        {
            CountDropped(CacheTier.Memory, batch.Count);
            return false;
        }

        if (!_queue.Writer.TryWrite(batch))
        {
            Interlocked.Add(ref _queuedBytes, -batch.Bytes);
            CountDropped(CacheTier.Memory, batch.Count);
            return false;
        }

        return true;
    }

    /// <summary>Never throws: it runs before the read is recorded, and a listener's exception would fail a read the
    /// connector answered.</summary>
    internal void CountDropped(CacheTier tier, int records)
    {
        try
        {
            CountWrites(tier, CacheWriteOutcome.Dropped, records);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Never throws, as <see cref="CountDropped"/>.</summary>
    internal void CountKeys(CacheTier tier, CacheKeyOutcome outcome, int keys)
    {
        if (keys > 0 && (Memory || tier != CacheTier.Memory))
        {
            try
            {
                StrataRecMetrics.CacheKeys.Add(keys, _keyTags[((int)tier * _keyTags.Length / Tiers) + (int)outcome]);
            }
            catch (Exception)
            {
            }
        }
    }

    internal void CountWrites(CacheTier tier, CacheWriteOutcome outcome, int records)
    {
        if (records > 0)
        {
            StrataRecMetrics.CacheWrites.Add(records, _writeTags[((int)tier * _writeTags.Length / Tiers) + (int)outcome]);
        }
    }

    internal async Task RunAsync(LocalTableWriter writer, CancellationToken cancellationToken)
    {
        writer.Checkpoint(UpdatePosition.Empty); // an empty cache is complete: every key misses
        var reader = _queue.Reader;
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            WriteQueued(writer);
        }
    }

    internal int WriteQueued(LocalTableWriter writer)
    {
        var batches = 0;
        while (_queue.Reader.TryRead(out var batch))
        {
            Interlocked.Add(ref _queuedBytes, -batch.Bytes);
            writer.ShrinkToLimit(this); // before the writes: a limit lowered under the table is met whether they fit or not
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
            var result = WriteOne(writer, batch.Key(i), batch.Records[i].Span);
            switch (result)
            {
                case WriteResult.Written:
                    written++;
                    break;
                case WriteResult.Failed:
                    failed++;
                    break;
                case WriteResult.Ignored:
                    break;
            }
        }

        CountWrites(CacheTier.Memory, CacheWriteOutcome.Written, written);
        CountWrites(CacheTier.Memory, CacheWriteOutcome.Failed, failed);
    }

    private WriteResult WriteOne(LocalTableWriter writer, ReadOnlySpan<EntityKey> key, ReadOnlySpan<byte> record)
    {
        if (CacheRecord.ReadHeader(record, Layout.FieldCount, Layout.Fingerprint, out var writtenMs, out _) != CacheRecordHeader.Ok)
        {
            return WriteResult.Failed; // never produced by a read, which checks what it hands over; a defect
        }

        try
        {
            var builder = key.Length == 1 ? writer.Begin(key[0], writtenMs) : writer.Begin(key, writtenMs);
            if (builder.IsIgnored)
            {
                return CacheRecord.IsWellFormed(record, Layout) ? WriteResult.Ignored : WriteResult.Failed;
            }

            // The builder is private until commit, so a malformed field can discard the staged prefix.
            var visitor = new BuilderVisitor(builder, this);
            if (!CacheRecord.Read(record, Layout, ref visitor))
            {
                writer.Discard();
                return WriteResult.Failed;
            }

            return builder.CommitCached(this) ? WriteResult.Written : WriteResult.Ignored;
        }
        catch (LocalTableFullException)
        {
            return WriteResult.Failed; // nothing written; the builder is discarded
        }
    }

    /// <summary>Counts only the records removed from a version that was published.</summary>
    internal void CountEvictions(int expired, int capacity)
    {
        if (expired > 0)
        {
            StrataRecMetrics.CacheEvictions.Add(expired, _evictedExpiredTags);
        }

        if (capacity > 0)
        {
            StrataRecMetrics.CacheEvictions.Add(capacity, _evictedCapacityTags);
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

    private const int Tiers = 3; // CacheTier's values

    private static KeyValuePair<string, object?>[][] TagMatrix(KeyValuePair<string, object?>[] table, int outcomes, Func<int, string> outcomeName)
    {
        var matrix = new KeyValuePair<string, object?>[Tiers * outcomes][];
        for (var tier = 0; tier < Tiers; tier++)
        {
            for (var outcome = 0; outcome < outcomes; outcome++)
            {
                matrix[(tier * outcomes) + outcome] =
                [
                    table[0], table[1], new(StrataRecTags.CacheTier, (CacheTier)tier switch
                    {
                        CacheTier.Memory => "memory",
                        CacheTier.Distributed => "distributed",
                        CacheTier.Request => "request",
                    }),
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
        Failed,
    }

    private struct BuilderVisitor(RecordBuilder builder, LocalTableCache owner) : ICacheRecordVisitor
    {
        public void Missing(int field) => builder.SetMissing(field);

        public void Bool(int field, bool value) => builder.SetBool(field, value);

        public void Int32(int field, int value) => builder.SetInt32(field, value);

        public void Int64(int field, long value) => builder.SetInt64(field, value);

        public void Float32(int field, float value) => builder.SetFloat32(field, value);

        public void Float64(int field, double value) => builder.SetFloat64(field, value);

        public void String(int field, ReadOnlySpan<byte> utf8) => builder.SetString(field, Decode(utf8));

        public void List<T>(int field, ReadOnlySpan<T> value)
            where T : unmanaged => builder.SetList(field, value);

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
/// Stateless; the queue and the policy belong to the table (<see cref="LocalFeatureTable.Cache"/>), so whichever plan's
/// spec created the table, its writer drains the queue every plan's requests write to.
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
