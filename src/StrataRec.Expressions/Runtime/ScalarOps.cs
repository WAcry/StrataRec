using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using StrataRec.Core.Columns;
using StrataRec.Core.Hashing;
using StrataRec.Core.Tables;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions.Binding;
using StrataRec.Expressions.Ir;

namespace StrataRec.Expressions.Runtime;

/// <summary>
/// The scalar semantics of every IR operation (design 7.4, 7.5): one value in, one value out, total for all inputs.
/// </summary>
/// <remarks>This is the single definition used by constant folding, the once-per-request scalar program and the
/// reference interpreter. The vectorized kernels must agree with it (checked by differential tests).</remarks>
internal static class ScalarOps
{
    /// <summary>True when any missing argument makes the result missing (C#-style lifting).</summary>
    public static bool IsLifted(IrOp op) => op is not (IrOp.And or IrOp.Or or IrOp.Conditional or IrOp.Coalesce or IrOp.Has or IrOp.Lookup);

    /// <summary>Applies a non-symbol node to already evaluated argument values.</summary>
    public static ScalarValue Apply(IrNode node, ReadOnlySpan<ScalarValue> args, ExpressionCounters? counters)
    {
        switch (node.Op)
        {
            case IrOp.Const:
                return node.Value;
            case IrOp.And:
                return And(args[0], args[1]);
            case IrOp.Or:
                return Or(args[0], args[1]);
            case IrOp.Conditional:
                return args[0].IsMissing ? ScalarValue.Missing : args[0].AsBool() ? args[1] : args[2];
            case IrOp.Coalesce:
                return args[0].IsMissing ? args[1] : args[0];
            case IrOp.Has:
                return ScalarValue.FromBool(!args[0].IsMissing);
            case IrOp.Lookup:
                return Lookup((ILookupTable)node.Data!, node.Type, args[0], args[1]);
        }

        foreach (var a in args)
        {
            if (a.IsMissing)
            {
                return ScalarValue.Missing;
            }
        }

        var isInt = node.Type.Kind == TypeKind.Int;
        switch (node.Op)
        {
            case IrOp.Neg:
                return isInt ? ScalarValue.FromInt64(unchecked(-args[0].AsInt64())) : ScalarValue.FromFloat64(-args[0].AsFloat64());
            case IrOp.Not:
                return ScalarValue.FromBool(!args[0].AsBool());
            case IrOp.ToFloat:
                return ScalarValue.FromFloat64(args[0].AsFloat64());
            case IrOp.Abs:
                return isInt ? ScalarValue.FromInt64(AbsInt(args[0].AsInt64())) : ScalarValue.FromFloat64(Math.Abs(args[0].AsFloat64()));
            case IrOp.Sqrt:
                return ScalarValue.FromFloat64(Math.Sqrt(args[0].AsFloat64()));
            case IrOp.Exp:
                return ScalarValue.FromFloat64(MathKernels.Exp(args[0].AsFloat64()));
            case IrOp.Log:
                return ScalarValue.FromFloat64(MathKernels.Log(args[0].AsFloat64()));
            case IrOp.Log1p:
                return ScalarValue.FromFloat64(MathKernels.Log1p(args[0].AsFloat64()));
            case IrOp.Sigmoid:
                return ScalarValue.FromFloat64(MathKernels.Sigmoid(args[0].AsFloat64()));
            case IrOp.Add:
                return isInt ? ScalarValue.FromInt64(unchecked(args[0].AsInt64() + args[1].AsInt64())) : ScalarValue.FromFloat64(args[0].AsFloat64() + args[1].AsFloat64());
            case IrOp.Sub:
                return isInt ? ScalarValue.FromInt64(unchecked(args[0].AsInt64() - args[1].AsInt64())) : ScalarValue.FromFloat64(args[0].AsFloat64() - args[1].AsFloat64());
            case IrOp.Mul:
                return isInt ? ScalarValue.FromInt64(unchecked(args[0].AsInt64() * args[1].AsInt64())) : ScalarValue.FromFloat64(args[0].AsFloat64() * args[1].AsFloat64());
            case IrOp.Div:
                return ScalarValue.FromFloat64(args[0].AsFloat64() / args[1].AsFloat64());
            case IrOp.Mod:
                return ScalarValue.FromInt64(Mod(args[0].AsInt64(), args[1].AsInt64(), counters));
            case IrOp.Pow:
                return ScalarValue.FromFloat64(MathKernels.Pow(args[0].AsFloat64(), args[1].AsFloat64()));
            case IrOp.Min:
                return isInt ? ScalarValue.FromInt64(Math.Min(args[0].AsInt64(), args[1].AsInt64())) : ScalarValue.FromFloat64(Math.Min(args[0].AsFloat64(), args[1].AsFloat64()));
            case IrOp.Max:
                return isInt ? ScalarValue.FromInt64(Math.Max(args[0].AsInt64(), args[1].AsInt64())) : ScalarValue.FromFloat64(Math.Max(args[0].AsFloat64(), args[1].AsFloat64()));
            case IrOp.Eq or IrOp.Ne or IrOp.Lt or IrOp.Le or IrOp.Gt or IrOp.Ge:
                return ScalarValue.FromBool(Compare(node.Op, node.Args[0].Type.Kind, args[0], args[1]));
            case IrOp.Size:
                return ScalarValue.FromInt64(node.Args[0].Type.Kind switch
                {
                    TypeKind.ListInt => args[0].AsInt64List().Length,
                    TypeKind.ListString => args[0].AsStringList().Length,
                    _ => TextOps.CodePointCount(args[0].AsString()),
                });
            case IrOp.Contains:
                return ScalarValue.FromBool(node.Args[0].Type.Kind switch
                {
                    TypeKind.ListInt => args[0].AsInt64List().AsSpan().Contains(args[1].AsInt64()),
                    TypeKind.ListString => ListOps.Contains(args[0].AsStringList(), args[1].AsString()),
                    _ => args[0].AsString().Contains(args[1].AsString(), StringComparison.Ordinal),
                });
            case IrOp.InSet:
            {
                var set = (ConstSet)node.Data!;
                return ScalarValue.FromBool(set.ElementKind switch
                {
                    TypeKind.Int => set.Contains(args[0].AsInt64()),
                    TypeKind.Float => set.Contains(args[0].AsFloat64()),
                    _ => set.Contains(args[0].AsString()),
                });
            }

            case IrOp.Overlap:
                return ScalarValue.FromInt64(node.Args[0].Type.Kind == TypeKind.ListInt
                    ? ListOps.Overlap(args[0].AsInt64List(), args[1].AsInt64List())
                    : ListOps.Overlap(args[0].AsStringList(), args[1].AsStringList()));
            case IrOp.StartsWith:
                return ScalarValue.FromBool(args[0].AsString().StartsWith(args[1].AsString(), StringComparison.Ordinal));
            case IrOp.Dot:
                return ScalarValue.FromFloat64(VectorOps.Dot(args[0].AsVector(), args[1].AsVector()));
            case IrOp.Cosine:
                return ScalarValue.FromFloat64(VectorOps.Cosine(args[0].AsVector(), args[1].AsVector()));
            case IrOp.L2Norm:
                return ScalarValue.FromFloat64(VectorOps.L2Norm(args[0].AsVector()));
            case IrOp.PiecewiseLinear:
                return ScalarValue.FromFloat64(((Knots)node.Data!).Evaluate(args[0].AsFloat64()));
            case IrOp.Hash01:
                return ScalarValue.FromFloat64(Hash01(node, args));
            default:
                throw new InvalidOperationException($"No scalar semantics for {node.Op}.");
        }
    }

    public static ScalarValue And(in ScalarValue a, in ScalarValue b)
    {
        // Kleene: false wins over missing.
        if ((!a.IsMissing && !a.AsBool()) || (!b.IsMissing && !b.AsBool()))
        {
            return ScalarValue.FromBool(false);
        }

        return a.IsMissing || b.IsMissing ? ScalarValue.Missing : ScalarValue.FromBool(true);
    }

    public static ScalarValue Or(in ScalarValue a, in ScalarValue b)
    {
        // Kleene: true wins over missing.
        if ((!a.IsMissing && a.AsBool()) || (!b.IsMissing && b.AsBool()))
        {
            return ScalarValue.FromBool(true);
        }

        return a.IsMissing || b.IsMissing ? ScalarValue.Missing : ScalarValue.FromBool(false);
    }

    public static long AbsInt(long x) => x < 0 ? unchecked(-x) : x;

    /// <summary>Total integer remainder: <c>x % 0 = 0</c> (counted), <c>long.MinValue % -1 = 0</c> (design 7.5).</summary>
    public static long Mod(long a, long b, ExpressionCounters? counters)
    {
        if (b == 0)
        {
            counters?.AddIntModZero(1);
            return 0;
        }

        return b == -1 ? 0 : a % b;
    }

    private static bool Compare(IrOp op, TypeKind kind, in ScalarValue a, in ScalarValue b)
    {
        switch (kind)
        {
            case TypeKind.Int:
            {
                long x = a.AsInt64(), y = b.AsInt64();
                return op switch
                {
                    IrOp.Eq => x == y,
                    IrOp.Ne => x != y,
                    IrOp.Lt => x < y,
                    IrOp.Le => x <= y,
                    IrOp.Gt => x > y,
                    _ => x >= y,
                };
            }

            case TypeKind.Float:
            {
                double x = a.AsFloat64(), y = b.AsFloat64();
                return op switch
                {
                    IrOp.Eq => x == y,
                    IrOp.Ne => x != y,
                    IrOp.Lt => x < y,
                    IrOp.Le => x <= y,
                    IrOp.Gt => x > y,
                    _ => x >= y,
                };
            }

            case TypeKind.Bool:
                return (a.AsBool() == b.AsBool()) == (op == IrOp.Eq);
            default:
                return string.Equals(a.AsString(), b.AsString(), StringComparison.Ordinal) == (op == IrOp.Eq);
        }
    }

    private static ScalarValue Lookup(ILookupTable table, ExprType type, in ScalarValue key, in ScalarValue fallback)
    {
        if (!key.IsMissing && table.TryGet(key, out var value) && !value.IsMissing && value.TryConvert(type.ToColumnType(), out var converted))
        {
            return converted;
        }

        return fallback;
    }

    private static double Hash01(IrNode node, ReadOnlySpan<ScalarValue> args)
    {
        var allInt = true;
        foreach (var a in node.Args)
        {
            allInt &= a.Type.Kind == TypeKind.Int;
        }

        if (allInt && args.Length == 2)
        {
            return StableHash.Hash01(args[0].AsInt64(), args[1].AsInt64());
        }

        if (allInt && args.Length == 3)
        {
            return StableHash.Hash01(args[0].AsInt64(), args[1].AsInt64(), args[2].AsInt64());
        }

        var encoder = new Hash01Encoder(stackalloc byte[128]);
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                switch (node.Args[i].Type.Kind)
                {
                    case TypeKind.Int: encoder.Add(args[i].AsInt64()); break;
                    case TypeKind.Float: encoder.Add(args[i].AsFloat64()); break;
                    case TypeKind.Bool: encoder.Add(args[i].AsBool()); break;
                    default: encoder.Add(args[i].AsString()); break;
                }
            }

            return encoder.Finish();
        }
        finally
        {
            encoder.Dispose();
        }
    }
}

internal static class ListOps
{
    public static bool Contains(ReadOnlySpan<string> list, string value)
    {
        foreach (var s in list)
        {
            if (string.Equals(s, value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Number of elements of <paramref name="a"/> (with multiplicity) that occur in <paramref name="b"/>.</summary>
    public static long Overlap(ReadOnlySpan<long> a, ReadOnlySpan<long> b)
    {
        long count = 0;
        foreach (var x in a)
        {
            if (b.Contains(x))
            {
                count++;
            }
        }

        return count;
    }

    public static long Overlap(ReadOnlySpan<string> a, ReadOnlySpan<string> b)
    {
        long count = 0;
        foreach (var x in a)
        {
            if (Contains(b, x))
            {
                count++;
            }
        }

        return count;
    }
}

internal static class TextOps
{
    /// <summary>Length in Unicode code points (CEL <c>size(string)</c>).</summary>
    public static long CodePointCount(string? s)
    {
        if (s is null)
        {
            return 0;
        }

        long count = s.Length;
        for (var i = 0; i + 1 < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && char.IsLowSurrogate(s[i + 1]))
            {
                count--;
                i++;
            }
        }

        return count;
    }
}

/// <summary>Vector functions over float32 storage (design 7.6). Dimension mismatches give NaN (total semantics).</summary>
internal static class VectorOps
{
    public static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            return double.NaN;
        }

        return a.IsEmpty ? 0.0 : TensorPrimitives.Dot(a, b);
    }

    public static double L2Norm(ReadOnlySpan<float> a) => a.IsEmpty ? 0.0 : TensorPrimitives.Norm(a);

    /// <summary>dot / (|a| |b|); NaN when a norm is zero or dimensions differ.</summary>
    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.IsEmpty)
        {
            return double.NaN;
        }

        double dot = TensorPrimitives.Dot(a, b);
        double na = TensorPrimitives.Norm(a);
        double nb = TensorPrimitives.Norm(b);
        return dot / (na * nb);
    }
}

/// <summary>Reads symbol values: request-level values from <see cref="EvalContext"/> and single cells from columns.</summary>
internal static class ValueAccess
{
    /// <summary>Reads a <c>ctx.*</c> or <c>param.*</c> value converted to the symbol's expression type.</summary>
    public static ScalarValue ReadRequestValue(IrNode node, in EvalContext context)
    {
        var symbol = node.Symbol;
        var values = symbol.Kind == SymbolKind.Context ? context.Context : context.Params;
        if (values is null || (uint)symbol.Id >= (uint)values.Length)
        {
            throw new InvalidOperationException(
                $"'{symbol.Name}' has id {symbol.Id}, but the request provides {values?.Length ?? 0} {(symbol.Kind == SymbolKind.Context ? "context values" : "parameters")}.");
        }

        return Normalize(values[symbol.Id], node.Type, symbol.Name);
    }

    /// <summary>Converts a stored value to an expression type (int64↔float64, string↔text).</summary>
    public static ScalarValue Normalize(in ScalarValue value, ExprType type, string name)
    {
        if (value.IsMissing)
        {
            return type.Nullable
                ? ScalarValue.Missing
                : throw new InvalidOperationException($"'{name}' is declared {type} (non-nullable) but has no value in this request; the engine must supply it.");
        }

        switch (type.Kind)
        {
            case TypeKind.Int when value.Kind == StorageKind.Int64:
            case TypeKind.Float when value.Kind == StorageKind.Float64:
            case TypeKind.Bool when value.Kind == StorageKind.Bool:
            case TypeKind.String when value.Kind == StorageKind.String:
            case TypeKind.Text when value.Kind == StorageKind.Text:
            case TypeKind.Vector when value.Kind == StorageKind.Vector:
            case TypeKind.ListInt when value.Kind == StorageKind.ListInt64:
            case TypeKind.ListString when value.Kind == StorageKind.ListString:
                return value;
            case TypeKind.Float when value.Kind == StorageKind.Int64:
                return ScalarValue.FromFloat64(value.AsInt64());
            case TypeKind.Int when value.Kind == StorageKind.Float64 && value.TryConvert(ColumnType.Int64, out var asInt):
                return asInt;
            case TypeKind.String when value.Kind == StorageKind.Text:
                return ScalarValue.FromString(value.AsString());
            case TypeKind.Text when value.Kind == StorageKind.String:
                return ScalarValue.FromText(value.AsString());
            default:
                throw new InvalidOperationException($"'{name}' is declared {type} but holds a {value.Kind} value ({value}).");
        }
    }

    /// <summary>The physical slot of a column symbol in this request, or -1 when unresolved.</summary>
    public static ColumnBuffer? ResolveColumn(in EvalContext context, in Symbol symbol)
    {
        var table = context.Table;
        var slot = symbol.FixedSlot >= 0 ? symbol.FixedSlot : context.Bindings is null ? OutputBindings.NotProduced : context.Bindings.Resolve(symbol.Id);
        if (slot < 0 || table is null)
        {
            return null;
        }

        if (slot >= table.Schema.SlotCount)
        {
            throw new InvalidOperationException($"'{symbol.Name}' resolves to slot {slot}, but the table has {table.Schema.SlotCount} slots.");
        }

        return table.TryGetSlot(slot);
    }

    public static InvalidOperationException MissingNonNullableColumn(in Symbol symbol) =>
        new($"Column '{symbol.Name}' is declared non-nullable ({symbol.Type}) but has no data in this request " +
            "(unresolved slot or never written). The engine must produce it before evaluating this expression.");

    public static InvalidOperationException ColumnTypeMismatch(in Symbol symbol, ColumnBuffer buffer) =>
        new($"Column '{symbol.Name}' is declared {symbol.Type} but its buffer holds {buffer.Type}.");

    /// <summary>Reads one cell (reference interpreter). Non-nullable symbols ignore validity, like the vectorized
    /// backend, which never reads validity for statically non-nullable inputs.</summary>
    public static ScalarValue ReadCell(IrNode node, in EvalContext context, int row)
    {
        var symbol = node.Symbol;
        var buffer = ResolveColumn(context, symbol);
        if (buffer is null)
        {
            return node.Type.Nullable ? ScalarValue.Missing : throw MissingNonNullableColumn(symbol);
        }

        if (node.Type.Nullable && !buffer.IsValid(row))
        {
            return ScalarValue.Missing;
        }

        return (node.Type.Kind, buffer) switch
        {
            (TypeKind.Float, ScalarColumn<double> c) => ScalarValue.FromFloat64(c[row]),
            (TypeKind.Float, ScalarColumn<float> c) => ScalarValue.FromFloat64(c[row]),
            (TypeKind.Int, ScalarColumn<long> c) => ScalarValue.FromInt64(c[row]),
            (TypeKind.Int, ScalarColumn<int> c) => ScalarValue.FromInt64(c[row]),
            (TypeKind.Bool, ScalarColumn<bool> c) => ScalarValue.FromBool(MemoryMarshal.AsBytes(c.Values.AsSpan(row, 1))[0] != 0),
            (TypeKind.String, DictStringColumn c) => ScalarValue.FromString(c.Get(row)),
            (TypeKind.Text, TextColumn c) => ScalarValue.FromText(c.Get(row)),
            (TypeKind.Vector, VectorColumn c) => ScalarValue.FromVector(c.Row(row).ToArray()),
            (TypeKind.ListInt, ListColumn<long> c) => ScalarValue.FromInt64List(c.Get(row).ToArray()),
            (TypeKind.ListString, ListColumn<string> c) => ScalarValue.FromStringList(c.Get(row).ToArray()),
            _ => throw ColumnTypeMismatch(symbol, buffer),
        };
    }
}
