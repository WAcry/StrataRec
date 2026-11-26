using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
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
        "item_store", "item", EntityKeyType.Int64, LocalTableMode.Cache, [new("clicks", ColumnType.Float64), new("name", ColumnType.String)]);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void VersionsSharingDensePages_KeepThePinnedValuesAfterLaterWrites(bool stringKeys, bool variableWidth)
    {
        var clock = new ManualClock();
        var schema = new LocalTableSchema("s", "e", stringKeys ? EntityKeyType.String : EntityKeyType.Int64, LocalTableMode.Cache,
            [new("v", ColumnType.List(StorageKind.Float32))]);
        var table = new LocalFeatureTable(schema, new LocalFeatureTableOptions
        {
            MaxBytes = 256 << 10, PageSize = 4096, Cache = new CacheTableOptions { TtlMs = 60_000, Clock = clock },
        });
        var cache = table.Cache!;
        var writer = new CacheRecordWriter(cache.Layout, 512);
        float[][] vectors = [new float[4], new float[16], new float[64], new float[192]];
        var pinned = table.Pin();
        var key = 0;
        while (ReferenceEquals(table.Pin(), pinned) && key < 2_000)
        {
            Write(key, key);
            key++;
        }

        var first = table.Pin();
        Assert.NotSame(pinned, first);
        var nextKey = key;
        while (ReferenceEquals(first, table.Pin()) && nextKey < key * 2)
        {
            Write(nextKey, nextKey);
            nextKey++;
        }

        // The hand crosses both copied and shared pages. With no hits, the original cold suffix stays in FIFO order.
        var second = table.Pin();
        Assert.NotSame(first, second);
        var oldest = Enumerable.Range(0, key).First(k => second.Find(Key(k)).Found);
        for (var k = 0; k < key; k++)
        {
            Assert.Equal(k >= oldest, second.Find(Key(k)).Found);
        }

        var shared = 0;
        for (var k = 0; k < key - 1; k++)
        {
            var before = pinned.Find(Key(k));
            var after = first.Find(Key(k));
            if (after.Found && ReferenceEquals(before.Page, after.Page))
            {
                shared++;
                Write(k, -k - 1);
            }
        }

        Assert.True(shared > 0);
        for (var k = 0; k < key - 1; k++)
        {
            Assert.Equal(k, pinned.Read(0, pinned.Find(Key(k))).AsFloat64List()[0]);
        }

        EntityKey Key(int k) => stringKeys ? EntityKey.FromString($"item-{k}") : EntityKey.FromInt64(k);
        void Write(int k, int marker)
        {
            var value = variableWidth ? vectors[unchecked((uint)k * 2654435761u) >> 30] : vectors[2];
            value[0] = marker;
            writer.Begin(clock.NowMs, cache.Fingerprint, absent: false);
            writer.List<float>(0, value);
            var (offset, length) = writer.End();
            Assert.True(cache.TryWriteBack(new CacheWriteBatch([Key(k)], [writer.Bytes.AsMemory(offset, length)], 1)));
            cache.WriteQueued(table.Writer);
            clock.AdvanceMs(1);
        }
    }

    [Fact]
    public void AWriteBackPastTheQueuesBytes_IsDropped_AndCounted_AndTheQueueTakesBatchesAgainOnceDrained()
    {
        var record = Batch(Table(maxBytes: 1 << 20).Cache, 0, 1).Bytes; // 66 B: a 34-byte record, its key and its slots
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

    /// <summary>
    /// A write-back is charged what it holds while it waits, its keys' strings with its records.
    /// A 10,000-char key with an 18-byte record held 20 KB against a queue that counted 18 B, in either tier. Each queue
    /// holds a small batch here, since an empty one takes even a batch over its limit.
    /// </summary>
    [Fact]
    public async Task AWriteBack_IsChargedItsKeys_NotOnlyItsRecords_InEitherTier()
    {
        var schema = new LocalTableSchema("s", "item", EntityKeyType.String, LocalTableMode.Cache, [new("clicks", ColumnType.Float64)]);
        var table = new LocalFeatureTable(schema, new LocalFeatureTableOptions
        {
            MaxBytes = 1 << 20,
            PageSize = 16 << 10,
            CompactionMinBytes = 0,
            Cache = new CacheTableOptions { TtlMs = 60_000, WriteQueueBytes = 4096, Clock = new ManualClock() },
        });
        var cache = table.Cache!;
        var writer = new CacheRecordWriter(cache.Layout, 64);
        writer.Begin(0, cache.Fingerprint, absent: false);
        writer.Float64(0, 1);
        var (offset, length) = writer.End();
        var record = writer.Bytes.AsMemory(offset, length);
        var key = new string('k', 10_000);
        using var dropped = new Counted(StrataRecMetrics.Names.CacheWrites, "dropped");

        var batch = new CacheWriteBatch([EntityKey.FromString(key)], [record], 1);
        Assert.InRange(batch.Bytes, 20_000, 20_100);
        Assert.True(cache.TryWriteBack(new CacheWriteBatch([EntityKey.FromString("k")], [record], 1)));
        Assert.False(cache.TryWriteBack(batch));

        var store = new HeldStore();
        await using var tier = new DistributedCacheTier(store, queueBytes: 4096);
        Assert.True(tier.TryWrite(cache, [new CacheStoreEntry("k", record, TimeSpan.FromMinutes(1))], 1)); // being written
        await store.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(tier.TryWrite(cache, [new CacheStoreEntry("k", record, TimeSpan.FromMinutes(1))], 1)); // waiting
        Assert.False(tier.TryWrite(cache, [new CacheStoreEntry(key, record, TimeSpan.FromMinutes(1))], 1));
        Assert.Equal(2, dropped.Total);
        store.Release();
    }

    /// <summary>
    /// A key part several keys share, the same string object (a request input's or a context feature's part
    /// of a composite key), is one string held, charged once. 4,096 candidates sharing a 128-char part were charged that
    /// string 4,096 times, about 1.44 MiB, and an empty 1 MiB queue dropped the batch. An equal string that is another
    /// object for each key is held, and charged, once per key.
    /// </summary>
    [Fact]
    public void AKeyPartTheKeysShare_IsChargedOnce()
    {
        const int Count = 4096;
        var cache = Table(maxBytes: 16 << 20).Cache;
        var writer = new CacheRecordWriter(cache.Layout, 64);
        writer.Begin(0, cache.Fingerprint, absent: false);
        writer.Float64(0, 1);
        var (offset, length) = writer.End();
        var records = Enumerable.Repeat((ReadOnlyMemory<byte>)writer.Bytes.AsMemory(offset, length), Count).ToArray();
        var shared = new string('l', 128);

        var sharing = new CacheWriteBatch(Keys(_ => shared), records, Count, partCount: 2);
        var distinct = new CacheWriteBatch(Keys(_ => new string('l', 128)), records, Count, partCount: 2);

        var slots = (2L * Count * Unsafe.SizeOf<EntityKey>()) + (Count * Unsafe.SizeOf<ReadOnlyMemory<byte>>()) + ((long)Count * length);
        Assert.Equal(slots + TableVersion.StringObjectBytes(128), sharing.Bytes);
        Assert.Equal(slots + (Count * TableVersion.StringObjectBytes(128)), distinct.Bytes);
        Assert.True(sharing.Bytes < 512 << 10);

        static EntityKey[] Keys(Func<int, string> part) =>
            Enumerable.Range(0, Count).SelectMany(i => new[] { EntityKey.FromInt64(i), EntityKey.FromString(part(i)) }).ToArray();
    }

    /// <summary>
    /// One request's misses make one write-back, whatever max_batch split its calls into, so a batch larger
    /// than the whole queue limit was dropped by an idle queue on every request. An empty queue takes it (as the log
    /// pipeline takes a batch larger than its budget); one that holds some takes nothing over its limit, so it holds at
    /// most that one batch over it.
    /// </summary>
    [Fact]
    public void AWriteBackLargerThanTheQueue_GoesIntoAnEmptyQueue_AndNotIntoOneThatHoldsSome()
    {
        var (table, cache, clock) = Table(maxBytes: 1 << 20, writeQueueBytes: 4096);
        var large = Batch(cache, clock.NowMs, new string('n', 5_000), 1);
        Assert.True(large.Bytes > 4096);
        using var dropped = new Counted(StrataRecMetrics.Names.CacheWrites, "dropped");

        Assert.True(cache.TryWriteBack(large));
        Assert.Equal(large.Bytes, cache.QueuedBytes);
        Assert.False(cache.TryWriteBack(Batch(cache, clock.NowMs, 2))); // the queue is past its limit
        Assert.Equal(1, cache.WriteQueued(table.Writer));

        Assert.True(cache.TryWriteBack(Batch(cache, clock.NowMs, 2)));
        Assert.False(cache.TryWriteBack(Batch(cache, clock.NowMs, new string('n', 5_000), 3))); // the queue holds some
        Assert.Equal(1, cache.WriteQueued(table.Writer));
        Assert.Equal(0, cache.QueuedBytes);
        Assert.Equal(2, dropped.Total);
        Assert.Equal(2, table.Pin().RecordCount);
    }

    [Theory]
    [InlineData(100L)]
    [InlineData(1L)] // eviction drops the old record of the very key being replaced
    public void Eviction_CountsASharedStringAsFreed_OnlyWhenItsLastRecordGoes(long writtenKey)
    {
        // Ten records share one 25,000-char string. Each record's share of it was counted as freed, so the
        // eviction for a 10,000-char write-back dropped one record, the shared string stayed whole, and the write-back
        // still did not fit and was dropped.
        var schema = new LocalTableSchema("s", "item", EntityKeyType.Int64, LocalTableMode.Cache, [new("name", ColumnType.String)]);
        var clock = new StrataRec.UnitTests.Features.ManualClock();
        var table = new LocalFeatureTable(schema, new LocalFeatureTableOptions
        {
            MaxBytes = 64 << 10,
            PageSize = 16 << 10,
            CompactionMinBytes = 0,
            Cache = new CacheTableOptions { TtlMs = 60_000, Clock = clock },
        });
        var shared = new string('s', 25_000);
        for (long key = 1; key <= 10; key++)
        {
            var b = table.Writer.Begin(EntityKey.FromInt64(key), clock.NowMs + key);
            b.SetString(0, shared);
            Assert.True(b.Commit());
        }

        var cache = table.Cache!;
        var codec = new CacheRecordWriter(cache.Layout, 64);
        codec.Begin(clock.NowMs + 100, cache.Fingerprint, absent: false);
        var replacement = new string('n', 10_000);
        codec.String(0, replacement);
        var (offset, length) = codec.End();
        Assert.True(cache.TryWriteBack(new CacheWriteBatch([EntityKey.FromInt64(writtenKey)], [codec.Bytes.AsMemory(offset, length)], 1)));
        cache.WriteQueued(table.Writer);

        var version = table.Pin();
        Assert.Equal(replacement, version.Read(0, version.Find(writtenKey)).AsString());
        Assert.True(version.Bytes <= 64 << 10);
    }

    [Fact]
    public void AddingAKeyAtTheIndexGrowthBoundary_PreservesMostSmallRecords()
    {
        const int Keys = 1024;
        const int MaxBytes = 64 << 10;
        var clock = new ManualClock();
        var schema = new LocalTableSchema("s", "item", EntityKeyType.Int64, LocalTableMode.Cache, [new("score", ColumnType.Float64)]);
        var table = new LocalFeatureTable(schema, new LocalFeatureTableOptions
        {
            MaxBytes = MaxBytes, Cache = new CacheTableOptions { TtlMs = 60_000, Clock = clock },
        });
        for (long key = 0; key < Keys; key++)
        {
            var builder = table.Writer.Begin(key, clock.NowMs);
            builder.SetFloat64(0, key);
            Assert.True(builder.Commit());
        }

        var cache = table.Cache!;
        var codec = new CacheRecordWriter(cache.Layout, 32);
        codec.Begin(clock.NowMs, cache.Fingerprint, absent: false);
        codec.Float64(0, Keys);
        var (offset, length) = codec.End();
        Assert.True(cache.TryWriteBack(new CacheWriteBatch([EntityKey.FromInt64(Keys)], [codec.Bytes.AsMemory(offset, length)], 1)));
        cache.WriteQueued(table.Writer);

        var version = table.Pin();
        Assert.True(version.Find((long)Keys).Found);
        Assert.InRange(version.RecordCount, Keys * 2 / 3, Keys);
        Assert.True(version.Bytes <= MaxBytes);
    }

    [Fact]
    public void Eviction_PreservesMostOfAnEquallySizedBatchFetchedAtOneTime()
    {
        const long MaxBytes = 256 << 10;
        var (table, cache, clock) = Table(maxBytes: MaxBytes);
        using var oldest = new Counted(StrataRecMetrics.Names.CacheEvictions, "oldest");
        long key = 0;
        while (oldest.Total == 0 && key < 100)
        {
            Write(table, cache, clock.NowMs, key++);
        }

        var version = table.Pin();
        Assert.InRange(oldest.Total, 1, key / 2); // freeing a quarter of the table does not cost its whole batch
        Assert.Equal(key - oldest.Total, version.RecordCount);
        Assert.True(version.Find(key - 1).Found);
        Assert.True(version.Bytes <= MaxBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Eviction_KeepsAHitUntilItHelpsTheRecordSurvive(bool reclaimExpired)
    {
        var (table, cache, clock) = Table(maxBytes: 256 << 10, ttlMs: 1_000);
        for (long k = 0; k < 20; k++)
        {
            Write(table, cache, clock.NowMs, k);
        }

        if (reclaimExpired)
        {
            clock.AdvanceMs(2_000);
        }

        // Older records make room first. Once eviction reaches this pair, the hit should protect 20 over 21.
        Write(table, cache, clock.NowMs, 20);
        table.Pin().Find(20L).MarkCacheHit();
        long key = 21;
        do
        {
            clock.AdvanceMs(1);
            Write(table, cache, clock.NowMs, key++);
        }
        while (table.Pin().Find(21L).Found && key < 200);

        Assert.False(table.Pin().Find(21L).Found);
        Assert.True(table.Pin().Find(20L).Found);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Eviction_ResumesAtTheNextInsertionAfterCompaction(bool refreshNext)
    {
        var (table, cache, clock) = Table(maxBytes: 256 << 10);
        var deletedAt = clock.NowMs;
        Assert.True(table.Writer.Delete(-1L, deletedAt));
        Write(table, cache, clock.NowMs, 0);
        table.Pin().Find(0L).MarkCacheHit();
        long key = 1;
        while (table.Compactions == 0 && key < 100)
        {
            Write(table, cache, clock.NowMs, key++); // every insertion has the same fetch timestamp
        }

        var first = table.Pin();
        Assert.True(first.Find(0L).Found);
        var next = Enumerable.Range(1, (int)key - 1).First(k => first.Find((long)k).Found);
        if (refreshNext)
        {
            clock.AdvanceMs(1);
            Write(table, cache, clock.NowMs, next); // the replacement is now the newest insertion
        }

        table.Writer.Compact();
        var before = table.Pin();
        while (ReferenceEquals(before, table.Pin()) && key < 200)
        {
            Write(table, cache, clock.NowMs, key++);
        }

        var after = table.Pin();
        Assert.NotSame(before, after);
        Assert.True(after.Find(0L).Found); // the hand already gave this key its chance on the first sweep
        Assert.Equal(refreshNext, after.Find((long)next).Found);
        Assert.False(after.Find((long)next + 1).Found);
        Assert.True(before.Find((long)next + 1).Found);
        Write(table, cache, deletedAt, -1); // pressure must preserve the deletion watermark
        Assert.False(table.Pin().Find(-1L).Found);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(50)]
    public void AWriteThatDoesNotFit_DropsExpiredRecordsFirst_ThenColdInsertions_AndStaysWithinMaxBytes(long zeroAt)
    {
        const long MaxBytes = 256 << 10;
        var (table, cache, clock) = Table(maxBytes: MaxBytes, ttlMs: 1_000, staleIfErrorMs: 1_000);
        using var expired = new Counted(StrataRecMetrics.Names.CacheEvictions, "expired");
        using var oldest = new Counted(StrataRecMetrics.Names.CacheEvictions, "oldest");

        // Records of about 4 KiB, written 10 ms apart: the first 20 expire (and pass their stale window) by the end.
        var key = 0L;
        for (; key < 20; key++)
        {
            Write(table, cache, clock.NowMs, Id(key));
            clock.AdvanceMs(10);
        }

        var bytesPerRecord = table.Pin().RecordBytes / key; // variable-length names are dictionary entries
        clock.AdvanceMs(3_000);
        for (; key < 120 && (table.Pin().RecordCount == key || expired.Total == 0); key++)
        {
            Write(table, cache, clock.NowMs, Id(key));
            clock.AdvanceMs(10);
            Assert.True(table.Pin().Bytes <= MaxBytes);
        }

        var version = table.Pin();
        Assert.Equal(20, expired.Total);
        Assert.Equal(0, oldest.Total); // the expired ones made room
        Assert.Equal(bytesPerRecord * version.RecordCount, version.RecordBytes);
        Assert.All(Enumerable.Range(0, 20), k => Assert.False(version.Find(Id(k)).Found));
        Assert.True(version.Find(Id(key - 1)).Found);

        // The hand has not moved yet: the oldest cold insertions now leave first.
        var first = key;
        while (oldest.Total == 0 && key < first + 120)
        {
            Write(table, cache, clock.NowMs, Id(key++));
            clock.AdvanceMs(1);
            Assert.True(table.Pin().Bytes <= MaxBytes);
        }

        version = table.Pin();
        Assert.True(oldest.Total > 0);
        Assert.Equal(bytesPerRecord * version.RecordCount, version.RecordBytes);
        Assert.True(version.Bytes <= MaxBytes * 3 / 4 + (8 << 10), $"{version.Bytes} B");
        for (long k = 20; k < key; k++)
        {
            Assert.Equal(k >= 20 + oldest.Total, version.Find(Id(k)).Found);
        }

        long Id(long k) => k == zeroAt ? 0 : k + 1;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacingACachedValue_MakesRoomFromItsOldValueAndSupersededDataFirst(bool superseded)
    {
        var (table, cache, clock) = Table(maxBytes: 256 << 10);
        for (var key = 0; key < 38; key++)
        {
            Write(table, cache, clock.NowMs, key);
        }

        clock.AdvanceMs(1);
        if (superseded)
        {
            for (var key = 0; key < 11; key++)
            {
                Set(key, key + new string('u', 2_000));
            }
        }

        Set(100, new string('x', 20_000));
        Assert.Equal(39, table.Pin().RecordCount);
        clock.AdvanceMs(1);
        using var evicted = new Counted(StrataRecMetrics.Names.CacheEvictions, "oldest");
        var replacement = new string('y', 34_000);
        Set(100, replacement);

        var version = table.Pin();
        Assert.Equal(replacement, version.Read(1, version.Find(100L)).AsString());
        Assert.Equal(39, version.RecordCount); // all the other fresh keys still fit alongside the replacement
        Assert.Equal(0, evicted.Total);

        void Set(long key, string value)
        {
            Assert.True(cache.TryWriteBack(Batch(cache, clock.NowMs, value, key)));
            cache.WriteQueued(table.Writer);
        }
    }

    [Theory]
    [InlineData(200_000)] // the string alone exceeds the budget
    [InlineData(130_900)] // the string fits, but not with even an empty table's overhead
    public void AWriteBackThatCannotFitInTheTable_KeepsItsExistingRecords(int length)
    {
        var (table, cache, clock) = Table(maxBytes: 256 << 10);
        for (var key = 0; key < 58; key++)
        {
            Write(table, cache, clock.NowMs, key);
            clock.AdvanceMs(1);
        }

        using var failed = new Counted(StrataRecMetrics.Names.CacheWrites, "failed");
        using var oldest = new Counted(StrataRecMetrics.Names.CacheEvictions, "oldest");
        var before = table.Pin();
        Assert.True(cache.TryWriteBack(Batch(cache, clock.NowMs, new string('x', length), 1000)));
        cache.WriteQueued(table.Writer);

        Assert.Same(before, table.Pin());
        Assert.Equal(58, table.Pin().RecordCount);
        Assert.False(table.Pin().Find(1000L).Found);
        Assert.Equal(1, failed.Total);
        Assert.Equal(0, oldest.Total);
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

        var truncated = Batch(cache, clock.NowMs, "title", 3).Records[0][..^1]; // valid first field, incomplete second field
        cache.TryWriteBack(new CacheWriteBatch(
            [EntityKey.FromInt64(1), EntityKey.FromInt64(2), EntityKey.FromInt64(3)],
            [writer.Bytes.AsMemory(offset, length), new byte[] { 1, 2, 3 }, truncated], 3));
        cache.WriteQueued(table.Writer);

        Assert.Equal(0, table.Pin().RecordCount);
        Assert.Equal(3, failed.Total);
    }

    [Fact]
    public void TheCodec_RoundTripsEveryType_AndChecksItsHeader()
    {
        var schema = new LocalTableSchema("s", "e", EntityKeyType.String, LocalTableMode.Cache,
        [
            new("b", ColumnType.Bool), new("f32", ColumnType.Float32), new("f64", ColumnType.Float64), new("i32", ColumnType.Int32),
            new("i64", ColumnType.Int64), new("li32", ColumnType.List(StorageKind.Int32)), new("li64", ColumnType.List(StorageKind.Int64)), new("ls", ColumnType.List(StorageKind.String)),
            new("s", ColumnType.String), new("t", ColumnType.String), new("v", ColumnType.List(StorageKind.Float32, fixedLength: 3)),
        ]);
        var layout = new CacheRecordLayout(schema);
        var writer = new CacheRecordWriter(layout, 8);
        writer.Begin(123_456, layout.Fingerprint, absent: false);
        writer.Bool(0, true);
        writer.Float32(1, float.NaN);
        writer.Int32(3, int.MinValue);
        writer.Int64(4, long.MaxValue);
        writer.List<int>(5, [1, -1]);
        writer.List<long>(6, []);
        writer.StringList(7, ["", "zé", "✓"]);
        writer.String(8, "music");
        writer.String(9, "");
        writer.List<float>(10, [1, 2, 3]); // a fixed-length list is encoded as every list is
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
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(otherFormat, 2026010101); // 2026-01-01.1
        Assert.Equal(CacheRecordHeader.Mismatch, CacheRecord.ReadHeader(otherFormat, layout.FieldCount, layout.Fingerprint, out _, out _));
        // 2026-11-14.1 encoded a vector as N float32s without a count: such a record is another format, not this one's list.
        Assert.Equal(2026111601u, CacheRecord.Format);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(otherFormat, 2026111401);
        Assert.Equal(CacheRecordHeader.Mismatch, CacheRecord.ReadHeader(otherFormat, layout.FieldCount, layout.Fingerprint, out _, out _));
        Assert.Equal(CacheRecordHeader.Corrupt, CacheRecord.ReadHeader(record[..10], layout.FieldCount, layout.Fingerprint, out _, out _));
        Assert.False(CacheRecord.IsWellFormed(record[..^1], layout));
        Assert.False(CacheRecord.IsWellFormed([.. record, 0], layout));
    }

    [Fact]
    public void TheCodec_RoundTripsListsOfEveryElementType_AndRefusesABoolByteItDidNotWrite()
    {
        var schema = new LocalTableSchema("s", "e", EntityKeyType.Int64, LocalTableMode.Cache,
        [
            new("lb", ColumnType.List(StorageKind.Bool)), new("lf32", ColumnType.List(StorageKind.Float32)),
            new("lf64", ColumnType.List(StorageKind.Float64)), new("ls", ColumnType.List(StorageKind.String)),
        ]);
        var layout = new CacheRecordLayout(schema);
        var writer = new CacheRecordWriter(layout, 8);
        writer.Begin(1, layout.Fingerprint, absent: false);
        writer.List<bool>(0, [true, false, true]);
        writer.List<float>(1, [0.5f, float.NaN]);
        writer.List<double>(2, [0.1, -2]);
        writer.StringList(3, ["a"]);
        var (offset, length) = writer.End();
        var record = writer.Bytes.AsSpan(offset, length).ToArray();

        var visitor = new Describe(["lb", "lf32", "lf64", "ls"]);
        Assert.True(CacheRecord.Read(record, layout, ref visitor));
        Assert.Equal("lb=[True,False,True] lf32=[0.5,NaN] lf64=[0.1,-2] ls=[a]", visitor.Text);

        // The first bool is the byte after the record's prefix and the list's count; a 2 there is not a bool this codec wrote.
        record[CacheRecord.PrefixSize(layout.FieldCount) + 4] = 2;
        Assert.False(CacheRecord.IsWellFormed(record, layout));

        // A writer refuses a list of another element type than the field's.
        writer.Begin(1, layout.Fingerprint, absent: false);
        Assert.Throws<ArgumentException>(() => writer.List<double>(1, [1.0]));
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
            [new("ls", ColumnType.List(StorageKind.String)), new("t", ColumnType.String), new("v", ColumnType.List(StorageKind.Float32, fixedLength: 2)), new("li64", ColumnType.List(StorageKind.Int64))]));
        var random = new Random(7);
        for (var i = 0; i < 20_000; i++)
        {
            var bytes = new byte[random.Next(0, 64)];
            random.NextBytes(bytes);
            if (bytes.Length >= CacheRecord.PrefixSize(layout.FieldCount))
            {
                CacheRecord.WriteHeader(bytes, (ushort)layout.FieldCount, random.NextInt64(), layout.Fingerprint, absent: false); // random values after it
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
        // About 4 KiB of string per record, distinct per key: what the table's dictionary holds for it alone.
        Assert.True(cache.TryWriteBack(Batch(cache, writtenMs, key + new string('n', 2_000), key)));
        cache.WriteQueued(table.Writer);
    }

    /// <summary>A store whose writes wait for <see cref="Release"/>, saying when the first began.</summary>
    private sealed class HeldStore : ICacheStore
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string StoreType => TestCacheStoreFactory.TypeName;

        public void Release() => _release.TrySetResult();

        public ValueTask GetAsync(CacheKeyspace keyspace, ReadOnlyMemory<string> keys, Memory<ReadOnlyMemory<byte>> records, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask SetAsync(CacheKeyspace keyspace, ReadOnlyMemory<CacheStoreEntry> entries, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return new ValueTask(_release.Task.WaitAsync(cancellationToken));
        }
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
        private readonly string[]? _names;

        public Describe(string[] names)
        {
            _names = names;
            Text = null!;
        }
        private List<string>? _items;

        public string Text { get; private set; }

        public void Missing(int field) => Add(field, "-");

        public void Bool(int field, bool value) => Add(field, value.ToString());

        public void Int32(int field, int value) => Add(field, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public void Int64(int field, long value) => Add(field, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public void Float32(int field, float value) => Add(field, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public void Float64(int field, double value) => Add(field, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public void String(int field, ReadOnlySpan<byte> utf8) => Add(field, System.Text.Encoding.UTF8.GetString(utf8));

        public void List<T>(int field, ReadOnlySpan<T> value)
            where T : unmanaged => Add(field, "[" + string.Join(",", value.ToArray()) + "]");

        public void StringListStart(int field, int count) => _items = new List<string>();

        public void StringListItem(int field, ReadOnlySpan<byte> utf8) => _items!.Add(System.Text.Encoding.UTF8.GetString(utf8));

        public void StringListEnd(int field) => Add(field, "[" + string.Join(",", _items!) + "]");

        private void Add(int field, string value) => Text = (Text is null ? string.Empty : Text + " ") + (_names ?? Names)[field] + "=" + value;
    }
}

/// <summary>Tests that count the process-wide StrataRec metrics run alone, so other tests' measurements do not reach their listeners.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MetricMeasurement
{
    public const string Name = "Metric measurement";
}
