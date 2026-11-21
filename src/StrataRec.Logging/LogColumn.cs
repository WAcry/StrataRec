using System.Collections.Immutable;
using StrataRec.Core.Columns;
using System.Runtime.CompilerServices;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.Logging;

/// <summary>
/// One logged column of a batch (design 8.7), laid out as Arrow lays out a column: the values of the batch's rows in one
/// typed array, a validity bitmap, and for a list the offsets of each row's elements. A batch owns its columns, so the
/// request's candidate table can go back to the pool while the batch waits in a queue.
/// </summary>
/// <remarks>
/// <para>Columns used to be one <see cref="ScalarValue"/> per row (24 bytes) and a copied array per list row: a candidate tap
/// of 1,000 rows and 10 numeric columns held 240 KB of values; typed it holds 80 KB in 10 arrays (40 KB when they are
/// float32 or int32), and a list column is one array of elements instead of one per row. A sink reads the arrays
/// directly; <see cref="Get"/> gives a row as a value, for cold readers.</para>
/// <para>The arrays have the column's <see cref="Type"/>, so a sink whose own types are narrow (an int32 feature log, a
/// float32 training table) writes them as they are: an <c>int32</c> column is an <c>int[]</c>, a
/// <c>list&lt;float32,64&gt;</c> embedding a <c>float[]</c> of 64 elements per row.</para>
/// </remarks>
public sealed class LogColumn
{
    private LogColumn(string name, ColumnType type, int rowCount, Array values, int[]? offsets, ulong[]? validity)
    {
        Name = name;
        Type = type;
        RowCount = rowCount;
        Values = values;
        Offsets = offsets;
        Validity = validity;
    }

    public string Name { get; }

    /// <summary>The field's type (<see cref="LogField.Type"/>): for a column logged as a plain reference to a column,
    /// feature or context value, the type that is declared with (<c>int32</c>, <c>float32?</c>,
    /// <c>list&lt;float32,64&gt;</c>); for a computed expression its promoted result type (<c>bool</c>, <c>int64</c>,
    /// <c>float64</c>, <c>string</c>, or lists of those).</summary>
    public ColumnType Type { get; }

    public int RowCount { get; }

    /// <summary>
    /// The values, in the storage of <see cref="Type"/>: for a scalar one per row, a <c>bool[]</c>, <c>int[]</c>
    /// (<c>int32</c>), <c>long[]</c>, <c>float[]</c> (<c>float32</c>), <c>double[]</c> or <c>string?[]</c>; for a list the
    /// elements of every row in row order (<c>bool[]</c>, <c>int[]</c>, <c>long[]</c>, <c>float[]</c>, <c>double[]</c> or
    /// <c>string[]</c>), which <see cref="Offsets"/> delimits, a fixed-length list included. A missing row holds zeros
    /// (null for a string) and a missing list row no elements. Every float is finite: a non-finite one is missing.
    /// </summary>
    public Array Values { get; }

    /// <summary>For a list, <see cref="RowCount"/> + 1 entries: row r's elements are
    /// <c>Values[Offsets[r]..Offsets[r + 1]]</c>. Null for other types.</summary>
    public int[]? Offsets { get; }

    /// <summary>Bit r set when row r has a value (<see cref="Bits"/>); null when every row has one.</summary>
    public ulong[]? Validity { get; }

    public bool IsMissing(int row) => Validity is { } validity && !Bits.Get(validity, row);

    /// <summary>Row <paramref name="row"/> as a value (promoted, as every value is); a list row is copied. For tests and
    /// cold readers.</summary>
    public ScalarValue Get(int row)
    {
        if (IsMissing(row))
        {
            return ScalarValue.Missing;
        }

        if (Offsets is { } offsets)
        {
            var (start, length) = (offsets[row], offsets[row + 1] - offsets[row]);
            return Values switch
            {
                bool[] b => ColumnValues.ListValue<bool>(b.AsSpan(start, length)),
                int[] i => ColumnValues.ListValue<int>(i.AsSpan(start, length)),
                long[] l => ColumnValues.ListValue<long>(l.AsSpan(start, length)),
                float[] f => ColumnValues.ListValue<float>(f.AsSpan(start, length)),
                double[] d => ColumnValues.ListValue<double>(d.AsSpan(start, length)),
                string[] s => ColumnValues.ListValue<string>(s.AsSpan(start, length)),
                _ => throw new InvalidOperationException($"Unexpected list values {Values.GetType().Name}."),
            };
        }

        return Values switch
        {
            bool[] b => ScalarValue.FromBool(b[row]),
            int[] i => ScalarValue.FromInt64(i[row]),
            long[] l => ScalarValue.FromInt64(l[row]),
            float[] f => ScalarValue.FromFloat32(f[row]),
            double[] d => ScalarValue.FromFloat64(d[row]),
            string?[] s => ScalarValue.FromString(s[row]),
            _ => throw new InvalidOperationException($"Unexpected values {Values.GetType().Name}."),
        };
    }

    /// <summary>A column of <paramref name="rowCount"/> missing rows: what a tap logs for a column that reads a feature
    /// whose fetch failed, or a column no node produced in the request.</summary>
    public static LogColumn AllMissing(string name, ColumnType type, int rowCount)
    {
        var values = type.IsList ? ElementArray(type.Element, 0) : ElementArray(type.Kind, rowCount);
        return new LogColumn(name, type, rowCount, values, type.IsList ? new int[rowCount + 1] : null, rowCount == 0 ? null : new ulong[Bits.WordCount(rowCount)]);
    }

    /// <summary>
    /// The <paramref name="rows"/> of <paramref name="buffer"/> as a column of <paramref name="type"/>, in that order, copied
    /// in the buffer's storage without widening: a candidate column's own storage for a column logged as a reference to
    /// it, an expression's result otherwise. A NaN or ±∞ number and a list with such an element are missing: logs carry
    /// no non-finite values (design 8.7).
    /// </summary>
    /// <remarks>A row is missing when the buffer's validity says so, whatever the type's nullability: a table slot is
    /// missing until something writes it, and a log records what is there rather than whatever an unwritten row
    /// holds.</remarks>
    /// <exception cref="ArgumentException">The buffer does not store <paramref name="type"/> (another kind, or another
    /// element kind).</exception>
    public static LogColumn Capture(string name, ColumnType type, ColumnBuffer buffer, ReadOnlySpan<int> rows)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Type.Kind != type.Kind || buffer.Type.Element != type.Element)
        {
            throw new ArgumentException($"Column '{name}' is logged as {type}, but its buffer stores {buffer.Type}.", nameof(buffer));
        }

        ulong[]? validity = null;
        int[]? offsets = null;
        Array values = buffer switch
        {
            ScalarColumn<bool> b => Scalars(b, rows, ref validity),
            ScalarColumn<int> i => Scalars(i, rows, ref validity),
            ScalarColumn<long> l => Scalars(l, rows, ref validity),
            ScalarColumn<float> f => Scalars(f, rows, ref validity),
            ScalarColumn<double> d => Scalars(d, rows, ref validity),
            StringColumn s => Strings(s, rows, ref validity),
            ListColumn<bool> b => Elements(b, rows, offsets = new int[rows.Length + 1], ref validity),
            ListColumn<int> i => Elements(i, rows, offsets = new int[rows.Length + 1], ref validity),
            ListColumn<long> l => Elements(l, rows, offsets = new int[rows.Length + 1], ref validity),
            ListColumn<float> f => Elements(f, rows, offsets = new int[rows.Length + 1], ref validity),
            ListColumn<double> d => Elements(d, rows, offsets = new int[rows.Length + 1], ref validity),
            ListColumn<string> s => Elements(s, rows, offsets = new int[rows.Length + 1], ref validity),
            _ => throw new NotSupportedException($"Unsupported column {buffer.GetType().Name}."),
        };
        return new LogColumn(name, type, rows.Length, values, offsets, validity);
    }

    /// <summary>A column from one value per row, for cold paths and tests: each value missing or a value of
    /// <paramref name="type"/> held promoted, as <see cref="ColumnValues.Get"/> reads one, and narrowed here to the type's
    /// storage. Non-finite values are missing, as in <see cref="Capture"/>.</summary>
    /// <exception cref="InvalidOperationException">A value is not one of <paramref name="type"/> (another kind, or an
    /// integer outside an <c>int32</c> type's range).</exception>
    public static LogColumn FromValues(string name, ColumnType type, ReadOnlySpan<ScalarValue> values)
    {
        var count = values.Length;
        ulong[]? validity = null;
        int[]? offsets = type.IsList ? new int[count + 1] : null;
        var result = type.IsList ? ElementArray(type.Element, 0) : ElementArray(type.Kind, count);
        var at = 0;
        for (var i = 0; i < count; i++)
        {
            var value = values[i];
            if (offsets is not null)
            {
                offsets[i] = at;
            }

            if (value.IsMissing || ColumnValues.IsNonFinite(value))
            {
                MarkMissing(ref validity, count, i);
                continue;
            }

            switch (result)
            {
                case bool[] b when offsets is null: b[i] = value.AsBool(); break;
                case int[] n when offsets is null: n[i] = value.AsInt32(); break;
                case long[] l when offsets is null: l[i] = value.AsInt64(); break;
                case float[] f when offsets is null:
                    f[i] = value.AsFloat32();
                    if (!float.IsFinite(f[i]))
                    {
                        f[i] = 0; // beyond the float32 range: an infinity in float32 storage, so missing
                        MarkMissing(ref validity, count, i);
                    }

                    break;
                case double[] d when offsets is null: d[i] = value.AsFloat64(); break;
                case string?[] s when offsets is null: s[i] = value.AsString(); break;
                default:
                    var list = value.ListLength;
                    result = type.Element switch
                    {
                        StorageKind.Bool => Append((bool[])result, at, value.AsBoolList()),
                        StorageKind.Int32 => AppendInt32((int[])result, at, value.AsInt64List()),
                        StorageKind.Int64 => Append((long[])result, at, value.AsInt64List()),
                        StorageKind.Float32 => AppendFloat32((float[])result, at, value.AsFloat64List()),
                        StorageKind.Float64 => Append((double[])result, at, value.AsFloat64List()),
                        _ => Append((string[])result, at, value.AsStringList()),
                    };
                    if (result is float[] floats && !ColumnValues.AllFinite(floats.AsSpan(at, list)))
                    {
                        MarkMissing(ref validity, count, i); // its elements are overwritten by the next row's
                        break;
                    }

                    at += list;
                    break;
            }
        }

        if (offsets is null)
        {
            return new LogColumn(name, type, count, result, null, validity);
        }

        offsets[count] = at;
        return new LogColumn(name, type, count, Trim(result, at), offsets, validity);
    }

    private static T[] Append<T>(T[] array, int at, ReadOnlySpan<T> values)
    {
        array = Grow(array, at + values.Length);
        values.CopyTo(array.AsSpan(at));
        return array;
    }

    private static int[] AppendInt32(int[] array, int at, ReadOnlySpan<long> values)
    {
        if (!Int32Range.AllFit(values))
        {
            throw new InvalidOperationException("A list<int32> value has an element outside the int32 range.");
        }

        array = Grow(array, at + values.Length);
        Int32Range.Narrow(values, array.AsSpan(at));
        return array;
    }

    private static float[] AppendFloat32(float[] array, int at, ReadOnlySpan<double> values)
    {
        array = Grow(array, at + values.Length);
        ColumnValues.NarrowFloats(values, array.AsSpan(at));
        return array;
    }

    private static T[] Grow<T>(T[] array, int length)
    {
        if (array.Length < length)
        {
            Array.Resize(ref array, Math.Max(length, array.Length * 2));
        }

        return array;
    }

    /// <summary>The first <paramref name="length"/> elements, so <see cref="Values"/> holds no spare capacity.</summary>
    private static Array Trim(Array elements, int length) => elements.Length == length ? elements : elements switch
    {
        bool[] b => b[..length],
        int[] i => i[..length],
        long[] l => l[..length],
        float[] f => f[..length],
        double[] d => d[..length],
        _ => ((string[])elements)[..length],
    };

    private static Array ElementArray(StorageKind kind, int length) => kind switch
    {
        StorageKind.Bool => new bool[length],
        StorageKind.Int32 => new int[length],
        StorageKind.Int64 => new long[length],
        StorageKind.Float32 => new float[length],
        StorageKind.Float64 => new double[length],
        StorageKind.String => new string?[length],
        _ => throw new NotSupportedException($"Unsupported log column kind {kind}."),
    };

    private static void MarkMissing(ref ulong[]? validity, int count, int row)
    {
        if (validity is null)
        {
            validity = new ulong[Bits.WordCount(count)];
            Bits.SetRange(validity, 0, count);
        }

        Bits.Clear(validity, row);
    }

    private static T[] Scalars<T>(ScalarColumn<T> column, ReadOnlySpan<int> rows, ref ulong[]? validity)
        where T : unmanaged
    {
        var values = new T[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var value = column[row];
            // typeof(T) tests are constants once the method is compiled for T.
            if (!column.IsValid(row)
                || (typeof(T) == typeof(double) && !double.IsFinite(Unsafe.As<T, double>(ref value)))
                || (typeof(T) == typeof(float) && !float.IsFinite(Unsafe.As<T, float>(ref value))))
            {
                MarkMissing(ref validity, rows.Length, i);
                continue;
            }

            values[i] = value;
        }

        return values;
    }

    private static string?[] Strings(StringColumn column, ReadOnlySpan<int> rows, ref ulong[]? validity)
    {
        var values = new string?[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (!column.IsValid(row))
            {
                MarkMissing(ref validity, rows.Length, i);
                continue;
            }

            values[i] = column.Get(row);
        }

        return values;
    }

    /// <summary>The rows' elements in one array, <paramref name="offsets"/> delimiting each row's; a row with a non-finite
    /// element is missing and adds none.</summary>
    private static T[] Elements<T>(ListColumn<T> list, ReadOnlySpan<int> rows, int[] offsets, ref ulong[]? validity)
    {
        // A row of a list<E,N> column with another length is missing, as an expression reading it finds it: a custom
        // operator can write any length into the column's storage.
        var width = list.Type.FixedLength;
        var total = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            total += Present(list, rows[i], width) ? list.LengthOf(rows[i]) : 0;
        }

        var doubles = list as ListColumn<double>;
        var floats = list as ListColumn<float>;
        var elements = new T[total];
        var at = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            offsets[i] = at;
            var row = rows[i];
            if (!Present(list, row, width)
                || (doubles is not null && !ColumnValues.AllFinite(doubles.Get(row)))
                || (floats is not null && !ColumnValues.AllFinite(floats.Get(row))))
            {
                MarkMissing(ref validity, rows.Length, i);
                continue;
            }

            list.Get(row).CopyTo(elements.AsSpan(at));
            at += list.LengthOf(row);
        }

        offsets[rows.Length] = at;
        return at == total ? elements : elements[..at];

        static bool Present(ListColumn<T> list, int row, int width) => list.IsValid(row) && (width == 0 || list.LengthOf(row) == width);
    }
}

/// <summary>
/// What a tap variant logs (design 8.7): its columns and context fields with their types and the expressions that define
/// them. Built once per plan and shared by every batch of the variant (<see cref="LogBatch.Schema"/>), so a sink can
/// publish the record a batch's schema id names, once per id, and the engine publishes nothing of its own.
/// </summary>
/// <param name="Id">Content id of the record (<see cref="Create"/>): one id names one record.</param>
public sealed record LogSchema(string Id, string TapId, ImmutableArray<LogField> Columns, ImmutableArray<LogField> Context)
{
    /// <summary>The schema of the batches that record a request's experiments when no tap fired (design 11.3).</summary>
    public static LogSchema Experiments { get; } = new("$experiments", "$experiments", [], []);

    /// <summary>
    /// The schema with its id: the sha256 of the record's JSON as a sink writes it, less the id. Everything the record says
    /// is in the id and nothing else is: a context field whose type changed gets a new id (it hashed the tap's definition
    /// and its columns' types, so a reader holding the record by id read the field with the old type; review 70), and a
    /// change of rate or filter that leaves the record as it was keeps it.
    /// </summary>
    public static LogSchema Create(string tapId, ImmutableArray<LogField> columns, ImmutableArray<LogField> context)
    {
        ArgumentNullException.ThrowIfNull(tapId);
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var json = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            LogBatchJson.WriteSchemaBody(json, tapId, columns, context);
        }

        var hash = System.Security.Cryptography.SHA256.HashData(buffer.WrittenSpan);
        return new LogSchema("sha256:" + Convert.ToHexStringLower(hash), tapId, columns, context);
    }
}

/// <summary>A logged column or context field: its name, type and the expression it is logged from.</summary>
/// <param name="Type">
/// The type a sink writes the field as. A field whose expression is a plain reference (<c>col.item.clicks</c>, a feature,
/// <c>col.score.final@fusion</c>, <c>ctx.request.age</c>, <c>param.k</c>) has the type the name is declared with, nullability
/// included: an <c>int32</c> feature logs as <c>int32</c>, a <c>list&lt;float32,64&gt;</c> embedding as itself. A computed
/// expression declares nothing and has its promoted result type (<c>int64</c>, <c>float64</c>, lists of those); to log a
/// computed value at a narrow type, declare a derived feature of that type and log the feature. A column's values are
/// stored in this type (<see cref="LogColumn.Values"/>); a context field's value is a <see cref="ScalarValue"/>, held
/// promoted like every value, which a sink narrows by this type (<see cref="ScalarValue.AsInt32"/>,
/// <see cref="ScalarValue.AsFloat32"/>).
/// </param>
public sealed record LogField(string Name, ColumnType Type, string Expression);
