using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions.Ir;
using StrataRec.Expressions.Syntax;

namespace StrataRec.Expressions.Binding;

/// <summary>
/// Resolves names, checks types and nullability (design 7.4) and lowers the AST into typed IR. Sugar is expanded here
/// so that both backends only implement primitives: n-ary <c>min</c>/<c>max</c>, <c>clamp</c>, <c>logit</c>,
/// <c>soft_floor</c>, <c>linear_decay</c> and <c>x in l</c>. <c>let</c> bindings become shared IR nodes.
/// Built-ins lift every argument (design 7.4 rule 2), so an expansion must not reach a constant through a
/// conditional branch that skips an argument: <c>?:</c> only lifts its condition.
/// </summary>
/// <remarks>Errors are collected rather than thrown; an ill-typed subexpression gets the poison type
/// <see cref="TypeKind.Error"/>, which silently propagates so one mistake produces one diagnostic.</remarks>
internal sealed class Binder
{
    private readonly IExpressionScope _scope;
    private readonly CompileOptions _options;
    private readonly List<ExpressionDiagnostic> _diagnostics;
    private readonly List<(string Name, IrNode Node)> _lets = new();

    public Binder(IExpressionScope scope, CompileOptions options, List<ExpressionDiagnostic> diagnostics)
    {
        _scope = scope;
        _options = options;
        _diagnostics = diagnostics;
    }

    public IrNode Bind(AstNode node) => node switch
    {
        LiteralAst l => IrNode.Const(l.Value, LiteralType(l.Value), l.Start, l.Length),
        ParenAst p => Bind(p.Inner),
        ListAst l => BindList(l),
        NameAst n => BindName(n),
        UnaryAst u => BindUnary(u),
        BinaryAst b => BindBinary(b),
        ConditionalAst c => BindConditional(c),
        CallAst c => BindCall(c),
        LetAst l => BindLet(l),
        _ => throw new InvalidOperationException($"Unexpected AST node {node.GetType().Name}."),
    };

    private static ExprType LiteralType(in ScalarValue v) => v.Kind switch
    {
        StorageKind.Bool => ExprType.Bool,
        StorageKind.Int64 => ExprType.Int,
        StorageKind.Float64 => ExprType.Float,
        _ => ExprType.String,
    };

    // ----------------------------------------------------------------------------------------------------------
    // Names and literals

    private IrNode BindList(ListAst list)
    {
        if (list.Items.Length == 0)
        {
            return Fail(ExpressionErrorCodes.TypeMismatch, "An empty list literal has no element type", list, "remove the test or list at least one value");
        }

        var values = new ScalarValue[list.Items.Length];
        bool allInt = true, allNumeric = true, allString = true;
        for (var i = 0; i < values.Length; i++)
        {
            var item = list.Items[i];
            while (item is ParenAst p)
            {
                item = p.Inner;
            }

            if (item is not LiteralAst literal)
            {
                return Fail(ExpressionErrorCodes.TypeMismatch, "List literal elements must be constant literals", item,
                    "list literals are constant sets, e.g. ['a', 'b'] or [0.0, 0.5, 1.0]");
            }

            values[i] = literal.Value;
            allInt &= literal.Value.Kind == StorageKind.Int64;
            allNumeric &= literal.Value.Kind is StorageKind.Int64 or StorageKind.Float64;
            allString &= literal.Value.Kind == StorageKind.String;
        }

        if (allInt)
        {
            var ints = new long[values.Length];
            for (var i = 0; i < ints.Length; i++)
            {
                ints[i] = values[i].AsInt64();
            }

            return IrNode.Const(ScalarValue.FromInt64List(ints), new ExprType(TypeKind.ListInt, false), list.Start, list.Length);
        }

        if (allNumeric)
        {
            var floats = new double[values.Length];
            for (var i = 0; i < floats.Length; i++)
            {
                floats[i] = values[i].AsFloat64();
            }

            return IrNode.Const(default, new ExprType(TypeKind.ListFloat, false), list.Start, list.Length, floats);
        }

        if (allString)
        {
            var strings = new string[values.Length];
            for (var i = 0; i < strings.Length; i++)
            {
                strings[i] = values[i].AsString();
            }

            return IrNode.Const(ScalarValue.FromStringList(strings), new ExprType(TypeKind.ListString, false), list.Start, list.Length);
        }

        return Fail(ExpressionErrorCodes.TypeMismatch, "List literal elements must all be numbers or all be strings", list);
    }

    private IrNode BindName(NameAst n)
    {
        var name = n.Name;
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        var at = name.IndexOf('@', StringComparison.Ordinal);
        if (dot < 0 && at < 0)
        {
            if (TryGetLet(name, out var bound))
            {
                return bound;
            }

            if (Builtins.IsNamespace(name))
            {
                return Fail(ExpressionErrorCodes.UnknownName, $"'{name}' is a namespace, not a value", n,
                    $"write a full name such as '{name}.{(name == "col" ? "item.clicks_7d" : name == "ctx" ? "request.seed" : "w_click")}'");
            }

            if (Builtins.IsFunction(name))
            {
                return Fail(ExpressionErrorCodes.UnknownName, $"'{name}' is a function, not a value", n, $"call it, e.g. {name}(x)");
            }

            return Fail(ExpressionErrorCodes.UnknownName, $"Unknown name '{name}'", n, NameHint(name, true));
        }

        var rootEnd = dot < 0 ? at : at < 0 ? dot : Math.Min(dot, at);
        var root = name[..rootEnd];
        if (TryGetLet(root, out _))
        {
            return Fail(ExpressionErrorCodes.UnknownName, $"'{root}' is a let binding and has no field '{name[(rootEnd + 1)..]}'", n,
                "only col., ctx. and param. names are dotted paths");
        }

        if (!Builtins.IsNamespace(root))
        {
            return Fail(ExpressionErrorCodes.UnknownName, $"Unknown name '{name}': names start with 'col.', 'ctx.' or 'param.'", n, NameHint(name, false));
        }

        if (at >= 0 && root != "col")
        {
            return Fail(ExpressionErrorCodes.UnknownName, $"Version reference '{name[at..]}' is only valid on col.* names", n,
                "remove the '@node' suffix");
        }

        if (!_scope.TryResolve(name, out var symbol))
        {
            return Fail(ExpressionErrorCodes.UnknownName, $"Unknown name '{name}'", n, NameHint(name, false));
        }

        var type = ExprType.FromColumnType(symbol.Type);
        if (type.IsError)
        {
            return Fail(ExpressionErrorCodes.TypeMismatch, $"'{name}' has type {symbol.Type}, which expressions cannot read", n);
        }

        if (symbol.Kind == SymbolKind.Column && _options.RequestLevelOnly)
        {
            Report(ExpressionErrorCodes.RowReferenceInRequestLevel,
                $"'{name}' is a candidate column, but this is a request-level expression evaluated once per request",
                n.Start, n.Length,
                "request-level sites (when, funnel keys, context features) can only read ctx.* and param.*");
        }

        return IrNode.Read(symbol, type, n.Start, n.Length);
    }

    private string? NameHint(string name, bool includeLets)
    {
        IEnumerable<string> candidates = _scope.KnownNames;
        if (includeLets && _lets.Count > 0)
        {
            var all = new List<string>(_lets.Count);
            foreach (var (letName, _) in _lets)
            {
                all.Add(letName);
            }

            all.AddRange(_scope.KnownNames);
            candidates = all;
        }

        return NameSuggester.Hint(name, candidates);
    }

    private bool TryGetLet(string name, out IrNode node)
    {
        for (var i = _lets.Count - 1; i >= 0; i--)
        {
            if (_lets[i].Name == name)
            {
                node = _lets[i].Node;
                return true;
            }
        }

        node = null!;
        return false;
    }

    private IrNode BindLet(LetAst let)
    {
        var pushed = 0;
        foreach (var binding in let.Bindings)
        {
            var name = binding.Name;
            string? problem = null;
            if (Builtins.IsNamespace(name))
            {
                problem = $"'{name}' is a namespace and cannot be a let name";
            }
            else if (Builtins.IsReserved(name))
            {
                problem = $"'{name}' is a reserved word and cannot be a let name";
            }
            else if (Builtins.IsFunction(name))
            {
                problem = $"'{name}' is a built-in function and cannot be a let name";
            }
            else if (TryGetLet(name, out _))
            {
                problem = $"'{name}' is already bound; let names cannot be redefined or shadowed";
            }

            if (problem is not null)
            {
                Report(ExpressionErrorCodes.InvalidLet, problem, binding.NameStart, name.Length, "choose another name, e.g. '" + name + "_value'");
            }

            var value = Bind(binding.Value);
            if (problem is null)
            {
                _lets.Add((name, value.WithNameHint(name)));
                pushed++;
            }
        }

        var body = Bind(let.Body);
        _lets.RemoveRange(_lets.Count - pushed, pushed);
        return body;
    }

    // ----------------------------------------------------------------------------------------------------------
    // Operators

    private IrNode BindUnary(UnaryAst u)
    {
        var operand = Bind(u.Operand);
        if (operand.Type.IsError)
        {
            return Poison(u);
        }

        if (u.Op == UnaryOp.Neg)
        {
            return operand.Type.IsNumeric
                ? IrNode.Make(IrOp.Neg, operand.Type, new[] { operand }, u.Start, u.Length)
                : Fail(ExpressionErrorCodes.TypeMismatch, $"Unary '-' needs a number, got {operand.Type}", u);
        }

        return operand.Type.Kind == TypeKind.Bool
            ? IrNode.Make(IrOp.Not, operand.Type, new[] { operand }, u.Start, u.Length)
            : Fail(ExpressionErrorCodes.TypeMismatch, $"'!' needs a bool, got {operand.Type}", u,
                operand.Type.IsNumeric ? "compare explicitly, e.g. '!(x > 0.0)' or 'x == 0'" : null);
    }

    private IrNode BindBinary(BinaryAst b)
    {
        var l = Bind(b.Left);
        var r = Bind(b.Right);
        if (l.Type.IsError || r.Type.IsError)
        {
            return Poison(b);
        }

        var op = BinaryAst.Spelling(b.Op);
        switch (b.Op)
        {
            case BinaryOp.Coalesce:
            {
                if (!TryUnify(l.Type, r.Type, out var kind, out var dim))
                {
                    return Fail(ExpressionErrorCodes.TypeMismatch, $"'??' needs both sides of the same type, got {l.Type} and {r.Type}", b);
                }

                return IrNode.Make(IrOp.Coalesce, new ExprType(kind, r.Type.Nullable, dim), new[] { Coerce(l, kind), Coerce(r, kind) }, b.Start, b.Length);
            }

            case BinaryOp.In:
                return BindContains(r, l, b, isIn: true);

            case BinaryOp.And:
            case BinaryOp.Or:
                if (l.Type.Kind != TypeKind.Bool || r.Type.Kind != TypeKind.Bool)
                {
                    return Fail(ExpressionErrorCodes.TypeMismatch, $"'{op}' needs bool operands, got {l.Type} and {r.Type}", b,
                        "compare numbers explicitly, e.g. 'x > 0.0'");
                }

                return Lift(b.Op == BinaryOp.And ? IrOp.And : IrOp.Or, TypeKind.Bool, b, l, r);

            case BinaryOp.Add:
            case BinaryOp.Sub:
            case BinaryOp.Mul:
            {
                if (!l.Type.IsNumeric || !r.Type.IsNumeric)
                {
                    return ArithmeticMismatch(op, l, r, b);
                }

                var kind = l.Type.Kind == TypeKind.Int && r.Type.Kind == TypeKind.Int ? TypeKind.Int : TypeKind.Float;
                var irOp = b.Op switch
                {
                    BinaryOp.Add => IrOp.Add,
                    BinaryOp.Sub => IrOp.Sub,
                    _ => IrOp.Mul,
                };
                return Lift(irOp, kind, b, Coerce(l, kind), Coerce(r, kind));
            }

            case BinaryOp.Div:
                if (!l.Type.IsNumeric || !r.Type.IsNumeric)
                {
                    return ArithmeticMismatch(op, l, r, b);
                }

                return Lift(IrOp.Div, TypeKind.Float, b, ToFloat(l), ToFloat(r));

            case BinaryOp.Mod:
                if (l.Type.Kind != TypeKind.Int || r.Type.Kind != TypeKind.Int)
                {
                    return Fail(ExpressionErrorCodes.TypeMismatch, $"'%' needs int64 operands, got {l.Type} and {r.Type}", b,
                        "'%' is integer remainder only (design 7.5)");
                }

                return Lift(IrOp.Mod, TypeKind.Int, b, l, r);

            default:
                return BindComparison(b, l, r, op);
        }
    }

    private IrNode ArithmeticMismatch(string op, IrNode l, IrNode r, AstNode at)
    {
        string? hint = null;
        if (l.Type.Kind == TypeKind.Bool || r.Type.Kind == TypeKind.Bool)
        {
            hint = "bool is not a number; write 'cond ? 1.0 : 0.0' (design 7.4)";
        }
        else if (op == "+" && (l.Type.Kind is TypeKind.String or TypeKind.Text || r.Type.Kind is TypeKind.String or TypeKind.Text))
        {
            hint = "expressions do not build strings; use a Mustache template in the operator";
        }

        return Fail(ExpressionErrorCodes.TypeMismatch, $"'{op}' needs numbers, got {l.Type} and {r.Type}", at, hint);
    }

    private IrNode BindComparison(BinaryAst b, IrNode l, IrNode r, string op)
    {
        var irOp = b.Op switch
        {
            BinaryOp.Eq => IrOp.Eq,
            BinaryOp.Ne => IrOp.Ne,
            BinaryOp.Lt => IrOp.Lt,
            BinaryOp.Le => IrOp.Le,
            BinaryOp.Gt => IrOp.Gt,
            _ => IrOp.Ge,
        };
        var equality = irOp is IrOp.Eq or IrOp.Ne;
        if (l.Type.IsNumeric && r.Type.IsNumeric)
        {
            var kind = l.Type.Kind == TypeKind.Int && r.Type.Kind == TypeKind.Int ? TypeKind.Int : TypeKind.Float;
            return Lift(irOp, TypeKind.Bool, b, Coerce(l, kind), Coerce(r, kind));
        }

        if (equality && l.Type.Kind == r.Type.Kind && l.Type.Kind is TypeKind.Bool or TypeKind.String)
        {
            return Lift(irOp, TypeKind.Bool, b, l, r);
        }

        string? hint = null;
        if (l.Type.Kind == TypeKind.String && r.Type.Kind == TypeKind.String)
        {
            hint = "strings support ==, !=, 'in' and startsWith() only";
        }
        else if (l.Type.Kind == TypeKind.Text || r.Type.Kind == TypeKind.Text)
        {
            hint = "text supports size() and contains() only";
        }

        return Fail(ExpressionErrorCodes.TypeMismatch, $"'{op}' is not defined for {l.Type} and {r.Type}", b, hint);
    }

    private IrNode BindConditional(ConditionalAst c)
    {
        var cond = Bind(c.Condition);
        var a = Bind(c.Then);
        var b = Bind(c.Else);
        if (cond.Type.IsError || a.Type.IsError || b.Type.IsError)
        {
            return Poison(c);
        }

        if (cond.Type.Kind != TypeKind.Bool)
        {
            return Fail(ExpressionErrorCodes.TypeMismatch, $"The condition of '?:' must be bool, got {cond.Type}", c.Condition,
                cond.Type.IsNumeric ? "compare explicitly, e.g. 'x > 0.0 ? a : b'" : null);
        }

        if (!TryUnify(a.Type, b.Type, out var kind, out var dim))
        {
            return Fail(ExpressionErrorCodes.TypeMismatch, $"The branches of '?:' have different types: {a.Type} and {b.Type}", c);
        }

        var type = new ExprType(kind, cond.Type.Nullable || a.Type.Nullable || b.Type.Nullable, dim);
        return IrNode.Make(IrOp.Conditional, type, new[] { cond, Coerce(a, kind), Coerce(b, kind) }, c.Start, c.Length);
    }

    // ----------------------------------------------------------------------------------------------------------
    // Functions

    private IrNode BindCall(CallAst call)
    {
        var name = call.Name;
        if (!Builtins.TryGetArity(name, out var min, out var max, out var usage))
        {
            // Bind arguments for their own errors, but not a dotted receiver: 'col.item.foo(1)' would only cascade.
            for (var i = call.IsMethod && call.Args[0] is NameAst ? 1 : 0; i < call.Args.Length; i++)
            {
                Bind(call.Args[i]);
            }

            Report(ExpressionErrorCodes.UnknownFunction, $"Unknown function '{name}'", call.NameStart, name.Length,
                NameSuggester.Hint(name, Builtins.FunctionNames) ?? "see the built-in functions of design 7.6");
            return Poison(call);
        }

        var count = call.Args.Length;
        if (count < min || (max >= 0 && count > max))
        {
            var expected = max < 0 ? $"at least {min}" : min == max ? $"{min}" : $"{min} to {max}";
            Report(ExpressionErrorCodes.ArgCount, $"'{name}' takes {expected} argument(s) but got {count}", call.Start, call.Length, "usage: " + usage);
            return Poison(call);
        }

        switch (name)
        {
            case "lookup":
                return BindLookup(call);
            case "piecewise_linear":
                return BindPiecewise(call);
        }

        var args = new IrNode[count];
        var failed = false;
        for (var i = 0; i < count; i++)
        {
            args[i] = Bind(call.Args[i]);
            failed |= args[i].Type.IsError;
        }

        if (failed)
        {
            return Poison(call);
        }

        switch (name)
        {
            case "has":
                return IrNode.Make(IrOp.Has, ExprType.Bool, args, call.Start, call.Length);

            case "min":
            case "max":
            {
                if (!AllNumeric(args, call, name, out var kind))
                {
                    return Poison(call);
                }

                var acc = Coerce(args[0], kind);
                for (var i = 1; i < args.Length; i++)
                {
                    acc = Lift(name == "min" ? IrOp.Min : IrOp.Max, kind, call, acc, Coerce(args[i], kind));
                }

                return acc;
            }

            case "clamp":
            {
                if (!AllNumeric(args, call, name, out var kind))
                {
                    return Poison(call);
                }

                var lower = Lift(IrOp.Max, kind, call, Coerce(args[0], kind), Coerce(args[1], kind));
                return Lift(IrOp.Min, kind, call, lower, Coerce(args[2], kind));
            }

            case "abs":
                return AllNumeric(args, call, name, out var absKind) ? Lift(IrOp.Abs, absKind, call, args[0]) : Poison(call);

            case "sqrt":
            case "exp":
            case "log":
            case "log1p":
            case "sigmoid":
            {
                if (!AllNumeric(args, call, name, out _))
                {
                    return Poison(call);
                }

                var op = name switch
                {
                    "sqrt" => IrOp.Sqrt,
                    "exp" => IrOp.Exp,
                    "log" => IrOp.Log,
                    "log1p" => IrOp.Log1p,
                    _ => IrOp.Sigmoid,
                };
                return Lift(op, TypeKind.Float, call, ToFloat(args[0]));
            }

            case "pow":
                return AllNumeric(args, call, name, out _) ? Lift(IrOp.Pow, TypeKind.Float, call, ToFloat(args[0]), ToFloat(args[1])) : Poison(call);

            case "logit":
            {
                // logit(p) = log(p / (1 - p))
                if (!AllNumeric(args, call, name, out _))
                {
                    return Poison(call);
                }

                var p = ToFloat(args[0]);
                var ratio = Lift(IrOp.Div, TypeKind.Float, call, p, Lift(IrOp.Sub, TypeKind.Float, call, Float(1.0), p));
                return Lift(IrOp.Log, TypeKind.Float, call, ratio);
            }

            case "soft_floor":
            {
                // floor + softplus(k * (x - floor)) / k with softplus(z) = max(z, 0) + log1p(exp(-|z|)) (stable form).
                if (!AllNumeric(args, call, name, out _))
                {
                    return Poison(call);
                }

                var x = ToFloat(args[0]);
                var floor = ToFloat(args[1]);
                var k = args.Length == 3 ? ToFloat(args[2]) : Float(1.0);
                var z = Lift(IrOp.Mul, TypeKind.Float, call, k, Lift(IrOp.Sub, TypeKind.Float, call, x, floor));
                var tail = Lift(IrOp.Log1p, TypeKind.Float, call,
                    Lift(IrOp.Exp, TypeKind.Float, call,
                        Lift(IrOp.Neg, TypeKind.Float, call, Lift(IrOp.Abs, TypeKind.Float, call, z))));
                var softplus = Lift(IrOp.Add, TypeKind.Float, call, Lift(IrOp.Max, TypeKind.Float, call, z, Float(0.0)), tail);
                return Lift(IrOp.Add, TypeKind.Float, call, floor, Lift(IrOp.Div, TypeKind.Float, call, softplus, k));
            }

            case "linear_decay":
            {
                // x <= start ? 1.0 : (x >= end ? 0.0 : (end - x) / (end - start)); end <= start gives a step at start.
                // That form returns 1.0 without looking at end, so a missing end would not make the result missing.
                // Lowered instead as x >= end ? (x <= start ? 1.0 : 0.0) : (x <= start ? 1.0 : fraction): the same
                // value for every present input, but every path tests both conditions, so the result is missing
                // whenever any argument is (design 7.4 rule 2).
                if (!AllNumeric(args, call, name, out _))
                {
                    return Poison(call);
                }

                var x = ToFloat(args[0]);
                var start = ToFloat(args[1]);
                var end = ToFloat(args[2]);
                var fraction = Lift(IrOp.Div, TypeKind.Float, call,
                    Lift(IrOp.Sub, TypeKind.Float, call, end, x),
                    Lift(IrOp.Sub, TypeKind.Float, call, end, start));
                var notStarted = Lift(IrOp.Le, TypeKind.Bool, call, x, start);
                var ended = Lift(IrOp.Ge, TypeKind.Bool, call, x, end);
                return Conditional(ended,
                    Conditional(notStarted, Float(1.0), Float(0.0), call),
                    Conditional(notStarted, Float(1.0), fraction, call),
                    call);
            }

            case "dot":
            case "cosine":
                if (!RequireKind(args[0], TypeKind.Vector, name, call.Args[0]) || !RequireKind(args[1], TypeKind.Vector, name, call.Args[1]))
                {
                    return Poison(call);
                }

                if (args[0].Type.Dim > 0 && args[1].Type.Dim > 0 && args[0].Type.Dim != args[1].Type.Dim)
                {
                    return Fail(ExpressionErrorCodes.TypeMismatch, $"'{name}' needs vectors of the same dimension, got {args[0].Type} and {args[1].Type}", call);
                }

                return Lift(name == "dot" ? IrOp.Dot : IrOp.Cosine, TypeKind.Float, call, args[0], args[1]);

            case "l2_norm":
                return RequireKind(args[0], TypeKind.Vector, name, call.Args[0]) ? Lift(IrOp.L2Norm, TypeKind.Float, call, args[0]) : Poison(call);

            case "size":
                if (args[0].Type.Kind is TypeKind.ListInt or TypeKind.ListString or TypeKind.Text)
                {
                    return Lift(IrOp.Size, TypeKind.Int, call, args[0]);
                }

                return Fail(ExpressionErrorCodes.TypeMismatch, $"'size' needs a list or text, got {args[0].Type}", call.Args[0],
                    args[0].Type.Kind == TypeKind.String ? "strings support ==, !=, 'in' and startsWith() only" : null);

            case "contains":
                return BindContains(args[0], args[1], call, isIn: false);

            case "overlap_count":
                if (args[0].Type.Kind == args[1].Type.Kind && args[0].Type.IsList)
                {
                    return Lift(IrOp.Overlap, TypeKind.Int, call, args[0], args[1]);
                }

                return Fail(ExpressionErrorCodes.TypeMismatch, $"'overlap_count' needs two lists of the same element type, got {args[0].Type} and {args[1].Type}", call);

            case "startsWith":
                if (args[0].Type.Kind == TypeKind.String && args[1].Type.Kind == TypeKind.String)
                {
                    return Lift(IrOp.StartsWith, TypeKind.Bool, call, args[0], args[1]);
                }

                return Fail(ExpressionErrorCodes.TypeMismatch, $"'startsWith' needs two strings, got {args[0].Type} and {args[1].Type}", call);

            case "hash01":
                for (var i = 0; i < args.Length; i++)
                {
                    if (args[i].Type.Kind is not (TypeKind.Int or TypeKind.Float or TypeKind.Bool or TypeKind.String))
                    {
                        return Fail(ExpressionErrorCodes.TypeMismatch, $"'hash01' arguments must be int64, float64, bool or string, got {args[i].Type}", call.Args[i]);
                    }
                }

                return Lift(IrOp.Hash01, TypeKind.Float, call, args);

            default:
                throw new InvalidOperationException($"Built-in '{name}' has no binder.");
        }
    }

    private IrNode BindContains(IrNode list, IrNode x, AstNode at, bool isIn)
    {
        var what = isIn ? "'in'" : "'contains'";
        if (list.Type.IsError || x.Type.IsError)
        {
            return Poison(at);
        }

        if (list.IsConst && list.Type.Kind is TypeKind.ListInt or TypeKind.ListFloat or TypeKind.ListString)
        {
            ConstSet? set = null;
            var element = x;
            if (list.Type.Kind == TypeKind.ListString && x.Type.Kind == TypeKind.String)
            {
                set = ConstSet.OfStrings(list.Value.AsStringList());
            }
            else if (list.Type.Kind == TypeKind.ListInt && x.Type.Kind == TypeKind.Int)
            {
                set = ConstSet.OfInts(list.Value.AsInt64List());
            }
            else if (list.Type.Kind is TypeKind.ListInt or TypeKind.ListFloat && x.Type.IsNumeric)
            {
                set = ConstSet.OfFloats(list.Type.Kind == TypeKind.ListFloat ? (double[])list.Data! : ToDoubles(list.Value.AsInt64List()));
                element = ToFloat(x);
            }

            if (set is not null)
            {
                return IrNode.Make(IrOp.InSet, new ExprType(TypeKind.Bool, element.Type.Nullable), new[] { element }, at.Start, at.Length, set);
            }
        }
        else if ((list.Type.Kind == TypeKind.ListInt && x.Type.Kind == TypeKind.Int)
                 || (list.Type.Kind == TypeKind.ListString && x.Type.Kind == TypeKind.String)
                 || (!isIn && list.Type.Kind == TypeKind.Text && x.Type.Kind is TypeKind.String or TypeKind.Text))
        {
            return Lift(IrOp.Contains, TypeKind.Bool, at, list, x);
        }

        var hint = list.Type.Kind == TypeKind.Text && isIn ? "use contains(text, s) for substring tests" : null;
        if (list.Type.Kind == TypeKind.ListInt && x.Type.Kind == TypeKind.Float)
        {
            hint = "the list holds int64 values; compare with an int64";
        }

        return Fail(ExpressionErrorCodes.TypeMismatch,
            isIn ? $"{what} needs a value and a list of the same element type, got {x.Type} in {list.Type}"
                 : $"{what} needs (list, element) or (text, string), got {list.Type} and {x.Type}",
            at, hint);
    }

    private IrNode BindLookup(CallAst call)
    {
        var nameArg = call.Args[0];
        while (nameArg is ParenAst p)
        {
            nameArg = p.Inner;
        }

        var key = Bind(call.Args[1]);
        var fallback = Bind(call.Args[2]);
        if (nameArg is not LiteralAst { Value.Kind: StorageKind.String } literal)
        {
            return Fail(ExpressionErrorCodes.TypeMismatch, "The table name of 'lookup' must be a string literal", call.Args[0],
                "write lookup('table_name', key, default)");
        }

        var tableName = literal.Value.AsString();
        if (!_scope.TryGetLookupTable(tableName, out var table))
        {
            return Fail(ExpressionErrorCodes.UnknownTable, $"Unknown lookup table '{tableName}'", nameArg, "declare the table as a DataTableArtifact of this plan");
        }

        if (key.Type.IsError || fallback.Type.IsError)
        {
            return Poison(call);
        }

        var keyType = ExprType.FromColumnType(table.KeyType);
        if (key.Type.Kind != keyType.Kind)
        {
            return Fail(ExpressionErrorCodes.TypeMismatch, $"Table '{tableName}' has {keyType.WithNullable(false)} keys, got {key.Type}", call.Args[1]);
        }

        var valueType = ExprType.FromColumnType(table.ValueType).WithNullable(false);
        if (fallback.Type.Nullable)
        {
            return Fail(ExpressionErrorCodes.TypeMismatch, "The default of 'lookup' must not be nullable", call.Args[2],
                $"write '... ?? {DefaultLiteral(valueType)}' for the default");
        }

        if (!(fallback.Type.Kind == valueType.Kind || (valueType.Kind == TypeKind.Float && fallback.Type.Kind == TypeKind.Int)))
        {
            return Fail(ExpressionErrorCodes.TypeMismatch, $"Table '{tableName}' has {valueType} values but the default is {fallback.Type}", call.Args[2]);
        }

        return IrNode.Make(IrOp.Lookup, valueType, new[] { key, Coerce(fallback, valueType.Kind) }, call.Start, call.Length, table);
    }

    private IrNode BindPiecewise(CallAst call)
    {
        var x = Bind(call.Args[0]);
        var xs = Bind(call.Args[1]);
        var ys = Bind(call.Args[2]);
        if (x.Type.IsError || xs.Type.IsError || ys.Type.IsError)
        {
            return Poison(call);
        }

        if (!x.Type.IsNumeric)
        {
            return Fail(ExpressionErrorCodes.TypeMismatch, $"'piecewise_linear' needs a number, got {x.Type}", call.Args[0]);
        }

        var xv = KnotValues(xs, call.Args[1]);
        var yv = KnotValues(ys, call.Args[2]);
        if (xv is null || yv is null)
        {
            return Poison(call);
        }

        if (xv.Length != yv.Length)
        {
            return Fail(ExpressionErrorCodes.InvalidArgument, $"'piecewise_linear' has {xv.Length} x knots but {yv.Length} y values", call.Args[2],
                "list one y value per x knot");
        }

        if (xv.Length < 2)
        {
            return Fail(ExpressionErrorCodes.InvalidArgument, "'piecewise_linear' needs at least 2 knots", call.Args[1]);
        }

        for (var i = 1; i < xv.Length; i++)
        {
            if (!(xv[i] > xv[i - 1]))
            {
                return Fail(ExpressionErrorCodes.InvalidArgument,
                    $"'piecewise_linear' x knots must be strictly ascending, but {Format(xv[i])} follows {Format(xv[i - 1])}", call.Args[1]);
            }
        }

        return LiftWithData(IrOp.PiecewiseLinear, TypeKind.Float, call, new Knots(xv, yv), ToFloat(x));

        static string Format(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }

    private double[]? KnotValues(IrNode list, AstNode at)
    {
        if (list.IsConst && list.Type.Kind == TypeKind.ListFloat)
        {
            return (double[])list.Data!;
        }

        if (list.IsConst && list.Type.Kind == TypeKind.ListInt)
        {
            return ToDoubles(list.Value.AsInt64List());
        }

        Fail(ExpressionErrorCodes.TypeMismatch, "'piecewise_linear' knots must be constant number lists", at, "write e.g. piecewise_linear(x, [0.0, 1.0], [0.0, 2.0])");
        return null;
    }

    // ----------------------------------------------------------------------------------------------------------
    // Result checks (design 7.4 rule 4)

    /// <summary>Checks the bound result against the site's requirements.</summary>
    public void CheckResult(IrNode root, AstNode ast)
    {
        if (root.Type.IsError)
        {
            return;
        }

        if (root.Type.Kind == TypeKind.ListFloat)
        {
            Report(ExpressionErrorCodes.TypeMismatch, "A float list literal cannot be an expression result", ast.Start, ast.Length,
                "float lists are only used with 'in' and piecewise_linear");
            return;
        }

        if (_options.ExpectedType is { } expected && !Accepts(expected, root.Type))
        {
            string? hint = null;
            if (expected.Kind == StorageKind.Bool && root.Type.IsNumeric)
            {
                hint = "write a comparison, e.g. 'x > 0.0'";
            }
            else if (expected.IsNumeric && root.Type.Kind == TypeKind.Bool)
            {
                hint = "write 'cond ? 1.0 : 0.0'";
            }
            else if (expected.IsInteger && root.Type.Kind == TypeKind.Float)
            {
                hint = "float results cannot be written to integer columns";
            }

            Report(ExpressionErrorCodes.TypeMismatch, $"The expression has type {root.Type.WithNullable(false)} but this site expects {expected.AsNonNullable()}",
                ast.Start, ast.Length, hint);
            return;
        }

        if (root.Type.Nullable && !_options.AllowNullableResult)
        {
            ReportNullableResult(root, ast);
        }
    }

    private static bool Accepts(ColumnType expected, ExprType actual) => expected.Kind switch
    {
        StorageKind.Float32 or StorageKind.Float64 => actual.IsNumeric,
        StorageKind.Int32 or StorageKind.Int64 => actual.Kind == TypeKind.Int,
        StorageKind.Bool => actual.Kind == TypeKind.Bool,
        StorageKind.String => actual.Kind == TypeKind.String,
        StorageKind.Text => actual.Kind == TypeKind.Text,
        StorageKind.Vector => actual.Kind == TypeKind.Vector && (expected.Dimension == 0 || actual.Dim == 0 || expected.Dimension == actual.Dim),
        StorageKind.ListInt64 => actual.Kind == TypeKind.ListInt,
        StorageKind.ListString => actual.Kind == TypeKind.ListString,
        _ => false,
    };

    private void ReportNullableResult(IrNode root, AstNode ast)
    {
        var sources = new List<IrNode>();
        CollectNullableSources(root, sources, new HashSet<IrNode>(ReferenceEqualityComparer.Instance));
        var fallback = DefaultLiteral(root.Type);
        string message;
        string hint;
        int offset = ast.Start, length = ast.Length;
        if (sources.Count > 0)
        {
            var names = new List<string>();
            foreach (var s in sources)
            {
                if (!names.Contains("'" + s.Symbol.Name + "'"))
                {
                    names.Add("'" + s.Symbol.Name + "'");
                }
            }

            var first = sources[0];
            var firstFallback = DefaultLiteral(first.Type);
            message = $"The result has type {root.Type} and is missing whenever {string.Join(" or ", names)} is missing, but this site requires a value";
            hint = firstFallback is null
                ? $"append ' ?? {fallback ?? "<default>"}' to the expression, or test has({first.Symbol.Name})"
                : $"write '({first.Symbol.Name} ?? {firstFallback})' at offset {first.Start}, or append ' ?? {fallback ?? "<default>"}' to the expression";
            offset = first.Start;
            length = first.Length;
        }
        else
        {
            message = $"The result has type {root.Type} and may be missing, but this site requires a value";
            hint = $"append ' ?? {fallback ?? "<default>"}' to the expression";
        }

        Report(ExpressionErrorCodes.NullableResult, message, offset, Math.Max(1, length), hint);
    }

    /// <summary>Finds the nullable symbols that make <paramref name="node"/> nullable (in source order).</summary>
    private static void CollectNullableSources(IrNode node, List<IrNode> sources, HashSet<IrNode> visited)
    {
        if (!node.Type.Nullable || !visited.Add(node))
        {
            return;
        }

        switch (node.Op)
        {
            case IrOp.Column or IrOp.Context or IrOp.Param:
                sources.Add(node);
                return;
            case IrOp.Coalesce:
                CollectNullableSources(node.Args[1], sources, visited);
                return;
            default:
                foreach (var a in node.Args)
                {
                    CollectNullableSources(a, sources, visited);
                }

                return;
        }
    }

    private static string? DefaultLiteral(ExprType type) => type.Kind switch
    {
        TypeKind.Float => "0.0",
        TypeKind.Int => "0",
        TypeKind.Bool => "false",
        TypeKind.String or TypeKind.Text => "''",
        _ => null,
    };

    // ----------------------------------------------------------------------------------------------------------
    // Helpers

    private static bool TryUnify(ExprType a, ExprType b, out TypeKind kind, out int dim)
    {
        dim = 0;
        kind = TypeKind.Error;
        if (a.Kind == b.Kind && a.Kind != TypeKind.ListFloat)
        {
            if (a.Kind == TypeKind.Vector)
            {
                if (a.Dim > 0 && b.Dim > 0 && a.Dim != b.Dim)
                {
                    return false;
                }

                dim = Math.Max(a.Dim, b.Dim);
            }

            kind = a.Kind;
            return true;
        }

        if (a.IsNumeric && b.IsNumeric)
        {
            kind = TypeKind.Float;
            return true;
        }

        return false;
    }

    private bool AllNumeric(IrNode[] args, CallAst call, string name, out TypeKind kind)
    {
        kind = TypeKind.Int;
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].Type.IsNumeric)
            {
                Fail(ExpressionErrorCodes.TypeMismatch, $"'{name}' needs numbers, got {args[i].Type}", call.Args[i],
                    args[i].Type.Kind == TypeKind.Bool ? "bool is not a number; write 'cond ? 1.0 : 0.0'" : null);
                return false;
            }

            if (args[i].Type.Kind == TypeKind.Float)
            {
                kind = TypeKind.Float;
            }
        }

        return true;
    }

    private bool RequireKind(IrNode arg, TypeKind kind, string name, AstNode at)
    {
        if (arg.Type.Kind == kind)
        {
            return true;
        }

        Fail(ExpressionErrorCodes.TypeMismatch, $"'{name}' needs a vector, got {arg.Type}", at);
        return false;
    }

    private static double[] ToDoubles(long[] values)
    {
        var result = new double[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            result[i] = values[i];
        }

        return result;
    }

    private static IrNode Float(double value) => IrNode.Const(ScalarValue.FromFloat64(value), ExprType.Float);

    private static IrNode ToFloat(IrNode node) =>
        node.Type.Kind == TypeKind.Int
            ? IrNode.Make(IrOp.ToFloat, new ExprType(TypeKind.Float, node.Type.Nullable), new[] { node }, node.Start, node.Length)
            : node;

    private static IrNode Coerce(IrNode node, TypeKind kind) => kind == TypeKind.Float ? ToFloat(node) : node;

    private static IrNode Lift(IrOp op, TypeKind kind, AstNode at, params IrNode[] args) => LiftWithData(op, kind, at, null, args);

    private static IrNode LiftWithData(IrOp op, TypeKind kind, AstNode at, object? data, params IrNode[] args)
    {
        var nullable = false;
        foreach (var a in args)
        {
            nullable |= a.Type.Nullable;
        }

        return IrNode.Make(op, new ExprType(kind, nullable), args, at.Start, at.Length, data);
    }

    private static IrNode Conditional(IrNode cond, IrNode a, IrNode b, AstNode at)
    {
        var type = new ExprType(a.Type.Kind, cond.Type.Nullable || a.Type.Nullable || b.Type.Nullable, a.Type.Dim);
        return IrNode.Make(IrOp.Conditional, type, new[] { cond, a, b }, at.Start, at.Length);
    }

    private void Report(string code, string message, int offset, int length, string? hint = null) =>
        _diagnostics.Add(new ExpressionDiagnostic(code, message.EndsWith('.') ? message : message + ".", offset, Math.Max(1, length), hint));

    private IrNode Fail(string code, string message, AstNode at, string? hint = null)
    {
        Report(code, message, at.Start, at.Length, hint);
        return Poison(at);
    }

    private static IrNode Poison(AstNode at) => IrNode.Const(default, ExprType.Error, at.Start, at.Length);
}
