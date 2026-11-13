using StrataRec.Core.Columns;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Pipeline.Execution;

namespace StrataRec.Pipeline.Operators;

/// <summary>
/// The runtime view one node gets of its request. Row-set operations are available only for the effects the
/// operator declared (design 5.2), so declarations and behavior cannot drift apart.
/// </summary>
public sealed class OperatorContext
{
    private readonly RequestState _state;
    private readonly Effects _effects;

    internal OperatorContext(RequestState state, string nodeId, Effects effects, CancellationToken cancellationToken)
    {
        _state = state;
        NodeId = nodeId;
        _effects = effects;
        CancellationToken = cancellationToken;
    }

    public string NodeId { get; }

    public CancellationToken CancellationToken { get; }

    public CandidateTable Table => _state.Table;

    /// <summary>Live physical rows in rank order.</summary>
    public ReadOnlySpan<int> LiveRows => _state.Table.Rows.Order;

    public RequestMemory Memory => _state.Memory;

    public string RequestId => _state.RequestId;

    /// <summary>Request seed for deterministic randomness (design 5.7).</summary>
    public long Seed => _state.Seed;

    public RequestInputs Inputs => _state.Inputs;

    public EvalContext EvalContext => new(_state.Table, _state.Bindings, _state.Context, _state.Params, _state.Counters);

    /// <summary>Time left before the request deadline.</summary>
    public TimeSpan Remaining => _state.Remaining;

    internal string? FailureReason { get; private set; }

    internal string? DegradedReason { get; private set; }

    /// <summary>The committed version of a column (or its fixed slot); null when not produced in this request.</summary>
    public ColumnBuffer? Read(in ColumnRef column)
    {
        var slot = column.FixedSlot >= 0 ? column.FixedSlot : _state.Bindings.Resolve(column.LogicalId);
        return slot < 0 ? null : _state.Table.TryGetSlot(slot);
    }

    /// <summary>This node's buffer for an output column. Written values become visible only if the node succeeds.</summary>
    public ColumnBuffer Output(in OutputColumn column) => _state.Table.GetOrCreateSlot(column.Slot);

    public ScalarValue Param(in ParamRef parameter) => _state.Params[parameter.Id];

    public ScalarValue ContextValue(int contextId) => _state.Context[contextId];

    /// <summary>Drops rows whose rank position is not flagged. Requires <see cref="Effects.RowFilter"/>.</summary>
    public void Retain(ReadOnlySpan<bool> keepByRank)
    {
        Require(Effects.RowFilter, nameof(Retain));
        _state.Table.Rows.Retain(keepByRank);
    }

    /// <summary>Keeps the first <paramref name="count"/> rows. Requires <see cref="Effects.RowFilter"/>.</summary>
    public void Truncate(int count)
    {
        Require(Effects.RowFilter, nameof(Truncate));
        _state.Table.Rows.Truncate(count);
    }

    /// <summary>
    /// Sets a new ranking. A permutation of the live rows needs <see cref="Effects.RowOrder"/>; dropping rows also
    /// needs <see cref="Effects.RowFilter"/>.
    /// </summary>
    public void SetOrder(ReadOnlySpan<int> newOrder)
    {
        Require(Effects.RowOrder, nameof(SetOrder));
        var drops = newOrder.Length < _state.Table.Rows.Count;
        if (drops)
        {
            Require(Effects.RowFilter, nameof(SetOrder) + " (dropping rows)");
        }

        _state.Table.Rows.Replace(newOrder, allowDrop: drops);
    }

    /// <summary>Appends rows. Requires <see cref="Effects.RowSource"/>.</summary>
    public int AppendRows(ReadOnlySpan<long> keys)
    {
        Require(Effects.RowSource, nameof(AppendRows));
        return _state.Table.AppendRows(keys);
    }

    /// <summary>Appends rows with string keys. Requires <see cref="Effects.RowSource"/>.</summary>
    public int AppendRows(IReadOnlyList<string> keys)
    {
        Require(Effects.RowSource, nameof(AppendRows));
        return _state.Table.AppendRows(keys);
    }

    /// <summary>Marks the result as Degraded (completed with partial or stale inputs, design 5.6).</summary>
    public void ReportDegraded(string reason) => DegradedReason ??= reason;

    /// <summary>Fails the node without throwing; the engine applies <c>on_failure</c>.</summary>
    public void Fail(string reason) => FailureReason ??= reason;

    private void Require(Effects effect, string operation)
    {
        if ((_effects & effect) == 0)
        {
            throw new InvalidOperationException(
                $"Node '{NodeId}' called {operation}, which needs the {effect} effect, but its operator does not declare it.");
        }
    }
}
