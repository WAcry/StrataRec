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
/// Columns used to be one <see cref="ScalarValue"/> per row (24 bytes) and a copied array per vector or list row: a
/// candidate tap of 1,000 rows and 10 numeric columns held 240 KB of values; typed it holds 80 KB in 10 arrays, and a list
/// or vector column is one array of elements instead of one per row. A sink reads the arrays directly; <see cref="Get"/>
/// gives a row as a value, for cold readers.
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

    /// <summary>The column expression's type: <c>bool</c>, <c>int64</c>, <c>float64</c>, <c>string</c>,
    /// <c>vec&lt;float32,N&gt;</c> or <c>list&lt;E&gt;</c> of those scalars.</summary>
    public ColumnType Type { get; }

    public int RowCount { get; }

    /// <summary>
    /// The values: for a scalar one per row, a <c>bool[]</c>, <c>long[]</c>, <c>double[]</c> or <c>string?[]</c>; for a
    /// vector a <c>float[]</c> of <see cref="ColumnType.Dimension"/> per row; for a list the elements of every row in row
    /// order (<c>bool[]</c>, <c>long[]</c>, <c>double[]</c> or <c>string[]</c>), which <see cref="Offsets"/> delimits. A
    /// missing row holds zeros (null for a string) and a missing list row no elements.
    /// </summary>
    public Array Values { get; }

    /// <summary>For a list, <see cref="RowCount"/> + 1 entries: row r's elements are
    /// <c>Values[Offsets[r]..Offsets[r + 1]]</c>. Null for other types.</summary>
    public int[]? Offsets { get; }

    /// <summary>Bit r set when row r has a value (<see cref="Bits"/>); null when every row has one.</summary>
    public ulong[]? Validity { get; }

    public bool IsMissing(int row) => Validity is { } validity && !Bits.Get(validity, row);

    /// <summary>Row <paramref name="row"/> as a value; a vector or list row is copied. For tests and the JSON encoding.</summary>
    public ScalarValue Get(int row)
    {
        if (IsMissing(row))
        {
            return ScalarValue.Missing;
        }

        switch (Values)
        {
            case float[] vectors:
                return ScalarValue.FromVector(vectors.AsSpan(row * Type.Dimension, Type.Dimension).ToArray());
        }

        if (Offsets is { } offsets)
        {
            var (start, length) = (offsets[row], offsets[row + 1] - offsets[row]);
            return Values switch
            {
                bool[] b => ScalarValue.FromBoolList(b.AsSpan(start, length).ToArray()),
                long[] l => ScalarValue.FromInt64List(l.AsSpan(start, length).ToArray()),
                double[] d => ScalarValue.FromFloat64List(d.AsSpan(start, length).ToArray()),
                string[] s => ScalarValue.FromStringList(s.AsSpan(start, length).ToArray()),
                _ => throw new InvalidOperationException($"Unexpected list values {Values.GetType().Name}."),
            };
        }

        return Values switch
        {
            bool[] b => ScalarValue.FromBool(b[row]),
            long[] l => ScalarValue.FromInt64(l[row]),
            double[] d => ScalarValue.FromFloat64(d[row]),
            string?[] s => ScalarValue.FromString(s[row]),
            _ => throw new InvalidOperationException($"Unexpected values {Values.GetType().Name}."),
        };
    }

    /// <summary>A column of <paramref name="rowCount"/> missing rows: what a tap logs for a column that reads a feature
    /// whose fetch failed.</summary>
    public static LogColumn AllMissing(string name, ColumnType type, int rowCount)
    {
        Array values = type.Kind switch
        {
            StorageKind.Vector => new float[rowCount * type.Dimension],
            StorageKind.List => ElementArray(type.Element, 0),
            _ => ElementArray(type.Kind, rowCount),
        };
        return new LogColumn(name, type, rowCount, values, type.IsList ? new int[rowCount + 1] : null, rowCount == 0 ? null : new ulong[Bits.WordCount(rowCount)]);
    }

    /// <summary>
    /// The <paramref name="rows"/> of <paramref name="buffer"/> (an expression's result, so its numbers are promoted:
    /// int64, float64), in that order. A NaN or ±∞ number, a vector with such a component and a list with such an element
    /// are missing: logs carry no non-finite values (design 8.7).
    /// </summary>
    public static LogColumn Capture(string name, ColumnBuffer buffer, ReadOnlySpan<int> rows)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ulong[]? validity = null;
        int[]? offsets = null;
        Array values = buffer switch
        {
            ScalarColumn<bool> b => Scalars(b, rows, ref validity),
            ScalarColumn<long> l => Scalars(l, rows, ref validity),
            ScalarColumn<double> d => Scalars(d, rows, ref validity),
            DictStringColumn or TextColumn => Strings(buffer, rows, ref validity),
            VectorColumn v => Vectors(v, rows, ref validity),
            ListColumn<bool> b => Elements(b, rows, offsets = new int[rows.Length + 1], ref validity),
            ListColumn<long> l => Elements(l, rows, offsets = new int[rows.Length + 1], ref validity),
            ListColumn<double> d => Elements(d, rows, offsets = new int[rows.Length + 1], ref validity),
            ListColumn<string> s => Elements(s, rows, offsets = new int[rows.Length + 1], ref validity),
            _ => throw new NotSupportedException($"A log column holds an expression's result (promoted types), not a {buffer.GetType().Name}."),
        };
        return new LogColumn(name, buffer.Type, rows.Length, values, offsets, validity);
    }

    /// <summary>A column from one value per row, for cold paths and tests: each value missing or of <paramref name="type"/>
    /// as <see cref="ColumnValues.Get"/> reads it (promoted numbers). Non-finite values are missing, as in
    /// <see cref="Capture"/>.</summary>
    public static LogColumn FromValues(string name, ColumnType type, ReadOnlySpan<ScalarValue> values)
    {
        var count = values.Length;
        ulong[]? validity = null;
        int[]? offsets = type.IsList ? new int[count + 1] : null;
        var elements = new List<ScalarValue>();
        Array result = type.Kind switch
        {
            StorageKind.Vector => new float[count * type.Dimension],
            StorageKind.List => ElementArray(type.Element, 0),
            _ => ElementArray(type.Kind, count),
        };
        var at = 0;
        for (var i = 0; i < count; i++)
        {
            var value = values[i];
            if (offsets is not null)
            {
                offsets[i] = at;
            }

            if (value.IsMissing || ColumnValues.IsNonFinite(value) || (value.Kind == StorageKind.Vector && !ColumnValues.AllFinite(value.AsVector())))
            {
                MarkMissing(ref validity, count, i);
                continue;
            }

            switch (result)
            {
                case bool[] b when offsets is null: b[i] = value.AsBool(); break;
                case long[] l when offsets is null: l[i] = value.AsInt64(); break;
                case double[] d when offsets is null: d[i] = value.AsFloat64(); break;
                case string?[] s when offsets is null: s[i] = value.AsString(); break;
                case float[] f: value.AsVector().CopyTo(f.AsSpan(i * type.Dimension, type.Dimension)); break;
                default:
                    var list = value.ListLength;
                    result = type.Element switch
                    {
                        StorageKind.Bool => Append((bool[])result, at, value.AsBoolList()),
                        StorageKind.Int32 or StorageKind.Int64 => Append((long[])result, at, value.AsInt64List()),
                        StorageKind.Float32 or StorageKind.Float64 => Append((double[])result, at, value.AsFloat64List()),
                        _ => Append((string[])result, at, value.AsStringList()),
                    };
                    at += list;
                    break;
            }
        }

        if (offsets is not null)
        {
            offsets[count] = at;
        }

        return new LogColumn(name, type, count, result, offsets, validity);
    }

    private static T[] Append<T>(T[] array, int at, ReadOnlySpan<T> values)
    {
        if (array.Length < at + values.Length)
        {
            Array.Resize(ref array, Math.Max(at + values.Length, array.Length * 2));
        }

        values.CopyTo(array.AsSpan(at));
        return array;
    }

    private static Array ElementArray(StorageKind kind, int length) => kind switch
    {
        StorageKind.Bool => new bool[length],
        StorageKind.Int32 or StorageKind.Int64 => new long[length],
        StorageKind.Float32 or StorageKind.Float64 => new double[length],
        StorageKind.String or StorageKind.Text => new string?[length],
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
            if (!column.IsValid(row) || (typeof(T) == typeof(double) && !double.IsFinite(Unsafe.As<T, double>(ref value))))
            {
                MarkMissing(ref validity, rows.Length, i);
                continue;
            }

            values[i] = value;
        }

        return values;
    }

    private static string?[] Strings(ColumnBuffer column, ReadOnlySpan<int> rows, ref ulong[]? validity)
    {
        var values = new string?[rows.Length];
        var dictionary = column as DictStringColumn;
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (!column.IsValid(row))
            {
                MarkMissing(ref validity, rows.Length, i);
                continue;
            }

            values[i] = dictionary is not null ? dictionary.Get(row) : ((TextColumn)column).Get(row);
        }

        return values;
    }

    private static float[] Vectors(VectorColumn column, ReadOnlySpan<int> rows, ref ulong[]? validity)
    {
        var dimension = column.Dimension;
        var values = new float[rows.Length * dimension];
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (!column.IsValid(row) || !ColumnValues.AllFinite(column.Row(row)))
            {
                MarkMissing(ref validity, rows.Length, i);
                continue;
            }

            column.Row(row).CopyTo(values.AsSpan(i * dimension, dimension));
        }

        return values;
    }

    /// <summary>The rows' elements in one array, <paramref name="offsets"/> delimiting each row's; a row with a non-finite
    /// element is missing and adds none.</summary>
    private static T[] Elements<T>(ListColumn<T> list, ReadOnlySpan<int> rows, int[] offsets, ref ulong[]? validity)
    {
        var total = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            total += list.IsValid(rows[i]) ? list.LengthOf(rows[i]) : 0;
        }

        var elements = new T[total];
        var at = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            offsets[i] = at;
            var row = rows[i];
            if (!list.IsValid(row) || (list is ListColumn<double> doubles && !ColumnValues.AllFinite(doubles.Get(row))))
            {
                MarkMissing(ref validity, rows.Length, i);
                continue;
            }

            list.Get(row).CopyTo(elements.AsSpan(at));
            at += list.LengthOf(row);
        }

        offsets[rows.Length] = at;
        return at == total ? elements : elements[..at];
    }
}

/// <summary>
/// What a tap variant logs (design 8.7): its columns and context fields with their types and the expressions that define
/// them. Built once per plan and shared by every batch of the variant (<see cref="LogBatch.Schema"/>), so a sink can
/// publish the record a batch's schema id names, once per id, and the engine publishes nothing of its own.
/// </summary>
/// <param name="Id">Content id of the record: the tap's definition and the resolved types.</param>
public sealed record LogSchema(string Id, string TapId, ImmutableArray<LogField> Columns, ImmutableArray<LogField> Context)
{
    /// <summary>The schema of the batches that record a request's experiments when no tap fired (design 11.3).</summary>
    public static LogSchema Experiments { get; } = new("$experiments", "$experiments", [], []);
}

/// <summary>A logged column or context field: its name, type and the expression it is logged from.</summary>
public sealed record LogField(string Name, ColumnType Type, string Expression);
