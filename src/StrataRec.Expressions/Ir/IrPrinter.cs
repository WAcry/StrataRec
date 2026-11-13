using System.Globalization;
using System.Text;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions.Binding;

namespace StrataRec.Expressions.Ir;

/// <summary>
/// Prints IR back in ColumnExpr syntax for explain-plan output. Nodes shared by several parents (let bindings and
/// common subexpressions) are printed once as <c>let</c> bindings, so the text is linear in the DAG size.
/// </summary>
internal static class IrPrinter
{
    private const int PrecConditional = 1;
    private const int PrecCoalesce = 2;
    private const int PrecOr = 3;
    private const int PrecAnd = 4;
    private const int PrecRelation = 5;
    private const int PrecAdditive = 6;
    private const int PrecMultiplicative = 7;
    private const int PrecUnary = 8;
    private const int PrecPrimary = 9;

    public static string Print(IrNode root)
    {
        var parents = new Dictionary<IrNode, int>(ReferenceEqualityComparer.Instance);
        var order = new List<IrNode>();
        CountParents(root, parents, order);

        var names = new Dictionary<IrNode, string>(ReferenceEqualityComparer.Instance);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var node in order)
        {
            if (ReferenceEquals(node, root) || node.Args.Length == 0 || parents[node] < 2)
            {
                continue;
            }

            var name = node.NameHint is { } hint && used.Add(hint) ? hint : NextName(used);
            sb.Append("let ").Append(name).Append(" = ").Append(Expr(node, names, 0).Text).Append(";\n");
            names[node] = name;
        }

        sb.Append(Expr(root, names, 0).Text);
        return sb.ToString();
    }

    private static string NextName(HashSet<string> used)
    {
        for (var i = 1; ; i++)
        {
            var name = "t" + i.ToString(CultureInfo.InvariantCulture);
            if (used.Add(name))
            {
                return name;
            }
        }
    }

    /// <summary>Post-order (children first) traversal recording each node's parent count.</summary>
    private static void CountParents(IrNode node, Dictionary<IrNode, int> parents, List<IrNode> order)
    {
        if (parents.TryGetValue(node, out var count))
        {
            parents[node] = count + 1;
            return;
        }

        parents[node] = 1;
        foreach (var arg in node.Args)
        {
            CountParents(arg, parents, order);
        }

        order.Add(node);
    }

    private static (string Text, int Prec) Expr(IrNode node, Dictionary<IrNode, string> names, int depth)
    {
        if (depth > 0 && names.TryGetValue(node, out var name))
        {
            return (name, PrecPrimary);
        }

        var a = node.Args;
        switch (node.Op)
        {
            case IrOp.Const:
                return FormatConst(node);
            case IrOp.Column or IrOp.Context or IrOp.Param:
                return (node.Symbol.Name, PrecPrimary);
            case IrOp.Neg:
            {
                var operand = Operand(a[0], names, depth, PrecUnary);
                return ("-" + (operand.StartsWith('-') ? "(" + operand + ")" : operand), PrecUnary);
            }

            case IrOp.Not:
                return ("!" + Operand(a[0], names, depth, PrecUnary), PrecUnary);
            case IrOp.Add: return Binary("+", PrecAdditive, a, names, depth);
            case IrOp.Sub: return Binary("-", PrecAdditive, a, names, depth);
            case IrOp.Mul: return Binary("*", PrecMultiplicative, a, names, depth);
            case IrOp.Div: return Binary("/", PrecMultiplicative, a, names, depth);
            case IrOp.Mod: return Binary("%", PrecMultiplicative, a, names, depth);
            case IrOp.Eq: return Binary("==", PrecRelation, a, names, depth);
            case IrOp.Ne: return Binary("!=", PrecRelation, a, names, depth);
            case IrOp.Lt: return Binary("<", PrecRelation, a, names, depth);
            case IrOp.Le: return Binary("<=", PrecRelation, a, names, depth);
            case IrOp.Gt: return Binary(">", PrecRelation, a, names, depth);
            case IrOp.Ge: return Binary(">=", PrecRelation, a, names, depth);
            case IrOp.And: return Binary("&&", PrecAnd, a, names, depth);
            case IrOp.Or: return Binary("||", PrecOr, a, names, depth);
            case IrOp.Coalesce:
                return (Operand(a[0], names, depth, PrecCoalesce + 1) + " ?? " + Operand(a[1], names, depth, PrecCoalesce), PrecCoalesce);
            case IrOp.Conditional:
                return (Operand(a[0], names, depth, PrecConditional + 1) + " ? " + Operand(a[1], names, depth, PrecConditional + 1) + " : " +
                        Operand(a[2], names, depth, PrecConditional), PrecConditional);
            case IrOp.InSet:
                return (Operand(a[0], names, depth, PrecRelation + 1) + " in " + ((ConstSet)node.Data!).Display, PrecRelation);
            case IrOp.ToFloat: return Call("double", node, names, depth);
            case IrOp.Abs: return Call("abs", node, names, depth);
            case IrOp.Sqrt: return Call("sqrt", node, names, depth);
            case IrOp.Exp: return Call("exp", node, names, depth);
            case IrOp.Log: return Call("log", node, names, depth);
            case IrOp.Log1p: return Call("log1p", node, names, depth);
            case IrOp.Sigmoid: return Call("sigmoid", node, names, depth);
            case IrOp.Pow: return Call("pow", node, names, depth);
            case IrOp.Min: return Call("min", node, names, depth);
            case IrOp.Max: return Call("max", node, names, depth);
            case IrOp.Has: return Call("has", node, names, depth);
            case IrOp.Size: return Call("size", node, names, depth);
            case IrOp.Contains: return Call("contains", node, names, depth);
            case IrOp.Overlap: return Call("overlap_count", node, names, depth);
            case IrOp.StartsWith: return Call("startsWith", node, names, depth);
            case IrOp.Dot: return Call("dot", node, names, depth);
            case IrOp.Cosine: return Call("cosine", node, names, depth);
            case IrOp.L2Norm: return Call("l2_norm", node, names, depth);
            case IrOp.Hash01: return Call("hash01", node, names, depth);
            case IrOp.PiecewiseLinear:
            {
                var knots = (Knots)node.Data!;
                return ($"piecewise_linear({Operand(a[0], names, depth, 0)}, {FormatList(knots.Xs)}, {FormatList(knots.Ys)})", PrecPrimary);
            }

            case IrOp.Lookup:
            {
                var table = (ILookupTable)node.Data!;
                return ($"lookup({Quote(table.Name)}, {Operand(a[0], names, depth, 0)}, {Operand(a[1], names, depth, 0)})", PrecPrimary);
            }

            default:
                return (node.Op.ToString(), PrecPrimary);
        }
    }

    private static string Operand(IrNode node, Dictionary<IrNode, string> names, int depth, int minPrec)
    {
        var (text, prec) = Expr(node, names, depth + 1);
        return prec < minPrec ? "(" + text + ")" : text;
    }

    private static (string, int) Binary(string op, int prec, IrNode[] a, Dictionary<IrNode, string> names, int depth) =>
        (Operand(a[0], names, depth, prec) + " " + op + " " + Operand(a[1], names, depth, prec + 1), prec);

    private static (string, int) Call(string name, IrNode node, Dictionary<IrNode, string> names, int depth)
    {
        var sb = new StringBuilder(name).Append('(');
        for (var i = 0; i < node.Args.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(Operand(node.Args[i], names, depth, 0));
        }

        return (sb.Append(')').ToString(), PrecPrimary);
    }

    private static (string, int) FormatConst(IrNode node)
    {
        var v = node.Value;
        if (node.Type.Kind == TypeKind.ListFloat)
        {
            return (FormatList((double[])node.Data!), PrecPrimary);
        }

        switch (v.Kind)
        {
            case StorageKind.Bool:
                return (v.AsBool() ? "true" : "false", PrecPrimary);
            case StorageKind.Int64:
            {
                var i = v.AsInt64();
                return (i.ToString(CultureInfo.InvariantCulture), i < 0 ? PrecUnary : PrecPrimary);
            }

            case StorageKind.Float64:
            {
                var d = v.AsFloat64();
                var text = FormatDouble(d);
                return (text, text.StartsWith('(') ? PrecPrimary : d < 0 || (d == 0 && double.IsNegative(d)) ? PrecUnary : PrecPrimary);
            }

            case StorageKind.String or StorageKind.Text:
                return (Quote(v.AsString()), PrecPrimary);
            case StorageKind.ListInt64:
                return (FormatList(v.AsInt64List()), PrecPrimary);
            case StorageKind.ListString:
                return (FormatList(v.AsStringList()), PrecPrimary);
            default:
                return (v.ToString(), PrecPrimary);
        }
    }

    public static string FormatDouble(double d)
    {
        if (double.IsNaN(d))
        {
            return "(0.0 / 0.0)";
        }

        if (double.IsInfinity(d))
        {
            return d > 0 ? "(1.0 / 0.0)" : "(-1.0 / 0.0)";
        }

        var text = d.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
    }

    public static string FormatList(long[] values)
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < values.Length; i++)
        {
            sb.Append(i > 0 ? ", " : string.Empty).Append(values[i].ToString(CultureInfo.InvariantCulture));
        }

        return sb.Append(']').ToString();
    }

    public static string FormatList(double[] values)
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < values.Length; i++)
        {
            sb.Append(i > 0 ? ", " : string.Empty).Append(FormatDouble(values[i]));
        }

        return sb.Append(']').ToString();
    }

    public static string FormatList(string[] values)
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < values.Length; i++)
        {
            sb.Append(i > 0 ? ", " : string.Empty).Append(Quote(values[i]));
        }

        return sb.Append(']').ToString();
    }

    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('\'');
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\'': sb.Append("\\'"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c))
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.Append('\'').ToString();
    }
}
