using System.Diagnostics.Metrics;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Features.Cache;
using StrataRec.Features.Local;

namespace StrataRec.UnitTests.Features.Cache;

/// <summary>The in-process tier's write side (<see cref="LocalTableCache"/>) and the record codec (<see cref="CacheRecord"/>).</summary>
[Collection(MetricMeasurement.Name)]
public class CacheTableTests
{
    private static readonly LocalTableSchema Schema = new(
        "item_store", "item", EntityKeyType.Int64, LocalTableMode.Cache, [new("clicks", ColumnType.Float64), new("name", ColumnType.Text)]);

    [Fact]
    public void AWriteBackPastTheQueuesBytes_IsDropped_AndCounted_AndTheQueueTakesBatchesAgainOnceDrained()
    {
        var record = Batch(Table(maxBytes: 1 << 20).Cache, 0, 1).Bytes; // 34 B
        var (table, cache, clock) = Table(maxBytes: 1 << 20, writeQueueBytes: (record * 7) / 2); // room for three and a half
        using var dropped = new Counted(StrataRecMetrics.Names.CacheWrites, "dropped");

        Assert.True(cache.TryWriteBack(Batch(cache, clock.NowMs, 1, 2)));
        Assert.True(cache.TryWriteBack(Batch(cache, clock.NowMs, 3)));
        Assert.False(cache.TryWriteBack(Batch(cache, clock.NowMs, 4, 5))); // five would wait
        Assert.Equal(2, dropped.Total);
        Assert.Equal(3 * record, cache.QueuedBytes);

        Assert.Equal(2, cache.WriteQueued(table.Writer));
        Assert.Equal(0, cache.QueuedBytes);
        Assert.True(cache.TryWriteBack(Batch(cache, clock.NowMs, 4, 5)));
        cache.WriteQueued(table.Writer);
        Assert.Equal(5, table.Pin().RecordCount);
    }

    [Fact]
    public void AWriteThatDoesNotFit_DropsExpiredRecordsFirst_ThenTheOldestWritten_AndTheTableStaysWithinMaxBytes()
    {
        const long MaxBytes = 256 << 10;
        var (table, cache, clock) = Table(maxBytes: MaxBytes, ttlMs: 1_000, staleIfErrorMs: 1_000);
        using var expired = new Counted(StrataRecMetrics.Names.CacheEvictions, "expired");
        using var oldest = new Counted(StrataRecMetrics.Names.CacheEvictions, "oldest");

        // Records of about 4 KiB, written 10 ms apart: the first 20 expire (and pass their stale window) by the end.
        var key = 0L;
        for (; key < 20; key++)
        {
            Write(table, cache, clock.NowMs, key);
            clock.AdvanceMs(10);
        }

        clock.AdvanceMs(3_000);
        for (; table.Pin().RecordCount == key || expired.Total == 0; key++)
        {
            Write(table, cache, clock.NowMs, key);
            clock.AdvanceMs(10);
            Assert.True(table.Pin().Bytes <= MaxBytes);
        }

        var version = table.Pin();
        Assert.Equal(20, expired.Total);
        Assert.Equal(0, oldest.Total); // the expired ones made room
        Assert.All(Enumerable.Range(0, 20), k => Assert.False(version.Find(k).Found));
        Assert.True(version.Find(key - 1).Found);

        // Now nothing has expired: a write that does not fit drops the oldest written down to three quarters.
        var first = key;
        while (oldest.Total == 0)
        {
            Write(table, cache, clock.NowMs, key++);
            clock.AdvanceMs(1);
            Assert.True(table.Pin().Bytes <= MaxBytes);
        }

        version = table.Pin();
        Assert.True(version.Bytes <= MaxBytes * 3 / 4 + (8 << 10), $"{version.Bytes} B");
        Assert.False(version.Find(20L).Found); // the oldest
        Assert.True(version.Find(key - 1).Found); // the newest
        Assert.True(version.Find(first).Found || oldest.Total > first - 20);
    }

    [Fact]
    public void ARecordOfAnotherFieldSet_OrNotARecord_IsNotWritten()
    {
        var (table, cache, clock) = Table(maxBytes: 1 << 20);
        var other = new CacheRecordLayout(new LocalTableSchema("item_store", "item", EntityKeyType.Int64, LocalTableMode.Cache, [new("clicks", ColumnType.Float64)]));
        var writer = new CacheRecordWriter(other, 64);
        writer.Begin(clock.NowMs, other.Fingerprint, absent: false);
        writer.Float64(0, 1);
        var (offset, length) = writer.End();
        using var failed = new Counted(StrataRecMetrics.Names.CacheWrites, "failed");

        cache.TryWriteBack(new CacheWriteBatch([EntityKey.FromInt64(1), EntityKey.FromInt64(2)], [writer.Bytes.AsMemory(offset, length), new byte[] { 1, 2, 3 }], 2));
        cache.WriteQueued(table.Writer);

        Assert.Equal(0, table.Pin().RecordCount);
        Assert.Equal(2, failed.Total);
    }

    [Fact]
    public void TheCodec_RoundTripsEveryType_AndChecksItsHeader()
    {
        var schema = new LocalTableSchema("s", "e", EntityKeyType.String, LocalTableMode.Cache,
        [
            new("b", ColumnType.Bool), new("f32", ColumnType.Float32), new("f64", ColumnType.Float64), new("i32", ColumnType.Int32),
            new("i64", ColumnType.Int64), new("li32", ColumnType.ListInt32), new("li64", ColumnType.ListInt64), new("ls", ColumnType.ListString),
            new("s", ColumnType.String), new("t", ColumnType.Text), new("v", ColumnType.Vector(3)),
        ]);
        var layout = new CacheRecordLayout(schema);
        var writer = new CacheRecordWriter(layout, 8);
        writer.Begin(123_456, layout.Fingerprint, absent: false);
        writer.Bool(0, true);
        writer.Float32(1, float.NaN);
        writer.Int32(3, int.MinValue);
        writer.Int64(4, long.MaxValue);
        writer.Int32List(5, [1, -1]);
        writer.Int64List(6, []);
        writer.StringList(7, ["", "zé", "✓"]);
        writer.String(8, "music");
        writer.String(9, "");
        writer.Vector(10, [1, 2, 3]);
        var (offset, length) = writer.End();
        var record = writer.Bytes.AsSpan(offset, length);

        Assert.Equal(CacheRecordHeader.Ok, CacheRecord.ReadHeader(record, layout.FieldCount, layout.Fingerprint, out var writtenMs, out var absent));
        Assert.Equal((123_456L, false), (writtenMs, absent));
        Assert.True(CacheRecord.IsWellFormed(record, layout));
        var visitor = new Describe();
        Assert.True(CacheRecord.Read(record, layout, ref visitor));
        Assert.Equal("b=True f32=NaN f64=- i32=-2147483648 i64=9223372036854775807 li32=[1,-1] li64=[] ls=[,zé,✓] s=music t= v=[1,2,3]", visitor.Text);

        Assert.Equal(CacheRecordHeader.Mismatch, CacheRecord.ReadHeader(record, layout.FieldCount, layout.Fingerprint + 1, out _, out _));
        Assert.Equal(CacheRecordHeader.Mismatch, CacheRecord.ReadHeader(record, layout.FieldCount - 1, layout.Fingerprint, out _, out _));
        var otherFormat = record.ToArray();
        otherFormat[0] = 2;
        Assert.Equal(CacheRecordHeader.Mismatch, CacheRecord.ReadHeader(otherFormat, layout.FieldCount, layout.Fingerprint, out _, out _));
        Assert.Equal(CacheRecordHeader.Corrupt, CacheRecord.ReadHeader(record[..10], layout.FieldCount, layout.Fingerprint, out _, out _));
        Assert.False(CacheRecord.IsWellFormed(record[..^1], layout));
        Assert.False(CacheRecord.IsWellFormed([.. record, 0], layout));
    }

    [Fact]
    public void TheFingerprint_CoversTheKeyType_TheFieldNames_AndTheirTypes()
    {
        var fingerprints = new[]
        {
            Of(EntityKeyType.Int64, ("a", ColumnType.Float64)),
            Of(EntityKeyType.String, ("a", ColumnType.Float64)),
            Of(EntityKeyType.Int64, ("b", ColumnType.Float64)),
            Of(EntityKeyType.Int64, ("a", ColumnType.Float32)),
            Of(EntityKeyType.Int64, ("a", ColumnType.Float64), ("b", ColumnType.Float64)),
        };

        Assert.Equal(fingerprints.Length, fingerprints.Distinct().Count());
        Assert.Equal(fingerprints[0], Of(EntityKeyType.Int64, ("a", ColumnType.Float64)));

        static ulong Of(EntityKeyType keyType, params (string Name, ColumnType Type)[] fields) =>
            CacheRecord.Fingerprint(new LocalTableSchema("s", "e", keyType, LocalTableMode.Cache, fields.Select(f => new LocalTableField(f.Name, f.Type)).ToList()));
    }

    [Fact]
    public void Decoding_NeverThrows_OnBytesThatAreNotARecord()
    {
        var layout = new CacheRecordLayout(new LocalTableSchema("s", "e", EntityKeyType.Int64, LocalTableMode.Cache,
            [new("ls", ColumnType.ListString), new("t", ColumnType.Text), new("v", ColumnType.Vector(2)), new("li64", ColumnType.ListInt64)]));
        var random = new Random(7);
        for (var i = 0; i < 20_000; i++)
        {
            var bytes = new byte[random.Next(0, 64)];
            random.NextBytes(bytes);
            if (bytes.Length >= CacheRecord.PrefixSize(layout.FieldCount))
            {
                bytes[0] = CacheRecord.Format;
                BitConverter.TryWriteBytes(bytes.AsSpan(2), (ushort)layout.FieldCount);
                BitConverter.TryWriteBytes(bytes.AsSpan(12), layout.Fingerprint);
            }

            if (CacheRecord.ReadHeader(bytes, layout.FieldCount, layout.Fingerprint, out _, out _) == CacheRecordHeader.Ok)
            {
                _ = CacheRecord.IsWellFormed(bytes, layout);
            }
        }
    }

    private static (LocalFeatureTable Table, LocalTableCache Cache, ManualClock Clock) Table(
        long maxBytes, long writeQueueBytes = 0, long ttlMs = 60_000, long staleIfErrorMs = 0)
    {
        var clock = new ManualClock();
        var table = new LocalFeatureTable(Schema, new LocalFeatureTableOptions
        {
            MaxBytes = maxBytes,
            PageSize = 16 << 10,
            CompactionMinBytes = 0,
            Cache = new CacheTableOptions { TtlMs = ttlMs, StaleIfErrorMs = staleIfErrorMs, WriteQueueBytes = writeQueueBytes, Clock = clock },
        });
        return (table, table.Cache!, clock);
    }

    private static CacheWriteBatch Batch(LocalTableCache cache, long writtenMs, params long[] keys) => Batch(cache, writtenMs, "x", keys);

    private static CacheWriteBatch Batch(LocalTableCache cache, long writtenMs, string name, params long[] keys)
    {
        var writer = new CacheRecordWriter(cache.Layout, 64);
        var spans = new (int Offset, int Length)[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            writer.Begin(writtenMs, cache.Fingerprint, absent: false);
            writer.Float64(0, keys[i]);
            writer.String(1, name);
            spans[i] = writer.End();
        }

        return new CacheWriteBatch(
            keys.Select(EntityKey.FromInt64).ToArray(), spans.Select(s => (ReadOnlyMemory<byte>)writer.Bytes.AsMemory(s.Offset, s.Length)).ToArray(), keys.Length);
    }

    private static void Write(LocalFeatureTable table, LocalTableCache cache, long writtenMs, long key)
    {
        Assert.True(cache.TryWriteBack(Batch(cache, writtenMs, new string('n', 2_000), key)));
        cache.WriteQueued(table.Writer);
    }

    /// <summary>Sums a counter's measurements with one <c>stratarec.outcome</c> while alive.</summary>
    private sealed class Counted : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _total;

        public Counted(string instrument, string outcome)
        {
            _listener.InstrumentPublished = (published, listener) =>
            {
                if (published.Meter.Name == StrataRecTelemetry.Name && published.Name == instrument)
                {
                    listener.EnableMeasurementEvents(published);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == StrataRecTags.Outcome && Equals(tag.Value, outcome))
                    {
                        Interlocked.Add(ref _total, value);
                    }
                }
            });
            _listener.Start();
        }

        public long Total => Interlocked.Read(ref _total);

        public void Dispose() => _listener.Dispose();
    }

    private struct Describe : ICacheRecordVisitor
    {
        private static readonly string[] Names = ["b", "f32", "f64", "i32", "i64", "li32", "li64", "ls", "s", "t", "v"];
        private List<string>? _items;

        public string Text { get; private set; }

        public void Missing(int field) => Add(field, "-");

        public void Bool(int field, bool value) => Add(field, value.ToString());

        public void Int32(int field, int value) => Add(field, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public void Int64(int field, long value) => Add(field, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public void Float32(int field, float value) => Add(field, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public void Float64(int field, double value) => Add(field, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public void Vector(int field, ReadOnlySpan<float> value) => Add(field, "[" + string.Join(",", value.ToArray()) + "]");

        public void String(int field, ReadOnlySpan<byte> utf8) => Add(field, System.Text.Encoding.UTF8.GetString(utf8));

        public void Int32List(int field, ReadOnlySpan<int> value) => Add(field, "[" + string.Join(",", value.ToArray()) + "]");

        public void Int64List(int field, ReadOnlySpan<long> value) => Add(field, "[" + string.Join(",", value.ToArray()) + "]");

        public void StringListStart(int field, int count) => _items = new List<string>();

        public void StringListItem(int field, ReadOnlySpan<byte> utf8) => _items!.Add(System.Text.Encoding.UTF8.GetString(utf8));

        public void StringListEnd(int field) => Add(field, "[" + string.Join(",", _items!) + "]");

        private void Add(int field, string value) => Text = (Text is null ? string.Empty : Text + " ") + Names[field] + "=" + value;
    }
}

/// <summary>Tests that count the process-wide StrataRec metrics run alone, so other tests' measurements do not reach their listeners.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MetricMeasurement
{
    public const string Name = "Metric measurement";
}
