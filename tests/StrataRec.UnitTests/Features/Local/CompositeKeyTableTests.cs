using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Connectors;
using StrataRec.Features.Local;
using static StrataRec.UnitTests.Features.Local.LocalTableTestSupport;

namespace StrataRec.UnitTests.Features.Local;

/// <summary>
/// Local tables keyed by several parts (design 6.6): a key's parts are encoded canonically, hashed, stored in the record
/// and compared on a lookup, so tuples are told apart whatever their hashes; the one-part forms stay as they were.
/// </summary>
public class CompositeKeyTableTests
{
    private static readonly LocalTableField[] Fields = [new("score", ColumnType.Float64), new("tag", ColumnType.String)];

    private static readonly EntityKeyLayout ItemCountry = new(
    [
        new EntityKeyPart("col.entity_id", EntityKeyType.Int64, false),
        new EntityKeyPart("ctx.request.country", EntityKeyType.String, true),
    ]);

    private static readonly EntityKeyLayout ThreeParts = EntityKeyLayout.OfTypes(EntityKeyType.String, EntityKeyType.Int64, EntityKeyType.String);

    private static LocalFeatureTable Table(EntityKeyLayout layout, LocalFeatureTableOptions? options = null) =>
        new(new LocalTableSchema("s", "e", layout, LocalTableMode.Local, Fields), options);

    private static EntityKey[] Two(long item, string country) => [EntityKey.FromInt64(item), EntityKey.FromString(country)];

    private static EntityKey[] Three(string a, long b, string c) => [EntityKey.FromString(a), EntityKey.FromInt64(b), EntityKey.FromString(c)];

    private static void Put(IFeatureTableWriter writer, EntityKey[] key, long version, double score, string? tag = null)
    {
        var builder = writer.Begin(key, version);
        builder.Set(0, ScalarValue.FromFloat64(score));
        if (tag is not null)
        {
            builder.Set(1, ScalarValue.FromString(tag));
        }

        builder.Commit();
    }

    private static double? Score(TableVersion version, EntityKey[] key)
    {
        var record = version.Find(key);
        return record.Found ? version.Read(0, record).AsFloat64() : null;
    }

    [Fact]
    public void TwoPartKeys_InsertUpdateDelete_AreToldApartByEveryPart()
    {
        var table = Table(ItemCountry);
        Put(table.Writer, Two(7, "US"), 1, 1.5);
        Put(table.Writer, Two(7, "DE"), 1, 2.5);
        Put(table.Writer, Two(8, "US"), 1, 3.5);
        Put(table.Writer, Two(7, "US"), 2, 4.5); // a newer version replaces
        Put(table.Writer, Two(7, "DE"), 0, 9.9); // an older one is ignored
        Assert.True(table.Writer.Delete(Two(8, "US"), 2));

        var version = table.Pin();
        Assert.Equal(4.5, Score(version, Two(7, "US")));
        Assert.Equal(2.5, Score(version, Two(7, "DE")));
        Assert.Null(Score(version, Two(8, "US")));
        Assert.Null(Score(version, Two(8, "DE")));
        Assert.Null(Score(version, Two(7, "us")));
        Assert.Equal(2, version.RecordCount);

        // A key of another layout is never found: one part, the parts swapped, a part of another type.
        Assert.False(version.Find(7L).Found);
        Assert.False(version.Find("US").Found);
        Assert.False(version.Find([EntityKey.FromString("US"), EntityKey.FromInt64(7)]).Found);
        Assert.False(version.Find([EntityKey.FromInt64(7), EntityKey.FromInt64(7)]).Found);
        Assert.Equal(0, version.Lookup(new long[] { 7 }, new LocalRecordRef[1]));
        Assert.Equal(0, version.Lookup(new string?[] { "US" }, new LocalRecordRef[1]));
    }

    [Fact]
    public void AStringPartFollowedByAnother_IsLengthPrefixed_SoTuplesThatConcatenateAlikeDiffer()
    {
        // ("ab", 1, "c") and ("a", 1, "bc") would share their characters without the length of the first string part.
        var table = Table(ThreeParts);
        Put(table.Writer, Three("ab", 1, "c"), 1, 1);
        Put(table.Writer, Three("a", 1, "bc"), 1, 2);
        Put(table.Writer, Three("", 1, "abc"), 1, 3);
        Put(table.Writer, Three("abc", 1, ""), 1, 4);

        var version = table.Pin();
        Assert.Equal(1.0, Score(version, Three("ab", 1, "c")));
        Assert.Equal(2.0, Score(version, Three("a", 1, "bc")));
        Assert.Equal(3.0, Score(version, Three("", 1, "abc")));
        Assert.Equal(4.0, Score(version, Three("abc", 1, "")));
        Assert.Null(Score(version, Three("abc", 2, "")));
        Assert.Equal(4, version.RecordCount);
    }

    [Fact]
    public void CollidingHashes_AreToldApart_ThroughGrowthAndCompaction()
    {
        // Every key hashes to one of two values: long probe chains, index growth, then a compaction rebuilds the index
        // from the stored keys.
        var table = Table(ThreeParts, new LocalFeatureTableOptions { KeyHasher = static key => (ulong)(key[^2] % 2), PageSize = 4096 });
        for (var id = 0; id < 600; id++)
        {
            Put(table.Writer, Three("seg-" + (id % 3), id, id % 2 == 0 ? "US" : "DE"), 1, id, "tag-" + id);
        }

        for (var id = 0; id < 600; id += 5)
        {
            Assert.True(table.Writer.Delete(Three("seg-" + (id % 3), id, id % 2 == 0 ? "US" : "DE"), 2));
        }

        table.Writer.Compact();
        var version = table.Pin();
        Assert.Equal(480, version.RecordCount);
        for (var id = 0; id < 600; id++)
        {
            var key = Three("seg-" + (id % 3), id, id % 2 == 0 ? "US" : "DE");
            Assert.Equal(id % 5 == 0 ? (double?)null : id, Score(version, key));
            Assert.Null(Score(version, Three("seg-" + (id % 3), id, id % 2 == 0 ? "DE" : "US")));
        }
    }

    [Fact]
    public void BatchLookup_FindsEachKeyOnce_WithSharedParts_CollisionsAndLongParts()
    {
        // A batch hands the shared country once for every key. Keys are taken in blocks: 150 keys are three blocks, every
        // hash collides, and keys of 1,000-character parts overflow the block's stack arena.
        var table = Table(ItemCountry, new LocalFeatureTableOptions { KeyHasher = static _ => 7 });
        var longCountry = new string('x', 1000);
        for (var id = 1; id <= 150; id++)
        {
            Put(table.Writer, Two(id, "US"), 1, id);
            Put(table.Writer, Two(id, longCountry), 1, -id);
        }

        var version = table.Pin();
        foreach (var country in new[] { "US", longCountry, "FR" })
        {
            var ids = new EntityKey[151];
            for (var i = 0; i < 150; i++)
            {
                ids[i] = EntityKey.FromInt64(150 - i);
            }

            ids[150] = EntityKey.FromInt64(1000); // no record
            var batch = EntityKeyBatch.Create(ItemCountry, 151, ids, new[] { EntityKey.FromString(country) });
            var records = new LocalRecordRef[151];

            var found = version.Lookup(batch, records);

            Assert.Equal(country == "FR" ? 0 : 150, found);
            for (var i = 0; i < 150; i++)
            {
                Assert.Equal(country != "FR", records[i].Found);
                if (records[i].Found)
                {
                    Assert.Equal((150 - i) * (country == "US" ? 1.0 : -1.0), version.Read(0, records[i]).AsFloat64());
                }
            }

            Assert.False(records[150].Found);
        }

        // A batch whose keys are of another type finds nothing (and a batch of another part count neither).
        var wrongType = EntityKeyBatch.Create(ItemCountry, 1, new[] { EntityKey.FromString("1") }, new[] { EntityKey.FromString("US") });
        Assert.Equal(0, version.Lookup(wrongType, new LocalRecordRef[1]));
        Assert.Equal(0, version.Lookup(new EntityKeyBatch(new[] { EntityKey.FromInt64(1) }), new LocalRecordRef[1]));
    }

    [Fact]
    public void OneStringBatchLookup_InBlocks_KeepsNullsAndCollisionsApart()
    {
        // The one-string form is blocked like the batch form: nulls (rows without a key) and keys past a block boundary.
        var table = Table(EntityKeyLayout.Single(EntityKeyType.String), new LocalFeatureTableOptions { KeyHasher = static key => (ulong)(key[^2] % 3) });
        for (var id = 0; id < 200; id++)
        {
            var builder = table.Writer.Begin("k-" + id, 1);
            builder.Set(0, ScalarValue.FromFloat64(id));
            builder.Commit();
        }

        var keys = new string?[201];
        for (var i = 0; i < 200; i++)
        {
            keys[i] = i % 7 == 0 ? null : "k-" + (199 - i);
        }

        keys[200] = "k-200";
        var records = new LocalRecordRef[201];
        var version = table.Pin();

        var found = version.Lookup(keys, records);

        Assert.Equal(171, found);
        for (var i = 0; i < 200; i++)
        {
            Assert.Equal(keys[i] is not null, records[i].Found);
            if (keys[i] is not null)
            {
                Assert.Equal(199 - i, version.Read(0, records[i]).AsFloat64());
            }
        }

        Assert.False(records[200].Found);
    }

    [Fact]
    public void AWriterKeyOfTheWrongShape_IsRefused_NamingTheLayout()
    {
        var table = Table(ItemCountry);

        var count = Assert.Throws<ArgumentException>(() => table.Writer.Begin([EntityKey.FromInt64(1)], 1));
        var type = Assert.Throws<ArgumentException>(() => table.Writer.Begin([EntityKey.FromInt64(1), EntityKey.FromInt64(2)], 1));
        var onePart = Assert.Throws<InvalidOperationException>(() => table.Writer.Begin(1L, 1));

        Assert.Contains("(int64,string)", count.Message, StringComparison.Ordinal);
        Assert.Contains("part 1 (ctx.request.country) is an int64", type.Message, StringComparison.Ordinal);
        Assert.Contains("(int64,string)", onePart.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotLines_WriteASeveralPartKeyAsAnArray()
    {
        using var dir = new TempDirectory();
        dir.Write("part.jsonl",
            """{"key": [7, "US"], "version": 1, "fields": {"score": 1.5, "tag": "a"}}""" + "\n" +
            """{"key": [7, "DE"], "version": 1, "fields": {"score": 2.5}}""" + "\n" +
            """{"key": [8, "US"], "version": 1, "fields": {"score": 3.5}}""" + "\n" +
            """{"key": [8, "US"], "version": 2, "deleted": true}""");
        dir.Write("snapshot.json", """{"files": ["part.jsonl"]}""");
        var table = Table(ItemCountry);

        new SnapshotFilesUpdateSource(Path.Combine(dir.Path, "snapshot.json")).Load(table.Writer, TestContext.Current.CancellationToken);

        var version = table.Pin();
        Assert.Equal(1.5, Score(version, Two(7, "US")));
        Assert.Equal("a", version.Read(1, version.Find(Two(7, "US"))).AsString());
        Assert.Equal(2.5, Score(version, Two(7, "DE")));
        Assert.Null(Score(version, Two(8, "US")));
        Assert.Equal(1, version.TombstoneCount);

        // Loaded again from the same files (a reload builds a new version): the same records.
        new SnapshotFilesUpdateSource(Path.Combine(dir.Path, "snapshot.json")).Load(table.Writer, TestContext.Current.CancellationToken);
        Assert.NotSame(version, table.Pin());
        Assert.Equal(1.5, Score(table.Pin(), Two(7, "US")));
        Assert.Equal(2, table.Pin().RecordCount);
    }

    public static TheoryData<string, string, string> BadKeys => new()
    {
        { "{\"key\": 7, \"version\": 1}", "/key", "[<int64>, <string>]" },
        { "{\"key\": [7], \"version\": 1}", "/key", "1 part(s)" },
        { "{\"key\": [7, \"US\", 1], \"version\": 1}", "/key", "more than 2 parts" },
        { "{\"key\": [\"7\", \"US\"], \"version\": 1}", "/key/0", "[<int64>, <string>]" },
        { "{\"key\": [7, 1], \"version\": 1}", "/key/1", "[<int64>, <string>]" },
        { "{\"key\": [7, null], \"version\": 1}", "/key/1", "[<int64>, <string>]" },
    };

    [Theory]
    [MemberData(nameof(BadKeys))]
    public void ASnapshotKeyOfTheWrongShape_NamesThePartAndTheKeyToWrite(string line, string pointer, string text)
    {
        using var dir = new TempDirectory();
        var file = dir.Write("part.jsonl", """{"key": [1, "US"], "version": 1}""" + "\n" + line);
        dir.Write("snapshot.json", """{"files": ["part.jsonl"]}""");
        var table = Table(ItemCountry);

        var error = Assert.Throws<FeatureUpdateException>(() => new SnapshotFilesUpdateSource(Path.Combine(dir.Path, "snapshot.json")).Load(table.Writer, TestContext.Current.CancellationToken));

        Assert.Equal(file + ":2", error.Location);
        Assert.Equal(pointer, error.Pointer);
        Assert.NotNull(error.Hint);
        Assert.Contains("[<int64>, <string>]", error.Hint, StringComparison.Ordinal);
        Assert.Contains(text, error.Reason + " " + error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MemoryUpdates_ApplySeveralPartKeys_AndRefuseOtherShapesAtTheirPart()
    {
        var table = Table(ItemCountry);
        var updates = new MemoryFeatureUpdateSource();
        updates.Upsert(Two(7, "US"), 1, new Dictionary<string, ScalarValue> { ["score"] = ScalarValue.FromFloat64(1.5) });
        updates.Upsert(Two(7, "DE"), 1, new Dictionary<string, ScalarValue> { ["score"] = ScalarValue.FromFloat64(2.5) });
        updates.Delete(Two(7, "DE"), 2);
        updates.Complete();
        await updates.RunAsync(UpdatePosition.Empty, table.Writer, TestContext.Current.CancellationToken);

        Assert.Equal(1.5, Score(table.Pin(), Two(7, "US")));
        Assert.Null(Score(table.Pin(), Two(7, "DE")));

        foreach (var (key, pointer) in new (EntityKey[] Key, string Pointer)[]
        {
            ([EntityKey.FromInt64(7)], "/key"),
            ([EntityKey.FromInt64(7), EntityKey.FromInt64(1)], "/key/1"),
            ([EntityKey.FromString("7"), EntityKey.FromString("US")], "/key/0"),
        })
        {
            var bad = new MemoryFeatureUpdateSource();
            bad.Upsert(key, 1, new Dictionary<string, ScalarValue>());
            bad.Complete();

            var error = await Assert.ThrowsAsync<FeatureUpdateException>(() => bad.RunAsync(UpdatePosition.Empty, table.Writer, TestContext.Current.CancellationToken));

            Assert.Equal(pointer, error.Pointer);
            Assert.Equal("an array of its 2 parts in order, [<int64>, <string>]", error.Hint![("write the key as ".Length)..]);
        }
    }

    [Fact]
    public void AOneStringKey_IsStoredAsBefore_ItsCodeUnitsHashed()
    {
        // The canonical bytes of a one-string key are its UTF-16 code units: the hash a table of one string part computes
        // is XXH3 of those, as before keys had parts.
        ulong seen = 0;
        var table = Table(EntityKeyLayout.Single(EntityKeyType.String), new LocalFeatureTableOptions
        {
            KeyHasher = key =>
            {
                seen = KeyHashes.Default(key);
                return seen;
            },
        });
        Put(table.Writer, [EntityKey.FromString("k-1")], 1, 1);

        Assert.Equal(System.IO.Hashing.XxHash3.HashToUInt64(System.Runtime.InteropServices.MemoryMarshal.AsBytes("k-1".AsSpan())), seen);
        Assert.Equal(1.0, Score(table.Pin(), [EntityKey.FromString("k-1")]));
    }
}
