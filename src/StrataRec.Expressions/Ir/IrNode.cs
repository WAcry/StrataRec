using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions.Binding;

namespace StrataRec.Expressions.Ir;

/// <summary>Operations of the typed intermediate representation. Sugar (clamp, logit, soft_floor, linear_decay,
/// n-ary min/max, <c>in</c>) is lowered by the binder into these primitives.</summary>
internal enum IrOp : byte
{
    Const,
    Column,
    Context,
    Param,

    // Unary.
    Neg,
    Not,
    ToFloat,
    Abs,
    Sqrt,
    Exp,
    Log,
    Log1p,
    Sigmoid,

    // Binary arithmetic.
    Add,
    Sub,
    Mul,
    Div,
    Mod,
    Pow,
    Min,
    Max,

    // Comparison (operands share one kind after promotion).
    Eq,
    Ne,
    Lt,
    Le,
    Gt,
    Ge,

    // Kleene logic and missing-value handling.
    And,
    Or,
    Conditional,
    Coalesce,
    Has,

    // Lists, text, strings.
    Size,
    Contains,
    InSet,
    Overlap,
    StartsWith,

    // Vectors.
    Dot,
    Cosine,
    L2Norm,

    // Other functions.
    PiecewiseLinear,
    Hash01,
    Lookup,
}

/// <summary>
/// A node of the typed IR. After optimization the IR is a DAG: let bindings and common subexpressions are shared
/// node instances. Nodes are immutable.
/// </summary>
internal sealed class IrNode
{
    private static readonly IrNode[] NoArgs = Array.Empty<IrNode>();

    private IrNode(IrOp op, ExprType type, IrNode[] args, ScalarValue value, Symbol symbol, object? data, int start, int length)
    {
        Op = op;
        Type = type;
        Args = args;
        Value = value;
        Symbol = symbol;
        Data = data;
        Start = start;
        Length = length;

        var depth = 0;
        var invariant = op != IrOp.Column;
        foreach (var a in args)
        {
            depth = Math.Max(depth, a.Depth);
            invariant &= a.IsInvariant;
        }

        Depth = args.Length == 0 ? 0 : depth + 1;
        IsInvariant = invariant;
    }

    public IrOp Op { get; }

    public ExprType Type { get; }

    public IrNode[] Args { get; }

    /// <summary>Constant value (<see cref="IrOp.Const"/>; float list literals keep their values in <see cref="Data"/>).</summary>
    public ScalarValue Value { get; }

    /// <summary>Resolved symbol for <see cref="IrOp.Column"/>, <see cref="IrOp.Context"/>, <see cref="IrOp.Param"/>.</summary>
    public Symbol Symbol { get; }

    /// <summary>Compile-time payload: <see cref="ConstSet"/> (InSet), <see cref="Knots"/> (PiecewiseLinear),
    /// <see cref="ILookupTable"/> (Lookup), <c>double[]</c> (float list literal).</summary>
    public object? Data { get; }

    /// <summary>Source span used for diagnostics.</summary>
    public int Start { get; }

    public int Length { get; }

    public int Depth { get; }

    /// <summary>True when the subtree reads no <c>col.*</c> symbol: evaluated once per request (design 7.7).</summary>
    public bool IsInvariant { get; }

    /// <summary>Name of the <c>let</c> binding this node came from, used by <c>Describe()</c>.</summary>
    public string? NameHint { get; private set; }

    public bool IsConst => Op == IrOp.Const;

    public static IrNode Const(ScalarValue value, ExprType type, int start = 0, int length = 0, object? data = null) =>
        new(IrOp.Const, type.WithNullable(false), NoArgs, value, default, data, start, length);

    public static IrNode Read(Symbol symbol, ExprType type, int start, int length)
    {
        var op = symbol.Kind switch
        {
            SymbolKind.Column => IrOp.Column,
            SymbolKind.Context => IrOp.Context,
            _ => IrOp.Param,
        };
        return new IrNode(op, type, NoArgs, default, symbol, null, start, length);
    }

    public static IrNode Make(IrOp op, ExprType type, IrNode[] args, int start, int length, object? data = null) =>
        new(op, type, args, default, default, data, start, length);

    /// <summary>Same operation and payload over new arguments (used by the optimizer).</summary>
    public IrNode WithArgs(IrNode[] args, ExprType type)
    {
        var node = new IrNode(Op, type, args, Value, Symbol, Data, Start, Length) { NameHint = NameHint };
        return node;
    }

    public IrNode WithNameHint(string name)
    {
        NameHint ??= name;
        return this;
    }

    /// <summary>Structural identity given canonical (already deduplicated) arguments; used for CSE.</summary>
    public bool StructurallyEquals(IrNode other)
    {
        if (Op != other.Op || Type != other.Type || Args.Length != other.Args.Length || !ReferenceEquals(Data, other.Data))
        {
            return false;
        }

        for (var i = 0; i < Args.Length; i++)
        {
            if (!ReferenceEquals(Args[i], other.Args[i]))
            {
                return false;
            }
        }

        return Op switch
        {
            IrOp.Const => ConstEquals(Value, other.Value),
            IrOp.Column or IrOp.Context or IrOp.Param => Symbol == other.Symbol,
            _ => true,
        };
    }

    public int StructuralHash()
    {
        var h = new HashCode();
        h.Add(Op);
        h.Add(Type);
        foreach (var a in Args)
        {
            h.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(a));
        }

        switch (Op)
        {
            case IrOp.Const:
                h.Add(Value.Kind);
                if (Value.Kind is StorageKind.Int64 or StorageKind.Bool)
                {
                    h.Add(Value.GetHashCode());
                }
                else if (Value.Kind == StorageKind.Float64)
                {
                    h.Add(BitConverter.DoubleToInt64Bits(Value.AsFloat64()));
                }

                break;
            case IrOp.Column or IrOp.Context or IrOp.Param:
                h.Add(Symbol.Id);
                h.Add(Symbol.FixedSlot);
                break;
        }

        return h.ToHashCode();
    }

    private static bool ConstEquals(in ScalarValue a, in ScalarValue b)
    {
        if (a.Kind != b.Kind)
        {
            return false;
        }

        // Bitwise for floats so that 0.0 and -0.0 (and NaN payloads) stay distinct.
        return a.Kind == StorageKind.Float64
            ? BitConverter.DoubleToInt64Bits(a.AsFloat64()) == BitConverter.DoubleToInt64Bits(b.AsFloat64())
            : a.Equals(b);
    }

    public override string ToString() => IrPrinter.Print(this);
}

/// <summary>Constant membership set of an <c>x in [literal list]</c> test, prepared at compile time.</summary>
internal sealed class ConstSet
{
    private readonly long[] _ints;
    private readonly double[] _floats;
    private readonly HashSet<string>? _strings;

    private ConstSet(TypeKind elementKind, long[] ints, double[] floats, string[] strings)
    {
        ElementKind = elementKind;
        _ints = ints;
        _floats = floats;
        Strings = strings;
        if (elementKind == TypeKind.String)
        {
            _strings = new HashSet<string>(strings, StringComparer.Ordinal);
        }
    }

    /// <summary>Int, Float or String.</summary>
    public TypeKind ElementKind { get; }

    /// <summary>Distinct string elements in source order.</summary>
    public string[] Strings { get; }

    /// <summary>Elements in source order for printing.</summary>
    public string Display { get; private init; } = string.Empty;

    public static ConstSet OfInts(long[] values)
    {
        var sorted = (long[])values.Clone();
        Array.Sort(sorted);
        return new ConstSet(TypeKind.Int, sorted, Array.Empty<double>(), Array.Empty<string>()) { Display = IrPrinter.FormatList(values) };
    }

    public static ConstSet OfFloats(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return new ConstSet(TypeKind.Float, Array.Empty<long>(), sorted, Array.Empty<string>()) { Display = IrPrinter.FormatList(values) };
    }

    public static ConstSet OfStrings(string[] values)
    {
        var distinct = new List<string>(values.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in values)
        {
            if (seen.Add(v))
            {
                distinct.Add(v);
            }
        }

        return new ConstSet(TypeKind.String, Array.Empty<long>(), Array.Empty<double>(), distinct.ToArray()) { Display = IrPrinter.FormatList(values) };
    }

    public bool Contains(long value) => Array.BinarySearch(_ints, value) >= 0;

    public bool Contains(double value) => !double.IsNaN(value) && Array.BinarySearch(_floats, value) >= 0;

    public bool Contains(string? value) => value is not null && _strings!.Contains(value);
}

/// <summary>Validated knots of <c>piecewise_linear(x, xs, ys)</c>: strictly ascending finite xs, finite ys.</summary>
internal sealed class Knots(double[] xs, double[] ys)
{
    public double[] Xs { get; } = xs;

    public double[] Ys { get; } = ys;

    /// <summary>Linear interpolation clamped at both ends; NaN for NaN input.</summary>
    public double Evaluate(double x)
    {
        var xs = Xs;
        var ys = Ys;
        if (double.IsNaN(x))
        {
            return double.NaN;
        }

        if (x <= xs[0])
        {
            return ys[0];
        }

        var last = xs.Length - 1;
        if (x >= xs[last])
        {
            return ys[last];
        }

        // Largest i with xs[i] <= x; i in [0, last - 1].
        int lo = 0, hi = last;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) >>> 1;
            if (xs[mid] <= x)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        var t = (x - xs[lo]) / (xs[lo + 1] - xs[lo]);
        return ys[lo] + (t * (ys[lo + 1] - ys[lo]));
    }
}
