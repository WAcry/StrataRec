using StrataRec.Core.Columns;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions.Ir;
using StrataRec.Expressions.Syntax;

namespace StrataRec.Expressions.Binding;

/// <summary>
/// Built-ins lift every argument, so an expansion must not reach a constant through a conditional branch that skips an
/// argument: <c>?:</c> only lifts its condition.
/// </summary>
internal sealed class Binder
{
    private readonly IExpressionScope _scope;
    private readonly CompileOptions _options;
    private readonly List<ExpressionDiagnostic> _diagnostics;
    private readonly List<(string Name, IrNode Node)> _lets = new();

    private const string StringOperations = "strings support ==, !=, 'in', size(), contains(), startsWith() and endsWith()";

    public bool ReadsUnusable { get; private set; }

    /// <summary>Every name resolved, in the order first seen: what the scope must declare (<see cref="CompiledExpression.Names"/>).</summary>
    public List<Symbol> Names { get; } = new();

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

    private IrNode BindList(ListAst list)
    {
        if (list.Items.Length == 0)
        {
            return Fail(ExpressionErrorCodes.TypeMismatch, "An empty list literal has no element type", list, "remove the test or list at least one value");
        }

        var values = new ScalarValue[list.Items.Length];
        bool allInt = true, allNumeric = true, allString = true, allBool = true;
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
                    "list literals are constants, e.g. ['a', 'b'] or [0.0, 0.5, 1.0]");
            }

            values[i] = literal.Value;
            allInt &= literal.Value.Kind == StorageKind.Int64;
            allNumeric &= literal.Value.Kind is StorageKind.Int64 or StorageKind.Float64;
            allString &= literal.Value.Kind == StorageKind.String;
            allBool &= literal.Value.Kind == StorageKind.Bool;
        }

        ScalarValue value;
        TypeKind kind;
        if (allInt)
        {
            var ints = new long[values.Length];
            for (var i = 0; i < ints.Length; i++)
            {
                ints[i] = values[i].AsInt64();
            }

            (value, kind) = (ScalarValue.FromInt64List(ints), TypeKind.ListInt);
        }
        else if (allNumeric)
        {
            var floats = new double[values.Length];
            for (var i = 0; i < floats.Length; i++)
            {
                floats[i] = values[i].AsFloat64();
            }

            (value, kind) = (ScalarValue.FromFloat64List(floats), TypeKind.ListFloat);
        }
        else if (allString)
        {
            var strings = new string[values.Length];
            for (var i = 0; i < strings.Length; i++)
            {
                strings[i] = values[i].AsString();
            }

            (value, kind) = (ScalarValue.FromStringList(strings), TypeKind.ListString);
        }
        else if (allBool)
        {
            var bools = new bool[values.Length];
            for (var i = 0; i < bools.Length; i++)
            {
                bools[i] = values[i].AsBool();
            }

            (value, kind) = (ScalarValue.FromBoolList(bools), TypeKind.ListBool);
        }
        else
        {
            AstNode odd = list.Items[0];
            for (var i = 1; i < values.Length; i++)
            {
                if (Category(values[i]) != Category(values[0]))
                {
                    odd = list.Items[i];
                    break;
                }
            }

            return FailAt(odd, ExpressionErrorCodes.TypeMismatch, "List literal elements must all be numbers, all strings or all bools", list);
        }

        // A literal's length is known, as a list<E,N>'s is: dot(col.embedding, [1, 0, 0]) with another N is a compile
        // error, and so is a literal of another length written for a fixed-length site.
        return IrNode.Const(value, new ExprType(kind, false, values.Length), list.Start, list.Length);

        static int Category(in ScalarValue v) => v.Kind switch
        {
            StorageKind.String => 0,
            StorageKind.Int64 or StorageKind.Float64 => 1,
            StorageKind.Bool => 2,
            _ => 3,
        };
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
                    $"write a full name such as '{name}.{name switch { "col" => "item.clicks_7d", "ctx" => "request.seed", "req" => "candidates.count", _ => "w_click" }}'");
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
                "only col., ctx., req. and param. names are dotted paths");
        }

        if (!Builtins.IsNamespace(root))
        {
            return Fail(ExpressionErrorCodes.UnknownName, $"Unknown name '{name}': names start with 'col.', 'ctx.', 'req.' or 'param.'", n, NameHint(name, false));
        }

        if (at >= 0 && root != "col")
        {
            return Fail(ExpressionErrorCodes.UnknownName, $"Version reference '{name[at..]}' is only valid on col.* names", n,
                "remove the '@node' suffix");
        }

        if (!_scope.TryResolve(name, out var symbol))
        {
            if (_scope.IsUnusable(name))
            {
                ReadsUnusable = true;
                return Poison(n);
            }

            return Fail(ExpressionErrorCodes.UnknownName, $"Unknown name '{name}'", n, NamespaceHint(name) ?? NameHint(name, false) ?? DeclareHint(name));
        }

        if (!Names.Contains(symbol))
        {
            Names.Add(symbol);
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
                "request-level sites (when, funnel keys, context features, recall queries, a tap's context, the output's values) read ctx.*, req.* and param.*");
        }

        return IrNode.Read(symbol, type, n.Start, n.Length);
    }

    /// <summary>
    /// The hint for a name written in the other request-level namespace. Only those lookups, which register nothing.
    /// </summary>
    private string? NamespaceHint(string name)
    {
        if (name.StartsWith("req.", StringComparison.Ordinal) && _scope.TryResolve("ctx.request." + name[4..], out _))
        {
            return $"'{name[4..]}' is a request input: read it as 'ctx.request.{name[4..]}' (req.* are request columns, which nodes write)";
        }

        var rest = name.StartsWith("ctx.request.", StringComparison.Ordinal) ? name[12..] : name.StartsWith("ctx.", StringComparison.Ordinal) ? name[4..] : null;
        return rest is not null && _scope.TryResolve("req." + rest, out _)
            ? $"'{rest}' is a request column, which nodes write: read it as 'req.{rest}'"
            : null;
    }

    private static string DeclareHint(string name)
    {
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        return (dot < 0 ? name : name[..dot]) switch
        {
            "col" => "nothing declares it: a col.* name is a declared column, a column an earlier node writes, or a candidate feature of the catalog",
            "ctx" => "nothing declares it: a ctx.* name is a request input (ctx.request.<input>) or a context feature of the catalog (ctx.<entity>.<feature>)",
            "req" => "nothing declares it: a req.* name is a declared request column, which an earlier node writes",
            "param" => "nothing declares it: a param.* name is a declared parameter",
            _ => "a name is col.<column>, ctx.<entity>.<feature>, ctx.request.<input>, req.<request column> or param.<parameter>",
        };
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
                : FailAt(u.Operand, ExpressionErrorCodes.TypeMismatch, $"Unary '-' needs a number, got {operand.Type}", u);
        }

        return operand.Type.Kind == TypeKind.Bool
            ? IrNode.Make(IrOp.Not, operand.Type, new[] { operand }, u.Start, u.Length)
            : FailAt(u.Operand, ExpressionErrorCodes.TypeMismatch, $"'!' needs a bool, got {operand.Type}", u,
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
                AdaptListLiterals(ref l, ref r);
                if (!TryUnify(l.Type, r.Type, out var kind, out var length))
                {
                    // The left side is the value read; the right one, its default, is what disagrees.
                    return FailAt(b.Right, ExpressionErrorCodes.TypeMismatch, $"'??' needs both sides of the same type, got {l.Type} and {r.Type}", b,
                        DefaultHint(l.Type));
                }

                return IrNode.Make(IrOp.Coalesce, new ExprType(kind, r.Type.Nullable, length), new[] { Coerce(l, kind), Coerce(r, kind) }, b.Start, b.Length);
            }

            case BinaryOp.In:
                return BindContains(r, b.Right, l, b.Left, b, isIn: true);

            case BinaryOp.And:
            case BinaryOp.Or:
                if (l.Type.Kind != TypeKind.Bool || r.Type.Kind != TypeKind.Bool)
                {
                    return FailAt(Culprit(b, l.Type.Kind != TypeKind.Bool, r.Type.Kind != TypeKind.Bool), ExpressionErrorCodes.TypeMismatch,
                        $"'{op}' needs bool operands, got {l.Type} and {r.Type}", b, "compare numbers explicitly, e.g. 'x > 0.0'");
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
                    return FailAt(Culprit(b, l.Type.Kind != TypeKind.Int, r.Type.Kind != TypeKind.Int), ExpressionErrorCodes.TypeMismatch,
                        $"'%' needs int64 operands, got {l.Type} and {r.Type}", b, "'%' is integer remainder only");
                }

                // x % 0 has no value: missing, so the result may be missing unless the divisor is a constant
                // that is not 0. A constant 0 is a mistake, reported here rather than leaving a column always missing.
                if (r.IsConst && !r.Value.IsMissing && r.Value.AsInt64() == 0)
                {
                    return FailAt(b.Right, ExpressionErrorCodes.InvalidArgument,
                        "'%' by 0 has no value", b, "use a non-zero divisor");
                }

                var mod = Lift(IrOp.Mod, TypeKind.Int, b, l, r);
                return r.IsConst ? mod : IrNode.Make(IrOp.Mod, new ExprType(TypeKind.Int, Nullable: true), mod.Args, b.Start, b.Length);

            default:
                return BindComparison(b, l, r, op);
        }
    }

    private IrNode ArithmeticMismatch(string op, IrNode l, IrNode r, BinaryAst at)
    {
        string? hint = null;
        if (l.Type.Kind == TypeKind.Bool || r.Type.Kind == TypeKind.Bool)
        {
            hint = "bool is not a number; write 'cond ? 1.0 : 0.0'";
        }
        else if (op == "+" && (l.Type.Kind == TypeKind.String || r.Type.Kind == TypeKind.String))
        {
            hint = "expressions do not build strings; use a Mustache template in the operator";
        }

        return FailAt(Culprit(at, !l.Type.IsNumeric, !r.Type.IsNumeric), ExpressionErrorCodes.TypeMismatch, $"'{op}' needs numbers, got {l.Type} and {r.Type}", at, hint);
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
            hint = StringOperations;
        }

        var culprit = equality
            ? Culprit(b, !IsEquatable(l.Type), !IsEquatable(r.Type))
            : Culprit(b, !l.Type.IsNumeric, !r.Type.IsNumeric);
        return FailAt(culprit, ExpressionErrorCodes.TypeMismatch, $"'{op}' is not defined for {l.Type} and {r.Type}", b, hint);
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

        AdaptListLiterals(ref a, ref b);
        if (!TryUnify(a.Type, b.Type, out var kind, out var length))
        {
            return FailAt(c.Else, ExpressionErrorCodes.TypeMismatch, $"The branches of '?:' have different types: {a.Type} and {b.Type}", c);
        }

        var type = new ExprType(kind, cond.Type.Nullable || a.Type.Nullable || b.Type.Nullable, length);
        return IrNode.Make(IrOp.Conditional, type, new[] { cond, Coerce(a, kind), Coerce(b, kind) }, c.Start, c.Length);
    }

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
                NameSuggester.Hint(name, Builtins.FunctionNames) ?? "the functions are: " + string.Join(", ", Builtins.FunctionNames));
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

            case "int":
                // A float64 that is not finite or whose truncation leaves the int64 range has no int64 value: the
                // result is missing, never a saturated or wrapped number.
                if (!AllNumeric(args, call, name, out _))
                {
                    return Poison(call);
                }

                return args[0].Type.Kind == TypeKind.Int
                    ? args[0]
                    : IrNode.Make(IrOp.ToInt, new ExprType(TypeKind.Int, true), args, call.Start, call.Length);

            case "double":
                return AllNumeric(args, call, name, out _) ? ToFloat(args[0]) : Poison(call);

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
                // Lowered so that every path tests both conditions, and the result is missing whenever any argument is:
                // the plain x <= start ? 1.0 : ... returns 1.0 without looking at end.
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
            {
                args[0] = AsFloatList(args[0]);
                args[1] = AsFloatList(args[1]);
                if (!RequireFloatList(args[0], name, call.Args[0]) || !RequireFloatList(args[1], name, call.Args[1]))
                {
                    return Poison(call);
                }

                int left = args[0].Type.Length, right = args[1].Type.Length;
                if (left > 0 && right > 0 && left != right)
                {
                    return FailAt(call.Args[1], ExpressionErrorCodes.TypeMismatch, $"'{name}' needs two lists of the same length, got {args[0].Type} and {args[1].Type}", call,
                        "it pairs the lists element by element: compare embeddings of the same model and dimension");
                }

                // Lists of different lengths have no dot product: a row where they differ is missing. Only when both
                // lengths are declared (and equal) can no row differ, and the result is as nullable as the lists are.
                var op = name == "dot" ? IrOp.Dot : IrOp.Cosine;
                var precision = IsFloat32List(args[0]) && IsFloat32List(args[1]) ? Float32Lists.Instance : null;
                var product = LiftWithData(op, TypeKind.Float, call, precision, args[0], args[1]);
                return left > 0 && left == right ? product : IrNode.Make(op, new ExprType(TypeKind.Float, true), product.Args, call.Start, call.Length, precision);
            }

            case "l2_norm":
                args[0] = AsFloatList(args[0]);
                return RequireFloatList(args[0], name, call.Args[0])
                    ? LiftWithData(IrOp.L2Norm, TypeKind.Float, call, IsFloat32List(args[0]) ? Float32Lists.Instance : null, args[0])
                    : Poison(call);

            case "size":
                if (args[0].Type.IsList || args[0].Type.Kind == TypeKind.String)
                {
                    return Lift(IrOp.Size, TypeKind.Int, call, args[0]);
                }

                return Fail(ExpressionErrorCodes.TypeMismatch, $"'size' needs a list or a string, got {args[0].Type}", call.Args[0]);

            case "contains":
                return BindContains(args[0], call.Args[0], args[1], call.Args[1], call, isIn: false);

            case "overlap_count":
                AdaptListLiterals(ref args[0], ref args[1]);
                if (args[0].Type.Kind == args[1].Type.Kind && args[0].Type.IsList)
                {
                    return Lift(IrOp.Overlap, TypeKind.Int, call, args[0], args[1]);
                }

                return FailAt(!args[0].Type.IsList ? call.Args[0] : call.Args[1], ExpressionErrorCodes.TypeMismatch,
                    $"'overlap_count' needs two lists of the same element type, got {args[0].Type} and {args[1].Type}", call);

            case "startsWith":
            case "endsWith":
                if (args[0].Type.Kind == TypeKind.String && args[1].Type.Kind == TypeKind.String)
                {
                    return Lift(name == "startsWith" ? IrOp.StartsWith : IrOp.EndsWith, TypeKind.Bool, call, args[0], args[1]);
                }

                return FailAt(args[0].Type.Kind != TypeKind.String ? call.Args[0] : call.Args[1], ExpressionErrorCodes.TypeMismatch,
                    $"'{name}' needs two strings, got {args[0].Type} and {args[1].Type}", call);

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

    private IrNode BindContains(IrNode list, AstNode listAt, IrNode x, AstNode xAt, AstNode at, bool isIn)
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
                set = ConstSet.OfFloats(list.Type.Kind == TypeKind.ListFloat ? list.Value.AsFloat64List().ToArray() : ToDoubles(list.Value.AsInt64List()));
                element = ToFloat(x);
            }

            if (set is not null)
            {
                return IrNode.Make(IrOp.InSet, new ExprType(TypeKind.Bool, element.Type.Nullable), new[] { element }, at.Start, at.Length, set);
            }
        }
        else if ((list.Type.IsList && x.Type.Kind == ExprType.ElementKind(list.Type.Kind))
                 || (!isIn && list.Type.Kind == TypeKind.String && x.Type.Kind == TypeKind.String))
        {
            return Lift(IrOp.Contains, TypeKind.Bool, at, list, x);
        }
        else if (list.Type.Kind == TypeKind.ListFloat && x.Type.Kind == TypeKind.Int)
        {
            return Lift(IrOp.Contains, TypeKind.Bool, at, list, ToFloat(x));
        }

        var hint = list.Type.Kind == TypeKind.String && isIn ? "'in' tests list membership; use contains(s, part) for a substring" : null;
        if (list.Type.Kind == TypeKind.ListInt && x.Type.Kind == TypeKind.Float)
        {
            hint = "the list holds int64 values; compare with an int64";
        }

        var container = list.Type.IsList || (!isIn && list.Type.Kind == TypeKind.String);
        return FailAt(container ? xAt : listAt, ExpressionErrorCodes.TypeMismatch,
            isIn ? $"{what} needs a value and a list of the same element type, got {x.Type} in {list.Type}"
                 : $"{what} needs (list, element) or (string, string), got {list.Type} and {x.Type}",
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
            return list.Value.AsFloat64List().ToArray();
        }

        if (list.IsConst && list.Type.Kind == TypeKind.ListInt)
        {
            return ToDoubles(list.Value.AsInt64List());
        }

        Fail(ExpressionErrorCodes.TypeMismatch, "'piecewise_linear' knots must be constant number lists", at, "write e.g. piecewise_linear(x, [0.0, 1.0], [0.0, 2.0])");
        return null;
    }

    public IrNode CheckResult(IrNode root, AstNode ast)
    {
        if (root.Type.IsError)
        {
            return root;
        }

        if (_options.ExpectedType is { IsList: true } list && list.ElementType.IsFloat)
        {
            root = AsFloatList(root);
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
            else if (expected.IsList && expected.ElementType.IsFloat && root.Type.Kind == TypeKind.ListInt)
            {
                hint = $"an int64 list is not converted to a float list; declare the site as {ColumnType.List(StorageKind.Int64, false, expected.FixedLength)}, or read a float list";
            }
            else if (expected.IsList && root.Type.IsList && root.Type.Kind == ExprType.FromColumnType(expected).Kind)
            {
                hint = $"a {expected.AsNonNullable()} holds exactly {expected.FixedLength} elements and the expression's lists hold {root.Type.Length}; "
                       + $"declare the site as {ColumnType.List(expected.Element, false, root.Type.Length)}, or write a list of {expected.FixedLength}";
            }

            Report(ExpressionErrorCodes.TypeMismatch, $"The expression has type {root.Type.WithNullable(false)} but this site expects {expected.AsNonNullable()}",
                ast.Start, ast.Length, hint);
            return root;
        }

        // A computed value outside the int32 or float32 range is replaced at run time; a constant one can only be a
        // configuration mistake, so it is reported here, where it can be fixed.
        if (_options.ExpectedType is { } narrow && root.IsConst && !ColumnValues.FitsStorage(root.Value, narrow))
        {
            var (range, width) = narrow.IsInt32Storage ? ($"[{int.MinValue}, {int.MaxValue}]", "int32") : (Float32Range.Text, "float32");
            Report(ExpressionErrorCodes.InvalidArgument,
                $"The constant {root.Value} does not fit this site's {narrow.AsNonNullable()} storage, which holds {range}",
                ast.Start, ast.Length, $"declare the column as {narrow.Promoted().AsNonNullable()}, or use values in the {width} range");
            return root;
        }

        if (root.Type.Nullable && !_options.AllowNullableResult)
        {
            ReportNullableResult(root, ast);
        }

        return root;
    }

    private static IrNode AsFloatList(IrNode node)
    {
        if (node.Type.Kind != TypeKind.ListInt || node.Op is not (IrOp.Const or IrOp.Conditional or IrOp.Coalesce))
        {
            return node;
        }

        return ToFloatList(node, node.IsConst ? null : new Dictionary<IrNode, IrNode?>(ReferenceEqualityComparer.Instance)) ?? node;
    }

    /// <summary>
    /// Memoized by node: a <c>let</c>'s references share one node, so the IR is a graph whose paths can be
    /// exponentially more than its nodes.
    /// </summary>
    private static IrNode? ToFloatList(IrNode node, Dictionary<IrNode, IrNode?>? converted)
    {
        if (converted is not null && converted.TryGetValue(node, out var known))
        {
            return known;
        }

        IrNode? result = null;
        var type = node.Type with { Kind = TypeKind.ListFloat };
        if (node.IsConst && !node.Value.IsMissing)
        {
            var ints = node.Value.AsInt64List();
            var floats = new double[ints.Length];
            for (var i = 0; i < floats.Length; i++)
            {
                floats[i] = ints[i];
            }

            result = IrNode.Const(ScalarValue.FromFloat64List(floats), type, node.Start, node.Length);
        }
        else if (node.Op is IrOp.Conditional or IrOp.Coalesce)
        {
            var args = (IrNode[])node.Args.Clone();
            var all = true;
            for (var i = node.Op == IrOp.Conditional ? 1 : 0; i < args.Length && all; i++)
            {
                var list = args[i].Type.Kind == TypeKind.ListInt ? ToFloatList(args[i], converted) : null;
                all = list is not null;
                args[i] = list ?? args[i];
            }

            result = all ? node.WithArgs(args, type) : null;
        }

        converted?.Add(node, result);
        return result;
    }

    private static bool IsFloat32List(IrNode node, Dictionary<IrNode, bool>? known = null)
    {
        switch (node.Op)
        {
            case IrOp.Column or IrOp.Context or IrOp.Param:
                return node.Symbol.Type.Kind == StorageKind.List && node.Symbol.Type.Element == StorageKind.Float32;
            case IrOp.Conditional or IrOp.Coalesce:
            {
                known ??= new Dictionary<IrNode, bool>(ReferenceEqualityComparer.Instance);
                if (known.TryGetValue(node, out var result))
                {
                    return result;
                }

                result = true;
                for (var i = node.Op == IrOp.Conditional ? 1 : 0; i < node.Args.Length && result; i++)
                {
                    result = IsFloat32List(node.Args[i], known);
                }

                known.Add(node, result);
                return result;
            }

            default:
                return false;
        }
    }

    private static void AdaptListLiterals(ref IrNode a, ref IrNode b)
    {
        if (a.Type.Kind == TypeKind.ListFloat)
        {
            b = AsFloatList(b);
        }
        else if (b.Type.Kind == TypeKind.ListFloat)
        {
            a = AsFloatList(a);
        }
    }

    private static bool Accepts(ColumnType expected, ExprType actual) => expected.Kind switch
    {
        StorageKind.Float32 or StorageKind.Float64 => actual.IsNumeric,
        StorageKind.Int32 or StorageKind.Int64 => actual.Kind == TypeKind.Int,
        StorageKind.Bool => actual.Kind == TypeKind.Bool,
        StorageKind.String => actual.Kind == TypeKind.String,
        // A list of any length (or an expression whose length is not known) may be written to a fixed-length site, whose
        // writer checks each row's length; a list known to have another length cannot.
        StorageKind.List => actual.Kind == ExprType.FromColumnType(expected).Kind
                            && (expected.FixedLength == 0 || actual.Length == 0 || expected.FixedLength == actual.Length),
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
                // Not "or test has(x)": has() does not narrow, so 'has(x) ? f(x) : 0.0' is still nullable and reports this again.
                ? $"append ' ?? {fallback ?? "<default>"}' to the expression"
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
        TypeKind.String => "''",
        _ => null,
    };

    private static bool TryUnify(ExprType a, ExprType b, out TypeKind kind, out int length)
    {
        length = 0;
        kind = TypeKind.Error;
        if (a.Kind == b.Kind)
        {
            length = a.Length == b.Length ? a.Length : 0;
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

    private bool RequireFloatList(IrNode arg, string name, AstNode at)
    {
        if (arg.Type.Kind == TypeKind.ListFloat)
        {
            return true;
        }

        Fail(ExpressionErrorCodes.TypeMismatch, $"'{name}' needs a float list (list<float32> or list<float64>, such as list<float32,64>), got {arg.Type}", at,
            arg.Type.Kind == TypeKind.ListInt ? "an int64 list is not converted to a float list; declare the list's elements as float32 or float64" : null);
        return false;
    }

    private static double[] ToDoubles(ReadOnlySpan<long> values)
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
        var type = new ExprType(a.Type.Kind, cond.Type.Nullable || a.Type.Nullable || b.Type.Nullable, a.Type.Kind == b.Type.Kind && a.Type.Length == b.Type.Length ? a.Type.Length : 0);
        return IrNode.Make(IrOp.Conditional, type, new[] { cond, a, b }, at.Start, at.Length);
    }

    private void Report(string code, string message, int offset, int length, string? hint = null) =>
        _diagnostics.Add(new ExpressionDiagnostic(code, message.EndsWith('.') ? message : message + ".", offset, Math.Max(1, length), hint));

    private IrNode Fail(string code, string message, AstNode at, string? hint = null)
    {
        Report(code, message, at.Start, at.Length, hint);
        return Poison(at);
    }

    private static string DefaultHint(ExprType type)
    {
        var plain = type.WithNullable(false);
        var example = type.Kind switch
        {
            TypeKind.Int => " such as 0",
            TypeKind.Float => " such as 0.0",
            TypeKind.String => " such as ''",
            TypeKind.Bool => " such as false",
            _ => string.Empty,
        };
        return $"the default after '??' is used where the value before it is missing: give a {plain}{example}";
    }

    private IrNode FailAt(Span culprit, string code, string message, AstNode at, string? hint = null)
    {
        Report(code, message, culprit.Start, culprit.Length, hint);
        return Poison(at);
    }

    private static Span Culprit(BinaryAst b, bool leftWrong, bool rightWrong) =>
        leftWrong == rightWrong ? new Span(b.OpStart, b.OpLength) : leftWrong ? b.Left : b.Right;

    private static bool IsEquatable(ExprType type) => type.IsNumeric || type.Kind is TypeKind.Bool or TypeKind.String;

    private readonly record struct Span(int Start, int Length)
    {
        public static implicit operator Span(AstNode node) => new(node.Start, node.Length);
    }

    private static IrNode Poison(AstNode at) => IrNode.Const(default, ExprType.Error, at.Start, at.Length);
}
