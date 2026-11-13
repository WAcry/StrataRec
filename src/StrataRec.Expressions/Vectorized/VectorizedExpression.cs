using System.Buffers;
using StrataRec.Core.Columns;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions.Binding;
using StrataRec.Expressions.Ir;
using StrataRec.Expressions.Runtime;

namespace StrataRec.Expressions.Vectorized;

/// <summary>A compiled expression on backend A (design 7.7). Immutable and safe for concurrent evaluation.</summary>
internal sealed class VectorizedExpression : CompiledExpression
{
    private readonly ExprType _type;
    private readonly ScalarProgram? _scalar;
    private readonly int _scalarRoot;
    private readonly VectorProgram? _vector;
    private readonly Symbol[] _reads;

    /// <summary>Arrays of list literals a request-level result can pass through unchanged (e.g. <c>c ? [1] : [2]</c>);
    /// null when there are none. They belong to this shared instance, so <see cref="EvaluateScalar"/> hands out copies.</summary>
    private readonly object[]? _constantArrays;

    public VectorizedExpression(string source, IrNode bound, IrNode optimized)
    {
        Source = source;
        Bound = bound;
        Optimized = optimized;
        _type = bound.Type;
        ResultType = bound.Type.ToColumnType();
        IsRowDependent = !optimized.IsInvariant;
        _reads = CollectReads(optimized);
        if (IsRowDependent)
        {
            _vector = VectorProgram.Build(optimized, _type);
        }
        else
        {
            var builder = new ScalarProgram.Builder();
            _scalarRoot = builder.Add(optimized);
            _scalar = builder.Build();
            _constantArrays = CollectConstantArrays(optimized);
        }
    }

    public override string Source { get; }

    public override ColumnType ResultType { get; }

    public override bool IsRowDependent { get; }

    public override IReadOnlyList<Symbol> Reads => _reads;

    /// <summary>The type-checked IR before optimization (input of the reference interpreter).</summary>
    internal IrNode Bound { get; }

    /// <summary>The optimized IR DAG the backend executes.</summary>
    internal IrNode Optimized { get; }

    /// <summary>The lowered program of a row-level expression (null for request-level ones).</summary>
    internal VectorProgram? Program => _vector;

    public override string Describe() => IrPrinter.Print(Optimized);

    public override ScalarValue EvaluateScalar(in EvalContext context)
    {
        var value = Run(context);
        return _constantArrays is null ? value : Detach(value, _constantArrays);
    }

    /// <summary>Evaluates the request-level program. The result may share a constant's array; callers that hand it
    /// out must <see cref="Detach"/> it (column writers copy, so broadcasting need not).</summary>
    private ScalarValue Run(in EvalContext context)
    {
        if (_scalar is null)
        {
            throw new InvalidOperationException($"Expression '{Source}' reads candidate columns and must be evaluated with Evaluate().");
        }

        var slots = ArrayPool<ScalarValue>.Shared.Rent(Math.Max(1, _scalar.SlotCount));
        try
        {
            _scalar.Run(context, slots);
            return slots[_scalarRoot];
        }
        finally
        {
            ArrayPool<ScalarValue>.Shared.Return(slots, clearArray: true);
        }
    }

    public override void Evaluate(in EvalContext context, ReadOnlySpan<int> rows, ColumnBuffer destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        DestinationWriter.Validate(_type, destination);
        if (rows.IsEmpty)
        {
            return;
        }

        if (_vector is null)
        {
            DestinationWriter.Broadcast(Run(context), rows, destination);
            return;
        }

        _vector.Evaluate(context, rows, destination);
    }

    /// <summary>Copies <paramref name="value"/> when its array is one of the expression's own constants.</summary>
    private static ScalarValue Detach(in ScalarValue value, object[] constants)
    {
        object array;
        switch (value.Kind)
        {
            case StorageKind.ListInt64: array = value.AsInt64List(); break;
            case StorageKind.ListString: array = value.AsStringList(); break;
            default: return value;
        }

        foreach (var constant in constants)
        {
            if (ReferenceEquals(constant, array))
            {
                return value.Kind == StorageKind.ListInt64
                    ? ScalarValue.FromInt64List((long[])value.AsInt64List().Clone())
                    : ScalarValue.FromStringList((string[])value.AsStringList().Clone());
            }
        }

        return value;
    }

    /// <summary>The arrays of int and string list literals in the DAG (float lists are never results; the language has
    /// no vector literals).</summary>
    private static object[]? CollectConstantArrays(IrNode root)
    {
        var seen = new HashSet<IrNode>(ReferenceEqualityComparer.Instance);
        var arrays = new List<object>();
        Visit(root);
        return arrays.Count == 0 ? null : arrays.ToArray();

        void Visit(IrNode node)
        {
            if (!seen.Add(node))
            {
                return;
            }

            if (node.IsConst && node.Value.Kind is StorageKind.ListInt64 or StorageKind.ListString)
            {
                arrays.Add(node.Value.Kind == StorageKind.ListInt64 ? node.Value.AsInt64List() : node.Value.AsStringList());
            }

            foreach (var a in node.Args)
            {
                Visit(a);
            }
        }
    }

    private static Symbol[] CollectReads(IrNode root)
    {
        var seen = new HashSet<IrNode>(ReferenceEqualityComparer.Instance);
        var symbols = new List<Symbol>();
        Visit(root);
        return symbols.ToArray();

        void Visit(IrNode node)
        {
            if (!seen.Add(node))
            {
                return;
            }

            if (node.Op is IrOp.Column or IrOp.Context or IrOp.Param && !symbols.Contains(node.Symbol))
            {
                symbols.Add(node.Symbol);
            }

            foreach (var a in node.Args)
            {
                Visit(a);
            }
        }
    }
}
