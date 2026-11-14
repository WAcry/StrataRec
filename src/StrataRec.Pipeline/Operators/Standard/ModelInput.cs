using System.Runtime.InteropServices;
using StrataRec.Core.Columns;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Expressions;
using StrataRec.Models;

namespace StrataRec.Pipeline.Operators.Standard;

/// <summary>One input's values for a call, dense and rented from the request: one entry per gathered row, or per batch entry of a context value.</summary>
/// <param name="Float32">Entries × the tensor's width floats, for a float32 tensor.</param>
/// <param name="Int64">One long per entry, for an int64 tensor.</param>
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
    /// vectors as stored), or a request-level value repeated <paramref name="broadcastCount"/> times, which every batch
    /// slices from its start.
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
        var width = tensor.Dimension;
        switch (buffer)
        {
            case ScalarColumn<long> c:
            {
                var src = c.Values;
                var dst = memory.Rent<long>(n);
                for (var r = 0; r < n; r++)
                {
                    dst[r] = src[rows[r]];
                }

                return new GatheredInput(null, dst);
            }

            case ScalarColumn<int> c:
            {
                var src = c.Values;
                var dst = memory.Rent<long>(n);
                for (var r = 0; r < n; r++)
                {
                    dst[r] = src[rows[r]];
                }

                return new GatheredInput(null, dst);
            }

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

            case ScalarColumn<bool> c:
            {
                // Any nonzero byte is true, as the expression evaluator reads it.
                var src = MemoryMarshal.AsBytes(c.Values.AsSpan());
                var dst = memory.Rent<float>(n);
                for (var r = 0; r < n; r++)
                {
                    dst[r] = src[rows[r]] != 0 ? 1f : 0f;
                }

                return new GatheredInput(dst, null);
            }

            case VectorColumn c when width > 0 && c.Dimension == width:
            {
                var dst = memory.Rent<float>(checked(n * width));
                for (var r = 0; r < n; r++)
                {
                    c.Row(rows[r]).CopyTo(dst.AsSpan(r * width, width));
                }

                return new GatheredInput(dst, null);
            }

            default:
                throw new InvalidOperationException($"Column '{symbol.Name}' is declared {symbol.Type}, which a model input cannot send.");
        }
    }

    private GatheredInput Repeat(OperatorContext context, int count)
    {
        // Non-nullable: the evaluator returns a value or throws.
        var value = expression.EvaluateScalar(context.EvalContext);
        var memory = context.Memory;
        if (tensor.ElementType == TensorElementType.Int64)
        {
            var dst = memory.Rent<long>(count);
            dst.AsSpan(0, count).Fill(value.AsInt64());
            return new GatheredInput(null, dst);
        }

        var width = tensor.Dimension;
        if (width == 0)
        {
            var dst = memory.Rent<float>(count);
            dst.AsSpan(0, count).Fill(value.Kind == StorageKind.Bool ? (value.AsBool() ? 1f : 0f) : (float)value.AsFloat64());
            return new GatheredInput(dst, null);
        }

        var vector = value.AsVector();
        if (vector.Length != width)
        {
            throw new InvalidOperationException($"'{symbol.Name}' is declared {symbol.Type} but holds {vector.Length} elements in this request.");
        }

        var entries = memory.Rent<float>(checked(count * width));
        for (var r = 0; r < count; r++)
        {
            vector.CopyTo(entries.AsSpan(r * width, width));
        }

        return new GatheredInput(entries, null);
    }
}
