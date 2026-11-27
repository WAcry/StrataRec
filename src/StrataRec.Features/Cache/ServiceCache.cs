using System.Runtime.InteropServices;
using System.Text;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Features.Local;

namespace StrataRec.Features.Cache;

/// <summary>
/// The tiers of one service's cache (docs/decisions/external-calls.md): an in-process <c>cache</c> table, which the
/// table host runs like a source's, and optionally the distributed tier the deployment shares. Every service's table has
/// one layout (<see cref="Schema"/>): a list field per element type, and a list of integers saying what the record holds.
/// What that is (which outputs of which call, which items) is the call's business: the call primitive writes and reads
/// records with <see cref="ServiceRecordBuilder"/> and <see cref="IServiceRecord"/>. A record's key is a 128-bit hash of the
/// call (or of the item), as two int64 parts.
/// </summary>
internal sealed class ServiceCache
{
    /// <summary>The entity of every service's table: its metric tag <c>stratarec.entity</c> and its keyspace's middle part.</summary>
    public const string Entity = "answers";

    public const int LayoutField = 0;
    public const int BoolField = 1;
    public const int Int32Field = 2;
    public const int Int64Field = 3;
    public const int Float32Field = 4;
    public const int Float64Field = 5;
    public const int StringField = 6;

    private static readonly LocalTableField[] Fields =
    [
        new("layout", ColumnType.List(StorageKind.Int32)),
        new("bool", ColumnType.List(StorageKind.Bool)),
        new("int32", ColumnType.List(StorageKind.Int32)),
        new("int64", ColumnType.List(StorageKind.Int64)),
        new("float32", ColumnType.List(StorageKind.Float32)),
        new("float64", ColumnType.List(StorageKind.Float64)),
        new("string", ColumnType.List(StorageKind.String)),
    ];

    /// <summary>Every record's key: the high and the low 64 bits of its hash.</summary>
    private static readonly EntityKeyLayout KeyLayout = EntityKeyLayout.OfTypes(EntityKeyType.Int64, EntityKeyType.Int64);

    /// <param name="answerIdentity">
    /// What the binding answers with, the same on every instance that has the same configuration: part of every key, so a
    /// binding whose client or settings changed (a hot reload, another deployment sharing the store) does not read the
    /// answers the earlier one left. The binding's <c>cache.version</c> when it names one.
    /// </param>
    /// <param name="policy">The binding's times as the plan compiled them.</param>
    public ServiceCache(
        string service, LocalFeatureTable table, CachePolicy policy, DistributedCacheTier? distributed, int distributedTimeoutMs, string answerIdentity)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(answerIdentity);
        Service = service;
        AnswerIdentity = answerIdentity;
        Table = table;
        Policy = policy;
        TableCache = table.Cache ?? throw new ArgumentException("A service's cache table is a cache table.", nameof(table));
        Distributed = distributed;
        DistributedTimeoutMs = distributedTimeoutMs;
        Io = new IoTarget(IoRoles.Cache, service);
    }

    /// <summary>The service's binding name.</summary>
    public string Service { get; }

    /// <summary>What the binding answers with (see the constructor): its <c>cache.version</c>, or a digest of its client and settings.</summary>
    public string AnswerIdentity { get; }

    public LocalFeatureTable Table { get; }

    /// <summary>How fresh this plan's calls find a record, in either tier.</summary>
    public CachePolicy Policy { get; }

    /// <summary>The table's side of the cache: the write-back queue, the records' layout, the clock, the counts.</summary>
    public LocalTableCache TableCache { get; }

    public DistributedCacheTier? Distributed { get; }

    /// <summary>The longest one read of the distributed tier may take (its <c>timeout_ms</c>, or half the service's).</summary>
    public int DistributedTimeoutMs { get; }

    /// <summary>The telemetry target of the distributed tier's reads: role <c>cache</c>, the service's name.</summary>
    public IoTarget Io { get; }

    /// <summary>The one layout of every table of whole answers, for the service (or the pipeline) <paramref name="owner"/>.</summary>
    /// <param name="entity">What the table holds, as its metric tag and its keyspace say it: a service's <see cref="Entity"/>, a pipeline's results.</param>
    public static LocalTableSchema Schema(string owner, string entity = Entity) => new(owner, entity, KeyLayout, LocalTableMode.Cache, Fields);

    /// <summary>The record of <paramref name="key"/> in a pinned version, or none.</summary>
    public static LocalRecordRef Find(TableVersion version, UInt128 key)
    {
        Span<EntityKey> parts = [High(key), Low(key)];
        return version.Find(parts);
    }

    /// <summary>How a record of <paramref name="key"/> written at <paramref name="writtenMs"/> stands at <paramref name="nowMs"/>.</summary>
    public CacheFreshness Classify(long writtenMs, UInt128 key, long nowMs) => Policy.Classify(writtenMs, (ulong)key, nowMs);

    /// <summary>The text of <paramref name="key"/> in the distributed tier (<see cref="CacheKeyspace.KeyText"/>), e.g. <c>-81234:9921</c>.</summary>
    public static string KeyText(UInt128 key)
    {
        Span<EntityKey> parts = [High(key), Low(key)];
        return CacheKeyspace.KeyText(parts);
    }

    /// <summary>
    /// A record a distributed read returned, decoded, or null when it is of another layout or format (<c>mismatch</c>) or
    /// cut short (<c>error</c>): <paramref name="outcome"/> says which, for its count.
    /// </summary>
    public DecodedServiceRecord? Decode(ReadOnlyMemory<byte> bytes, out CacheKeyOutcome outcome)
    {
        var record = bytes.Span;
        switch (CacheRecord.ReadHeader(record, Fields.Length, TableCache.Fingerprint, out var writtenMs, out _))
        {
            case CacheRecordHeader.Ok when CacheRecord.IsWellFormed(record, TableCache.Layout):
                var visitor = new DecodeVisitor(new DecodedServiceRecord(writtenMs, bytes));
                CacheRecord.Read(record, TableCache.Layout, ref visitor);
                outcome = CacheKeyOutcome.Hit;
                return visitor.Record;
            case CacheRecordHeader.Mismatch:
                outcome = CacheKeyOutcome.Mismatch;
                return null;
            default:
                outcome = CacheKeyOutcome.Error;
                return null;
        }
    }

    private static EntityKey High(UInt128 key) => EntityKey.FromInt64((long)(ulong)(key >> 64));

    private static EntityKey Low(UInt128 key) => EntityKey.FromInt64((long)(ulong)key);

    /// <summary>Copies a wire record's lists out of the record's bytes, which belong to the store's answer.</summary>
    private struct DecodeVisitor(DecodedServiceRecord record) : ICacheRecordVisitor
    {
        private List<string>? _strings;

        public readonly DecodedServiceRecord Record => record;

        public readonly void Missing(int field)
        {
        }

        public readonly void Bool(int field, bool value)
        {
        }

        public readonly void Int32(int field, int value)
        {
        }

        public readonly void Int64(int field, long value)
        {
        }

        public readonly void Float32(int field, float value)
        {
        }

        public readonly void Float64(int field, double value)
        {
        }

        public readonly void String(int field, ReadOnlySpan<byte> utf8)
        {
        }

        public readonly void List<T>(int field, ReadOnlySpan<T> value)
            where T : unmanaged
        {
            switch (field)
            {
                case LayoutField: record.LayoutValues = MemoryMarshal.Cast<T, int>(value).ToArray(); break;
                case BoolField: record.BoolValues = MemoryMarshal.Cast<T, bool>(value).ToArray(); break;
                case Int32Field: record.Int32Values = MemoryMarshal.Cast<T, int>(value).ToArray(); break;
                case Int64Field: record.Int64Values = MemoryMarshal.Cast<T, long>(value).ToArray(); break;
                case Float32Field: record.Float32Values = MemoryMarshal.Cast<T, float>(value).ToArray(); break;
                case Float64Field: record.Float64Values = MemoryMarshal.Cast<T, double>(value).ToArray(); break;
            }
        }

        public void StringListStart(int field, int count) => _strings = new List<string>(count);

        public readonly void StringListItem(int field, ReadOnlySpan<byte> utf8) => _strings!.Add(Encoding.UTF8.GetString(utf8));

        public readonly void StringListEnd(int field) => record.StringValues = _strings!.ToArray();
    }
}

/// <summary>
/// A service cache record's fields, wherever it was read from: a pinned local table version (<see cref="LocalServiceRecord"/>,
/// read in place) or a distributed read (<see cref="DecodedServiceRecord"/>, copied out). A list the record has no value
/// for is empty.
/// </summary>
internal interface IServiceRecord
{
    long WrittenMs { get; }

    ReadOnlySpan<int> Layout { get; }

    ReadOnlySpan<bool> Bools { get; }

    ReadOnlySpan<int> Int32s { get; }

    ReadOnlySpan<long> Int64s { get; }

    ReadOnlySpan<float> Float32s { get; }

    ReadOnlySpan<double> Float64s { get; }

    int StringCount { get; }

    string String(int index);
}

/// <summary>A record of a pinned version of the service's table, read in place: no copy, the version keeps the page alive.</summary>
internal readonly struct LocalServiceRecord(TableVersion version, LocalRecordRef record) : IServiceRecord
{
    public long WrittenMs => version.VersionOf(record);

    public ReadOnlySpan<int> Layout => version.List<int>(ServiceCache.LayoutField, record);

    public ReadOnlySpan<bool> Bools => version.List<bool>(ServiceCache.BoolField, record);

    public ReadOnlySpan<int> Int32s => version.List<int>(ServiceCache.Int32Field, record);

    public ReadOnlySpan<long> Int64s => version.List<long>(ServiceCache.Int64Field, record);

    public ReadOnlySpan<float> Float32s => version.List<float>(ServiceCache.Float32Field, record);

    public ReadOnlySpan<double> Float64s => version.List<double>(ServiceCache.Float64Field, record);

    public int StringCount => version.StringCodes(ServiceCache.StringField, record).Length;

    public string String(int index) => version.StringOf(version.StringCodes(ServiceCache.StringField, record)[index]);
}

/// <summary>A record a distributed read returned, copied out of the store's bytes (which the local write-back keeps).</summary>
internal sealed class DecodedServiceRecord(long writtenMs, ReadOnlyMemory<byte> bytes) : IServiceRecord
{
    public long WrittenMs { get; } = writtenMs;

    /// <summary>The record as the store returned it: what the memory tier is written from.</summary>
    public ReadOnlyMemory<byte> Bytes { get; } = bytes;

    internal int[] LayoutValues { get; set; } = [];

    internal bool[] BoolValues { get; set; } = [];

    internal int[] Int32Values { get; set; } = [];

    internal long[] Int64Values { get; set; } = [];

    internal float[] Float32Values { get; set; } = [];

    internal double[] Float64Values { get; set; } = [];

    internal string[] StringValues { get; set; } = [];

    public ReadOnlySpan<int> Layout => LayoutValues;

    public ReadOnlySpan<bool> Bools => BoolValues;

    public ReadOnlySpan<int> Int32s => Int32Values;

    public ReadOnlySpan<long> Int64s => Int64Values;

    public ReadOnlySpan<float> Float32s => Float32Values;

    public ReadOnlySpan<double> Float64s => Float64Values;

    public int StringCount => StringValues.Length;

    public string String(int index) => StringValues[index];
}

/// <summary>
/// Builds the records a call writes back, one after another, and hands them to the tiers: each record's lists are gathered
/// (<see cref="Layout"/> and the typed lists, in any order), then <see cref="End"/> encodes it. One per write-back; a few
/// arrays a batch, not one per record.
/// </summary>
internal sealed class ServiceRecordBuilder
{
    private readonly ServiceCache _cache;
    private readonly CacheRecordWriter _writer;
    private readonly long _writtenMs;
    private readonly List<(UInt128 Key, int Offset, int Length)> _records;

    public ServiceRecordBuilder(ServiceCache cache, int records, long writtenMs)
    {
        _cache = cache;
        _writtenMs = writtenMs;
        _writer = new CacheRecordWriter(cache.TableCache.Layout, Math.Max(1, records) * cache.TableCache.Layout.TypicalSize);
        _records = new List<(UInt128, int, int)>(records);
    }

    public List<int> Layout { get; } = new();

    public List<bool> Bools { get; } = new();

    public List<int> Int32s { get; } = new();

    public List<long> Int64s { get; } = new();

    public List<float> Float32s { get; } = new();

    public List<double> Float64s { get; } = new();

    public List<string> Strings { get; } = new();

    public int Count => _records.Count;

    /// <summary>Encodes the record gathered so far under <paramref name="key"/> and clears the lists for the next.</summary>
    public void End(UInt128 key)
    {
        _writer.Begin(_writtenMs, _cache.TableCache.Fingerprint, absent: false);
        Write(ServiceCache.LayoutField, Layout);
        Write(ServiceCache.BoolField, Bools);
        Write(ServiceCache.Int32Field, Int32s);
        Write(ServiceCache.Int64Field, Int64s);
        Write(ServiceCache.Float32Field, Float32s);
        Write(ServiceCache.Float64Field, Float64s);
        if (Strings.Count > 0)
        {
            _writer.StringList(ServiceCache.StringField, CollectionsMarshal.AsSpan(Strings));
        }

        var (offset, length) = _writer.End();
        _records.Add((key, offset, length));
        Clear();
    }

    /// <summary>Drops what was gathered for a record that will not be written.</summary>
    public void Clear()
    {
        Layout.Clear();
        Bools.Clear();
        Int32s.Clear();
        Int64s.Clear();
        Float32s.Clear();
        Float64s.Clear();
        Strings.Clear();
    }

    /// <summary>
    /// Hands the records to the tiers without waiting (<see cref="LocalTableCache.TryWriteBack"/>, and the distributed
    /// tier's queue with each record's time to live); a full queue drops them, counted.
    /// </summary>
    /// <param name="policy">The times the records are kept for; null for the cache's own (a service's binding's).</param>
    public void WriteBack(long nowMs, CachePolicy? policy = null)
    {
        var count = _records.Count;
        if (count == 0)
        {
            return;
        }

        var bytes = _writer.Bytes;
        var memory = _cache.TableCache.Memory;
        var keys = memory ? new EntityKey[2 * count] : []; // the in-process tier's, when the cache has one
        var records = new ReadOnlyMemory<byte>[count];
        for (var i = 0; i < count; i++)
        {
            var (key, offset, length) = _records[i];
            if (memory)
            {
                keys[2 * i] = EntityKey.FromInt64((long)(ulong)(key >> 64));
                keys[(2 * i) + 1] = EntityKey.FromInt64((long)(ulong)key);
            }

            records[i] = bytes.AsMemory(offset, length);
        }

        if (memory)
        {
            _cache.TableCache.TryWriteBack(new CacheWriteBatch(keys, records, count, partCount: 2));
        }

        if (_cache.Distributed is { } distributed)
        {
            var entries = new CacheStoreEntry[count];
            var ttl = (policy ?? _cache.Policy).StoreTimeToLive(_writtenMs, nowMs);
            for (var i = 0; i < count; i++)
            {
                entries[i] = new CacheStoreEntry(ServiceCache.KeyText(_records[i].Key), records[i], ttl);
            }

            distributed.TryWrite(_cache.TableCache, entries, count);
        }
    }

    private void Write<T>(int field, List<T> values)
        where T : unmanaged
    {
        if (values.Count > 0)
        {
            _writer.List<T>(field, CollectionsMarshal.AsSpan(values));
        }
    }
}
