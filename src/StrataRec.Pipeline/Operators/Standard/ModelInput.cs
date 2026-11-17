using System.Numerics;
using System.Runtime.CompilerServices;
using StrataRec.Core.Columns;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Expressions;
using StrataRec.Models;

namespace StrataRec.Pipeline.Operators.Standard;

/// <summary>One input's values for a call, dense and rented from the request: one entry per gathered row, or per batch entry of a context value.</summary>
/// <param name="Float32">Entries × the tensor's width floats, for a float32 tensor.</param>
/// <param name="Int64">Entries × the tensor's width longs, for an int64 tensor.</param>
internal readonly record struct GatheredInput(float[]? Float32, long[]? Int64);

/// <summary>
/// One mapped input of a model operator: the plain reference it reads and the value each row contributes
/// (<see cref="ModelTensorRule"/>). The one place that turns column storage or a request-level value into tensor data,
/// for every layout.
/// </summary>
/// <remarks>
/// Inputs are plain references, so a column is gathered straight from its storage into one buffer per call, rented from
/// the request and ordered like the rows asked for (the live rows, the rows of each pair, the list), and each call's
/// tensor is a slice of it; a request-level value is evaluated once and repeated for every entry of the largest batch.
/// No per-row allocation.
/// </remarks>
internal sealed class ModelInput(string name, CompiledExpression expression, Symbol symbol, ModelTensorRule.InputTensor tensor)
{
    private readonly ColumnRef _column = new(symbol.Id, symbol.FixedSlot, symbol.Type, symbol.Name);

    public string Name => name;

    public string Source => expression.Source;

    /// <summary>A request-level value (<c>ctx.*</c>, <c>param.*</c>): the same in every batch entry.</summary>
    public bool Broadcast => symbol.Kind != SymbolKind.Column;

    public ModelTensorRule.InputTensor Tensor => tensor;

    /// <summary>
    /// The input's values for a call: a column's value of each of <paramref name="rows"/>, in order (int64 values exact,
    /// a bool 0 or 1, a <c>list&lt;E,N&gt;</c>'s N elements), or a request-level value repeated
    /// <paramref name="broadcastCount"/> times, which every batch slices from its start.
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
        return values.Int64 is { } longs
            ? NamedInput.FromInt64(name, shape, longs.AsMemory(start, length))
            : NamedInput.FromFloat32(name, shape, values.Float32.AsMemory(start, length));
    }

    /// <summary>Copies the value of every row of a column's buffer, after checking that it holds the declared type.</summary>
    internal GatheredInput GatherFrom(ColumnBuffer buffer, ReadOnlySpan<int> rows, RequestMemory memory)
    {
        // Once per input, not per row: the switch below would otherwise accept a buffer of the same family (int64 for
        // int32, float64 for float32, bool for float32) and send values the declared type does not have.
        if (buffer.Type != symbol.Type)
        {
            throw new InvalidOperationException($"Column '{symbol.Name}' is declared {symbol.Type} but its buffer holds {buffer.Type}.");
        }

        var n = rows.Length;
        switch (buffer)
        {
            case ScalarColumn<long> c:
                return new GatheredInput(null, GatherLongs(c.Values, rows, memory));
            case ScalarColumn<int> c:
                return new GatheredInput(null, GatherLongs(c.Values, rows, memory));
            case ScalarColumn<bool> c:
                return new GatheredInput(null, GatherLongs(c.Values, rows, memory));
            case ScalarColumn<float> c:
            {
                var src = c.Values;
                var dst = memory.Rent<float>(n);
                for (var r = 0; r < n; r++)
                {
                    dst[r] = src[rows[r]];
                }

                return new GatheredInput(dst, null);
            }

            case ScalarColumn<double> c:
            {
                var src = c.Values;
                var dst = memory.Rent<float>(n);
                for (var r = 0; r < n; r++)
                {
                    dst[r] = (float)src[rows[r]];
                }

                return new GatheredInput(dst, null);
            }

            case ListColumn<float> c:
                return new GatheredInput(GatherFloats(c, rows, memory), null);
            case ListColumn<double> c:
                return new GatheredInput(GatherFloats(c, rows, memory), null);
            case ListColumn<long> c:
                return new GatheredInput(null, GatherLongs(c, rows, memory));
            case ListColumn<int> c:
                return new GatheredInput(null, GatherLongs(c, rows, memory));
            case ListColumn<bool> c:
                return new GatheredInput(null, GatherLongs(c, rows, memory));
            default:
                throw new InvalidOperationException($"Column '{symbol.Name}' is declared {symbol.Type}, which a model input cannot send.");
        }
    }

    /// <summary>One long per row: int64 and int32 values exact, a bool as 0 or 1.</summary>
    private static long[] GatherLongs<T>(ReadOnlySpan<T> src, ReadOnlySpan<int> rows, RequestMemory memory)
        where T : unmanaged
    {
        var dst = memory.Rent<long>(rows.Length);
        for (var r = 0; r < rows.Length; r++)
        {
            dst[r] = ToLong(in src[rows[r]]);
        }

        return dst;
    }

    /// <summary>N floats per row, row-major: a float64 element rounded to float32.</summary>
    private float[] GatherFloats<T>(ListColumn<T> column, ReadOnlySpan<int> rows, RequestMemory memory)
        where T : unmanaged, INumberBase<T>
    {
        var width = tensor.Dimension;
        var dst = memory.Rent<float>(checked(rows.Length * width));
        for (var r = 0; r < rows.Length; r++)
        {
            var row = Row(column, rows[r], width);
            var at = dst.AsSpan(r * width, width);
            for (var e = 0; e < width; e++)
            {
                at[e] = float.CreateTruncating(row[e]);
            }
        }

        return dst;
    }

    /// <summary>N longs per row, row-major: int64 and int32 elements exact, a bool element as 0 or 1.</summary>
    private long[] GatherLongs<T>(ListColumn<T> column, ReadOnlySpan<int> rows, RequestMemory memory)
        where T : unmanaged
    {
        var width = tensor.Dimension;
        var dst = memory.Rent<long>(checked(rows.Length * width));
        for (var r = 0; r < rows.Length; r++)
        {
            var row = Row(column, rows[r], width);
            var at = dst.AsSpan(r * width, width);
            for (var e = 0; e < width; e++)
            {
                at[e] = ToLong(in row[e]);
            }
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

    /// <summary>An int64 or int32 exact, a bool 0 or 1 (any nonzero byte is true, as the expression evaluator reads it);
    /// the type tests are constants once compiled for <typeparamref name="T"/>.</summary>
    private static long ToLong<T>(in T value)
        where T : unmanaged =>
        typeof(T) == typeof(bool) ? (Unsafe.As<T, byte>(ref Unsafe.AsRef(in value)) != 0 ? 1 : 0)
        : typeof(T) == typeof(int) ? Unsafe.As<T, int>(ref Unsafe.AsRef(in value))
        : Unsafe.As<T, long>(ref Unsafe.AsRef(in value));

    private GatheredInput Repeat(OperatorContext context, int count)
    {
        // Non-nullable: the evaluator returns a value or throws.
        var value = expression.EvaluateScalar(context.EvalContext);
        var memory = context.Memory;
        var width = tensor.Dimension;
        if (width == 0)
        {
            if (tensor.ElementType == TensorElementType.Int64)
            {
                var longs = memory.Rent<long>(count);
                longs.AsSpan(0, count).Fill(value.Kind == StorageKind.Bool ? (value.AsBool() ? 1 : 0) : value.AsInt64());
                return new GatheredInput(null, longs);
            }

            var floats = memory.Rent<float>(count);
            floats.AsSpan(0, count).Fill((float)value.AsFloat64());
            return new GatheredInput(floats, null);
        }

        // A list, held promoted: list<int32> elements as long, list<float32> as double.
        if (value.ListLength != width)
        {
            throw new InvalidOperationException($"'{symbol.Name}' is declared {symbol.Type} but holds {value.ListLength} elements in this request.");
        }

        if (tensor.ElementType == TensorElementType.Int64)
        {
            var entries = memory.Rent<long>(checked(count * width));
            var first = entries.AsSpan(0, width);
            if (value.ListElement == StorageKind.Bool)
            {
                var bools = value.AsBoolList();
                for (var e = 0; e < width; e++)
                {
                    first[e] = bools[e] ? 1 : 0;
                }
            }
            else
            {
                value.AsInt64List().CopyTo(first);
            }

            for (var r = 1; r < count; r++)
            {
                first.CopyTo(entries.AsSpan(r * width, width));
            }

            return new GatheredInput(null, entries);
        }

        var vector = memory.Rent<float>(checked(count * width));
        var head = vector.AsSpan(0, width);
        var elements = value.AsFloat64List();
        for (var e = 0; e < width; e++)
        {
            head[e] = (float)elements[e];
        }

        for (var r = 1; r < count; r++)
        {
            head.CopyTo(vector.AsSpan(r * width, width));
        }

        return new GatheredInput(vector, null);
    }
}
