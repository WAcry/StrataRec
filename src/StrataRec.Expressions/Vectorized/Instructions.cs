using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using StrataRec.Core.Columns;
using StrataRec.Core.Values;
using StrataRec.Expressions.Binding;
using StrataRec.Expressions.Ir;
using StrataRec.Expressions.Runtime;

namespace StrataRec.Expressions.Vectorized;

/// <summary>Register file of a value or validity register.</summary>
internal enum RegFile : byte
{
    None,
    F,
    I,
    B,
    S,
}

/// <summary>
/// One vectorized operation computing a whole column of one IR node (design 7.7, backend A). Instructions are
/// immutable after lowering and shared by concurrent evaluations; all per-call state lives in the <see cref="Frame"/>.
/// </summary>
/// <remarks>
/// <para>Kernels are element-wise and total: lanes that are missing or that belong to dead rows hold arbitrary
/// values, and every kernel must stay in bounds and must not throw on them.</para>
/// <para>Output registers are allocated before the operands' registers are released, so an output never aliases an
/// operand.</para>
/// </remarks>
internal abstract class Instr
{
    protected Instr(ExprType type, params Opnd[] operands)
    {
        Type = type;
        Operands = operands;
    }

    /// <summary>Index of the node this instruction computes (= its position in the program).</summary>
    public int Out { get; set; }

    public ExprType Type { get; }

    /// <summary>Operands, used for liveness.</summary>
    public Opnd[] Operands { get; }

    public int ValueReg { get; set; } = -1;

    /// <summary>Validity register; -1 when the result is statically non-nullable (never touches validity).</summary>
    public int ValidReg { get; set; } = -1;

    /// <summary>True when the result refers to its operands' storage (lazy vector/list selects): operands then stay
    /// live as long as this node.</summary>
    public virtual bool Retains => false;

    public RegFile ValueFile => Type.Kind switch
    {
        TypeKind.Float => RegFile.F,
        TypeKind.Int => RegFile.I,
        TypeKind.Bool => RegFile.B,
        TypeKind.String or TypeKind.Text => RegFile.S,
        _ => RegFile.None,
    };

    public abstract void Execute(ref Frame f);

    public override string ToString() => $"n{Out} = {GetType().Name.Replace("Instr", string.Empty, StringComparison.Ordinal)}({string.Join(", ", Operands)}) : {Type}";

    // ------------------------------------------------------------------------------------------------------------
    // Output helpers

    protected Span<double> OutF(scoped ref Frame f)
    {
        ref var l = ref f.Locs[Out];
        l.Values = f.F;
        l.Offset = ValueReg * f.N;
        return f.F.AsSpan(l.Offset, f.N);
    }

    protected Span<long> OutI(scoped ref Frame f)
    {
        ref var l = ref f.Locs[Out];
        l.Values = f.I;
        l.Offset = ValueReg * f.N;
        return f.I.AsSpan(l.Offset, f.N);
    }

    protected Span<ulong> OutB(scoped ref Frame f)
    {
        ref var l = ref f.Locs[Out];
        l.Values = f.B;
        l.Offset = ValueReg * f.Words;
        return f.B.AsSpan(l.Offset, f.Words);
    }

    protected Span<string?> OutS(scoped ref Frame f)
    {
        ref var l = ref f.Locs[Out];
        l.Values = f.S;
        l.Offset = ValueReg * f.N;
        return f.S.AsSpan(l.Offset, f.N);
    }

    protected Span<ulong> OutValid(scoped ref Frame f)
    {
        ref var l = ref f.Locs[Out];
        l.Valid = f.B;
        l.ValidOffset = ValidReg * f.Words;
        return f.B.AsSpan(l.ValidOffset, f.Words);
    }

    /// <summary>Marks every lane missing.</summary>
    protected void ClearValid(ref Frame f) => OutValid(ref f).Clear();

    /// <summary>Lifted validity: AND of the operands' validity. Returns false when every lane is missing, in which
    /// case the values need not (and, for missing scalars, must not) be computed.</summary>
    protected bool LiftValidity(ref Frame f, ReadOnlySpan<Opnd> operands)
    {
        if (ValidReg < 0)
        {
            return true;
        }

        var started = false;
        Span<ulong> dst = default;
        foreach (var o in operands)
        {
            if (o.IsScalar)
            {
                if (f.Scalars[o.Index].IsMissing)
                {
                    ClearValid(ref f);
                    return false;
                }

                continue;
            }

            ref var l = ref f.Locs[o.Index];
            if (l.Valid is null)
            {
                continue;
            }

            var bits = new ReadOnlySpan<ulong>(l.Valid, l.ValidOffset, f.Words);
            if (!started)
            {
                dst = OutValid(ref f);
                bits.CopyTo(dst);
                started = true;
            }
            else
            {
                Bits.And(dst, bits);
            }
        }

        if (!started)
        {
            f.Locs[Out].Valid = null;
        }

        return true;
    }

    /// <summary>Copies an operand's validity into this node (constant all-valid stays <c>null</c>).</summary>
    protected void CopyValidity(ref Frame f, Opnd o)
    {
        if (ValidReg < 0)
        {
            return;
        }

        var view = f.Validity(o);
        if (view.IsConstant && view.Fill == ulong.MaxValue)
        {
            f.Locs[Out].Valid = null;
            return;
        }

        var dst = OutValid(ref f);
        for (var w = 0; w < dst.Length; w++)
        {
            dst[w] = view[w];
        }
    }

    /// <summary>Validity taken from a column buffer: zero-copy in full-range mode, gathered otherwise.</summary>
    protected void ValidityFromColumn(ref Frame f, ColumnBuffer buffer)
    {
        ref var l = ref f.Locs[Out];
        var v = buffer.Validity;
        if (v is null)
        {
            l.Valid = null;
        }
        else if (f.Identity)
        {
            l.Valid = v;
            l.ValidOffset = 0;
        }
        else
        {
            LoadKernels.GatherBits(v, f.Map, f.Physical, OutValid(ref f));
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Operand helpers (missing scalars read as a neutral value; validity decides)

    protected static double ScalarF(ref Frame f, Opnd o)
    {
        ref var s = ref f.Scalar(o);
        return s.IsMissing ? 0.0 : s.AsFloat64();
    }

    protected static long ScalarI(ref Frame f, Opnd o)
    {
        ref var s = ref f.Scalar(o);
        return s.IsMissing ? 0L : s.AsInt64();
    }

    protected static string ScalarS(ref Frame f, Opnd o)
    {
        ref var s = ref f.Scalar(o);
        return s.IsMissing ? string.Empty : s.AsString();
    }

    protected static ReadOnlySpan<double> SpanF(ref Frame f, Opnd o) => o.IsScalar ? default : f.GetF(o.Index);

    protected static ReadOnlySpan<long> SpanI(ref Frame f, Opnd o) => o.IsScalar ? default : f.GetI(o.Index);

    /// <summary>Writes an operand's values into this node's register (broadcasting scalars).</summary>
    protected void CopyValues(ref Frame f, Opnd o)
    {
        switch (ValueFile)
        {
            case RegFile.F:
            {
                var dst = OutF(ref f);
                if (o.IsScalar)
                {
                    dst.Fill(ScalarF(ref f, o));
                }
                else
                {
                    f.GetF(o.Index).CopyTo(dst);
                }

                break;
            }

            case RegFile.I:
            {
                var dst = OutI(ref f);
                if (o.IsScalar)
                {
                    dst.Fill(ScalarI(ref f, o));
                }
                else
                {
                    f.GetI(o.Index).CopyTo(dst);
                }

                break;
            }

            case RegFile.B:
            {
                var dst = OutB(ref f);
                var view = f.BoolValues(o);
                for (var w = 0; w < dst.Length; w++)
                {
                    dst[w] = view[w];
                }

                break;
            }

            case RegFile.S:
            {
                var dst = OutS(ref f);
                if (o.IsScalar)
                {
                    dst.Fill(ScalarS(ref f, o));
                }
                else
                {
                    f.GetS(o.Index).CopyTo(dst);
                }

                break;
            }
        }
    }
}

/// <summary>Reads a column symbol: slot from <c>FixedSlot</c> or the request's output bindings, int32/float32
/// promoted, zero-copy in full-range mode where the storage already has the expression type.</summary>
internal sealed class ColumnInstr(Symbol symbol, ExprType type) : Instr(type)
{
    private static readonly float[] NoFloats = Array.Empty<float>();
    private static readonly long[] NoLongs = Array.Empty<long>();
    private static readonly string[] NoStrings = Array.Empty<string>();

    public Symbol Symbol { get; } = symbol;

    public override void Execute(ref Frame f)
    {
        var buffer = ValueAccess.ResolveColumn(f.Context, Symbol);
        if (buffer is null)
        {
            if (!Type.Nullable)
            {
                throw ValueAccess.MissingNonNullableColumn(Symbol);
            }

            SetAllMissing(ref f);
            return;
        }

        ref var loc = ref f.Locs[Out];
        var n = f.N;
        switch (Type.Kind, buffer)
        {
            case (TypeKind.Float, ScalarColumn<double> c):
                if (f.Identity)
                {
                    loc.Values = c.Values;
                    loc.Offset = 0;
                }
                else
                {
                    LoadKernels.Gather<double>(c.Values, f.Map, f.Physical, OutF(ref f));
                }

                break;
            case (TypeKind.Float, ScalarColumn<float> c):
                if (f.Identity)
                {
                    LoadKernels.Widen(c.Values.AsSpan(0, n), OutF(ref f));
                }
                else
                {
                    LoadKernels.GatherWiden(c.Values, f.Map, f.Physical, OutF(ref f));
                }

                break;

            case (TypeKind.Int, ScalarColumn<long> c):
                if (f.Identity)
                {
                    loc.Values = c.Values;
                    loc.Offset = 0;
                }
                else
                {
                    LoadKernels.Gather<long>(c.Values, f.Map, f.Physical, OutI(ref f));
                }

                break;
            case (TypeKind.Int, ScalarColumn<int> c):
                if (f.Identity)
                {
                    LoadKernels.Widen(c.Values.AsSpan(0, n), OutI(ref f));
                }
                else
                {
                    LoadKernels.GatherWiden(c.Values, f.Map, f.Physical, OutI(ref f));
                }

                break;

            case (TypeKind.Bool, ScalarColumn<bool> c):
            {
                var dst = OutB(ref f);
                var bytes = MemoryMarshal.AsBytes(c.Values.AsSpan());
                if (f.Identity)
                {
                    BitKernels.Pack(bytes, dst, n);
                }
                else
                {
                    var map = f.Map;
                    dst.Clear();
                    for (var i = 0; i < n; i++)
                    {
                        if (bytes[map[i]] != 0)
                        {
                            dst[i >> 6] |= 1UL << i;
                        }
                    }
                }

                break;
            }

            case (TypeKind.String, DictStringColumn c):
            {
                var dst = OutS(ref f);
                var dictionary = c.Dictionary;
                var codes = c.Codes;
                var count = dictionary.Count;
                for (var i = 0; i < n; i++)
                {
                    var code = codes[f.Row(i)];
                    dst[i] = (uint)code < (uint)count ? dictionary[code] : string.Empty;
                }

                break;
            }

            case (TypeKind.Text, TextColumn c):
            {
                var dst = OutS(ref f);
                for (var i = 0; i < n; i++)
                {
                    dst[i] = c.Get(f.Row(i));
                }

                break;
            }

            case (TypeKind.Vector, VectorColumn):
            case (TypeKind.ListInt, ListColumn<long>):
            case (TypeKind.ListString, ListColumn<string>):
                loc.Source = SourceKind.Column;
                loc.SourceRef = buffer;
                break;
            default:
                throw ValueAccess.ColumnTypeMismatch(Symbol, buffer);
        }

        if (Type.Nullable)
        {
            ValidityFromColumn(ref f, buffer);
        }
    }

    private void SetAllMissing(ref Frame f)
    {
        ref var loc = ref f.Locs[Out];
        switch (ValueFile)
        {
            case RegFile.F: OutF(ref f); break;
            case RegFile.I: OutI(ref f); break;
            case RegFile.B: OutB(ref f); break;
            case RegFile.S: OutS(ref f); break;
            default:
                loc.Source = SourceKind.Broadcast;
                loc.SourceRef = Type.Kind switch
                {
                    TypeKind.Vector => NoFloats,
                    TypeKind.ListInt => NoLongs,
                    _ => NoStrings,
                };
                break;
        }

        ClearValid(ref f);
    }

}

internal sealed class FloatBinaryInstr(IrOp op, ExprType type, Opnd left, Opnd right) : Instr(type, left, right)
{
    public override void Execute(ref Frame f)
    {
        Opnd a = Operands[0], b = Operands[1];
        var dst = OutF(ref f);
        if (!LiftValidity(ref f, Operands))
        {
            return;
        }

        if (a.IsScalar)
        {
            var s = ScalarF(ref f, a);
            var y = f.GetF(b.Index);
            switch (op)
            {
                case IrOp.Add: TensorPrimitives.Add(y, s, dst); break;
                case IrOp.Sub: TensorPrimitives.Subtract(s, y, dst); break;
                case IrOp.Mul: TensorPrimitives.Multiply(y, s, dst); break;
                case IrOp.Div: TensorPrimitives.Divide(s, y, dst); break;
                case IrOp.Pow: MathKernels.Pow(s, y, dst); break;
                case IrOp.Min: TensorPrimitives.Min(y, s, dst); break;
                default: TensorPrimitives.Max(y, s, dst); break;
            }
        }
        else if (b.IsScalar)
        {
            var x = f.GetF(a.Index);
            var s = ScalarF(ref f, b);
            switch (op)
            {
                case IrOp.Add: TensorPrimitives.Add(x, s, dst); break;
                case IrOp.Sub: TensorPrimitives.Subtract(x, s, dst); break;
                case IrOp.Mul: TensorPrimitives.Multiply(x, s, dst); break;
                case IrOp.Div: TensorPrimitives.Divide(x, s, dst); break;
                case IrOp.Pow: MathKernels.Pow(x, s, dst); break;
                case IrOp.Min: TensorPrimitives.Min(x, s, dst); break;
                default: TensorPrimitives.Max(x, s, dst); break;
            }
        }
        else
        {
            var x = f.GetF(a.Index);
            var y = f.GetF(b.Index);
            switch (op)
            {
                case IrOp.Add: TensorPrimitives.Add(x, y, dst); break;
                case IrOp.Sub: TensorPrimitives.Subtract(x, y, dst); break;
                case IrOp.Mul: TensorPrimitives.Multiply(x, y, dst); break;
                case IrOp.Div: TensorPrimitives.Divide(x, y, dst); break;
                case IrOp.Pow: MathKernels.Pow(x, y, dst); break;
                case IrOp.Min: TensorPrimitives.Min(x, y, dst); break;
                default: TensorPrimitives.Max(x, y, dst); break;
            }
        }
    }
}

internal sealed class IntBinaryInstr(IrOp op, ExprType type, Opnd left, Opnd right) : Instr(type, left, right)
{
    public override void Execute(ref Frame f)
    {
        Opnd a = Operands[0], b = Operands[1];
        var dst = OutI(ref f);
        if (!LiftValidity(ref f, Operands))
        {
            return;
        }

        if (op == IrOp.Mod)
        {
            Mod(ref f, dst);
            return;
        }

        if (a.IsScalar)
        {
            var s = ScalarI(ref f, a);
            var y = f.GetI(b.Index);
            switch (op)
            {
                case IrOp.Add: TensorPrimitives.Add(y, s, dst); break;
                case IrOp.Sub: TensorPrimitives.Subtract(s, y, dst); break;
                case IrOp.Mul: TensorPrimitives.Multiply(y, s, dst); break;
                case IrOp.Min: TensorPrimitives.Min(y, s, dst); break;
                default: TensorPrimitives.Max(y, s, dst); break;
            }
        }
        else if (b.IsScalar)
        {
            var x = f.GetI(a.Index);
            var s = ScalarI(ref f, b);
            switch (op)
            {
                case IrOp.Add: TensorPrimitives.Add(x, s, dst); break;
                case IrOp.Sub: TensorPrimitives.Subtract(x, s, dst); break;
                case IrOp.Mul: TensorPrimitives.Multiply(x, s, dst); break;
                case IrOp.Min: TensorPrimitives.Min(x, s, dst); break;
                default: TensorPrimitives.Max(x, s, dst); break;
            }
        }
        else
        {
            var x = f.GetI(a.Index);
            var y = f.GetI(b.Index);
            switch (op)
            {
                case IrOp.Add: TensorPrimitives.Add(x, y, dst); break;
                case IrOp.Sub: TensorPrimitives.Subtract(x, y, dst); break;
                case IrOp.Mul: TensorPrimitives.Multiply(x, y, dst); break;
                case IrOp.Min: TensorPrimitives.Min(x, y, dst); break;
                default: TensorPrimitives.Max(x, y, dst); break;
            }
        }
    }

    /// <summary>Total remainder (design 7.5); counts live, present lanes with a zero divisor.</summary>
    private void Mod(scoped ref Frame f, Span<long> dst)
    {
        Opnd a = Operands[0], b = Operands[1];
        var x = SpanI(ref f, a);
        var y = SpanI(ref f, b);
        var xs = a.IsScalar ? ScalarI(ref f, a) : 0;
        var ys = b.IsScalar ? ScalarI(ref f, b) : 0;
        var n = f.N;
        var zeros = false;
        for (var i = 0; i < n; i++)
        {
            var divisor = b.IsScalar ? ys : y[i];
            var dividend = a.IsScalar ? xs : x[i];
            if (divisor == 0)
            {
                zeros = true;
                dst[i] = 0;
            }
            else
            {
                dst[i] = divisor == -1 ? 0 : dividend % divisor;
            }
        }

        var counters = f.Context.Counters;
        if (zeros && counters is not null)
        {
            var valid = f.Validity(Opnd.Node(Out));
            long count = 0;
            for (var i = 0; i < n; i++)
            {
                if ((b.IsScalar ? ys : y[i]) == 0 && valid.Get(i) && f.IsLive(i))
                {
                    count++;
                }
            }

            if (count > 0)
            {
                counters.AddIntModZero(count);
            }
        }
    }
}

internal sealed class FloatUnaryInstr(IrOp op, ExprType type, Opnd operand) : Instr(type, operand)
{
    public override void Execute(ref Frame f)
    {
        var x = Operands[0];
        var dst = OutF(ref f);
        if (!LiftValidity(ref f, Operands))
        {
            return;
        }

        var src = f.GetF(x.Index);
        switch (op)
        {
            case IrOp.Neg: TensorPrimitives.Negate(src, dst); break;
            case IrOp.Abs: TensorPrimitives.Abs(src, dst); break;
            case IrOp.Sqrt: TensorPrimitives.Sqrt(src, dst); break;
            case IrOp.Exp: MathKernels.Exp(src, dst); break;
            case IrOp.Log: MathKernels.Log(src, dst); break;
            case IrOp.Log1p: MathKernels.Log1p(src, dst); break;
            default: MathKernels.Sigmoid(src, dst); break;
        }
    }
}

internal sealed class IntUnaryInstr(IrOp op, ExprType type, Opnd operand) : Instr(type, operand)
{
    public override void Execute(ref Frame f)
    {
        var x = Operands[0];
        var dst = OutI(ref f);
        if (!LiftValidity(ref f, Operands))
        {
            return;
        }

        var src = f.GetI(x.Index);
        if (op == IrOp.Neg)
        {
            for (var i = 0; i < dst.Length; i++)
            {
                dst[i] = unchecked(-src[i]);
            }
        }
        else
        {
            for (var i = 0; i < dst.Length; i++)
            {
                dst[i] = ScalarOps.AbsInt(src[i]);
            }
        }
    }
}

internal sealed class ToFloatInstr(ExprType type, Opnd operand) : Instr(type, operand)
{
    public override void Execute(ref Frame f)
    {
        var x = Operands[0];
        var dst = OutF(ref f);
        if (!LiftValidity(ref f, Operands))
        {
            return;
        }

        var src = f.GetI(x.Index);
        for (var i = 0; i < dst.Length; i++)
        {
            dst[i] = src[i];
        }
    }
}

internal sealed class CompareInstr(IrOp op, TypeKind operandKind, ExprType type, Opnd left, Opnd right) : Instr(type, left, right)
{
    public override void Execute(ref Frame f)
    {
        Opnd a = Operands[0], b = Operands[1];
        var dst = OutB(ref f);
        if (!LiftValidity(ref f, Operands))
        {
            return;
        }

        var n = f.N;
        switch (operandKind)
        {
            case TypeKind.Float:
                if (a.IsScalar)
                {
                    BitKernels.Compare(BitKernels.Flip(op), f.GetF(b.Index), default, ScalarF(ref f, a), true, dst, n);
                }
                else
                {
                    BitKernels.Compare(op, f.GetF(a.Index), SpanF(ref f, b), b.IsScalar ? ScalarF(ref f, b) : 0, b.IsScalar, dst, n);
                }

                break;
            case TypeKind.Int:
                if (a.IsScalar)
                {
                    BitKernels.Compare(BitKernels.Flip(op), f.GetI(b.Index), default, ScalarI(ref f, a), true, dst, n);
                }
                else
                {
                    BitKernels.Compare(op, f.GetI(a.Index), SpanI(ref f, b), b.IsScalar ? ScalarI(ref f, b) : 0, b.IsScalar, dst, n);
                }

                break;
            case TypeKind.Bool:
            {
                var va = f.BoolValues(a);
                var vb = f.BoolValues(b);
                for (var w = 0; w < dst.Length; w++)
                {
                    var diff = va[w] ^ vb[w];
                    dst[w] = op == IrOp.Eq ? ~diff : diff;
                }

                break;
            }

            default:
            {
                dst.Clear();
                var eq = op == IrOp.Eq;
                var sa = a.IsScalar ? ScalarS(ref f, a) : null;
                var sb = b.IsScalar ? ScalarS(ref f, b) : null;
                var xa = a.IsScalar ? default : f.GetS(a.Index);
                var xb = b.IsScalar ? default : f.GetS(b.Index);
                for (var i = 0; i < n; i++)
                {
                    if (string.Equals(sa ?? xa[i], sb ?? xb[i], StringComparison.Ordinal) == eq)
                    {
                        dst[i >> 6] |= 1UL << i;
                    }
                }

                break;
            }
        }
    }
}

/// <summary>Fused <c>col.s == 'const'</c> / <c>!=</c> on a dictionary-encoded column: the constant becomes a code once
/// per call and the loop compares integers (design 4.4).</summary>
internal sealed class DictEqualsInstr(IrOp op, Symbol column, ExprType columnType, ExprType type, Opnd constant) : Instr(type, constant)
{
    public override void Execute(ref Frame f)
    {
        var value = Operands[0];
        var dst = OutB(ref f);
        var buffer = ValueAccess.ResolveColumn(f.Context, column);
        if (buffer is null)
        {
            if (!columnType.Nullable)
            {
                throw ValueAccess.MissingNonNullableColumn(column);
            }

            ClearValid(ref f);
            return;
        }

        if (f.Scalar(value).IsMissing)
        {
            ClearValid(ref f);
            return;
        }

        var col = buffer as DictStringColumn ?? throw ValueAccess.ColumnTypeMismatch(column, buffer);
        var code = col.FindCode(f.Scalar(value).AsString());
        var n = f.N;
        if (code < 0)
        {
            dst.Clear();
        }
        else if (f.Identity)
        {
            BitKernels.EqualsCode(col.Codes.AsSpan(0, n), code, dst, n);
        }
        else
        {
            var codes = col.Codes;
            var map = f.Map;
            dst.Clear();
            for (var i = 0; i < n; i++)
            {
                if (codes[map[i]] == code)
                {
                    dst[i >> 6] |= 1UL << i;
                }
            }
        }

        if (op == IrOp.Ne)
        {
            for (var w = 0; w < dst.Length; w++)
            {
                dst[w] = ~dst[w];
            }
        }

        if (ValidReg >= 0)
        {
            if (columnType.Nullable)
            {
                ValidityFromColumn(ref f, buffer);
            }
            else
            {
                f.Locs[Out].Valid = null;
            }
        }
    }
}

/// <summary>Fused per-dictionary-entry predicate on a dictionary-encoded column: <c>col.s in [...]</c>,
/// <c>col.s in ctx.list</c> and <c>col.s.startsWith(p)</c> evaluate the predicate once per distinct value.</summary>
internal sealed class DictPredicateInstr : Instr
{
    private readonly Symbol _column;
    private readonly ExprType _columnType;
    private readonly ConstSet? _set;
    private readonly IrOp _op;

    /// <summary><paramref name="op"/> is InSet (with <paramref name="set"/>), Contains (scalar list operand) or
    /// StartsWith (scalar prefix operand).</summary>
    public DictPredicateInstr(IrOp op, Symbol column, ExprType columnType, ExprType type, ConstSet? set, Opnd[] operands)
        : base(type, operands)
    {
        _op = op;
        _column = column;
        _columnType = columnType;
        _set = set;
    }

    public override void Execute(ref Frame f)
    {
        var dst = OutB(ref f);
        var buffer = ValueAccess.ResolveColumn(f.Context, _column);
        if (buffer is null)
        {
            if (!_columnType.Nullable)
            {
                throw ValueAccess.MissingNonNullableColumn(_column);
            }

            ClearValid(ref f);
            return;
        }

        if (Operands.Length > 0 && f.Scalar(Operands[0]).IsMissing)
        {
            ClearValid(ref f);
            return;
        }

        var col = buffer as DictStringColumn ?? throw ValueAccess.ColumnTypeMismatch(_column, buffer);
        var dictionary = col.Dictionary;
        var count = dictionary.Count;
        var table = System.Buffers.ArrayPool<bool>.Shared.Rent(Math.Max(1, count));
        try
        {
            var flags = table.AsSpan(0, count);
            flags.Clear();
            switch (_op)
            {
                case IrOp.InSet:
                    foreach (var s in _set!.Strings)
                    {
                        Mark(col, s, flags);
                    }

                    break;
                case IrOp.Contains:
                    foreach (var s in f.Scalar(Operands[0]).AsStringList())
                    {
                        Mark(col, s, flags);
                    }

                    break;
                default:
                {
                    var prefix = f.Scalar(Operands[0]).AsString();
                    for (var c = 0; c < count; c++)
                    {
                        flags[c] = dictionary[c].StartsWith(prefix, StringComparison.Ordinal);
                    }

                    break;
                }
            }

            var codes = col.Codes;
            var n = f.N;
            dst.Clear();
            for (var i = 0; i < n; i++)
            {
                var code = codes[f.Row(i)];
                if ((uint)code < (uint)count && flags[code])
                {
                    dst[i >> 6] |= 1UL << i;
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<bool>.Shared.Return(table);
        }

        if (ValidReg >= 0)
        {
            if (_columnType.Nullable)
            {
                ValidityFromColumn(ref f, buffer);
            }
            else
            {
                f.Locs[Out].Valid = null;
            }
        }
    }

    private static void Mark(DictStringColumn column, string? value, Span<bool> flags)
    {
        if (value is null)
        {
            return;
        }

        var code = column.FindCode(value);
        if (code >= 0)
        {
            flags[code] = true;
        }
    }
}

/// <summary>Kleene <c>&amp;&amp;</c> / <c>||</c> on packed bits (design 7.4: false &amp;&amp; missing = false).</summary>
internal sealed class LogicInstr(IrOp op, ExprType type, Opnd left, Opnd right) : Instr(type, left, right)
{
    public override void Execute(ref Frame f)
    {
        Opnd a = Operands[0], b = Operands[1];
        var dst = OutB(ref f);
        var va = f.BoolValues(a);
        var vb = f.BoolValues(b);
        var and = op == IrOp.And;
        if (ValidReg < 0)
        {
            for (var w = 0; w < dst.Length; w++)
            {
                dst[w] = and ? va[w] & vb[w] : va[w] | vb[w];
            }

            return;
        }

        var ma = f.Validity(a);
        var mb = f.Validity(b);
        var valid = OutValid(ref f);
        for (var w = 0; w < dst.Length; w++)
        {
            ulong x = va[w], y = vb[w], px = ma[w], py = mb[w];
            if (and)
            {
                dst[w] = x & y;
                valid[w] = (px & py) | (px & ~x) | (py & ~y);
            }
            else
            {
                dst[w] = x | y;
                valid[w] = (px & py) | (px & x) | (py & y);
            }
        }
    }
}

internal sealed class NotInstr(ExprType type, Opnd operand) : Instr(type, operand)
{
    public override void Execute(ref Frame f)
    {
        var x = Operands[0];
        var dst = OutB(ref f);
        LiftValidity(ref f, Operands);
        var src = f.GetB(x.Index);
        for (var w = 0; w < dst.Length; w++)
        {
            dst[w] = ~src[w];
        }
    }
}

internal sealed class HasInstr(ExprType type, Opnd operand) : Instr(type, operand)
{
    public override void Execute(ref Frame f)
    {
        var x = Operands[0];
        var dst = OutB(ref f);
        var valid = f.Validity(x);
        for (var w = 0; w < dst.Length; w++)
        {
            dst[w] = valid[w];
        }
    }
}

/// <summary><c>c ? a : b</c> for value kinds: both branches are computed and selected by the condition mask
/// (design 7.7); a missing condition gives a missing result.</summary>
internal sealed class SelectInstr(ExprType type, Opnd condition, Opnd then, Opnd otherwise) : Instr(type, condition, then, otherwise)
{
    public override void Execute(ref Frame f)
    {
        Opnd c = Operands[0], a = Operands[1], b = Operands[2];
        if (c.IsScalar)
        {
            ref var cs = ref f.Scalar(c);
            if (cs.IsMissing)
            {
                CopyValues(ref f, a);
                ClearValid(ref f);
                return;
            }

            var chosen = cs.AsBool() ? a : b;
            CopyValues(ref f, chosen);
            CopyValidity(ref f, chosen);
            return;
        }

        var mask = f.BoolValues(c);
        var n = f.N;
        switch (ValueFile)
        {
            case RegFile.F:
                BitKernels.Select(mask, SpanF(ref f, a), a.IsScalar ? ScalarF(ref f, a) : 0, a.IsScalar,
                    SpanF(ref f, b), b.IsScalar ? ScalarF(ref f, b) : 0, b.IsScalar, OutF(ref f), n);
                break;
            case RegFile.I:
                BitKernels.Select(mask, SpanI(ref f, a), a.IsScalar ? ScalarI(ref f, a) : 0, a.IsScalar,
                    SpanI(ref f, b), b.IsScalar ? ScalarI(ref f, b) : 0, b.IsScalar, OutI(ref f), n);
                break;
            case RegFile.B:
            {
                var dst = OutB(ref f);
                var va = f.BoolValues(a);
                var vb = f.BoolValues(b);
                for (var w = 0; w < dst.Length; w++)
                {
                    dst[w] = (mask[w] & va[w]) | (~mask[w] & vb[w]);
                }

                break;
            }

            default:
            {
                var dst = OutS(ref f);
                var sa = a.IsScalar ? ScalarS(ref f, a) : null;
                var sb = b.IsScalar ? ScalarS(ref f, b) : null;
                var xa = a.IsScalar ? default : f.GetS(a.Index);
                var xb = b.IsScalar ? default : f.GetS(b.Index);
                for (var i = 0; i < n; i++)
                {
                    dst[i] = mask.Get(i) ? sa ?? xa[i] : sb ?? xb[i];
                }

                break;
            }
        }

        if (ValidReg >= 0)
        {
            var vc = f.Validity(c);
            var va = f.Validity(a);
            var vb = f.Validity(b);
            var valid = OutValid(ref f);
            for (var w = 0; w < valid.Length; w++)
            {
                valid[w] = vc[w] & ((mask[w] & va[w]) | (~mask[w] & vb[w]));
            }
        }
    }
}

/// <summary><c>x ?? y</c> for value kinds: selects by the validity of <c>x</c>.</summary>
internal sealed class CoalesceInstr(ExprType type, Opnd left, Opnd right) : Instr(type, left, right)
{
    public override void Execute(ref Frame f)
    {
        Opnd x = Operands[0], y = Operands[1];
        var mask = f.Validity(x);
        if (mask.IsConstant)
        {
            // x is present everywhere (or missing everywhere): no per-lane selection needed.
            var chosen = mask.Fill != 0 ? x : y;
            CopyValues(ref f, chosen);
            CopyValidity(ref f, chosen);
            return;
        }

        var n = f.N;
        switch (ValueFile)
        {
            case RegFile.F:
                BitKernels.Select(mask, f.GetF(x.Index), 0, false, SpanF(ref f, y), y.IsScalar ? ScalarF(ref f, y) : 0, y.IsScalar, OutF(ref f), n);
                break;
            case RegFile.I:
                BitKernels.Select(mask, f.GetI(x.Index), 0, false, SpanI(ref f, y), y.IsScalar ? ScalarI(ref f, y) : 0, y.IsScalar, OutI(ref f), n);
                break;
            case RegFile.B:
            {
                var dst = OutB(ref f);
                var vx = f.GetB(x.Index);
                var vy = f.BoolValues(y);
                for (var w = 0; w < dst.Length; w++)
                {
                    dst[w] = (mask[w] & vx[w]) | (~mask[w] & vy[w]);
                }

                break;
            }

            default:
            {
                var dst = OutS(ref f);
                var xs = f.GetS(x.Index);
                var sy = y.IsScalar ? ScalarS(ref f, y) : null;
                var ys = y.IsScalar ? default : f.GetS(y.Index);
                for (var i = 0; i < n; i++)
                {
                    dst[i] = mask.Get(i) ? xs[i] : sy ?? ys[i];
                }

                break;
            }
        }

        if (ValidReg >= 0)
        {
            var vy = f.Validity(y);
            var valid = OutValid(ref f);
            for (var w = 0; w < valid.Length; w++)
            {
                valid[w] = mask[w] | vy[w];
            }
        }
    }
}

/// <summary><c>c ? a : b</c> and <c>x ?? y</c> for vectors and lists: a lazy per-lane selection that keeps its
/// operands (and the mask) alive instead of copying variable-size data.</summary>
internal sealed class SourceSelectInstr : Instr
{
    private readonly bool _coalesce;

    private SourceSelectInstr(ExprType type, bool coalesce, Opnd[] operands)
        : base(type, operands)
    {
        _coalesce = coalesce;
    }

    public override bool Retains => true;

    public static SourceSelectInstr Conditional(ExprType type, Opnd c, Opnd a, Opnd b) => new(type, false, new[] { c, a, b });

    public static SourceSelectInstr Coalesce(ExprType type, Opnd x, Opnd y) => new(type, true, new[] { x, y });

    public override void Execute(ref Frame f)
    {
        ref var loc = ref f.Locs[Out];
        loc.Source = SourceKind.Select;
        if (_coalesce)
        {
            Opnd x = Operands[0], y = Operands[1];
            var mask = f.Validity(x);
            if (mask.IsConstant)
            {
                var chosen = mask.Fill != 0 ? x : y;
                loc.Mask = null;
                loc.SelA = loc.SelB = chosen;
                CopyValidity(ref f, chosen);
                return;
            }

            ref var lx = ref f.Locs[x.Index];
            loc.Mask = lx.Valid;
            loc.MaskOffset = lx.ValidOffset;
            loc.SelA = x;
            loc.SelB = y;
            if (ValidReg >= 0)
            {
                var vy = f.Validity(y);
                var valid = OutValid(ref f);
                for (var w = 0; w < valid.Length; w++)
                {
                    valid[w] = mask[w] | vy[w];
                }
            }

            return;
        }

        Opnd c = Operands[0], a = Operands[1], b = Operands[2];
        if (c.IsScalar)
        {
            ref var cs = ref f.Scalar(c);
            var chosen = !cs.IsMissing && cs.AsBool() ? a : b;
            loc.Mask = null;
            loc.SelA = loc.SelB = chosen;
            if (cs.IsMissing)
            {
                ClearValid(ref f);
            }
            else
            {
                CopyValidity(ref f, chosen);
            }

            return;
        }

        ref var lc = ref f.Locs[c.Index];
        loc.Mask = (ulong[])lc.Values!;
        loc.MaskOffset = lc.Offset;
        loc.SelA = a;
        loc.SelB = b;
        if (ValidReg >= 0)
        {
            var m = f.GetB(c.Index);
            var vc = f.Validity(c);
            var va = f.Validity(a);
            var vb = f.Validity(b);
            var valid = OutValid(ref f);
            for (var w = 0; w < valid.Length; w++)
            {
                valid[w] = vc[w] & ((m[w] & va[w]) | (~m[w] & vb[w]));
            }
        }
    }
}
