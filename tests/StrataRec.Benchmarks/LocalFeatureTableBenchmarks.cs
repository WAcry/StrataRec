using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Local;

namespace StrataRec.Benchmarks;

/// <summary>
/// The request read path of a local feature table (design 6.6 target: 500 rows × 30 features in about 20 μs): one
/// lookup per key, then the features written into candidate-table columns.
/// </summary>
/// <remarks>
/// <para>The table holds 1,000,000 items with 30 features each, the shape of design 6.3's <c>item_store</c>: 25
/// <c>float32</c>, 2 <c>int64</c>, 1 <c>bool</c>, 1 low-cardinality <c>string</c> and a 64-dimensional embedding
/// (about 400 B per record, 400 MB in all). Each invocation reads the next of 64 random key sets, so a request's records
/// are not already in the core's caches from the previous one (a large L3 may still hold much of the table). A tenth of the
/// keys are not in the table.</para>
/// <para><c>LookupAndFill</c> fills all features in one call (rows in blocks, every feature per block);
/// <c>LookupAndFillPerField</c> makes one call per feature, so each feature walks all records again; <c>LookupOnly</c> is
/// the index part alone; <c>LookupAndFillWarm</c> repeats one key set, which leaves the CPU cost without cache and TLB
/// misses.</para>
/// <para>Run: <c>dotnet run -c Release --project tests/StrataRec.Benchmarks -- --filter '*LocalFeatureTableBenchmarks*'</c>.</para>
/// </remarks>
[MemoryDiagnoser]
public class LocalFeatureTableBenchmarks
{
    public const int Entities = 1_000_000;
    private const int KeySets = 64;

    private LocalFeatureTable _table = null!;
    private long[][] _keySets = null!;
    private int _next;
    private int[] _rows = null!;
    private LocalRecordRef[] _records = null!;
    private ColumnBuffer[] _columns = null!;
    private ScalarValue[] _absent = null!;
    private LocalFieldRead[] _reads = null!;
    private int[] _nonFinite = null!;
    private RequestMemory _memory = null!;

    [Params(500, 5_000)]
    public int Keys { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _table = new LocalFeatureTable(ItemSchema(), new LocalFeatureTableOptions { InitialCapacity = Entities });
        var writer = _table.Writer;
        for (var id = 1; id <= Entities; id++)
        {
            WriteItem(writer, id, version: 1);
        }

        var random = new Random(42);
        _keySets = new long[KeySets][];
        for (var s = 0; s < KeySets; s++)
        {
            _keySets[s] = new long[Keys];
            for (var i = 0; i < Keys; i++)
            {
                _keySets[s][i] = i % 10 == 0 ? Entities + 1 + i : 1 + random.Next(Entities);
            }
        }

        _rows = new int[Keys];
        for (var i = 0; i < Keys; i++)
        {
            _rows[i] = i;
        }

        _records = new LocalRecordRef[Keys];
        _memory = new RequestMemory();
        var fields = _table.Schema.Fields;
        _columns = new ColumnBuffer[fields.Count];
        _absent = new ScalarValue[fields.Count];
        _reads = new LocalFieldRead[fields.Count];
        _nonFinite = new int[fields.Count];
        for (var f = 0; f < fields.Count; f++)
        {
            _columns[f] = ColumnBuffer.Create(fields[f].Type, Keys, _memory);
            _absent[f] = fields[f].Type.Kind == StorageKind.Float32 ? ScalarValue.FromFloat64(0) : ScalarValue.Missing;
            _reads[f] = new LocalFieldRead(f, _columns[f], _absent[f]);
        }

        // Fail fast on a broken setup instead of measuring an error path.
        if (LookupAndFill() != Keys - (Keys / 10) || LookupAndFillPerField() != Keys - (Keys / 10))
        {
            throw new InvalidOperationException("Unexpected lookup result.");
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _memory.Release();

    [Benchmark]
    public int LookupAndFill()
    {
        var version = _table.Pin();
        var found = version.Lookup(NextKeys(), _records);
        version.Fill(_records, _rows, _reads, _nonFinite);
        return found;
    }

    [Benchmark]
    public int LookupAndFillPerField()
    {
        var version = _table.Pin();
        var found = version.Lookup(NextKeys(), _records);
        for (var f = 0; f < _columns.Length; f++)
        {
            version.Fill(f, _records, _rows, _columns[f], _absent[f]);
        }

        return found;
    }

    [Benchmark]
    public int LookupOnly() => _table.Pin().Lookup(NextKeys(), _records);

    /// <summary>The same key set every time: the request's records stay in the core's caches, which leaves the CPU cost.</summary>
    [Benchmark]
    public int LookupAndFillWarm()
    {
        var version = _table.Pin();
        var found = version.Lookup(_keySets[0], _records);
        version.Fill(_records, _rows, _reads, _nonFinite);
        return found;
    }

    private long[] NextKeys() => _keySets[_next++ & (KeySets - 1)];

    /// <summary>25 float32, 2 int64, 1 bool, 1 string, 1 vec&lt;float32,64&gt;.</summary>
    public static LocalTableSchema ItemSchema()
    {
        var fields = new List<LocalTableField>();
        for (var i = 0; i < 25; i++)
        {
            fields.Add(new LocalTableField("f" + i.ToString("D2", CultureInfo.InvariantCulture), ColumnType.Float32));
        }

        fields.Add(new LocalTableField("creator_id", ColumnType.Int64));
        fields.Add(new LocalTableField("published_at", ColumnType.Int64));
        fields.Add(new LocalTableField("is_safe", ColumnType.Bool));
        fields.Add(new LocalTableField("category", ColumnType.String));
        fields.Add(new LocalTableField("embedding", ColumnType.Vector(64)));
        return new LocalTableSchema("item_store", "item", EntityKeyType.Int64, LocalTableMode.Local, fields);
    }

    public static void WriteItem(IFeatureTableWriter writer, long id, long version)
    {
        var builder = writer.Begin(id, version);
        for (var i = 0; i < 25; i++)
        {
            builder.SetFloat32(i, (id % 1000) + i);
        }

        builder.SetInt64(25, id % 120_000);
        builder.SetInt64(26, 1_700_000_000 + id);
        builder.SetBool(27, id % 7 != 0);
        builder.SetString(28, Categories[id % Categories.Length]);
        var embedding = builder.VectorBuffer(29);
        for (var j = 0; j < embedding.Length; j++)
        {
            embedding[j] = ((id + j) % 100) * 0.01f;
        }

        builder.Commit();
    }

    internal static readonly string[] Categories = CreateCategories();

    private static string[] CreateCategories()
    {
        var categories = new string[200];
        for (var i = 0; i < categories.Length; i++)
        {
            categories[i] = "category-" + i.ToString(CultureInfo.InvariantCulture);
        }

        return categories;
    }
}

/// <summary>
/// Load throughput of a local table: 100,000 of the 30-feature records of <see cref="LocalFeatureTableBenchmarks"/>,
/// written through the record builder (what an update stream consumer does), or parsed from a JSON Lines snapshot by
/// <c>snapshot_files@1</c>. Records per second = 100,000 / the measured time.
/// </summary>
/// <remarks>Run: <c>dotnet run -c Release --project tests/StrataRec.Benchmarks -- --filter '*LocalFeatureTableLoadBenchmarks*'</c>.</remarks>
[MemoryDiagnoser]
public class LocalFeatureTableLoadBenchmarks
{
    public const int Records = 100_000;

    private string _directory = null!;
    private SnapshotFilesUpdateSource _snapshot = null!;

    [GlobalSetup]
    public void Setup()
    {
        _directory = Directory.CreateTempSubdirectory("stratarec-snapshot-bench-").FullName;
        var text = new StringBuilder();
        using (var file = new StreamWriter(Path.Combine(_directory, "part-0000.jsonl"), append: false, new UTF8Encoding(false)))
        {
            for (var id = 1; id <= Records; id++)
            {
                text.Clear().Append("{\"key\":").Append(id).Append(",\"version\":1,\"fields\":{");
                for (var i = 0; i < 25; i++)
                {
                    text.Append("\"f").Append(i.ToString("D2", CultureInfo.InvariantCulture)).Append("\":").Append((id % 1000) + i).Append(',');
                }

                text.Append("\"creator_id\":").Append(id % 120_000).Append(",\"published_at\":").Append(1_700_000_000 + id)
                    .Append(",\"is_safe\":").Append(id % 7 != 0 ? "true" : "false")
                    .Append(",\"category\":\"").Append(LocalFeatureTableBenchmarks.Categories[id % 200]).Append("\",\"embedding\":[");
                for (var j = 0; j < 64; j++)
                {
                    text.Append(j == 0 ? string.Empty : ",").Append((((id + j) % 100) * 0.01f).ToString("R", CultureInfo.InvariantCulture));
                }

                file.Write(text.Append("]}}\n"));
            }
        }

        File.WriteAllText(Path.Combine(_directory, SnapshotFilesUpdateSource.ManifestFileName), "{\"position\": {\"0\": 100000}, \"files\": [\"part-0000.jsonl\"]}");
        _snapshot = new SnapshotFilesUpdateSource(Path.Combine(_directory, SnapshotFilesUpdateSource.ManifestFileName));
        if (SnapshotLoad() != Records)
        {
            throw new InvalidOperationException("Unexpected snapshot load result.");
        }
    }

    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    [Benchmark]
    public int WriteRecords()
    {
        var table = new LocalFeatureTable(LocalFeatureTableBenchmarks.ItemSchema());
        for (var id = 1; id <= Records; id++)
        {
            LocalFeatureTableBenchmarks.WriteItem(table.Writer, id, version: 1);
        }

        return table.Pin().RecordCount;
    }

    [Benchmark]
    public int SnapshotLoad()
    {
        var table = new LocalFeatureTable(LocalFeatureTableBenchmarks.ItemSchema());
        _snapshot.Load(table.Writer);
        return table.Pin().RecordCount;
    }
}
