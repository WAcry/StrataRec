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
            DestinationWriter.Broadcast(EvaluateScalar(context), rows, destination);
            return;
        }

        _vector.Evaluate(context, rows, destination);
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
