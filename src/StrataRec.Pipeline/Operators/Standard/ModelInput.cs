using StrataRec.Core.Columns;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Expressions;
using StrataRec.Models;

namespace StrataRec.Pipeline.Operators.Standard;

/// <summary>One input's values for a call, dense and rented from the request: one entry per gathered row, or per batch entry of a context value.</summary>
/// <param name="Values">Entries × the tensor's width elements of the declared type: a <c>bool[]</c>, <c>int[]</c>,
/// <c>long[]</c>, <c>float[]</c>, <c>double[]</c> or <c>string[]</c>.</param>
internal readonly record struct GatheredInput(Array Values);

/// <summary>
/// One mapped input of a model operator: the plain reference it reads and the value each row contributes
/// (<see cref="ModelTensorRule"/>). The one place that turns column storage or a request-level value into tensor data,
/// for every layout.
/// </summary>
/// <remarks>
/// Inputs are plain references, so a column is copied straight from its storage, at its declared width, into one buffer
/// per call, rented from the request and ordered like the rows asked for (the live rows, the rows of each pair, the
/// list), and each call's tensor is a slice of it; a request-level value is evaluated once, narrowed back to its declared
/// type (it is held promoted, exactly) and repeated for every entry of the largest batch. No per-row allocation.
/// </remarks>
/// <param name="declared">The reference's declared type: a column's symbol type, a context value's declared type (its
/// symbol has the promoted form).</param>
internal sealed class ModelInput(string name, CompiledExpression expression, Symbol symbol, ColumnType declared, ModelTensorRule.InputTensor tensor)
{
    private readonly ColumnRef _column = new(symbol.Id, symbol.FixedSlot, symbol.Type, symbol.Name);

    public string Name => name;

    public string Source => expression.Source;

    /// <summary>A request-level value (<c>ctx.*</c>, <c>param.*</c>): the same in every batch entry.</summary>
    public bool Broadcast => symbol.Kind != SymbolKind.Column;

    public ModelTensorRule.InputTensor Tensor => tensor;

    /// <summary>
    /// The input's values for a call: a column's value of each of <paramref name="rows"/>, in order (a
    /// <c>list&lt;E,N&gt;</c>'s N elements), or a request-level value repeated <paramref name="broadcastCount"/> times,
    /// which every batch slices from its start.
    /// </summary>
    public GatheredInput Gather(OperatorContext context, ReadOnlySpan<int> rows, int broadcastCount)
    {
        if (Broadcast)
        {
            return Repeat(context, broadcastCount);
        }

        // Non-nullable, so every row has a value and validity is not read, as the expression evaluator does not read it.
        var buffer = context.Read(_column)
                     ?? throw new InvalidOperationException($"Column '{symbol.Name}' is declared non-nullable ({symbol.Type}) but has no data in this " +
                                                            "request (unresolved slot or never written). The engine must produce it before a model reads it.");
        return GatherFrom(buffer, rows, context.Memory);
    }

    /// <summary>
    /// The tensor of one call: <paramref name="count"/> entries of the batch's dynamic dimension (rows, pairs, or the list's
    /// rows) from entry <paramref name="first"/>, in <paramref name="layout"/>'s shape (<see cref="ModelTensorRule.InputShape"/>).
    /// </summary>
    public NamedInput Batch(in GatheredInput values, TensorLayout layout, int first, int count)
    {
        var shape = ModelTensorRule.InputShape(layout, Broadcast, count, tensor.Dimension);
        var length = 1;
        foreach (var d in shape)
        {
            length *= d;
        }

        var start = Broadcast ? 0 : first * ModelTensorRule.ColumnEntries(layout) * Math.Max(1, tensor.Dimension);
        return values.Values switch
        {
            bool[] b => NamedInput.FromBool(name, shape, b.AsMemory(start, length)),
            int[] i => NamedInput.FromInt32(name, shape, i.AsMemory(start, length)),
            long[] l => NamedInput.FromInt64(name, shape, l.AsMemory(start, length)),
            float[] f => NamedInput.FromFloat32(name, shape, f.AsMemory(start, length)),
            double[] d => NamedInput.FromFloat64(name, shape, d.AsMemory(start, length)),
            string[] s => NamedInput.FromString(name, shape, s.AsMemory(start, length)),
            _ => throw new InvalidOperationException($"Input '{name}' gathered {values.Values.GetType().Name}."),
        };
    }

    /// <summary>Copies the value of every row of a column's buffer, after checking that it holds the declared type.</summary>
    internal GatheredInput GatherFrom(ColumnBuffer buffer, ReadOnlySpan<int> rows, RequestMemory memory)
    {
        // Once per input, not per row: the switch below would otherwise accept a buffer of the same storage under another
        // declared type (a fixed-length list of another length) and send a tensor the signature check did not see.
        if (buffer.Type != symbol.Type)
        {
            throw new InvalidOperationException($"Column '{symbol.Name}' is declared {symbol.Type} but its buffer holds {buffer.Type}.");
        }

        return buffer switch
        {
            ScalarColumn<bool> c => new GatheredInput(Gather(c.Values, rows, memory)),
            ScalarColumn<int> c => new GatheredInput(Gather(c.Values, rows, memory)),
            ScalarColumn<long> c => new GatheredInput(Gather(c.Values, rows, memory)),
            ScalarColumn<float> c => new GatheredInput(Gather(c.Values, rows, memory)),
            ScalarColumn<double> c => new GatheredInput(Gather(c.Values, rows, memory)),
            StringColumn c => new GatheredInput(GatherStrings(c, rows, memory)),
            ListColumn<bool> c => new GatheredInput(GatherList(c, rows, memory)),
            ListColumn<int> c => new GatheredInput(GatherList(c, rows, memory)),
            ListColumn<long> c => new GatheredInput(GatherList(c, rows, memory)),
            ListColumn<float> c => new GatheredInput(GatherList(c, rows, memory)),
            ListColumn<double> c => new GatheredInput(GatherList(c, rows, memory)),
            ListColumn<string> c => new GatheredInput(GatherList(c, rows, memory)),
            _ => throw new InvalidOperationException($"Column '{symbol.Name}' is declared {symbol.Type}, which a model input cannot send."),
        };
    }

    /// <summary>One value per row, as stored.</summary>
    private static T[] Gather<T>(ReadOnlySpan<T> src, ReadOnlySpan<int> rows, RequestMemory memory)
    {
        var dst = memory.Rent<T>(rows.Length);
        for (var r = 0; r < rows.Length; r++)
        {
            dst[r] = src[rows[r]];
        }

        return dst;
    }

    private static string[] GatherStrings(StringColumn column, ReadOnlySpan<int> rows, RequestMemory memory)
    {
        var dst = memory.Rent<string>(rows.Length);
        for (var r = 0; r < rows.Length; r++)
        {
            dst[r] = column.Get(rows[r]);
        }

        return dst;
    }

    /// <summary>N elements per row, row-major, as stored.</summary>
    private T[] GatherList<T>(ListColumn<T> column, ReadOnlySpan<int> rows, RequestMemory memory)
    {
        var width = tensor.Dimension;
        var dst = memory.Rent<T>(checked(rows.Length * width));
        for (var r = 0; r < rows.Length; r++)
        {
            Row(column, rows[r], width).CopyTo(dst.AsSpan(r * width, width));
        }

        return dst;
    }

    /// <summary>A row of a fixed-length list column, which its writers keep at the declared length (design 4.3).</summary>
    private ReadOnlySpan<T> Row<T>(ListColumn<T> column, int row, int width)
    {
        var value = column.Get(row);
        return value.Length == width ? value
            : throw new InvalidOperationException($"Column '{symbol.Name}' is declared {symbol.Type} but row {row} holds {value.Length} elements.");
    }

    /// <summary>
    /// The request-level value repeated <paramref name="count"/> times at its declared type. The value is held promoted
    /// (an int32 as int64, a float32 as float64), exactly: it was checked against its type where it entered (design 4.3),
    /// so narrowing it back loses nothing.
    /// </summary>
    private GatheredInput Repeat(OperatorContext context, int count)
    {
        // Non-nullable: the evaluator returns a value or throws.
        var value = expression.EvaluateScalar(context.EvalContext);
        var memory = context.Memory;
        var width = tensor.Dimension;
        if (width == 0)
        {
            return declared.Kind switch
            {
                StorageKind.Bool => new GatheredInput(Fill(memory, count, value.AsBool())),
                StorageKind.Int32 => new GatheredInput(Fill(memory, count, value.AsInt32())),
                StorageKind.Int64 => new GatheredInput(Fill(memory, count, value.AsInt64())),
                StorageKind.Float32 => new GatheredInput(Fill(memory, count, value.AsFloat32())),
                StorageKind.Float64 => new GatheredInput(Fill(memory, count, value.AsFloat64())),
                _ => new GatheredInput(Fill(memory, count, value.AsString())),
            };
        }

        if (value.ListLength != width)
        {
            throw new InvalidOperationException($"'{symbol.Name}' is declared {declared} but holds {value.ListLength} elements in this request.");
        }

        return declared.Element switch
        {
            StorageKind.Bool => new GatheredInput(Tile(memory, count, value.AsBoolList())),
            StorageKind.Int32 => new GatheredInput(TileInt32(memory, count, value.AsInt64List())),
            StorageKind.Int64 => new GatheredInput(Tile(memory, count, value.AsInt64List())),
            StorageKind.Float32 => new GatheredInput(TileFloat32(memory, count, value.AsFloat64List())),
            StorageKind.Float64 => new GatheredInput(Tile(memory, count, value.AsFloat64List())),
            _ => new GatheredInput(Tile(memory, count, value.AsStringList())),
        };
    }

    private static T[] Fill<T>(RequestMemory memory, int count, T value)
    {
        var values = memory.Rent<T>(count);
        values.AsSpan(0, count).Fill(value);
        return values;
    }

    /// <summary><paramref name="count"/> copies of <paramref name="list"/>, one after another.</summary>
    private static T[] Tile<T>(RequestMemory memory, int count, ReadOnlySpan<T> list)
    {
        var entries = memory.Rent<T>(checked(count * list.Length));
        if (count > 0)
        {
            list.CopyTo(entries);
            RepeatFirst(entries, count, list.Length);
        }

        return entries;
    }

    /// <summary><paramref name="count"/> copies of a <c>list&lt;int32&gt;</c> value's elements (int32 values held promoted), narrowed once.</summary>
    private static int[] TileInt32(RequestMemory memory, int count, ReadOnlySpan<long> list)
    {
        var entries = memory.Rent<int>(checked(count * list.Length));
        if (count > 0)
        {
            Int32Range.Narrow(list, entries.AsSpan(0, list.Length));
            RepeatFirst(entries, count, list.Length);
        }

        return entries;
    }

    /// <summary><paramref name="count"/> copies of a <c>list&lt;float32&gt;</c> value's elements (float32 values held promoted), narrowed once.</summary>
    private static float[] TileFloat32(RequestMemory memory, int count, ReadOnlySpan<double> list)
    {
        var entries = memory.Rent<float>(checked(count * list.Length));
        if (count > 0)
        {
            ColumnValues.NarrowFloats(list, entries.AsSpan(0, list.Length));
            RepeatFirst(entries, count, list.Length);
        }

        return entries;
    }

    /// <summary>Copies the first <paramref name="width"/> entries into the next <paramref name="count"/> − 1 slices.</summary>
    private static void RepeatFirst<T>(T[] entries, int count, int width)
    {
        var first = entries.AsSpan(0, width);
        for (var r = 1; r < count; r++)
        {
            first.CopyTo(entries.AsSpan(r * width, width));
        }
    }
}
