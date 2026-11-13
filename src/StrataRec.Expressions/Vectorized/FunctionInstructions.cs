using System.Buffers;
using StrataRec.Core.Columns;
using StrataRec.Core.Hashing;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions.Binding;
using StrataRec.Expressions.Ir;
using StrataRec.Expressions.Runtime;

namespace StrataRec.Expressions.Vectorized;

/// <summary>
/// One lane of an int list operand, viewed in place: int64 elements (<c>list&lt;int64&gt;</c> storage, request-level
/// values, literals) or int32 elements (<c>list&lt;int32&gt;</c> storage). Either way the elements mean int64 values
/// (design 4.3), so a kernel branches on the width instead of widening into a buffer per row: once per call for a plain
/// column (<see cref="Lanes.DirectIntListColumn"/>), once per lane for selects that may mix widths.
/// </summary>
internal readonly ref struct IntListLane
{
    public IntListLane(ReadOnlySpan<long> values)
    {
        Int64 = values;
    }

    public IntListLane(ReadOnlySpan<int> values)
    {
        Int32 = values;
        IsInt32 = true;
    }

    /// <summary>The elements when <see cref="IsInt32"/> is false.</summary>
    public ReadOnlySpan<long> Int64 { get; }

    /// <summary>The elements when <see cref="IsInt32"/> is true.</summary>
    public ReadOnlySpan<int> Int32 { get; }

    public bool IsInt32 { get; }

    public int Length => IsInt32 ? Int32.Length : Int64.Length;

    /// <summary>Membership with int64 semantics: a probe outside the int32 range is simply absent from int32 storage.</summary>
    public bool Contains(long value) => IsInt32 ? ListOps.Contains(Int32, value) : Int64.Contains(value);

    /// <summary><c>overlap_count(this, other)</c>: elements of this list (with multiplicity) that occur in <paramref name="other"/>.</summary>
    public long OverlapWith(IntListLane other) => (IsInt32, other.IsInt32) switch
    {
        (false, false) => ListOps.Overlap(Int64, other.Int64),
        (true, true) => ListOps.Overlap(Int32, other.Int32),
        (true, false) => ListOps.Overlap(Int32, other.Int64),
        _ => ListOps.Overlap(Int64, other.Int32),
    };
}

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

    /// <summary>
    /// The <c>ListColumn&lt;long&gt;</c> or <c>ListColumn&lt;int&gt;</c> an int list operand reads in place (a plain column
    /// reference), or null when its lanes must be resolved one by one (a request-level value, an all-missing column, a
    /// lazy select). Kernels test it once per call and loop over the typed column, keeping the width dispatch of
    /// <see cref="IntList"/> out of the common case.
    /// </summary>
    public static ColumnBuffer? DirectIntListColumn(ref Frame f, Opnd o)
    {
        if (o.IsScalar)
        {
            return null;
        }

        ref var loc = ref f.Locs[o.Index];
        return loc.Source == SourceKind.Column ? (ColumnBuffer)loc.SourceRef! : null;
    }

    /// <summary>An int list lane, zero-copy over whichever storage backs it: a <c>list&lt;int32&gt;</c> column yields its
    /// int32 extent, a <c>list&lt;int64&gt;</c> column or a request-level value its int64 one. A lazy select may mix the
    /// two from lane to lane.</summary>
    public static IntListLane IntList(ref Frame f, Opnd o, int lane)
    {
        if (o.IsScalar)
        {
            ref var s = ref f.Scalar(o);
            return s.IsMissing ? default : new IntListLane(s.AsInt64List());
        }

        ref var loc = ref f.Locs[o.Index];
        switch (loc.Source)
        {
            case SourceKind.Column:
                // ListColumn<T> is sealed: the test is one method-table compare per lane.
                return loc.SourceRef is ListColumn<int> narrow
                    ? new IntListLane(narrow.Get(f.Row(lane)))
                    : new IntListLane(((ListColumn<long>)loc.SourceRef!).Get(f.Row(lane)));
            case SourceKind.Broadcast:
                return new IntListLane((long[])loc.SourceRef!);
            case SourceKind.Select:
                return IntList(ref f, Pick(ref loc, lane), lane);
            default:
                return default;
        }
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

        switch (operandKind)
        {
            case TypeKind.ListInt:
                switch (Lanes.DirectIntListColumn(ref f, x))
                {
                    case ListColumn<long> c:
                        Sizes(ref f, c, dst);
                        break;
                    case ListColumn<int> c:
                        Sizes(ref f, c, dst);
                        break;
                    default:
                        for (var i = 0; i < dst.Length; i++)
                        {
                            dst[i] = Lanes.IntList(ref f, x, i).Length;
                        }

                        break;
                }

                break;
            case TypeKind.ListString:
                for (var i = 0; i < dst.Length; i++)
                {
                    dst[i] = Lanes.StringList(ref f, x, i).Length;
                }

                break;
            default:
                for (var i = 0; i < dst.Length; i++)
                {
                    dst[i] = TextOps.CodePointCount(Lanes.String(ref f, x, i));
                }

                break;
        }
    }

    private static void Sizes<T>(ref Frame f, ListColumn<T> column, Span<long> dst)
    {
        for (var i = 0; i < dst.Length; i++)
        {
            dst[i] = column.LengthOf(f.Row(i));
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
                switch (Lanes.DirectIntListColumn(ref f, l))
                {
                    case ListColumn<long> c:
                        for (var i = 0; i < n; i++)
                        {
                            BitKernels.SetBit(dst, i, c.Get(f.Row(i)).Contains(Lanes.Int(ref f, x, i)));
                        }

                        break;
                    case ListColumn<int> c:
                        for (var i = 0; i < n; i++)
                        {
                            BitKernels.SetBit(dst, i, ListOps.Contains(c.Get(f.Row(i)), Lanes.Int(ref f, x, i)));
                        }

                        break;
                    default:
                        for (var i = 0; i < n; i++)
                        {
                            BitKernels.SetBit(dst, i, Lanes.IntList(ref f, l, i).Contains(Lanes.Int(ref f, x, i)));
                        }

                        break;
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

    /// <summary>Elements of <paramref name="list"/> (with multiplicity) found in the sorted request-level keys.</summary>
    private static long CountIn(ReadOnlySpan<long> list, ReadOnlySpan<long> sortedKeys)
    {
        long count = 0;
        foreach (var v in list)
        {
            if (sortedKeys.BinarySearch(v) >= 0)
            {
                count++;
            }
        }

        return count;
    }

    /// <inheritdoc cref="CountIn(ReadOnlySpan{long}, ReadOnlySpan{long})"/>
    private static long CountIn(ReadOnlySpan<int> list, ReadOnlySpan<long> sortedKeys)
    {
        long count = 0;
        foreach (var v in list)
        {
            if (sortedKeys.BinarySearch((long)v) >= 0)
            {
                count++;
            }
        }

        return count;
    }

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
                    switch (Lanes.DirectIntListColumn(ref f, a))
                    {
                        case ListColumn<long> c:
                            for (var i = 0; i < n; i++)
                            {
                                dst[i] = CountIn(c.Get(f.Row(i)), keys);
                            }

                            break;
                        case ListColumn<int> c:
                            for (var i = 0; i < n; i++)
                            {
                                dst[i] = CountIn(c.Get(f.Row(i)), keys);
                            }

                            break;
                        default:
                            for (var i = 0; i < n; i++)
                            {
                                var list = Lanes.IntList(ref f, a, i);
                                dst[i] = list.IsInt32 ? CountIn(list.Int32, keys) : CountIn(list.Int64, keys);
                            }

                            break;
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
                    dst[i] = Lanes.IntList(ref f, a, i).OverlapWith(Lanes.IntList(ref f, b, i));
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
