using System.Buffers;
using StrataRec.Core.Values;
using StrataRec.Expressions.Ir;

namespace StrataRec.Expressions.Runtime;

/// <summary>
/// Request-level IR nodes in topological order, evaluated once per call into value slots. Used for
/// <see cref="CompiledExpression.EvaluateScalar"/> and for the hoisted loop invariants of row-level expressions
/// (design 7.7). Evaluation is linear in the DAG size, so shared <c>let</c> chains cannot blow up.
/// </summary>
internal sealed class ScalarProgram
{
    private readonly IrNode[] _nodes;
    private readonly int[][] _argSlots;
    private readonly int _maxArity;

    private ScalarProgram(IrNode[] nodes, int[][] argSlots)
    {
        _nodes = nodes;
        _argSlots = argSlots;
        foreach (var a in argSlots)
        {
            _maxArity = Math.Max(_maxArity, a.Length);
        }
    }

    public int SlotCount => _nodes.Length;

    /// <summary>Evaluates every node; <paramref name="slots"/> must have at least <see cref="SlotCount"/> entries.</summary>
    public void Run(in EvalContext context, ScalarValue[] slots)
    {
        if (_nodes.Length == 0)
        {
            return;
        }

        var scratch = ArrayPool<ScalarValue>.Shared.Rent(Math.Max(1, _maxArity));
        try
        {
            for (var i = 0; i < _nodes.Length; i++)
            {
                var node = _nodes[i];
                switch (node.Op)
                {
                    case IrOp.Const:
                        slots[i] = node.Value;
                        break;
                    case IrOp.Context or IrOp.Param:
                        slots[i] = ValueAccess.ReadRequestValue(node, context);
                        break;
                    case IrOp.Column:
                        throw new InvalidOperationException($"'{node.Symbol.Name}' is a column and cannot be evaluated once per request.");
                    default:
                    {
                        var argSlots = _argSlots[i];
                        for (var j = 0; j < argSlots.Length; j++)
                        {
                            scratch[j] = slots[argSlots[j]];
                        }

                        slots[i] = ScalarOps.Apply(node, scratch.AsSpan(0, argSlots.Length), context.Counters);
                        break;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<ScalarValue>.Shared.Return(scratch, clearArray: true);
        }
    }

    /// <summary>Collects request-level nodes and assigns them slots in dependency order.</summary>
    internal sealed class Builder
    {
        private readonly Dictionary<IrNode, int> _slots = new(ReferenceEqualityComparer.Instance);
        private readonly List<IrNode> _nodes = new();
        private readonly List<int[]> _args = new();

        public int Add(IrNode node)
        {
            if (_slots.TryGetValue(node, out var slot))
            {
                return slot;
            }

            var args = new int[node.Args.Length];
            for (var i = 0; i < args.Length; i++)
            {
                args[i] = Add(node.Args[i]);
            }

            slot = _nodes.Count;
            _nodes.Add(node);
            _args.Add(args);
            _slots.Add(node, slot);
            return slot;
        }

        public ScalarProgram Build() => new(_nodes.ToArray(), _args.ToArray());
    }
}

/// <summary>
/// Row-at-a-time tree-walking interpreter over the unoptimized typed IR. Deliberately naive and obviously correct:
/// it is the oracle for the differential tests of the optimizer and the vectorized backend (design 7.7).
/// </summary>
internal static class ReferenceInterpreter
{
    /// <summary>Evaluates <paramref name="node"/> for one physical row (ignored for request-level nodes).</summary>
    public static ScalarValue Evaluate(IrNode node, in EvalContext context, int row)
    {
        switch (node.Op)
        {
            case IrOp.Const:
                return node.Value;
            case IrOp.Column:
                return ValueAccess.ReadCell(node, context, row);
            case IrOp.Context or IrOp.Param:
                return ValueAccess.ReadRequestValue(node, context);
        }

        var args = new ScalarValue[node.Args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            args[i] = Evaluate(node.Args[i], context, row);
        }

        return ScalarOps.Apply(node, args, context.Counters);
    }
}
