using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Local;

namespace StrataRec.UnitTests.Features.Local;

/// <summary>Schemas, values and read helpers shared by the local feature table tests.</summary>
internal static class LocalTableTestSupport
{
    /// <summary>One field of every storage type a catalog feature can have (design 4.3).</summary>
    public static readonly LocalTableField[] AllTypes =
    {
        new("b", ColumnType.Bool),
        new("i32", ColumnType.Int32),
        new("i64", ColumnType.Int64),
        new("f32", ColumnType.Float32),
        new("f64", ColumnType.Float64),
        new("s", ColumnType.String),
        new("t", ColumnType.Text),
        new("v", ColumnType.Vector(4)),
        new("li32", ColumnType.ListInt32),
        new("li64", ColumnType.ListInt64),
        new("ls", ColumnType.ListString),
    };

    public static LocalTableSchema Schema(
        EntityKeyType keyType = EntityKeyType.Int64, LocalTableMode mode = LocalTableMode.Local, IReadOnlyList<LocalTableField>? fields = null) =>
        new("item_store", "item", keyType, mode, fields ?? AllTypes);

    public static LocalFeatureTable Table(
        EntityKeyType keyType = EntityKeyType.Int64, LocalFeatureTableOptions? options = null, LocalTableMode mode = LocalTableMode.Local) =>
        new(Schema(keyType, mode), options);

    public static EntityKey Key(EntityKeyType keyType, long id) =>
        keyType == EntityKeyType.Int64 ? EntityKey.FromInt64(id) : EntityKey.FromString("k-" + id);

    /// <summary>A value of every field derived from <paramref name="seed"/>, as promoted scalars.</summary>
    public static Dictionary<string, ScalarValue> Values(long seed) => new(StringComparer.Ordinal)
    {
        ["b"] = ScalarValue.FromBool(seed % 2 == 0),
        ["i32"] = ScalarValue.FromInt64(int.MinValue + seed),
        ["i64"] = ScalarValue.FromInt64(long.MaxValue - seed),
        ["f32"] = ScalarValue.FromFloat64((float)(seed * 0.25)),
        ["f64"] = ScalarValue.FromFloat64(seed / 3.0),
        ["s"] = ScalarValue.FromString("cat-" + (seed % 5)),
        ["t"] = ScalarValue.FromText("text " + seed + " é中😀"),
        ["v"] = ScalarValue.FromVector(new[] { seed, seed + 0.5f, -seed, 1e-3f * seed }),
        ["li32"] = ScalarValue.FromInt64List(new long[] { seed, int.MaxValue, int.MinValue, -seed }[..(int)(seed % 4)]),
        ["li64"] = ScalarValue.FromInt64List(new[] { seed, long.MinValue, seed * 7 }),
        ["ls"] = ScalarValue.FromStringList(new[] { "tag-" + (seed % 3), "x", "tag-" + seed }[..(int)(1 + (seed % 3))]),
    };

    /// <summary>Writes (or partially writes) a record through the writer; returns what <see cref="RecordBuilder.Commit"/> returned.</summary>
    public static bool Write(IFeatureTableWriter writer, EntityKey key, long version, IReadOnlyDictionary<string, ScalarValue> values)
    {
        var builder = writer.Begin(key, version);
        foreach (var (name, value) in values)
        {
            builder.Set(writer.Schema.FieldIndex(name), value);
        }

        return builder.Commit();
    }

    /// <summary>Every field of a key read through <see cref="TableVersion.Read"/>, or null when the key has no record.</summary>
    public static Dictionary<string, ScalarValue>? ReadRecord(TableVersion version, LocalTableSchema schema, EntityKey key)
    {
        var record = version.Find(key);
        if (!record.Found)
        {
            return null;
        }

        var values = new Dictionary<string, ScalarValue>(StringComparer.Ordinal);
        for (var f = 0; f < schema.FieldCount; f++)
        {
            values[schema.Fields[f].Name] = version.Read(f, record);
        }

        return values;
    }

    /// <summary>
    /// Every field of every key read the way a request reads them: one lookup, then one <see cref="TableVersion.Fill(int, ReadOnlySpan{LocalRecordRef}, ReadOnlySpan{int}, ColumnBuffer, in ScalarValue, Span{ulong})"/> per
    /// field into a fresh column (rows in reverse order, to show rows need not follow keys). Row i of the result is key i.
    /// </summary>
    public static ScalarValue[][] FillAll(
        TableVersion version, LocalTableSchema schema, IReadOnlyList<EntityKey> keys, Func<int, ScalarValue>? absent = null)
    {
        var memory = new RequestMemory();
        try
        {
            var records = new LocalRecordRef[keys.Count];
            var keyArray = new EntityKey[keys.Count];
            for (var i = 0; i < keys.Count; i++)
            {
                keyArray[i] = keys[i];
            }

            version.Lookup(keyArray, records);
            var rows = new int[keys.Count];
            for (var i = 0; i < rows.Length; i++)
            {
                rows[i] = keys.Count - 1 - i;
            }

            var result = new ScalarValue[keys.Count][];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = new ScalarValue[schema.FieldCount];
            }

            for (var f = 0; f < schema.FieldCount; f++)
            {
                var column = ColumnBuffer.Create(schema.Fields[f].Type, Math.Max(1, keys.Count), memory);
                var filled = new ulong[Bits.WordCount(Math.Max(1, keys.Count))];
                version.Fill(f, records, rows, column, absent?.Invoke(f) ?? ScalarValue.Missing, filled);
                for (var i = 0; i < keys.Count; i++)
                {
                    Assert.True(Bits.Get(filled, rows[i]));
                    result[i][f] = ColumnValues.Get(column, rows[i]);
                }
            }

            return result;
        }
        finally
        {
            memory.Release();
        }
    }

    /// <summary>The expected promoted value of a field after a round trip (float32 storage narrows).</summary>
    public static ScalarValue Stored(ColumnType type, ScalarValue value) => value.IsMissing
        ? value
        : type.Kind switch
        {
            StorageKind.Float32 => ScalarValue.FromFloat64((float)value.AsFloat64()),
            StorageKind.Text => ScalarValue.FromText(value.AsString()),
            _ => value,
        };

    public static void AssertRecord(IReadOnlyDictionary<string, ScalarValue> expected, LocalTableSchema schema, ScalarValue[] actual)
    {
        for (var f = 0; f < schema.FieldCount; f++)
        {
            var name = schema.Fields[f].Name;
            var want = expected.TryGetValue(name, out var v) ? Stored(schema.Fields[f].Type, v) : ScalarValue.Missing;
            Assert.True(want.Equals(actual[f]), $"field {name}: expected {want}, got {actual[f]}");
        }
    }

    public static void AssertRecord(IReadOnlyDictionary<string, ScalarValue> expected, LocalTableSchema schema, IReadOnlyDictionary<string, ScalarValue>? actual)
    {
        Assert.NotNull(actual);
        var array = new ScalarValue[schema.FieldCount];
        for (var f = 0; f < schema.FieldCount; f++)
        {
            array[f] = actual![schema.Fields[f].Name];
        }

        AssertRecord(expected, schema, array);
    }

    /// <summary>A scratch directory removed at dispose.</summary>
    public sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "stratarec-local-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string name, string content)
        {
            var path = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
