using StrataRec.Artifacts.Model;
using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Cache;
using StrataRec.Features.Catalog;
using StrataRec.Features.Local;
using StrataRec.Features.Runtime;
using static StrataRec.UnitTests.Features.Local.LocalTableTestSupport;

namespace StrataRec.UnitTests.Features.Local;

public class LocalFeatureTableTests
{
    [Theory]
    [InlineData(EntityKeyType.Int64)]
    [InlineData(EntityKeyType.String)]
    public void EveryType_RoundTrips(EntityKeyType keyType)
    {
        var table = Table(keyType);
        var keys = new List<EntityKey>();
        for (var id = 1; id <= 40; id++)
        {
            keys.Add(Key(keyType, id));
            Assert.True(Write(table.Writer, keys[^1], version: 1, Values(id)));
        }

        var version = table.Pin();
        Assert.Equal(40, version.RecordCount);
        var filled = FillAll(version, table.Schema, keys);
        for (var i = 0; i < keys.Count; i++)
        {
            AssertRecord(Values(i + 1), table.Schema, filled[i]);
            AssertRecord(Values(i + 1), table.Schema, ReadRecord(version, table.Schema, keys[i]));
        }
    }

    [Fact]
    public void ExtremeAndEmptyValues_RoundTrip()
    {
        var table = Table();
        var values = new Dictionary<string, ScalarValue>
        {
            ["b"] = ScalarValue.FromBool(true),
            ["i32"] = ScalarValue.FromInt64(int.MaxValue),
            ["i64"] = ScalarValue.FromInt64(long.MinValue),
            ["f32"] = ScalarValue.FromFloat64(float.Epsilon),
            ["f64"] = ScalarValue.FromFloat64(-0.0),
            ["s"] = ScalarValue.FromString(string.Empty),
            ["t"] = ScalarValue.FromText("lone surrogate \ud800 kept"),
            ["v"] = ScalarValue.FromVector(new[] { float.MaxValue, float.MinValue, 0f, -0f }),
            ["li32"] = ScalarValue.FromInt64List(Array.Empty<long>()),
            ["li64"] = ScalarValue.FromInt64List(Array.Empty<long>()),
            ["ls"] = ScalarValue.FromStringList(new[] { string.Empty, string.Empty }),
        };
        Write(table.Writer, EntityKey.FromInt64(0), 0, values);
        Write(table.Writer, EntityKey.FromInt64(long.MinValue), 0, values);
        Write(table.Writer, EntityKey.FromInt64(-1), 0, values);

        foreach (var id in new[] { 0L, long.MinValue, -1L })
        {
            var record = FillAll(table.Pin(), table.Schema, new[] { EntityKey.FromInt64(id) })[0];
            AssertRecord(values, table.Schema, record);
            Assert.True(BitConverter.DoubleToInt64Bits(record[table.Schema.FieldIndex("f64")].AsFloat64()) < 0, "-0.0 keeps its sign");
        }
    }

    /// <summary>
    /// The same data served by a connector (staging, then backfill) and by a local table (lookup, then fill) must give
    /// the request identical columns: values, defaults for absent keys and fields, missing values, and NaN/±∞ replaced
    /// for float features but kept in vectors (design 4.3, 6.6, 6.7).
    /// </summary>
    [Fact]
    public async Task ServesTheSameColumnsAsTheFetchPath()
    {
        const int Keys = 12;
        var defaults = new Dictionary<string, ScalarValue>
        {
            ["b"] = ScalarValue.FromBool(true),
            ["i32"] = ScalarValue.FromInt64(-7),
            ["f32"] = ScalarValue.FromFloat64(1.5),
            ["f64"] = ScalarValue.FromFloat64(-2.5),
            ["s"] = ScalarValue.FromString("none"),
            ["v"] = ScalarValue.FromVector(new[] { 9f, 9f, 9f, 9f }),
            ["li32"] = ScalarValue.FromInt64List(new long[] { 1, 2 }),
            ["ls"] = ScalarValue.FromStringList(new[] { "d" }),
        };

        // Key 3 has no record; key 5 has every field missing; NaN and ±∞ appear in floats and in a vector.
        Dictionary<string, ScalarValue>? Data(long key)
        {
            if (key == 3)
            {
                return null;
            }

            var values = key == 5 ? new Dictionary<string, ScalarValue>() : Values(key);
            if (key % 4 == 0)
            {
                values["f32"] = ScalarValue.FromFloat64(double.NaN);
                values["f64"] = ScalarValue.FromFloat64(key % 8 == 0 ? double.PositiveInfinity : double.NegativeInfinity);
                values["v"] = ScalarValue.FromVector(new[] { float.NaN, 1f, float.PositiveInfinity, 2f });
            }

            if (key % 3 == 1)
            {
                values.Remove("t");
                values.Remove("li64");
                values.Remove("s");
            }

            return values;
        }

        // Fetch path.
        var rig = new FeatureTestRig().Source("item_store", new ValueConnector(Data));
        var features = new int[AllTypes.Length];
        for (var f = 0; f < AllTypes.Length; f++)
        {
            var name = AllTypes[f].Name;
            features[f] = rig.Physical("item." + name, FeatureTestRig.Item, AllTypes[f].Type, "item_store",
                defaults.TryGetValue(name, out var d) ? d : null);
        }

        rig.Build();
        var candidates = new long[Keys];
        for (var i = 0; i < Keys; i++)
        {
            candidates[i] = i + 1;
        }

        var request = rig.NewRequest(candidates);
        var fill = await request.EnsureAsync(features);
        Assert.Equal(FeatureState.Ok, fill.State);

        // Local path, with the same absent value per feature: its default, or missing.
        var table = Table();
        for (var key = 1L; key <= Keys; key++)
        {
            if (Data(key) is { } values)
            {
                Write(table.Writer, EntityKey.FromInt64(key), 1, values);
            }
        }

        var memory = new RequestMemory();
        var version = table.Pin();
        var records = new LocalRecordRef[Keys];
        Assert.Equal(Keys - 1, version.Lookup(candidates, records));
        var rows = new int[Keys];
        for (var i = 0; i < Keys; i++)
        {
            rows[i] = i;
        }

        var replaced = 0;
        for (var f = 0; f < AllTypes.Length; f++)
        {
            var column = ColumnBuffer.Create(AllTypes[f].Type, Keys, memory);
            replaced += version.Fill(f, records, rows, column, defaults.TryGetValue(AllTypes[f].Name, out var d) ? d : ScalarValue.Missing);
            for (var row = 0; row < Keys; row++)
            {
                var fetched = request.Value(features[f], row);
                var local = ColumnValues.Get(column, row);
                Assert.True(fetched.Equals(local), $"{AllTypes[f].Name} row {row}: fetch {fetched}, local {local}");
            }
        }

        Assert.Equal(request.Fetcher.NonFiniteReplaced, replaced);
        Assert.Equal(6, replaced); // keys 4, 8 and 12, in f32 and f64
        memory.Release();
        request.Memory.Release();
    }

    [Fact]
    public void PartialUpdate_KeepsUnwrittenFields_AndSetMissingClearsOne()
    {
        var table = Table();
        var key = EntityKey.FromInt64(42);
        var full = Values(7);
        Write(table.Writer, key, 1, full);

        // Touch one fixed field, one string, one list; clear the text.
        var builder = table.Writer.Begin(key, 2);
        builder.SetFloat64(table.Schema.FieldIndex("f64"), 99.5);
        builder.SetString(table.Schema.FieldIndex("s"), "changed");
        builder.SetInt64List(table.Schema.FieldIndex("li64"), new long[] { 5, 6, 7, 8, 9 });
        builder.SetMissing(table.Schema.FieldIndex("t"));
        Assert.True(builder.Commit());

        var expected = new Dictionary<string, ScalarValue>(full)
        {
            ["f64"] = ScalarValue.FromFloat64(99.5),
            ["s"] = ScalarValue.FromString("changed"),
            ["li64"] = ScalarValue.FromInt64List(new long[] { 5, 6, 7, 8, 9 }),
        };
        expected.Remove("t");
        AssertRecord(expected, table.Schema, ReadRecord(table.Pin(), table.Schema, key));
        AssertRecord(expected, table.Schema, FillAll(table.Pin(), table.Schema, new[] { key })[0]);

        // A partial update of a key with no record leaves every other field missing.
        var fresh = EntityKey.FromInt64(43);
        builder = table.Writer.Begin(fresh, 1);
        builder.SetBool(table.Schema.FieldIndex("b"), false);
        builder.Commit();
        AssertRecord(new Dictionary<string, ScalarValue> { ["b"] = ScalarValue.FromBool(false) }, table.Schema, ReadRecord(table.Pin(), table.Schema, fresh));
    }

    [Fact]
    public void StaleAndDuplicateUpdates_AreIgnored()
    {
        var table = Table();
        var key = EntityKey.FromInt64(1);
        Assert.True(Write(table.Writer, key, 10, Values(10)));

        // Older and equal versions: ignored, whether full or partial.
        Assert.False(Write(table.Writer, key, 9, Values(9)));
        Assert.False(Write(table.Writer, key, 10, Values(11)));
        var builder = table.Writer.Begin(key, 5);
        Assert.True(builder.IsIgnored);
        builder.SetInt64(table.Schema.FieldIndex("i64"), 0); // still accepted
        Assert.False(builder.Commit());
        AssertRecord(Values(10), table.Schema, ReadRecord(table.Pin(), table.Schema, key));

        // Out-of-order delivery converges on the newest version.
        Assert.True(Write(table.Writer, key, 12, Values(12)));
        Assert.False(Write(table.Writer, key, 11, Values(11)));
        AssertRecord(Values(12), table.Schema, ReadRecord(table.Pin(), table.Schema, key));
        Assert.Equal(1, table.Pin().RecordCount);
    }

    [Fact]
    public void Tombstones_DeleteAndBlockOlderUpdates()
    {
        var table = Table();
        var key = EntityKey.FromInt64(7);
        Write(table.Writer, key, 1, Values(1));
        Assert.True(table.Writer.Delete(key, 2));
        Assert.False(table.Pin().Find(key).Found);
        Assert.Equal(0, table.Pin().RecordCount);
        Assert.Equal(1, table.Pin().TombstoneCount);

        // An update older than the delete arrives late: the key stays deleted. A stale delete is ignored too.
        Assert.False(Write(table.Writer, key, 2, Values(2)));
        Assert.False(table.Writer.Delete(key, 1));
        Assert.False(table.Pin().Find(key).Found);

        // A newer update brings it back, with no field carried over from before the delete.
        var builder = table.Writer.Begin(key, 3);
        builder.SetInt64(table.Schema.FieldIndex("i64"), 33);
        Assert.True(builder.Commit());
        AssertRecord(new Dictionary<string, ScalarValue> { ["i64"] = ScalarValue.FromInt64(33) }, table.Schema, ReadRecord(table.Pin(), table.Schema, key));

        // A delete that arrives before the key's first update still blocks older updates.
        var early = EntityKey.FromInt64(8);
        Assert.True(table.Writer.Delete(early, 5));
        Assert.False(Write(table.Writer, early, 4, Values(4)));
        Assert.False(table.Pin().Find(early).Found);
        Assert.Equal(1, table.Pin().RecordCount);
        Assert.Equal(1, table.Pin().TombstoneCount);
    }

    [Fact]
    public void AbsentKeysAndFields_TakeTheAbsentValue_NeverZero()
    {
        var table = Table();
        var builder = table.Writer.Begin(EntityKey.FromInt64(1), 1);
        builder.SetInt64(table.Schema.FieldIndex("i64"), 5);
        builder.Commit();

        var memory = new RequestMemory(new PoisoningPool());
        var version = table.Pin();
        var records = new LocalRecordRef[3];
        Assert.Equal(1, version.Lookup(new long[] { 1, 2, 3 }, records));
        Assert.True(records[0].Found);
        Assert.False(records[1].Found);
        var rows = new[] { 0, 1, 2 };
        var i64 = (ScalarColumn<long>)ColumnBuffer.Create(ColumnType.Int64, 3, memory);
        version.Fill(table.Schema.FieldIndex("i64"), records, rows, i64, ScalarValue.Missing);
        Assert.Equal(5, i64[0]);
        Assert.True(i64.IsValid(0));
        Assert.False(i64.IsValid(1));
        Assert.False(i64.IsValid(2));

        // The record exists but has no f32: the default, like a key absent from the table.
        var f32 = (ScalarColumn<float>)ColumnBuffer.Create(ColumnType.Float32, 3, memory);
        version.Fill(table.Schema.FieldIndex("f32"), records, rows, f32, ScalarValue.FromFloat64(0.25));
        Assert.Equal(new[] { 0.25f, 0.25f, 0.25f }, f32.Span.ToArray());
        Assert.Null(f32.Validity);
        memory.Release();
    }

    [Fact]
    public void StringKeys_WithCollidingHashes_AreToldApart()
    {
        // Every key hashes to the same value: the index must compare the stored keys.
        var table = Table(EntityKeyType.String, new LocalFeatureTableOptions { StringKeyHasher = static _ => 42 });
        var keys = new List<EntityKey>();
        for (var id = 1; id <= 100; id++)
        {
            keys.Add(Key(EntityKeyType.String, id));
            Write(table.Writer, keys[^1], 1, Values(id));
        }

        Assert.True(table.Writer.Delete(keys[49], 2));
        Write(table.Writer, keys[9], 3, Values(1000));

        var version = table.Pin();
        Assert.False(version.Find("k-0").Found);
        Assert.False(version.Find("k-1000").Found);
        Assert.False(version.Find(keys[49]).Found);
        var filled = FillAll(version, table.Schema, keys);
        for (var i = 0; i < keys.Count; i++)
        {
            if (i == 49)
            {
                Assert.True(filled[i][table.Schema.FieldIndex("i64")].IsMissing);
                continue;
            }

            AssertRecord(Values(i == 9 ? 1000 : i + 1), table.Schema, filled[i]);
        }

        // Keys of the other type are never found.
        Assert.False(version.Find(1L).Found);
        Assert.Equal(0, version.Lookup(new long[] { 1, 2 }, new LocalRecordRef[2]));
        Assert.Equal(1, version.Lookup(new string?[] { null, "k-2", "nope" }, new LocalRecordRef[3]));
    }

    [Fact]
    public void StringKeys_PartialCollisions_SurviveGrowthAndCompaction()
    {
        // Four hash values for 2,000 keys: long probe chains, several index growths, then a compaction.
        var table = Table(EntityKeyType.String, new LocalFeatureTableOptions
        {
            StringKeyHasher = static key => (ulong)(key[^1] % 4),
            PageSize = 4096,
        });
        for (var id = 1; id <= 2000; id++)
        {
            Write(table.Writer, Key(EntityKeyType.String, id), 1, new Dictionary<string, ScalarValue> { ["i64"] = ScalarValue.FromInt64(id) });
        }

        table.Writer.Compact();
        var version = table.Pin();
        for (var id = 1; id <= 2000; id++)
        {
            var record = version.Find("k-" + id);
            Assert.True(record.Found);
            Assert.Equal(id, version.Read(table.Schema.FieldIndex("i64"), record).AsInt64());
        }
    }

    [Fact]
    public void Compaction_PreservesLiveRecordsAndTombstones()
    {
        var table = Table(options: new LocalFeatureTableOptions { PageSize = 4096, CompactionMinBytes = long.MaxValue });
        for (var round = 1; round <= 5; round++)
        {
            for (var id = 1; id <= 200; id++)
            {
                Write(table.Writer, EntityKey.FromInt64(id), round, Values((round * 1000) + id));
            }
        }

        for (var id = 1; id <= 200; id += 10)
        {
            table.Writer.Delete(EntityKey.FromInt64(id), 6);
        }

        var before = table.Pin();
        Assert.True(before.SupersededBytes * 2 > before.RecordBytes);
        var stringsBefore = before.Find(2L);
        table.Writer.Compact();
        var after = table.Pin();

        Assert.NotSame(before, after);
        Assert.Equal(before.Generation + 1, after.Generation);
        Assert.Equal(0, after.SupersededBytes);
        Assert.True(after.Bytes < before.Bytes);
        Assert.Equal(180, after.RecordCount);
        Assert.Equal(20, after.TombstoneCount);
        var keys = new List<EntityKey>();
        for (var id = 1; id <= 200; id++)
        {
            keys.Add(EntityKey.FromInt64(id));
        }

        var filled = FillAll(after, table.Schema, keys);
        for (var id = 1; id <= 200; id++)
        {
            if (id % 10 == 1)
            {
                Assert.False(after.Find(id).Found);
                continue;
            }

            AssertRecord(Values(5000 + id), table.Schema, filled[id - 1]);
        }

        // Tombstones survived: a late update older than the delete is still ignored.
        Assert.False(Write(table.Writer, EntityKey.FromInt64(1), 5, Values(1)));

        // The version pinned before the compaction still reads its own (older) data.
        AssertRecord(Values(5002), table.Schema, ReadRecord(before, table.Schema, EntityKey.FromInt64(2)));
        Assert.True(stringsBefore.Found);
    }

    [Fact]
    public void Compaction_RunsByItself_AndDropsUnusedStrings()
    {
        var table = Table(options: new LocalFeatureTableOptions { PageSize = 4096, CompactionMinBytes = 16 * 1024 });
        for (var version = 1; version <= 400; version++)
        {
            // One key, rewritten with a new string every time: every string but the last becomes unused.
            var builder = table.Writer.Begin(EntityKey.FromInt64(1), version);
            builder.SetString(table.Schema.FieldIndex("s"), "value-" + version);
            builder.SetStringList(table.Schema.FieldIndex("ls"), new[] { "a-" + version, "b" });
            builder.Commit();
        }

        Assert.True(table.Compactions > 0);
        var current = table.Pin();
        Assert.True(current.SupersededBytes * 2 <= current.RecordBytes || current.SupersededBytes < 16 * 1024);
        AssertRecord(new Dictionary<string, ScalarValue>
        {
            ["s"] = ScalarValue.FromString("value-400"),
            ["ls"] = ScalarValue.FromStringList(new[] { "a-400", "b" }),
        }, table.Schema, ReadRecord(current, table.Schema, EntityKey.FromInt64(1)));

        table.Writer.Compact();
        Assert.Equal(3, table.Pin().StringCount);
    }

    [Fact]
    public void Growth_OfIndexPagesAndDictionary_KeepsEveryRecord()
    {
        var table = Table(options: new LocalFeatureTableOptions { PageSize = 256 });
        for (var id = 1; id <= 3000; id++)
        {
            var values = Values(id);
            values["s"] = ScalarValue.FromString("distinct-" + id);
            Write(table.Writer, EntityKey.FromInt64(id), 1, values);
        }

        // A text far larger than a page gets a page of its own.
        var big = new string('x', 10_000);
        Write(table.Writer, EntityKey.FromInt64(5000), 1, new Dictionary<string, ScalarValue> { ["t"] = ScalarValue.FromText(big) });

        var version = table.Pin();
        Assert.True(version.PageCount > 1000);
        Assert.True(version.StringCount > 3000);
        for (var id = 1; id <= 3000; id++)
        {
            var record = ReadRecord(version, table.Schema, EntityKey.FromInt64(id))!;
            Assert.Equal("distinct-" + id, record["s"].AsString());
            Assert.Equal(Values(id)["v"], record["v"]);
        }

        Assert.Equal(big, ReadRecord(version, table.Schema, EntityKey.FromInt64(5000))!["t"].AsString());
    }

    [Fact]
    public void MaxBytes_FailsTheLoad_AndLeavesTheTableUnchanged()
    {
        var table = Table(options: new LocalFeatureTableOptions { MaxBytes = 64 * 1024, PageSize = 4096 });
        var written = 0;
        var failure = Assert.Throws<LocalTableFullException>(() =>
        {
            for (var id = 1; id <= 100_000; id++)
            {
                Write(table.Writer, EntityKey.FromInt64(id), 1, Values(id));
                written++;
            }
        });

        Assert.Equal(64 * 1024, failure.MaxBytes);
        Assert.True(failure.RequiredBytes > failure.MaxBytes);
        Assert.Contains("max_bytes", failure.Message, StringComparison.Ordinal);
        Assert.Contains("hint:", failure.Message, StringComparison.Ordinal);
        var version = table.Pin();
        Assert.True(version.Bytes <= 64 * 1024);
        Assert.Equal(written, version.RecordCount);
        Assert.False(version.Find(written + 1).Found);
        AssertRecord(Values(written), table.Schema, ReadRecord(version, table.Schema, EntityKey.FromInt64(written)));
    }

    [Fact]
    public void MaxBytes_CompactsBeforeFailing()
    {
        // Rewriting 50 keys over and over fits, because superseded records are compacted away when the limit is reached.
        var table = Table(options: new LocalFeatureTableOptions { MaxBytes = 128 * 1024, PageSize = 4096, CompactionMinBytes = long.MaxValue });
        for (var round = 1; round <= 200; round++)
        {
            for (var id = 1; id <= 50; id++)
            {
                Write(table.Writer, EntityKey.FromInt64(id), round, Values(round + id));
            }
        }

        Assert.True(table.Compactions > 0);
        Assert.True(table.Pin().Bytes <= 128 * 1024);
        AssertRecord(Values(200 + 7), table.Schema, ReadRecord(table.Pin(), table.Schema, EntityKey.FromInt64(7)));
    }

    [Fact]
    public void Reload_BuildsInvisiblyAndSwapsWhole()
    {
        var table = Table();
        for (var id = 1; id <= 10; id++)
        {
            Write(table.Writer, EntityKey.FromInt64(id), 5, Values(id));
        }

        table.Writer.Checkpoint(UpdatePosition.Single(10));
        var before = table.Pin();

        table.Writer.BeginReload();
        Assert.Throws<InvalidOperationException>(() => table.Writer.Checkpoint(UpdatePosition.Single(11)));
        for (var id = 5; id <= 15; id++)
        {
            // Versions start afresh: the reload does not compare with the old table.
            Write(table.Writer, EntityKey.FromInt64(id), 1, Values(id * 100));
        }

        Assert.Same(before, table.Pin());
        Assert.Equal(UpdatePosition.Single(10), table.Position);
        table.Writer.CompleteReload(UpdatePosition.Single(20));

        var after = table.Pin();
        Assert.NotSame(before, after);
        Assert.Equal(UpdatePosition.Single(20), table.Position);
        Assert.False(after.Find(1L).Found); // not in the new snapshot: gone
        AssertRecord(Values(1500), table.Schema, ReadRecord(after, table.Schema, EntityKey.FromInt64(15)));
        AssertRecord(Values(1), table.Schema, ReadRecord(before, table.Schema, EntityKey.FromInt64(1)));

        // An aborted reload leaves the current version alone; writes go to it again afterwards.
        table.Writer.BeginReload();
        Write(table.Writer, EntityKey.FromInt64(99), 1, Values(99));
        table.Writer.AbortReload();
        Assert.Same(after, table.Pin());
        Assert.False(table.Pin().Find(99L).Found);
        Write(table.Writer, EntityKey.FromInt64(98), 1, Values(98));
        Assert.True(table.Pin().Find(98L).Found);
    }

    [Fact]
    public void Builder_MisuseIsReported()
    {
        var table = Table();
        var builder = table.Writer.Begin(EntityKey.FromInt64(1), 1);
        var wrongType = Assert.Throws<InvalidOperationException>(() => builder.SetInt64(table.Schema.FieldIndex("i32"), 1));
        Assert.Contains("SetInt32", wrongType.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => builder.SetVector(table.Schema.FieldIndex("v"), new float[3]));
        Assert.Throws<InvalidOperationException>(() => builder.AddInt64Item(table.Schema.FieldIndex("li64"), 1));
        builder.StartList(table.Schema.FieldIndex("li64"));
        builder.SetBool(table.Schema.FieldIndex("b"), true); // closes the open list
        Assert.Throws<InvalidOperationException>(() => builder.AddInt64Item(table.Schema.FieldIndex("li64"), 1));
        var outOfRange = Assert.Throws<ArgumentOutOfRangeException>(() => builder.Set(table.Schema.FieldIndex("i32"), ScalarValue.FromInt64(1L << 40)));
        Assert.Contains("int64", outOfRange.Message, StringComparison.Ordinal);

        // A later Begin discards the builder; using it throws.
        var next = table.Writer.Begin(EntityKey.FromInt64(2), 1);
        Assert.Throws<InvalidOperationException>(() => builder.SetBool(0, true));
        Assert.Throws<InvalidOperationException>(() => builder.Commit());
        Assert.True(next.Commit());
        Assert.Throws<InvalidOperationException>(() => next.Commit());
        Assert.False(table.Pin().Find(1L).Found);

        Assert.Throws<InvalidOperationException>(() => table.Writer.Begin("k".AsSpan(), 1));
        Assert.Throws<InvalidOperationException>(() => default(RecordBuilder).Commit());
        Assert.Throws<InvalidOperationException>(() => table.Writer.Compact(dropWrittenBefore: 5));
    }

    [Fact]
    public void Fill_RejectsAColumnOfAnotherType()
    {
        var table = Table();
        var memory = new RequestMemory();
        var column = ColumnBuffer.Create(ColumnType.Float64, 1, memory);
        Assert.Throws<ArgumentException>(() =>
            table.Pin().Fill(table.Schema.FieldIndex("f32"), new LocalRecordRef[1], new[] { 0 }, column, ScalarValue.Missing));
        Assert.Throws<ArgumentException>(() =>
            table.Pin().Fill(table.Schema.FieldIndex("f64"), new LocalRecordRef[2], new[] { 0 }, column, ScalarValue.Missing));
        memory.Release();
    }

    [Theory]
    [InlineData(EntityKeyType.Int64)]
    [InlineData(EntityKeyType.String)]
    public void MultiFieldFill_MatchesOneFillPerField(EntityKeyType keyType)
    {
        // 700 keys: several blocks of rows, so string memos carry across blocks. Absent keys, partial records and
        // non-finite floats exercise every absent path; six string reads pass the memos kept for the whole call.
        var table = Table(keyType);
        var schema = table.Schema;
        for (var id = 1; id <= 600; id++)
        {
            var values = Values(id);
            if (id % 7 == 0)
            {
                values.Remove("f32");
                values.Remove("v");
                values.Remove("s");
                values.Remove("ls");
            }

            if (id % 11 == 0)
            {
                values["f32"] = ScalarValue.FromFloat64(double.NaN);
                values["f64"] = ScalarValue.FromFloat64(double.NegativeInfinity);
            }

            Write(table.Writer, Key(keyType, id), 1, values);
        }

        var keys = new EntityKey[700];
        var random = new Random(3);
        for (var i = 0; i < keys.Length; i++)
        {
            keys[i] = Key(keyType, i % 9 == 0 ? 10_000 + i : 1 + random.Next(600));
        }

        var version = table.Pin();
        var records = new LocalRecordRef[keys.Length];
        version.Lookup(keys, records);
        var rows = new int[keys.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = (i * 7) % rows.Length; // a permutation: rows need not follow keys
        }

        var fields = new List<int>();
        for (var f = 0; f < schema.FieldCount; f++)
        {
            fields.Add(f);
        }

        for (var extra = 0; extra < 5; extra++)
        {
            fields.Add(schema.FieldIndex("s"));
        }

        var memory = new RequestMemory(new PoisoningPool());
        var reads = new LocalFieldRead[fields.Count];
        var single = new ColumnBuffer[fields.Count];
        var expectedNonFinite = new int[fields.Count];
        for (var r = 0; r < fields.Count; r++)
        {
            var f = fields[r];
            var absent = r % 2 == 0 ? Stored(schema.Fields[f].Type, Values(0)[schema.Fields[f].Name]) : ScalarValue.Missing;
            reads[r] = new LocalFieldRead(f, ColumnBuffer.Create(schema.Fields[f].Type, keys.Length, memory), absent);
            single[r] = ColumnBuffer.Create(schema.Fields[f].Type, keys.Length, memory);
            expectedNonFinite[r] = version.Fill(f, records, rows, single[r], absent);
        }

        var nonFinite = new int[fields.Count];
        version.Fill(records, rows, reads, nonFinite);
        Assert.Equal(expectedNonFinite, nonFinite);
        Assert.Contains(nonFinite, n => n > 0);
        for (var r = 0; r < fields.Count; r++)
        {
            for (var i = 0; i < keys.Length; i++)
            {
                Assert.Equal(ColumnValues.Get(single[r], rows[i]), ColumnValues.Get(reads[r].Target, rows[i]));
            }
        }

        memory.Release();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fill_RowsAliasingTheTargetColumn_CannotWritePastItsCapacity(bool multiField)
    {
        // Codex review 9, item 1: the rows are a view of the target column's own pooled array (capacity 2, physical length
        // 16). Read live, the first write (8, to row 1) would turn the next row into 8, past the capacity.
        var schema = Schema(fields: new[] { new LocalTableField("i32", ColumnType.Int32) });
        var table = new LocalFeatureTable(schema);
        WriteInt32(table, 1, 8);
        WriteInt32(table, 2, 99);
        var version = table.Pin();
        var records = new LocalRecordRef[2];
        Assert.Equal(2, version.Lookup(new long[] { 1, 2 }, records));

        var memory = new RequestMemory();
        var column = (ScalarColumn<int>)ColumnBuffer.Create(ColumnType.Int32, 2, memory);
        var values = column.Values;
        Assert.True(values.Length > 8, "the pooled array must have spare room for the probe");
        Array.Fill(values, -777);
        values[0] = 1;
        values[1] = 0;
        var rows = new ReadOnlySpan<int>(values, 0, 2);
        if (multiField)
        {
            version.Fill(records, rows, new[] { new LocalFieldRead(0, column, ScalarValue.Missing) });
        }
        else
        {
            version.Fill(0, records, rows, column, ScalarValue.Missing);
        }

        // The rows were [1, 0] when the call began: key 1 went to row 1 and key 2 to row 0, and nothing else was written.
        Assert.Equal(8, values[1]);
        Assert.Equal(99, values[0]);
        for (var i = 2; i < values.Length; i++)
        {
            Assert.Equal(-777, values[i]);
        }

        memory.Release();
    }

    [Fact]
    public void Fill_NonFiniteCountersAliasingTheRows_DoNotChangeWhereValuesGo()
    {
        // The counters are written while the rows are still being read (after each block): with a live view of the rows,
        // later blocks would go to the wrong rows.
        var schema = Schema(fields: new[] { new LocalTableField("f32", ColumnType.Float32) });
        var table = new LocalFeatureTable(schema);
        var keys = new long[300];
        for (var id = 1; id <= keys.Length; id++)
        {
            keys[id - 1] = id;
            var builder = table.Writer.Begin(id, 1);
            builder.SetFloat32(0, id % 3 == 0 ? float.NaN : id);
            builder.Commit();
        }

        var version = table.Pin();
        var records = new LocalRecordRef[keys.Length];
        version.Lookup(keys, records);
        var memory = new RequestMemory();
        var expected = (ScalarColumn<float>)ColumnBuffer.Create(ColumnType.Float32, keys.Length, memory);
        var actual = (ScalarColumn<float>)ColumnBuffer.Create(ColumnType.Float32, keys.Length, memory);
        var rows = new int[keys.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = keys.Length - 1 - i;
        }

        var reference = version.Fill(0, records, (int[])rows.Clone(), expected, ScalarValue.Missing);
        version.Fill(records, rows, new[] { new LocalFieldRead(0, actual, ScalarValue.Missing) }, rows.AsSpan(200, 1));

        Assert.Equal(reference, rows[200]);
        for (var row = 0; row < keys.Length; row++)
        {
            Assert.Equal(ColumnValues.Get(expected, row), ColumnValues.Get(actual, row));
        }

        memory.Release();
    }

    [Fact]
    public void Fill_ChecksRowsAndRecordsOnce()
    {
        var table = Table();
        Write(table.Writer, EntityKey.FromInt64(1), 1, Values(1));
        var version = table.Pin();
        var records = new LocalRecordRef[2];
        version.Lookup(new long[] { 1, 2 }, records);
        var memory = new RequestMemory();
        var column = ColumnBuffer.Create(ColumnType.Int64, 2, memory);
        var field = table.Schema.FieldIndex("i64");
        Assert.Throws<ArgumentOutOfRangeException>(() => version.Fill(field, records, new[] { 0, -1 }, column, ScalarValue.Missing));
        Assert.Throws<ArgumentOutOfRangeException>(() => version.Fill(field, records, new[] { 0, 2 }, column, ScalarValue.Missing));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            version.Fill(records, new[] { 2, 0 }, new[] { new LocalFieldRead(field, column, ScalarValue.Missing) }));

        // References from a table with shorter records: the ones near a page end would read past it.
        var small = new LocalFeatureTable(Schema(fields: new[] { new LocalTableField("b", ColumnType.Bool) }));
        var ids = new long[2000];
        for (var id = 1; id <= ids.Length; id++)
        {
            ids[id - 1] = id;
            var builder = small.Writer.Begin(id, 1);
            builder.SetBool(0, true);
            builder.Commit();
        }

        var foreign = new LocalRecordRef[ids.Length];
        Assert.Equal(ids.Length, small.Pin().Lookup(ids, foreign));
        var target = ColumnBuffer.Create(ColumnType.Int64, ids.Length, memory);
        var allRows = new int[ids.Length];
        for (var i = 0; i < allRows.Length; i++)
        {
            allRows[i] = i;
        }

        Assert.Throws<ArgumentException>(() => version.Fill(field, foreign, allRows, target, ScalarValue.Missing));
        memory.Release();
    }

    [Fact]
    public void CacheTable_DropsRecordsWrittenBeforeACutoff()
    {
        var table = Table(mode: LocalTableMode.Cache, options: new LocalFeatureTableOptions { Cache = new CacheTableOptions { TtlMs = 1000 } });
        for (var id = 1; id <= 10; id++)
        {
            // The version of a cache record is its write time.
            Write(table.Writer, EntityKey.FromInt64(id), writeTime(id), Values(id));
        }

        table.Writer.Compact(dropWrittenBefore: writeTime(6));
        var version = table.Pin();
        Assert.Equal(5, version.RecordCount);
        Assert.False(version.Find(5L).Found);
        Assert.Equal(writeTime(6), version.VersionOf(version.Find(6L)));

        static long writeTime(long id) => 1_700_000_000_000 + (id * 1000);
    }

    [Fact]
    public void Schema_FromCatalog_TakesTheSourcesPhysicalFields()
    {
        var artifact = ArtifactSerializer.Deserialize<FeatureCatalogArtifact>("""
            {
              "catalog_id": "c",
              "entities": {
                "item": { "scope": "candidate", "key": "col.entity_id", "key_type": "int64" },
                "user": { "scope": "context", "key": "ctx.request.user_id", "key_type": "string" },
                "user_item": { "scope": "candidate", "key": ["ctx.request.user_id", "col.entity_id"], "key_type": ["string", "int64"] }
              },
              "features": {
                "item.clicks": { "type": "float32", "source": "item_store", "field": "i_clk", "default": 0.0 },
                "item.clicks_again": { "type": "float32", "source": "item_store", "field": "i_clk" },
                "item.tags": { "type": "list<int32>", "source": "item_store", "field": "i_tags" },
                "item.other": { "type": "int64", "source": "other_store", "field": "o" },
                "user.emb": { "type": "vec<float32,8>", "source": "user_profile", "field": "u_emb" },
                "user_item.x": { "type": "float32", "source": "ui", "field": "x" }
              },
              "derived_features": {
                "item.ratio": { "type": "float32", "expr": "col.item.clicks * 2.0" }
              }
            }
            """);
        var diagnostics = new DiagnosticBag();
        var catalog = FeatureCatalog.Build(artifact, new Dictionary<string, ColumnType> { ["user_id"] = ColumnType.String }, diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));

        var items = LocalTableSchema.FromCatalog(catalog, "item_store", "item", LocalTableMode.Local);
        Assert.Equal(EntityKeyType.Int64, items.KeyType);
        Assert.Equal(new[] { "i_clk", "i_tags" }, items.Fields.Select(f => f.Name).ToArray());
        Assert.Equal(ColumnType.Float32, items.Fields[0].Type);

        var users = LocalTableSchema.FromCatalog(catalog, "user_profile", "user", LocalTableMode.Cache);
        Assert.Equal(EntityKeyType.String, users.KeyType);
        Assert.Equal(ColumnType.Vector(8), users.Fields[0].Type);

        var cross = Assert.Throws<ArgumentException>(() => LocalTableSchema.FromCatalog(catalog, "ui", "user_item", LocalTableMode.Cache));
        Assert.Contains("fetch", cross.Message, StringComparison.Ordinal);
        Assert.Contains("did you mean 'i_clk'", Assert.Throws<KeyNotFoundException>(() => items.FieldIndex("i_clck")).Message, StringComparison.Ordinal);
    }

    /// <summary>Writes the values of <c>data(key)</c> for every key and requested field (absent = not written).</summary>
    private sealed class ValueConnector(Func<long, Dictionary<string, ScalarValue>?> data) : StrataRec.Features.Connectors.IFeatureSourceConnector
    {
        public string ConnectorType => TestConnectorFactory.Type;

        public bool Supports(KeyShape shape) => shape == KeyShape.Many;

        public ValueTask<StrataRec.Features.Connectors.FetchStatus> FetchAsync(
            StrataRec.Features.Connectors.EntityKeyBatch keys,
            ReadOnlyMemory<StrataRec.Features.Connectors.CompiledFieldBinding> fields,
            StrataRec.Features.Connectors.IFeatureBatchWriter writer,
            CancellationToken cancellationToken)
        {
            for (var k = 0; k < keys.CandidateKeys.Length; k++)
            {
                if (data(keys.CandidateKeys.Span[k].Int64Value) is not { } values)
                {
                    continue;
                }

                foreach (var field in fields.Span)
                {
                    if (values.TryGetValue(field.Field, out var value) && !value.IsMissing)
                    {
                        Write(writer, field, k, value);
                    }
                }
            }

            return ValueTask.FromResult(StrataRec.Features.Connectors.FetchStatus.Ok);
        }

        private static void Write(StrataRec.Features.Connectors.IFeatureBatchWriter writer, StrataRec.Features.Connectors.CompiledFieldBinding field, int k, ScalarValue value)
        {
            var i = field.FieldIndex;
            switch (field.Type.Kind)
            {
                case StorageKind.Bool: writer.GetValues<bool>(i).Span[k] = value.AsBool(); writer.SetValid(i, k); break;
                case StorageKind.Int32: writer.GetValues<int>(i).Span[k] = (int)value.AsInt64(); writer.SetValid(i, k); break;
                case StorageKind.Int64: writer.GetValues<long>(i).Span[k] = value.AsInt64(); writer.SetValid(i, k); break;
                case StorageKind.Float32: writer.GetValues<float>(i).Span[k] = (float)value.AsFloat64(); writer.SetValid(i, k); break;
                case StorageKind.Float64: writer.GetValues<double>(i).Span[k] = value.AsFloat64(); writer.SetValid(i, k); break;
                case StorageKind.String or StorageKind.Text: writer.SetString(i, k, value.AsString()); break;
                case StorageKind.Vector:
                    value.AsVector().CopyTo(writer.GetValues<float>(i).Span.Slice(k * field.Type.Dimension, field.Type.Dimension));
                    writer.SetValid(i, k);
                    break;
                case StorageKind.ListInt32: writer.SetInt32List(i, k, value.AsInt64List().Select(x => (int)x).ToArray()); break;
                case StorageKind.ListInt64: writer.SetInt64List(i, k, value.AsInt64List()); break;
                case StorageKind.ListString: writer.SetStringList(i, k, value.AsStringList()); break;
            }
        }
    }

    private static void WriteInt32(LocalFeatureTable table, long key, int value)
    {
        var builder = table.Writer.Begin(key, 1);
        builder.SetInt32(table.Schema.FieldIndex("i32"), value);
        Assert.True(builder.Commit());
    }
}
