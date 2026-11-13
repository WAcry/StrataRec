using System.Buffers;
using StrataRec.Core.Columns;
using StrataRec.Core.Hashing;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions.Binding;
using StrataRec.Expressions.Ir;
using StrataRec.Expressions.Runtime;

namespace StrataRec.Expressions.Vectorized;

/// <summary>Per-lane access to vector, list and string operands.</summary>
internal static class Lanes
{
    public static ReadOnlySpan<float> Vector(ref Frame f, Opnd o, int lane)
    {
        if (o.IsScalar)
        {
            ref var s = ref f.Scalar(o);
            return s.IsMissing ? default : s.AsVector();
        }

        ref var loc = ref f.Locs[o.Index];
        return loc.Source switch
        {
            SourceKind.Column => ((VectorColumn)loc.SourceRef!).Row(f.Row(lane)),
            SourceKind.Broadcast => (float[])loc.SourceRef!,
            SourceKind.Select => Vector(ref f, Pick(ref loc, lane), lane),
            _ => default,
        };
    }

    public static ReadOnlySpan<long> LongList(ref Frame f, Opnd o, int lane)
    {
        if (o.IsScalar)
        {
            ref var s = ref f.Scalar(o);
            return s.IsMissing ? default : s.AsInt64List();
        }

        ref var loc = ref f.Locs[o.Index];
        return loc.Source switch
        {
            SourceKind.Column => ((ListColumn<long>)loc.SourceRef!).Get(f.Row(lane)),
            SourceKind.Broadcast => (long[])loc.SourceRef!,
            SourceKind.Select => LongList(ref f, Pick(ref loc, lane), lane),
            _ => default,
        };
    }

    public static ReadOnlySpan<string> StringList(ref Frame f, Opnd o, int lane)
    {
        if (o.IsScalar)
        {
            ref var s = ref f.Scalar(o);
            return s.IsMissing ? default : s.AsStringList();
        }

        ref var loc = ref f.Locs[o.Index];
        return loc.Source switch
        {
            SourceKind.Column => ((ListColumn<string>)loc.SourceRef!).Get(f.Row(lane)),
            SourceKind.Broadcast => (string[])loc.SourceRef!,
            SourceKind.Select => StringList(ref f, Pick(ref loc, lane), lane),
            _ => default,
        };
    }

    public static string String(ref Frame f, Opnd o, int lane)
    {
        if (o.IsScalar)
        {
            ref var s = ref f.Scalar(o);
            return s.IsMissing ? string.Empty : s.AsString();
        }

        return f.GetS(o.Index)[lane] ?? string.Empty;
    }

    public static long Int(ref Frame f, Opnd o, int lane)
    {
        if (o.IsScalar)
        {
            ref var s = ref f.Scalar(o);
            return s.IsMissing ? 0 : s.AsInt64();
        }

        return f.GetI(o.Index)[lane];
    }

    public static double Float(ref Frame f, Opnd o, int lane)
    {
        if (o.IsScalar)
        {
            ref var s = ref f.Scalar(o);
            return s.IsMissing ? 0 : s.AsFloat64();
        }

        return f.GetF(o.Index)[lane];
    }

    public static bool Bool(ref Frame f, Opnd o, int lane) => f.BoolValues(o).Get(lane);

    private static Opnd Pick(ref Loc loc, int lane)
    {
        var a = loc.Mask is null || ((loc.Mask[loc.MaskOffset + (lane >> 6)] >> lane) & 1) != 0;
        return a ? loc.SelA : loc.SelB;
    }
}

/// <summary><c>size(list)</c> and <c>size(text)</c> (code points).</summary>
internal sealed class SizeInstr(TypeKind operandKind, ExprType type, Opnd operand) : Instr(type, operand)
{
    public override void Execute(ref Frame f)
    {
        var x = Operands[0];
        var dst = OutI(ref f);
        if (!LiftValidity(ref f, Operands))
        {
            return;
        }

        for (var i = 0; i < f.N; i++)
        {
            dst[i] = operandKind switch
            {
                TypeKind.ListInt => Lanes.LongList(ref f, x, i).Length,
                TypeKind.ListString => Lanes.StringList(ref f, x, i).Length,
                _ => TextOps.CodePointCount(Lanes.String(ref f, x, i)),
            };
        }
    }
}

/// <summary><c>contains(list, x)</c> / <c>x in list</c> and <c>contains(text, s)</c>. A request-level list is sorted
/// once per call and probed by binary search.</summary>
internal sealed class ContainsInstr(TypeKind listKind, ExprType type, Opnd list, Opnd element) : Instr(type, list, element)
{
    private static readonly Comparison<string> Ordinal = string.CompareOrdinal;

    public override void Execute(ref Frame f)
    {
        Opnd l = Operands[0], x = Operands[1];
        var dst = OutB(ref f);
        if (!LiftValidity(ref f, Operands))
        {
            return;
        }

        dst.Clear();
        var n = f.N;
        switch (listKind)
        {
            case TypeKind.ListInt when l.IsScalar:
            {
                var source = f.Scalar(l).AsInt64List();
                var sorted = ArrayPool<long>.Shared.Rent(Math.Max(1, source.Length));
                try
                {
                    var keys = sorted.AsSpan(0, source.Length);
                    source.CopyTo(keys);
                    keys.Sort();
                    for (var i = 0; i < n; i++)
                    {
                        BitKernels.SetBit(dst, i, keys.BinarySearch(Lanes.Int(ref f, x, i)) >= 0);
                    }
                }
                finally
                {
                    ArrayPool<long>.Shared.Return(sorted);
                }

                break;
            }

            case TypeKind.ListInt:
                for (var i = 0; i < n; i++)
                {
                    BitKernels.SetBit(dst, i, Lanes.LongList(ref f, l, i).Contains(Lanes.Int(ref f, x, i)));
                }

                break;
            case TypeKind.ListString when l.IsScalar:
            {
                var source = f.Scalar(l).AsStringList();
                var sorted = ArrayPool<string>.Shared.Rent(Math.Max(1, source.Length));
                try
                {
                    var keys = sorted.AsSpan(0, source.Length);
                    source.CopyTo(keys);
                    keys.Sort(Ordinal);
                    for (var i = 0; i < n; i++)
                    {
                        BitKernels.SetBit(dst, i, keys.BinarySearch(Lanes.String(ref f, x, i), StringComparer.Ordinal) >= 0);
                    }
                }
                finally
                {
                    ArrayPool<string>.Shared.Return(sorted, clearArray: true);
                }

                break;
            }

            case TypeKind.ListString:
                for (var i = 0; i < n; i++)
                {
                    BitKernels.SetBit(dst, i, ListOps.Contains(Lanes.StringList(ref f, l, i), Lanes.String(ref f, x, i)));
                }

                break;
            default:
                for (var i = 0; i < n; i++)
                {
                    BitKernels.SetBit(dst, i, Lanes.String(ref f, l, i).Contains(Lanes.String(ref f, x, i), StringComparison.Ordinal));
                }

                break;
        }
    }
}

/// <summary><c>x in [constants]</c> against a compile-time set.</summary>
internal sealed class InSetInstr(ConstSet set, ExprType type, Opnd element) : Instr(type, element)
{
    public override void Execute(ref Frame f)
    {
        var x = Operands[0];
        var dst = OutB(ref f);
        if (!LiftValidity(ref f, Operands))
        {
            return;
        }

        dst.Clear();
        var n = f.N;
        switch (set.ElementKind)
        {
            case TypeKind.Int:
            {
                var values = f.GetI(x.Index);
                for (var i = 0; i < n; i++)
                {
                    BitKernels.SetBit(dst, i, set.Contains(values[i]));
                }

                break;
            }

            case TypeKind.Float:
            {
                var values = f.GetF(x.Index);
                for (var i = 0; i < n; i++)
                {
                    BitKernels.SetBit(dst, i, set.Contains(values[i]));
                }

                break;
            }

            default:
            {
                var values = f.GetS(x.Index);
                for (var i = 0; i < n; i++)
                {
                    BitKernels.SetBit(dst, i, set.Contains(values[i]));
                }

                break;
            }
        }
    }
}

/// <summary><c>overlap_count(l1, l2)</c>: elements of l1 (with multiplicity) that occur in l2.</summary>
internal sealed class OverlapInstr(TypeKind listKind, ExprType type, Opnd first, Opnd second) : Instr(type, first, second)
{
    private static readonly Comparison<string> Ordinal = string.CompareOrdinal;

    public override void Execute(ref Frame f)
    {
        Opnd a = Operands[0], b = Operands[1];
        var dst = OutI(ref f);
        if (!LiftValidity(ref f, Operands))
        {
            return;
        }

        var n = f.N;
        if (listKind == TypeKind.ListInt)
        {
            if (b.IsScalar)
            {
                var source = f.Scalar(b).AsInt64List();
                var sorted = ArrayPool<long>.Shared.Rent(Math.Max(1, source.Length));
                try
                {
                    var keys = sorted.AsSpan(0, source.Length);
                    source.CopyTo(keys);
                    keys.Sort();
                    for (var i = 0; i < n; i++)
                    {
                        long count = 0;
                        foreach (var v in Lanes.LongList(ref f, a, i))
                        {
                            if (keys.BinarySearch(v) >= 0)
                            {
                                count++;
                            }
                        }

                        dst[i] = count;
                    }
                }
                finally
                {
                    ArrayPool<long>.Shared.Return(sorted);
                }
            }
            else
            {
                for (var i = 0; i < n; i++)
                {
                    dst[i] = ListOps.Overlap(Lanes.LongList(ref f, a, i), Lanes.LongList(ref f, b, i));
                }
            }

            return;
        }

        if (b.IsScalar)
        {
            var source = f.Scalar(b).AsStringList();
            var sorted = ArrayPool<string>.Shared.Rent(Math.Max(1, source.Length));
            try
            {
                var keys = sorted.AsSpan(0, source.Length);
                source.CopyTo(keys);
                keys.Sort(Ordinal);
                for (var i = 0; i < n; i++)
                {
                    long count = 0;
                    foreach (var v in Lanes.StringList(ref f, a, i))
                    {
                        if (v is not null && keys.BinarySearch(v, StringComparer.Ordinal) >= 0)
                        {
                            count++;
                        }
                    }

                    dst[i] = count;
                }
            }
            finally
            {
                ArrayPool<string>.Shared.Return(sorted, clearArray: true);
            }
        }
        else
        {
            for (var i = 0; i < n; i++)
            {
                dst[i] = ListOps.Overlap(Lanes.StringList(ref f, a, i), Lanes.StringList(ref f, b, i));
            }
        }
    }
}

internal sealed class StartsWithInstr(ExprType type, Opnd text, Opnd prefix) : Instr(type, text, prefix)
{
    public override void Execute(ref Frame f)
    {
        Opnd s = Operands[0], p = Operands[1];
        var dst = OutB(ref f);
        if (!LiftValidity(ref f, Operands))
        {
            return;
        }

        dst.Clear();
        for (var i = 0; i < f.N; i++)
        {
            BitKernels.SetBit(dst, i, Lanes.String(ref f, s, i).StartsWith(Lanes.String(ref f, p, i), StringComparison.Ordinal));
        }
    }
}

/// <summary><c>dot</c>, <c>cosine</c>, <c>l2_norm</c> per lane with the shared SIMD kernels.</summary>
internal sealed class VectorFunctionInstr(IrOp op, ExprType type, Opnd[] operands) : Instr(type, operands)
{
    public override void Execute(ref Frame f)
    {
        var dst = OutF(ref f);
        if (!LiftValidity(ref f, Operands))
        {
            return;
        }

        var a = Operands[0];
        for (var i = 0; i < f.N; i++)
        {
            dst[i] = op switch
            {
                IrOp.Dot => VectorOps.Dot(Lanes.Vector(ref f, a, i), Lanes.Vector(ref f, Operands[1], i)),
                IrOp.Cosine => VectorOps.Cosine(Lanes.Vector(ref f, a, i), Lanes.Vector(ref f, Operands[1], i)),
                _ => VectorOps.L2Norm(Lanes.Vector(ref f, a, i)),
            };
        }
    }
}

internal sealed class PiecewiseInstr(Knots knots, ExprType type, Opnd operand) : Instr(type, operand)
{
    public override void Execute(ref Frame f)
    {
        var dst = OutF(ref f);
        if (!LiftValidity(ref f, Operands))
        {
            return;
        }

        var x = f.GetF(Operands[0].Index);
        for (var i = 0; i < dst.Length; i++)
        {
            dst[i] = knots.Evaluate(x[i]);
        }
    }
}

/// <summary><c>hash01(...)</c>; two or three int64 arguments use <see cref="StableHash.Hash01(long, long)"/>.</summary>
internal sealed class Hash01Instr(TypeKind[] argumentKinds, ExprType type, Opnd[] operands) : Instr(type, operands)
{
    private readonly TypeKind[] _kinds = argumentKinds;
    private readonly bool _allInt = AllInt(argumentKinds) && argumentKinds.Length is 2 or 3;

    public override void Execute(ref Frame f)
    {
        var dst = OutF(ref f);
        if (!LiftValidity(ref f, Operands))
        {
            return;
        }

        var n = f.N;
        var ops = Operands;
        if (_allInt && ops.Length == 2)
        {
            Opnd a = ops[0], b = ops[1];
            if (a.IsScalar && !b.IsScalar)
            {
                var s = f.Scalar(a).AsInt64();
                var y = f.GetI(b.Index);
                for (var i = 0; i < n; i++)
                {
                    dst[i] = StableHash.Hash01(s, y[i]);
                }
            }
            else
            {
                for (var i = 0; i < n; i++)
                {
                    dst[i] = StableHash.Hash01(Lanes.Int(ref f, a, i), Lanes.Int(ref f, b, i));
                }
            }

            return;
        }

        if (_allInt)
        {
            for (var i = 0; i < n; i++)
            {
                dst[i] = StableHash.Hash01(Lanes.Int(ref f, ops[0], i), Lanes.Int(ref f, ops[1], i), Lanes.Int(ref f, ops[2], i));
            }

            return;
        }

        var encoder = new Hash01Encoder(stackalloc byte[256]);
        try
        {
            for (var i = 0; i < n; i++)
            {
                encoder.Reset();
                for (var k = 0; k < ops.Length; k++)
                {
                    switch (_kinds[k])
                    {
                        case TypeKind.Int: encoder.Add(Lanes.Int(ref f, ops[k], i)); break;
                        case TypeKind.Float: encoder.Add(Lanes.Float(ref f, ops[k], i)); break;
                        case TypeKind.Bool: encoder.Add(Lanes.Bool(ref f, ops[k], i)); break;
                        default: encoder.Add(Lanes.String(ref f, ops[k], i)); break;
                    }
                }

                dst[i] = encoder.Finish();
            }
        }
        finally
        {
            encoder.Dispose();
        }
    }

    private static bool AllInt(TypeKind[] kinds)
    {
        foreach (var k in kinds)
        {
            if (k != TypeKind.Int)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary><c>lookup('table', key, default)</c>: a missing key or a key not in the table gives the default.</summary>
internal sealed class LookupInstr(ILookupTable table, TypeKind keyKind, ExprType type, Opnd key, Opnd fallback) : Instr(type, key, fallback)
{
    private readonly ColumnType _valueType = type.ToColumnType();

    public override void Execute(ref Frame f)
    {
        Opnd k = Operands[0], d = Operands[1];
        var keyValid = f.Validity(k);
        var n = f.N;
        var kind = Type.Kind;
        Span<double> fd = default;
        Span<long> id = default;
        Span<ulong> bd = default;
        Span<string?> sd = default;
        switch (ValueFile)
        {
            case RegFile.F: fd = OutF(ref f); break;
            case RegFile.I: id = OutI(ref f); break;
            case RegFile.B: bd = OutB(ref f); bd.Clear(); break;
            default: sd = OutS(ref f); break;
        }

        for (var i = 0; i < n; i++)
        {
            ScalarValue value = default;
            var found = false;
            if (keyValid.Get(i))
            {
                var keyValue = keyKind == TypeKind.Int ? ScalarValue.FromInt64(Lanes.Int(ref f, k, i)) : ScalarValue.FromString(Lanes.String(ref f, k, i));
                found = table.TryGet(keyValue, out var raw) && !raw.IsMissing && raw.TryConvert(_valueType, out value);
            }

            switch (kind)
            {
                case TypeKind.Float: fd[i] = found ? value.AsFloat64() : Lanes.Float(ref f, d, i); break;
                case TypeKind.Int: id[i] = found ? value.AsInt64() : Lanes.Int(ref f, d, i); break;
                case TypeKind.Bool: BitKernels.SetBit(bd, i, found ? value.AsBool() : Lanes.Bool(ref f, d, i)); break;
                default: sd[i] = found ? value.AsString() : Lanes.String(ref f, d, i); break;
            }
        }
    }
}
