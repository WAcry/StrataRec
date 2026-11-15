using StrataRec.Core.Values;
using StrataRec.Expressions.Binding;

namespace StrataRec.Expressions.Syntax;

/// <summary>
/// Recursive-descent parser with CEL precedence plus <c>let</c> and <c>??</c>.
/// </summary>
/// <remarks>
/// <para>Precedence, loosest first: <c>?:</c>, <c>??</c>, <c>||</c>, <c>&amp;&amp;</c>, relations
/// (<c>== != &lt; &lt;= &gt; &gt;= in</c>), <c>+ -</c>, <c>* / %</c>, unary <c>! -</c>, member call. <c>??</c> sits
/// between <c>?:</c> and <c>||</c> (as in C#) and is right-associative, so <c>a ?? b ?? c</c> is
/// <c>a ?? (b ?? c)</c> and <c>x || y ?? z</c> is <c>(x || y) ?? z</c>.</para>
/// <para>Depth (design 7.9) is enforced twice: a nesting counter is checked <em>before</em> descending into any
/// nested construct, so the parser's own recursion is bounded; and every node's depth is checked when built, which
/// bounds left-associative chains that the parser builds iteratively.</para>
/// </remarks>
internal sealed class Parser
{
    private readonly List<Token> _tokens;
    private readonly int _maxDepth;
    private int _pos;
    private int _nesting;

    private Parser(List<Token> tokens, int maxDepth)
    {
        _tokens = tokens;
        _maxDepth = maxDepth;
    }

    /// <summary>Parses a whole expression; throws <see cref="SyntaxErrorException"/> on the first error.</summary>
    public static AstNode Parse(string source, int maxDepth)
    {
        var parser = new Parser(Lexer.Tokenize(source), Math.Max(1, maxDepth));
        var node = parser.ParseExpression();
        if (parser.Cur.Kind != TokenKind.End)
        {
            throw parser.Unexpected("Expected an operator or the end of the expression");
        }

        return node;
    }

    private Token Cur => _tokens[_pos];

    private Token Peek(int ahead) => _tokens[Math.Min(_pos + ahead, _tokens.Count - 1)];

    private Token Advance() => _tokens[_pos++];

    private bool IsIdent(Token t, string text) => t.Kind == TokenKind.Ident && t.Text == text;

    private AstNode ParseExpression()
    {
        if (IsIdent(Cur, "let") && Peek(1).Kind == TokenKind.Ident)
        {
            return ParseLet();
        }

        return ParseConditional();
    }

    private AstNode ParseLet()
    {
        var start = Cur.Start;
        var bindings = new List<LetBinding>();
        while (IsIdent(Cur, "let") && Peek(1).Kind == TokenKind.Ident)
        {
            Advance();
            var name = Advance();
            Expect(TokenKind.Assign, $"Expected '=' after 'let {name.Text}'");
            var value = Nested(static p => p.ParseConditional());
            Expect(TokenKind.Semicolon, $"Expected ';' after the value of 'let {name.Text}'", "each binding ends with ';' and the body follows the last one");
            bindings.Add(new LetBinding(name.Text!, name.Start, value));
        }

        var body = Nested(static p => p.ParseConditional());
        return Check(new LetAst(start, bindings.ToArray(), body));
    }

    private AstNode ParseConditional()
    {
        var condition = ParseCoalesce();
        if (Cur.Kind != TokenKind.Question)
        {
            return condition;
        }

        Advance();
        var then = Nested(static p => p.ParseConditional());
        Expect(TokenKind.Colon, "Expected ':' in conditional expression");
        var otherwise = Nested(static p => p.ParseConditional());
        return Check(new ConditionalAst(condition, then, otherwise));
    }

    private AstNode ParseCoalesce()
    {
        var left = ParseOr();
        if (Cur.Kind != TokenKind.QuestionQuestion)
        {
            return left;
        }

        var op = Advance();
        var right = Nested(static p => p.ParseCoalesce());
        return Check(new BinaryAst(BinaryOp.Coalesce, left, right, op.Start, op.Length));
    }

    private AstNode ParseOr()
    {
        var left = ParseAnd();
        while (Cur.Kind == TokenKind.OrOr)
        {
            var op = Advance();
            left = Check(new BinaryAst(BinaryOp.Or, left, ParseAnd(), op.Start, op.Length));
        }

        return left;
    }

    private AstNode ParseAnd()
    {
        var left = ParseRelation();
        while (Cur.Kind == TokenKind.AndAnd)
        {
            var op = Advance();
            left = Check(new BinaryAst(BinaryOp.And, left, ParseRelation(), op.Start, op.Length));
        }

        return left;
    }

    private AstNode ParseRelation()
    {
        var left = ParseAdditive();
        while (true)
        {
            BinaryOp op;
            switch (Cur.Kind)
            {
                case TokenKind.EqEq: op = BinaryOp.Eq; break;
                case TokenKind.NotEq: op = BinaryOp.Ne; break;
                case TokenKind.Lt: op = BinaryOp.Lt; break;
                case TokenKind.Le: op = BinaryOp.Le; break;
                case TokenKind.Gt: op = BinaryOp.Gt; break;
                case TokenKind.Ge: op = BinaryOp.Ge; break;
                case TokenKind.Ident when Cur.Text == "in": op = BinaryOp.In; break;
                case TokenKind.Assign:
                    throw Error(Cur, "Unexpected '='", "use '==' to compare values");
                default:
                    return left;
            }

            var token = Advance();
            left = Check(new BinaryAst(op, left, ParseAdditive(), token.Start, token.Length));
        }
    }

    private AstNode ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Cur.Kind is TokenKind.Plus or TokenKind.Minus)
        {
            var token = Advance();
            var op = token.Kind == TokenKind.Plus ? BinaryOp.Add : BinaryOp.Sub;
            left = Check(new BinaryAst(op, left, ParseMultiplicative(), token.Start, token.Length));
        }

        return left;
    }

    private AstNode ParseMultiplicative()
    {
        var left = ParseUnary();
        while (Cur.Kind is TokenKind.Star or TokenKind.Slash or TokenKind.Percent)
        {
            var token = Advance();
            var op = token.Kind switch
            {
                TokenKind.Star => BinaryOp.Mul,
                TokenKind.Slash => BinaryOp.Div,
                _ => BinaryOp.Mod,
            };
            left = Check(new BinaryAst(op, left, ParseUnary(), token.Start, token.Length));
        }

        return left;
    }

    private AstNode ParseUnary()
    {
        var token = Cur;
        if (token.Kind == TokenKind.Bang)
        {
            Advance();
            var operand = Nested(static p => p.ParseUnary());
            return Check(new UnaryAst(token.Start, operand.End - token.Start, UnaryOp.Not, operand));
        }

        if (token.Kind == TokenKind.Minus)
        {
            var next = Peek(1);
            if (next.Kind is TokenKind.Int or TokenKind.Float && next.Start == token.End)
            {
                // A negative numeric literal (CEL treats the sign as part of the literal); allows int64.MinValue.
                Advance();
                Advance();
                var literal = next.Kind == TokenKind.Int
                    ? new LiteralAst(token.Start, next.End - token.Start, ScalarValue.FromInt64(unchecked(-(long)next.IntValue)))
                    : new LiteralAst(token.Start, next.End - token.Start, ScalarValue.FromFloat64(-next.FloatValue));
                return ParseMemberSuffix(literal);
            }

            Advance();
            var operand = Nested(static p => p.ParseUnary());
            return Check(new UnaryAst(token.Start, operand.End - token.Start, UnaryOp.Neg, operand));
        }

        return ParseMemberSuffix(ParsePrimary());
    }

    private AstNode ParseMemberSuffix(AstNode receiver)
    {
        while (true)
        {
            if (Cur.Kind == TokenKind.Dot && Peek(1).Kind == TokenKind.Ident)
            {
                var name = Peek(1);
                if (Peek(2).Kind != TokenKind.LParen)
                {
                    throw Error(name, $"Field selection '.{name.Text}' is only supported on 'col.', 'ctx.' and 'param.' names",
                        "call a function with method syntax, e.g. x.size(), or write the full dotted name");
                }

                Advance();
                Advance();
                var start = receiver.Start;
                var args = ParseArguments(receiver);
                receiver = Check(new CallAst(start, _tokens[_pos - 1].End - start, name.Text!, name.Start, args, isMethod: true));
                continue;
            }

            if (Cur.Kind == TokenKind.LBracket)
            {
                throw Error(Cur, "Indexing with '[...]' is not supported", "use contains(), size() or 'x in list'");
            }

            if (Cur.Kind == TokenKind.Dot)
            {
                throw Error(Peek(1), "Expected a name after '.'");
            }

            return receiver;
        }
    }

    private AstNode ParsePrimary()
    {
        var token = Cur;
        switch (token.Kind)
        {
            case TokenKind.Int:
                Advance();
                if (token.IntValue > long.MaxValue)
                {
                    throw Error(token, "Integer literal is out of the int64 range");
                }

                return new LiteralAst(token.Start, token.Length, ScalarValue.FromInt64((long)token.IntValue));
            case TokenKind.Float:
                Advance();
                return new LiteralAst(token.Start, token.Length, ScalarValue.FromFloat64(token.FloatValue));
            case TokenKind.String:
                Advance();
                return new LiteralAst(token.Start, token.Length, ScalarValue.FromString(token.Text));
            case TokenKind.Ident:
                switch (token.Text)
                {
                    case "true":
                    case "false":
                        Advance();
                        return new LiteralAst(token.Start, token.Length, ScalarValue.FromBool(token.Text == "true"));
                    case "null":
                        throw Error(token, "ColumnExpr has no null literal",
                            "test presence with has(x) and supply a value for missing inputs with 'x ?? default'");
                    case "in":
                        throw Error(token, "Unexpected 'in'");
                    default:
                        return ParseNameOrCall();
                }

            case TokenKind.LParen:
            {
                Advance();
                var inner = Nested(static p => p.ParseExpression());
                var close = Expect(TokenKind.RParen, "Expected ')'");
                return Check(new ParenAst(token.Start, close.End - token.Start, inner));
            }

            case TokenKind.LBracket:
                return ParseList();
            case TokenKind.LBrace:
                throw Error(token, "Map literals are not supported");
            case TokenKind.End:
                throw Error(token, "Unexpected end of expression");
            default:
                throw Unexpected("Expected a value");
        }
    }

    private AstNode ParseNameOrCall()
    {
        var first = Advance();
        if (Cur.Kind == TokenKind.LParen)
        {
            var args = ParseArguments(null);
            return Check(new CallAst(first.Start, _tokens[_pos - 1].End - first.Start, first.Text!, first.Start, args, isMethod: false));
        }

        var name = first.Text!;
        var end = first.End;
        while (Cur.Kind == TokenKind.Dot && Peek(1).Kind == TokenKind.Ident)
        {
            var segment = Peek(1);

            // `a.b.f(` where f is a function: method call on `a.b` (handled by ParseMemberSuffix).
            // `a.b.g(` where g is not a function is reported as an unknown function by the binder.
            if (Peek(2).Kind == TokenKind.LParen)
            {
                break;
            }

            Advance();
            Advance();
            name = name + "." + segment.Text;
            end = segment.End;
        }

        if (Cur.Kind == TokenKind.At)
        {
            var at = Advance();
            if (Cur.Kind != TokenKind.Ident || Cur.Start != at.End)
            {
                throw Error(at, "Expected a node id after '@'", "write a version reference like 'col.score.final@fusion'");
            }

            var node = Advance();
            name = name + "@" + node.Text;
            end = node.End;
        }

        return new NameAst(first.Start, end - first.Start, name);
    }

    private AstNode[] ParseArguments(AstNode? receiver)
    {
        Expect(TokenKind.LParen, "Expected '('");
        var args = new List<AstNode>();
        if (receiver is not null)
        {
            args.Add(receiver);
        }

        if (Cur.Kind != TokenKind.RParen)
        {
            while (true)
            {
                args.Add(Nested(static p => p.ParseExpression()));
                if (Cur.Kind == TokenKind.Comma)
                {
                    Advance();
                    continue;
                }

                break;
            }
        }

        Expect(TokenKind.RParen, "Expected ',' or ')' in argument list");
        return args.ToArray();
    }

    private AstNode ParseList()
    {
        var open = Advance();
        var items = new List<AstNode>();
        while (Cur.Kind != TokenKind.RBracket)
        {
            items.Add(Nested(static p => p.ParseExpression()));
            if (Cur.Kind == TokenKind.Comma)
            {
                Advance();
                continue;
            }

            if (Cur.Kind != TokenKind.RBracket)
            {
                throw Unexpected("Expected ',' or ']' in list literal");
            }
        }

        var close = Advance();
        return Check(new ListAst(open.Start, close.End - open.Start, items.ToArray()));
    }

    private AstNode Nested(Func<Parser, AstNode> parse)
    {
        if (_nesting + 1 > _maxDepth)
        {
            // Report at the token that opens the nested construct ('(', '!', '?', ',', ...).
            var opener = _tokens[Math.Max(0, _pos - 1)];
            throw TooDeep(opener.Start, Math.Max(1, opener.Length));
        }

        _nesting++;
        var node = parse(this);
        _nesting--;
        return node;
    }

    private AstNode Check(AstNode node)
    {
        if (node.Depth > _maxDepth)
        {
            throw TooDeep(node.Start, node.Length);
        }

        return node;
    }

    private SyntaxErrorException TooDeep(int offset, int length) =>
        new(new ExpressionDiagnostic(
            ExpressionErrorCodes.TooDeep,
            $"Expression nesting exceeds the maximum depth of {_maxDepth}",
            offset,
            Math.Max(1, length),
            "split the formula with 'let' bindings or move the logic into an operator (design 7.9)"));

    private Token Expect(TokenKind kind, string message, string? hint = null)
    {
        if (Cur.Kind != kind)
        {
            throw Unexpected(message, hint);
        }

        return Advance();
    }

    private SyntaxErrorException Unexpected(string message, string? hint = null) =>
        Error(Cur, Cur.Kind == TokenKind.End ? message + " but reached the end of the expression" : $"{message} but found '{Describe(Cur)}'", hint);

    private static SyntaxErrorException Error(Token token, string message, string? hint = null) =>
        Lexer.Error(token.Start, token.Length, message.EndsWith('.') ? message : message + ".", hint);

    private static string Describe(Token t) => t.Kind switch
    {
        TokenKind.Ident or TokenKind.String => t.Text ?? string.Empty,
        TokenKind.Int => t.IntValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
        TokenKind.Float => t.FloatValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        _ => Builtins.TokenSpelling(t.Kind),
    };
}
