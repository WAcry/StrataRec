using StrataRec.Core.Values;
using StrataRec.Expressions.Binding;
using StrataRec.Expressions.Runtime;

namespace StrataRec.Expressions.Ir;

/// <summary>
/// IR optimizer (design 7.7): constant folding, value-preserving algebraic simplification and common subexpression
/// elimination by hash-consing. Loop-invariant hoisting needs no rewrite: <see cref="IrNode.IsInvariant"/> marks every
/// subtree that depends only on <c>ctx.*</c>, <c>param.*</c> and constants, and the backend evaluates those once per
/// call.
/// </summary>
/// <remarks>Every rewrite is exact for all inputs, including NaN, ±∞, ±0 and missing values, so the optimized program
/// is bit-identical to the unoptimized one (the differential tests run the reference interpreter on the
/// unoptimized IR).</remarks>
internal sealed class Optimizer
{
    private readonly Dictionary<IrNode, IrNode> _memo = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IrNode, IrNode> _canonical = new(StructuralComparer.Instance);

    public static IrNode Optimize(IrNode root) => new Optimizer().Visit(root);

    private IrNode Visit(IrNode node)
    {
        if (_memo.TryGetValue(node, out var done))
        {
            return done;
        }

        IrNode result;
        if (node.Args.Length == 0)
        {
            result = Intern(node);
        }
        else
        {
            var args = new IrNode[node.Args.Length];
            var changed = false;
            for (var i = 0; i < args.Length; i++)
            {
                args[i] = Visit(node.Args[i]);
                changed |= !ReferenceEquals(args[i], node.Args[i]);
            }

            result = Intern(Simplify(changed ? node.WithArgs(args, node.Type) : node));
        }

        _memo[node] = result;
        return result;
    }

    private IrNode Intern(IrNode node)
    {
        if (_canonical.TryGetValue(node, out var existing))
        {
            return existing;
        }

        _canonical.Add(node, node);
        return node;
    }

    private IrNode Simplify(IrNode node)
    {
        var args = node.Args;
        if (TryFold(node, out var folded))
        {
            return folded;
        }

        switch (node.Op)
        {
            case IrOp.Coalesce when !args[0].Type.Nullable:
                return args[0];
            case IrOp.Has when !args[0].Type.Nullable:
                return IrNode.Const(ScalarValue.FromBool(true), ExprType.Bool, node.Start, node.Length);
            case IrOp.And:
                // Kleene: false && x = false, true && x = x.
                if (IsBool(args[0], out var a))
                {
                    return a ? args[1] : args[0];
                }

                if (IsBool(args[1], out var b))
                {
                    return b ? args[0] : args[1];
                }

                break;
            case IrOp.Or:
                if (IsBool(args[0], out var c))
                {
                    return c ? args[0] : args[1];
                }

                if (IsBool(args[1], out var d))
                {
                    return d ? args[1] : args[0];
                }

                break;
            case IrOp.Not when args[0].Op == IrOp.Not:
            case IrOp.Neg when args[0].Op == IrOp.Neg:
                return args[0].Args[0];
            case IrOp.Conditional when IsBool(args[0], out var cond):
                return cond ? args[1] : args[2];
            case IrOp.Mul when node.Type.Kind == TypeKind.Float:
                if (IsFloat(args[0], 1.0))
                {
                    return args[1];
                }

                if (IsFloat(args[1], 1.0))
                {
                    return args[0];
                }

                return MoveNegationToInvariant(node);
            case IrOp.Mul when node.Type.Kind == TypeKind.Int:
                if (IsInt(args[0], 1))
                {
                    return args[1];
                }

                if (IsInt(args[1], 1))
                {
                    return args[0];
                }

                break;
            case IrOp.Div:
                if (IsFloat(args[1], 1.0))
                {
                    return args[0];
                }

                return MoveNegationToInvariant(node);
        }

        return node;
    }

    /// <summary>
    /// <c>(-a) * s</c> → <c>a * (-s)</c> and <c>(-a) / s</c> → <c>a / (-s)</c> when <c>s</c> is invariant: IEEE
    /// multiplication and division are sign-symmetric, so the result is identical while the negation moves out of the
    /// per-row loop (e.g. <c>exp(-age / param.half_life)</c>).
    /// </summary>
    private IrNode MoveNegationToInvariant(IrNode node)
    {
        var (left, right) = (node.Args[0], node.Args[1]);
        if (left.Op == IrOp.Neg && !left.IsInvariant && right.IsInvariant)
        {
            var negated = Intern(Simplify(IrNode.Make(IrOp.Neg, right.Type, new[] { right }, right.Start, right.Length)));
            return node.WithArgs(new[] { left.Args[0], negated }, node.Type);
        }

        if (node.Op == IrOp.Mul && right.Op == IrOp.Neg && !right.IsInvariant && left.IsInvariant)
        {
            var negated = Intern(Simplify(IrNode.Make(IrOp.Neg, left.Type, new[] { left }, left.Start, left.Length)));
            return node.WithArgs(new[] { negated, right.Args[0] }, node.Type);
        }

        return node;
    }

    private static bool TryFold(IrNode node, out IrNode folded)
    {
        folded = node;
        if (node.Op is IrOp.Const or IrOp.Column or IrOp.Context or IrOp.Param)
        {
            return false;
        }

        var values = new ScalarValue[node.Args.Length];
        for (var i = 0; i < values.Length; i++)
        {
            if (!node.Args[i].IsConst || node.Args[i].Type.Kind == TypeKind.ListFloat)
            {
                return false;
            }

            values[i] = node.Args[i].Value;
        }

        // Keep x % 0 for runtime so that expr_int_mod_zero_total still counts it (design 7.5).
        if (node.Op == IrOp.Mod && values[1].AsInt64() == 0)
        {
            return false;
        }

        var value = ScalarOps.Apply(node, values, counters: null);
        if (value.IsMissing)
        {
            return false;
        }

        folded = IrNode.Const(value, node.Type, node.Start, node.Length);
        return true;
    }

    private static bool IsBool(IrNode node, out bool value)
    {
        value = node.IsConst && node.Type.Kind == TypeKind.Bool && node.Value.AsBool();
        return node.IsConst && node.Type.Kind == TypeKind.Bool;
    }

    private static bool IsFloat(IrNode node, double value) =>
        node.IsConst && node.Type.Kind == TypeKind.Float && BitConverter.DoubleToInt64Bits(node.Value.AsFloat64()) == BitConverter.DoubleToInt64Bits(value);

    private static bool IsInt(IrNode node, long value) => node.IsConst && node.Type.Kind == TypeKind.Int && node.Value.AsInt64() == value;

    private sealed class StructuralComparer : IEqualityComparer<IrNode>
    {
        public static readonly StructuralComparer Instance = new();

        public bool Equals(IrNode? x, IrNode? y) => ReferenceEquals(x, y) || (x is not null && y is not null && x.StructurallyEquals(y));

        public int GetHashCode(IrNode obj) => obj.StructuralHash();
    }
}
