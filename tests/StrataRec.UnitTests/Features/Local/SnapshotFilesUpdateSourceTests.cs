using System.Text.Json;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Local;
using static StrataRec.UnitTests.Features.Local.LocalTableTestSupport;

namespace StrataRec.UnitTests.Features.Local;

public class SnapshotFilesUpdateSourceTests
{
    private const string FullLine =
        """{"key": 1, "version": 3, "fields": {"b": true, "i32": -2147483648, "i64": 9223372036854775807, "f32": 0.25, "f64": -1e300, "s": "cat", "t": "multi\nline é", "v": [1, 2.5, -3, 4e-3], "li32": [1, 2147483647], "li64": [-9223372036854775808], "ls": ["a", "b\"q"]}}""";

    [Fact]
    public void Load_DecodesEveryTypeAndLineForm()
    {
        using var dir = new TempDirectory();
        dir.Write("part-0.jsonl", "﻿" + FullLine + "\r\n" +
            // Fields the table does not hold are skipped, whatever their shape; null is missing; NaN strings are kept.
            """{"version": 1, "fields": {"unknown": {"deep": [1, {"x": null}]}, "f32": "NaN", "f64": "-Infinity", "v": ["Infinity", 0, 0, 0], "s": null}, "key": 2}""" + "\n" +
            "\n   \n" +
            // A line without fields is a record with no values; a later line of the same key with an older version is ignored.
            """{"key": 3, "version": 9}""" + "\n" +
            """{"key": 3, "version": 8, "fields": {"i64": 1}}""" + "\n" +
            """{"key": 4, "version": 1, "fields": {"i64": 4}}""" + "\n" +
            """{"key": 4, "version": 2, "deleted": true}""");
        // A number beyond float32 reads as +∞, exactly as memory@1 reads it; the read path replaces it (design 4.3).
        dir.Write("more/part-1.jsonl", """{"key": 5, "version": 1, "fields": {"i64": 5, "t": "😀", "f32": 1e39}}""");
        dir.Write("snapshot.json", """{"format": "jsonl", "position": {"0": 120, "3": 7}, "files": ["part-0.jsonl", "more/part-1.jsonl"]}""");

        var table = Table();
        var position = new SnapshotFilesUpdateSource(Path.Combine(dir.Path, "snapshot.json")).Load(table.Writer);

        Assert.Equal(new UpdatePosition(new[] { new PartitionOffset(3, 7), new PartitionOffset(0, 120) }), position);
        Assert.Equal(position, table.Position);
        var version = table.Pin();
        AssertRecord(new Dictionary<string, ScalarValue>
        {
            ["b"] = ScalarValue.FromBool(true),
            ["i32"] = ScalarValue.FromInt64(int.MinValue),
            ["i64"] = ScalarValue.FromInt64(long.MaxValue),
            ["f32"] = ScalarValue.FromFloat64(0.25),
            ["f64"] = ScalarValue.FromFloat64(-1e300),
            ["s"] = ScalarValue.FromString("cat"),
            ["t"] = ScalarValue.FromText("multi\nline é"),
            ["v"] = ScalarValue.FromVector(new[] { 1f, 2.5f, -3f, 4e-3f }),
            ["li32"] = ScalarValue.FromInt64List(new long[] { 1, int.MaxValue }),
            ["li64"] = ScalarValue.FromInt64List(new[] { long.MinValue }),
            ["ls"] = ScalarValue.FromStringList(new[] { "a", "b\"q" }),
        }, table.Schema, ReadRecord(version, table.Schema, EntityKey.FromInt64(1)));

        var two = ReadRecord(version, table.Schema, EntityKey.FromInt64(2))!;
        Assert.True(double.IsNaN(two["f32"].AsFloat64()));
        Assert.Equal(double.NegativeInfinity, two["f64"].AsFloat64());
        Assert.Equal(float.PositiveInfinity, two["v"].AsVector()[0]);
        Assert.True(two["s"].IsMissing);

        Assert.True(ReadRecord(version, table.Schema, EntityKey.FromInt64(3))!["i64"].IsMissing);
        Assert.False(version.Find(4L).Found);
        Assert.Equal(1, version.TombstoneCount);
        Assert.Equal("😀", ReadRecord(version, table.Schema, EntityKey.FromInt64(5))!["t"].AsString());
        Assert.Equal(double.PositiveInfinity, ReadRecord(version, table.Schema, EntityKey.FromInt64(5))!["f32"].AsFloat64());
        Assert.Equal(4, version.RecordCount);
    }

    [Fact]
    public void Load_StringKeys()
    {
        using var dir = new TempDirectory();
        dir.Write("a.jsonl", """{"key": "ué-1", "version": 1, "fields": {"i64": 1}}""" + "\n" + """{"key": "u-2", "version": 1, "fields": {"i64": 2}}""");
        dir.Write("snapshot.json", """{"files": ["a.jsonl"]}""");
        var table = Table(EntityKeyType.String);
        Assert.True(new SnapshotFilesUpdateSource(Path.Combine(dir.Path, "snapshot.json")).Load(table.Writer).IsEmpty);
        Assert.Equal(1, table.Pin().Read(table.Schema.FieldIndex("i64"), table.Pin().Find("ué-1")).AsInt64());
        Assert.Equal(2, table.Pin().Read(table.Schema.FieldIndex("i64"), table.Pin().Find("u-2")).AsInt64());
    }

    public static TheoryData<string, string, string> BadLines => new()
    {
        { "{\"key\": 1, \"version\": 1, \"fields\": {\"i32\": 2147483648}}", "/fields/i32", "int64" },
        { "{\"key\": 1, \"version\": 1, \"fields\": {\"i32\": 1.5}}", "/fields/i32", "int64" },
        { "{\"key\": 1, \"version\": 1, \"fields\": {\"f32\": true}}", "/fields/f32", "float32" },
        { "{\"key\": 1, \"version\": 1, \"fields\": {\"f64\": \"nan\"}}", "/fields/f64", "\"NaN\"" },
        { "{\"key\": 1, \"version\": 1, \"fields\": {\"b\": 1}}", "/fields/b", "bool" },
        { "{\"key\": 1, \"version\": 1, \"fields\": {\"s\": 5}}", "/fields/s", "string" },
        { "{\"key\": 1, \"version\": 1, \"fields\": {\"v\": [1, 2, 3]}}", "/fields/v", "exactly 4" },
        { "{\"key\": 1, \"version\": 1, \"fields\": {\"v\": [1, 2, 3, 4, 5]}}", "/fields/v", "exactly 4" },
        { "{\"key\": 1, \"version\": 1, \"fields\": {\"v\": [1, \"x\", 3, 4]}}", "/fields/v/1", "\"NaN\"" },
        { "{\"key\": 1, \"version\": 1, \"fields\": {\"li32\": [1, 3000000000]}}", "/fields/li32/1", "int64" },
        { "{\"key\": 1, \"version\": 1, \"fields\": {\"li64\": [1, 2.5]}}", "/fields/li64/1", "fraction" },
        { "{\"key\": 1, \"version\": 1, \"fields\": {\"ls\": [\"a\", null]}}", "/fields/ls/1", "null" },
        { "{\"key\": 1, \"version\": 1, \"fields\": {\"li64\": 5}}", "/fields/li64", "list<int64>" },
        { "{\"key\": 1, \"version\": 1, \"fields\": {\"i64\": 1, \"i64\": 2}}", "/fields/i64", "once" },
        { "{\"key\": \"1\", \"version\": 1}", "/key", "JSON number" },
        { "{\"key\": 1, \"version\": \"7\"}", "/version", "event version" },
        { "{\"key\": 1}", "", "\"version\": 17" },
        { "{\"version\": 1}", "", "\"key\": 10042" },
        { "{\"key\": 1, \"version\": 1, \"fileds\": {}}", "/fileds", "'fields'" },
        { "{\"key\": 1, \"key\": 2, \"version\": 1}", "/key", "once" },
        { "{\"key\": 1, \"version\": 2, \"deleted\": true, \"fields\": {}}", "/fields", "drop" },
        { "{\"key\": 1, \"version\": 1, \"deleted\": \"yes\"}", "/deleted", "true or false" },
        { "{\"key\": 1, \"version\": 1, \"fields\": []}", "/fields", "object" },
        { "[1, 2]", "", "JSON object" },
        { "{\"key\": 1, \"version\": 1} {}", "", "one JSON object" },
        { "{\"key\": 1, \"version\": 1, \"fields\": {\"i64\": 1,}}", "", "one JSON object" },
        { "{\"key\": 1, \"version\": 1", "", "one JSON object" },
    };

    [Theory]
    [MemberData(nameof(BadLines))]
    public void ParseErrors_NameTheFileLinePointerAndFix(string badLine, string pointer, string hint)
    {
        using var dir = new TempDirectory();
        var file = dir.Write("part.jsonl", FullLine + "\n\n" + badLine + "\n" + FullLine);
        dir.Write("snapshot.json", """{"files": ["part.jsonl"]}""");
        var table = Table();

        var error = Assert.Throws<FeatureUpdateException>(() => new SnapshotFilesUpdateSource(Path.Combine(dir.Path, "snapshot.json")).Load(table.Writer));

        Assert.Equal(file + ":3", error.Location);
        Assert.Equal(pointer, error.Pointer);
        Assert.NotNull(error.Hint);
        Assert.Contains(hint, error.Reason + " " + error.Hint, StringComparison.Ordinal);
        Assert.StartsWith(file + ":3" + (pointer.Length == 0 ? ":" : " " + pointer + ":"), error.Message, StringComparison.Ordinal);

        // The failed load was a reload: nothing of it is visible and the writer accepts the next one.
        Assert.False(table.Writer.IsReloading);
        Assert.Equal(0, table.Pin().RecordCount);
        Assert.True(table.Position.IsEmpty);
    }

    public static TheoryData<string, string, string> BadManifests => new()
    {
        { "{\"files\": \"part.jsonl\"}", "/files", "\"files\": [" },
        { "{\"files\": [\"\"]}", "/files/0", "relative" },
        { "{\"files\": [], \"position\": {\"zero\": 1}}", "/position/zero", "partition" },
        { "{\"files\": [], \"position\": {\"0\": -1}}", "/position/0", "partition" },
        { "{\"files\": [], \"format\": \"parquet\"}", "/format", "jsonl" },
        { "{\"files\": [], \"generation\": 3}", "/generation", "'files'" },
        { "{\"position\": {}}", "", "list the data files" },
        { "[]", "", "\"files\"" },
        { "{\"files\": [\"missing.jsonl\"]}", "", "before the manifest" },
    };

    [Theory]
    [MemberData(nameof(BadManifests))]
    public void ManifestErrors_NameTheMemberAndFix(string manifest, string pointer, string hint)
    {
        using var dir = new TempDirectory();
        var path = dir.Write("snapshot.json", manifest);
        var table = Table();

        var error = Assert.Throws<FeatureUpdateException>(() => new SnapshotFilesUpdateSource(path).Load(table.Writer));

        Assert.EndsWith(pointer.Length == 0 && hint.Contains("before", StringComparison.Ordinal) ? "missing.jsonl" : "snapshot.json", error.Location, StringComparison.Ordinal);
        Assert.Equal(pointer, error.Pointer);
        Assert.Contains(hint, error.Reason + " " + error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingManifest_IsReported()
    {
        using var dir = new TempDirectory();
        var error = Assert.Throws<FeatureUpdateException>(() => new SnapshotFilesUpdateSource(Path.Combine(dir.Path, "snapshot.json")).Load(Table().Writer));
        Assert.Contains("snapshot.json", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Reload_SwapsTheWholeTable_AndAFailedReloadKeepsTheOldOne()
    {
        using var dir = new TempDirectory();
        var manifest = Path.Combine(dir.Path, "snapshot.json");
        dir.Write("gen-1.jsonl", Lines(1, 100, version: 1));
        dir.Write("snapshot.json", """{"position": {"0": 100}, "files": ["gen-1.jsonl"]}""");
        var table = Table();
        var source = new SnapshotFilesUpdateSource(manifest);
        source.Load(table.Writer);
        var first = table.Pin();

        // The next generation drops keys 1-50 and adds 101-150; versions start afresh.
        dir.Write("gen-2.jsonl", Lines(51, 150, version: 0));
        dir.Write("snapshot.json", """{"position": {"0": 250}, "files": ["gen-2.jsonl"]}""");
        source.Load(table.Writer);
        var second = table.Pin();

        Assert.NotSame(first, second);
        Assert.Equal(100, second.RecordCount);
        Assert.False(second.Find(1L).Found);
        Assert.True(second.Find(150L).Found);
        Assert.Equal(UpdatePosition.Single(250), table.Position);
        Assert.True(first.Find(1L).Found); // a request that pinned the old version keeps reading it

        // A broken generation: the load fails at its line, and the table keeps the second generation.
        dir.Write("gen-3.jsonl", Lines(1, 10, version: 1) + "\n{\"key\": 11}");
        dir.Write("snapshot.json", """{"position": {"0": 300}, "files": ["gen-3.jsonl"]}""");
        var error = Assert.Throws<FeatureUpdateException>(() => source.Load(table.Writer));
        Assert.EndsWith("gen-3.jsonl:11", error.Location, StringComparison.Ordinal);
        Assert.Same(second, table.Pin());
        Assert.Equal(UpdatePosition.Single(250), table.Position);
    }

    [Fact]
    public async Task RunAsync_LoadsOnce_AndSkipsASnapshotTheTableAlreadyHas()
    {
        using var dir = new TempDirectory();
        dir.Write("a.jsonl", Lines(1, 10, version: 1));
        dir.Write("snapshot.json", """{"position": {"0": 10}, "files": ["a.jsonl"]}""");
        var source = new SnapshotFilesUpdateSource(Path.Combine(dir.Path, "snapshot.json"));

        var table = Table();
        await source.RunAsync(UpdatePosition.Empty, table.Writer, CancellationToken.None);
        Assert.Equal(10, table.Pin().RecordCount);

        // Resuming a table that already covers the snapshot's position does not rebuild it.
        var resumed = Table();
        await source.RunAsync(UpdatePosition.Single(10), resumed.Writer, CancellationToken.None);
        Assert.Equal(0, resumed.Pin().RecordCount);
    }

    [Fact]
    public async Task RunAsync_WithReload_PicksUpNewSnapshotsUntilCancelled()
    {
        using var dir = new TempDirectory();
        dir.Write("gen-1.jsonl", Lines(1, 5, version: 1));
        dir.Write("snapshot.json", """{"position": {"0": 5}, "files": ["gen-1.jsonl"]}""");
        var table = Table();
        var source = new SnapshotFilesUpdateSource(Path.Combine(dir.Path, "snapshot.json"), TimeSpan.FromMilliseconds(5));
        using var cancel = new CancellationTokenSource();
        var run = source.RunAsync(UpdatePosition.Empty, table.Writer, cancel.Token);

        await WaitFor(() => table.Pin().RecordCount == 5);
        var loaded = table.Pin();
        dir.Write("gen-2.jsonl", Lines(1, 8, version: 1));

        // Published the way a snapshot job does it: data first, then the manifest replaced by a rename.
        var staged = dir.Write("snapshot.json.tmp", """{"position": {"0": 8}, "files": ["gen-2.jsonl"]}""");
        File.Move(staged, Path.Combine(dir.Path, "snapshot.json"), overwrite: true);
        await WaitFor(() => table.Pin().RecordCount == 8);
        Assert.NotSame(loaded, table.Pin());

        // An unchanged manifest is not reloaded: the version stays the same across several periods.
        var current = table.Pin();
        await Task.Delay(50);
        Assert.Same(current, table.Pin());

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public void Factory_ResolvesTheUriAndParsesReload()
    {
        using var dir = new TempDirectory();
        var factory = new SnapshotFilesUpdateSourceFactory();
        var relative = (SnapshotFilesUpdateSource)factory.Create("item_store", Settings("""{"source": "snapshot_files@1", "uri": "snapshots/items", "reload": "PT1H"}"""), dir.Path);
        Assert.Equal(Path.Combine(dir.Path, "snapshots/items", "snapshot.json"), relative.ManifestPath);
        Assert.Equal(TimeSpan.FromHours(1), relative.Reload);

        var uri = (SnapshotFilesUpdateSource)factory.Create("item_store", Settings($$"""{"uri": "{{new Uri(Path.Combine(dir.Path, "m.json")).AbsoluteUri}}"}"""), null);
        Assert.Equal(Path.Combine(dir.Path, "m.json"), uri.ManifestPath);
        Assert.Null(uri.Reload);
    }

    [Theory]
    [InlineData("""{"uri": "s3://features/items/"}""", "/uri", "local directory")]
    [InlineData("""{"uri": ""}""", "/uri", "file://")]
    [InlineData("""{"uri": 5}""", "/uri", "file://")]
    [InlineData("""{"reload": "PT1H"}""", "", "\"uri\"")]
    [InlineData("""{"uri": "x", "reload": "1h"}""", "/reload", "PT1H")]
    [InlineData("""{"uri": "x", "reload": "-PT1H"}""", "/reload", "PT1H")]
    [InlineData("""{"uri": "x", "reload": 3600}""", "/reload", "PT1H")]
    [InlineData("""{"uri": "x", "reloads": "PT1H"}""", "/reloads", "'reload'")]
    public void Factory_SettingErrors_NameTheSetting(string settings, string pointer, string hint)
    {
        var error = Assert.Throws<FeatureUpdateException>(() => new SnapshotFilesUpdateSourceFactory().Create("item_store", Settings(settings), null));
        Assert.Contains("item_store", error.Location, StringComparison.Ordinal);
        Assert.Equal(pointer, error.Pointer);
        Assert.Contains(hint, error.Hint, StringComparison.Ordinal);
    }

    /// <summary>
    /// JSON string contents (as written in the file) and the value they decode to. The decode buffer starts at 256
    /// chars and grows when a value's escaped UTF-8 form is longer, so the cases sit on both sides of that size, and
    /// include values whose bytes pass it while their chars do not (multi-byte UTF-8, escapes).
    /// </summary>
    public static IEnumerable<object[]> LongStrings() => new[]
    {
        new object[] { new string('a', 256), new string('a', 256) },
        new object[] { new string('a', 257), new string('a', 257) },
        new object[] { new string('汉', 100), new string('汉', 100) },
        new object[] { string.Concat(Enumerable.Repeat("\\u0041", 60)) + "\\n\\\"\\\\", new string('A', 60) + "\n\"\\" },
        new object[] { string.Concat(Enumerable.Repeat("\\ud83d\\ude00", 30)), string.Concat(Enumerable.Repeat("😀", 30)) },
    };

    [Theory]
    [MemberData(nameof(LongStrings))]
    public void Load_StringsThatGrowTheDecodeBuffer_ReadBackExactly(string json, string value)
    {
        // Each value is a key, a string, a text and list items. The line before has short values; the line after has
        // the value twice and three times over, so the buffers grow again and are reused at every size.
        using var dir = new TempDirectory();
        dir.Write("a.jsonl",
            "{\"key\": \"short\", \"version\": 1, \"fields\": {\"s\": \"x\", \"t\": \"y\", \"ls\": [\"z\"]}}\n" +
            "{\"key\": \"" + json + "\", \"version\": 1, \"fields\": {\"s\": \"" + json + "\", \"t\": \"" + json + "\", \"ls\": [\"" + json + "\", \"q\", \"" + json + "\"]}}\n" +
            "{\"key\": \"after\", \"version\": 1, \"fields\": {\"s\": \"" + json + json + "\", \"t\": \"" + json + json + json + "\", \"ls\": [\"" + json + json + json + "\"]}}");
        dir.Write("snapshot.json", """{"files": ["a.jsonl"]}""");
        var table = Table(EntityKeyType.String);
        new SnapshotFilesUpdateSource(Path.Combine(dir.Path, "snapshot.json")).Load(table.Writer);

        var version = table.Pin();
        Assert.Equal(3, version.RecordCount);
        var shortRecord = ReadRecord(version, table.Schema, EntityKey.FromString("short"))!;
        Assert.Equal("x", shortRecord["s"].AsString());
        Assert.Equal(new[] { "z" }, shortRecord["ls"].AsStringList().ToArray());

        var record = ReadRecord(version, table.Schema, EntityKey.FromString(value));
        Assert.NotNull(record);
        Assert.Equal(value, record["s"].AsString());
        Assert.Equal(value, record["t"].AsString());
        Assert.Equal(new[] { value, "q", value }, record["ls"].AsStringList().ToArray());

        var after = ReadRecord(version, table.Schema, EntityKey.FromString("after"))!;
        Assert.Equal(value + value, after["s"].AsString());
        Assert.Equal(value + value + value, after["t"].AsString());
        Assert.Equal(new[] { value + value + value }, after["ls"].AsStringList().ToArray());
    }

    [Fact]
    public void Load_WithACancelledToken_ChangesNothing()
    {
        using var dir = new TempDirectory();
        var (table, source, loaded) = LoadFirstGeneration(dir);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.Throws<OperationCanceledException>(() => source.Load(table.Writer, cancel.Token));
        AssertUnchanged(table, loaded, source);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Load_CancelledWhileLoading_AbortsTheReload(int cancelAtLine)
    {
        // A two-line snapshot: far fewer lines than the hot loop's periodic check, so only the checks at the file
        // boundary and before the swap can see the cancellation.
        using var dir = new TempDirectory();
        var (table, source, loaded) = LoadFirstGeneration(dir);
        using var cancel = new CancellationTokenSource();
        var writer = new CancellingWriter(table.Writer, cancel, cancelAtLine);

        Assert.Throws<OperationCanceledException>(() => source.Load(writer, cancel.Token));
        AssertUnchanged(table, loaded, source);
    }

    [Fact]
    public void Load_CancelledInOneFile_DoesNotReadTheNext()
    {
        using var dir = new TempDirectory();
        var (table, source, loaded) = LoadFirstGeneration(dir);
        dir.Write("b.jsonl", Lines(10, 11, version: 1));
        dir.Write("c.jsonl", Lines(12, 13, version: 1));
        dir.Write("snapshot.json", """{"position": {"0": 13}, "files": ["b.jsonl", "c.jsonl"]}""");
        using var cancel = new CancellationTokenSource();
        var writer = new CancellingWriter(table.Writer, cancel, cancelAtBegin: 1);

        Assert.Throws<OperationCanceledException>(() => source.Load(writer, cancel.Token));
        Assert.Equal(2, writer.Begins); // b.jsonl finished; c.jsonl never opened
        AssertUnchanged(table, loaded, source);
    }

    [Fact]
    public async Task RunAsync_CancelledDuringTheFirstLoad_PublishesNothing()
    {
        using var dir = new TempDirectory();
        dir.Write("a.jsonl", Lines(1, 2, version: 1));
        dir.Write("snapshot.json", """{"position": {"0": 2}, "files": ["a.jsonl"]}""");
        var table = Table();
        var empty = table.Pin();
        using var cancel = new CancellationTokenSource();
        var writer = new CancellingWriter(table.Writer, cancel, cancelAtBegin: 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new SnapshotFilesUpdateSource(Path.Combine(dir.Path, "snapshot.json")).RunAsync(UpdatePosition.Empty, writer, cancel.Token));
        Assert.Same(empty, table.Pin());
        Assert.Equal(UpdatePosition.Empty, table.Position);
        Assert.False(table.Writer.IsReloading);
    }

    /// <summary>Loads a first generation (keys 1-3, position 3), then publishes a second two-line one to be cancelled.</summary>
    private static (LocalFeatureTable Table, SnapshotFilesUpdateSource Source, TableVersion Loaded) LoadFirstGeneration(TempDirectory dir)
    {
        dir.Write("gen-1.jsonl", Lines(1, 3, version: 1));
        dir.Write("snapshot.json", """{"position": {"0": 3}, "files": ["gen-1.jsonl"]}""");
        var table = Table();
        var source = new SnapshotFilesUpdateSource(Path.Combine(dir.Path, "snapshot.json"));
        source.Load(table.Writer);
        dir.Write("gen-2.jsonl", Lines(4, 5, version: 1));
        dir.Write("snapshot.json", """{"position": {"0": 5}, "files": ["gen-2.jsonl"]}""");
        return (table, source, table.Pin());
    }

    /// <summary>The first generation is still current and complete; then the same snapshot loads without cancellation.</summary>
    private static void AssertUnchanged(LocalFeatureTable table, TableVersion loaded, SnapshotFilesUpdateSource source)
    {
        Assert.Same(loaded, table.Pin());
        Assert.Equal(UpdatePosition.Single(3), table.Position);
        Assert.False(table.Writer.IsReloading);
        Assert.Equal(3, table.Pin().RecordCount);
        Assert.Equal(3L, table.Pin().Read(table.Schema.FieldIndex("i64"), table.Pin().Find(3L)).AsInt64());

        var position = source.Load(table.Writer);
        Assert.Equal(position, table.Position);
        Assert.NotSame(loaded, table.Pin());
        Assert.False(table.Pin().Find(1L).Found);
    }

    /// <summary>Passes every call to the table's writer and cancels the token at the n-th <c>Begin</c>.</summary>
    private sealed class CancellingWriter(IFeatureTableWriter inner, CancellationTokenSource cancel, int cancelAtBegin) : IFeatureTableWriter
    {
        public int Begins { get; private set; }

        public LocalTableSchema Schema => inner.Schema;

        public RecordBuilder Begin(long key, long version)
        {
            Count();
            return inner.Begin(key, version);
        }

        public RecordBuilder Begin(ReadOnlySpan<char> key, long version)
        {
            Count();
            return inner.Begin(key, version);
        }

        public RecordBuilder Begin(in EntityKey key, long version)
        {
            Count();
            return inner.Begin(key, version);
        }

        public bool Delete(long key, long version) => inner.Delete(key, version);

        public bool Delete(ReadOnlySpan<char> key, long version) => inner.Delete(key, version);

        public bool Delete(in EntityKey key, long version) => inner.Delete(key, version);

        public void Checkpoint(UpdatePosition position) => inner.Checkpoint(position);

        public void BeginReload() => inner.BeginReload();

        public void CompleteReload(UpdatePosition position) => inner.CompleteReload(position);

        public void AbortReload() => inner.AbortReload();

        private void Count()
        {
            if (++Begins == cancelAtBegin)
            {
                cancel.Cancel();
            }
        }
    }

    private static string Lines(int from, int to, long version)
    {
        var lines = new List<string>();
        for (var id = from; id <= to; id++)
        {
            lines.Add("{\"key\": " + id + ", \"version\": " + version + ", \"fields\": {\"i64\": " + id + ", \"s\": \"c" + (id % 3) + "\"}}");
        }

        return string.Join("\n", lines);
    }

    private static Dictionary<string, JsonElement> Settings(string json)
    {
        using var document = JsonDocument.Parse(json);
        var settings = new Dictionary<string, JsonElement>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            settings[property.Name] = property.Value.Clone();
        }

        return settings;
    }

    /// <summary>Waits for a condition the background source makes true; the timeout only bounds a broken run.</summary>
    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the source did not get there within 30 s");
            await Task.Delay(5);
        }
    }
}
