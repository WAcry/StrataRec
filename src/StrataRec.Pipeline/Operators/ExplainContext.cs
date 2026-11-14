using System.Runtime.InteropServices;
using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Tables;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Pipeline.Execution;

namespace StrataRec.Pipeline.Operators;

/// <summary>
/// An operator that can say why it treated an explained candidate as it did (design 13.3): the expression a filter
/// tested, the cut a truncation made, the terms of a score. Optional: every node is explained without it, by the
/// candidate's state and the values of the columns the node read and wrote.
/// </summary>
/// <remarks>
/// Called only for requests that ask to explain candidates, once per attempt that returned without failing, before its
/// outputs are committed: the table is as the operator left it, and columns resolve to the versions it read. If the commit
/// then fails, what it wrote is dropped with the attempt. It reads only: it must not change the table. An exception it
/// throws is reported in the explanation and changes nothing else.
/// </remarks>
public interface IExplainableOperator
{
    /// <summary>Writes a reason and terms for the targets the node saw or produced (<see cref="ExplainContext.WasAlive"/>, <see cref="ExplainContext.IsAlive"/>).</summary>
    void Explain(ExplainContext context);
}

/// <summary>
/// What an <see cref="IExplainableOperator"/> sees of an explained request: the targets (by index, 0 to
/// <see cref="Count"/>), their rows and liveness before and after the node, the request's columns and parameters, and
/// where to write each target's reason and terms. Work here is per target, never per row of the table.
/// </summary>
public sealed class ExplainContext
{
    private readonly RequestState _state;
    private readonly CandidateExplainer _explainer;

    internal ExplainContext(RequestState state, CandidateExplainer explainer, string nodeId)
    {
        _state = state;
        _explainer = explainer;
        NodeId = nodeId;
    }

    public string NodeId { get; }

    /// <summary>Number of targets.</summary>
    public int Count => _explainer.Count;

    public CandidateTable Table => _state.Table;

    /// <summary>Live physical rows in rank order, after the node.</summary>
    public ReadOnlySpan<int> LiveRows => _state.Table.Rows.Order;

    public EntityKey Key(int target) => _explainer.Key(target);

    /// <summary>The target's physical row, or -1 when it is not in the table.</summary>
    public int Row(int target) => _explainer.PendingRow(target);

    /// <summary>The target was live before the node: the node saw it.</summary>
    public bool WasAlive(int target) => _explainer.RankBefore(target) > 0;

    /// <summary>The target is live after the node.</summary>
    public bool IsAlive(int target) => _explainer.PendingRank(target) > 0;

    /// <summary>The target's 1-based position among the live rows after the node; 0 when it is not live.</summary>
    public int Rank(int target) => _explainer.PendingRank(target);

    /// <summary>The committed version of a column (or its fixed slot), as the operator read it; null when not produced.</summary>
    public ColumnBuffer? Read(in ColumnRef column)
    {
        var slot = column.FixedSlot >= 0 ? column.FixedSlot : _state.Bindings.Resolve(column.LogicalId);
        return slot < 0 ? null : _state.Table.TryGetSlot(slot);
    }

    /// <summary>A column's value for a target; missing when the target has no row or the column no value.</summary>
    public ScalarValue Value(in ColumnRef column, int target)
    {
        var row = Row(target);
        return row >= 0 && Read(column) is { } buffer && row < buffer.Capacity ? ColumnValues.Get(buffer, row) : ScalarValue.Missing;
    }

    public ScalarValue Param(in ParamRef parameter) => _state.Params[parameter.Id];

    /// <summary>
    /// The value of an expression for each target the node saw or produced (missing for the others), evaluated on those
    /// rows only, as the operator evaluated it: the same columns, context and parameters.
    /// </summary>
    public ScalarValue[] Evaluate(CompiledExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var values = new ScalarValue[Count];
        var rows = new List<int>(Count);
        for (var t = 0; t < Count; t++)
        {
            if (Row(t) >= 0 && (WasAlive(t) || IsAlive(t)))
            {
                rows.Add(Row(t));
            }
        }

        if (rows.Count == 0)
        {
            return values;
        }

        var buffer = _explainer.Scratch(expression.ResultType, _state.Table);
        var span = CollectionsMarshal.AsSpan(rows);

        // Without the request's counters: explaining must not change what the request reports (out-of-range values).
        expression.Evaluate(new EvalContext(_state.Table, _state.Bindings, _state.Context, _state.Params), span, buffer);
        for (var t = 0; t < Count; t++)
        {
            if (Row(t) >= 0 && (WasAlive(t) || IsAlive(t)))
            {
                values[t] = ColumnValues.Get(buffer, Row(t));
            }
        }

        return values;
    }

    /// <summary>The operator's account of its decision for a target, in a sentence; the last one written wins.</summary>
    public void Reason(int target, string reason) => _explainer.SetReason(target, reason);

    /// <summary>A named value the decision for a target rests on, e.g. <c>k</c> or <c>multiplier.quality</c>.</summary>
    public void Term(int target, string name, in ScalarValue value) => _explainer.AddTerm(target, name, value);
}
