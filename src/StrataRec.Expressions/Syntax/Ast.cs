using StrataRec.Core.Values;

namespace StrataRec.Expressions.Syntax;

/// <summary>Parse tree node with its source span. <see cref="Depth"/> is the nesting depth checked against
/// <see cref="CompileOptions.MaxDepth"/>: leaves are 0, every operator, call, list, parenthesized group, conditional
/// and let adds one.</summary>
internal abstract class AstNode(int start, int length)
{
    public int Start { get; } = start;

    public int Length { get; } = length;

    public int End => Start + Length;

    public int Depth { get; protected init; }

    protected static int MaxDepth(AstNode a) => a.Depth;

    protected static int MaxDepth(AstNode a, AstNode b) => Math.Max(a.Depth, b.Depth);

    protected static int MaxDepth(AstNode[] nodes)
    {
        var d = 0;
        foreach (var n in nodes)
        {
            d = Math.Max(d, n.Depth);
        }

        return d;
    }
}

internal sealed class LiteralAst : AstNode
{
    public LiteralAst(int start, int length, ScalarValue value)
        : base(start, length)
    {
        Value = value;
    }

    public ScalarValue Value { get; }
}

internal sealed class ListAst : AstNode
{
    public ListAst(int start, int length, AstNode[] items)
        : base(start, length)
    {
        Items = items;
        Depth = 1 + MaxDepth(items);
    }

    public AstNode[] Items { get; }
}

/// <summary>A bare identifier (let reference) or a dotted path such as <c>col.score.final@fusion</c>.</summary>
internal sealed class NameAst : AstNode
{
    public NameAst(int start, int length, string name)
        : base(start, length)
    {
        Name = name;
    }

    public string Name { get; }
}

internal enum UnaryOp : byte
{
    Neg,
    Not,
}

internal sealed class UnaryAst : AstNode
{
    public UnaryAst(int start, int length, UnaryOp op, AstNode operand)
        : base(start, length)
    {
        Op = op;
        Operand = operand;
        Depth = 1 + operand.Depth;
    }

    public UnaryOp Op { get; }

    public AstNode Operand { get; }
}

internal enum BinaryOp : byte
{
    Or,
    And,
    Coalesce,
    Eq,
    Ne,
    Lt,
    Le,
    Gt,
    Ge,
    In,
    Add,
    Sub,
    Mul,
    Div,
    Mod,
}

internal sealed class BinaryAst : AstNode
{
    public BinaryAst(BinaryOp op, AstNode left, AstNode right, int opStart, int opLength)
        : base(left.Start, right.End - left.Start)
    {
        Op = op;
        Left = left;
        Right = right;
        OpStart = opStart;
        OpLength = opLength;
        Depth = 1 + MaxDepth(left, right);
    }

    public BinaryOp Op { get; }

    public AstNode Left { get; }

    public AstNode Right { get; }

    public int OpStart { get; }

    public int OpLength { get; }

    public static string Spelling(BinaryOp op) => op switch
    {
        BinaryOp.Or => "||",
        BinaryOp.And => "&&",
        BinaryOp.Coalesce => "??",
        BinaryOp.Eq => "==",
        BinaryOp.Ne => "!=",
        BinaryOp.Lt => "<",
        BinaryOp.Le => "<=",
        BinaryOp.Gt => ">",
        BinaryOp.Ge => ">=",
        BinaryOp.In => "in",
        BinaryOp.Add => "+",
        BinaryOp.Sub => "-",
        BinaryOp.Mul => "*",
        BinaryOp.Div => "/",
        _ => "%",
    };
}

internal sealed class ConditionalAst : AstNode
{
    public ConditionalAst(AstNode condition, AstNode then, AstNode otherwise)
        : base(condition.Start, otherwise.End - condition.Start)
    {
        Condition = condition;
        Then = then;
        Else = otherwise;
        Depth = 1 + Math.Max(condition.Depth, MaxDepth(then, otherwise));
    }

    public AstNode Condition { get; }

    public AstNode Then { get; }

    public AstNode Else { get; }
}

/// <summary>A function call; for method-call syntax <c>a.f(b)</c> the receiver is <c>Args[0]</c>.</summary>
internal sealed class CallAst : AstNode
{
    public CallAst(int start, int length, string name, int nameStart, AstNode[] args, bool isMethod)
        : base(start, length)
    {
        Name = name;
        NameStart = nameStart;
        Args = args;
        IsMethod = isMethod;
        Depth = 1 + MaxDepth(args);
    }

    public string Name { get; }

    public int NameStart { get; }

    public AstNode[] Args { get; }

    public bool IsMethod { get; }
}

internal readonly record struct LetBinding(string Name, int NameStart, AstNode Value);

/// <summary><c>let a = e1; let b = e2; body</c> as one node so binding chains do not recurse.</summary>
internal sealed class LetAst : AstNode
{
    public LetAst(int start, LetBinding[] bindings, AstNode body)
        : base(start, body.End - start)
    {
        Bindings = bindings;
        Body = body;
        var d = body.Depth;
        foreach (var b in bindings)
        {
            d = Math.Max(d, b.Value.Depth);
        }

        Depth = 1 + d;
    }

    public LetBinding[] Bindings { get; }

    public AstNode Body { get; }
}

internal sealed class ParenAst : AstNode
{
    public ParenAst(int start, int length, AstNode inner)
        : base(start, length)
    {
        Inner = inner;
        Depth = 1 + inner.Depth;
    }

    public AstNode Inner { get; }
}
