using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Tables;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Pipeline.Execution;

/// <summary>
/// Records, for the candidates a request asks to explain, their state after every node (design 13.3): whether they are
/// in the table and live, their rank, and the values of the columns the node read and wrote, plus what an
/// <see cref="IExplainableOperator"/> adds. Exists only for such requests; the executor holds none otherwise.
/// </summary>
/// <remarks>
/// <para>Generic: everything comes from what the executor already knows of every node (its declared reads and writes,
/// the row selection before and after), so custom operators are explained without code of their own.</para>
/// <para>Cost is per target, not per row: a target's row is found by key (<see cref="CandidateTable.FindRow"/>) once,
/// after the row source that appends it, and followed through compactions (<see cref="Compacted"/>) without looking it up
/// again. The one exception is the rank: the ranking has no inverse, so after a node that changes the row set or the order
/// one pass over the live rows, which stops at the last live target, finds every target's position.</para>
/// <para>An attempt is observed before it commits (<see cref="Observe"/>, where the operator explains itself, seeing the
/// columns it read) and recorded after (<see cref="Ran"/>, reading the values it wrote); a failed attempt's observation is
/// dropped, and its step says it was rolled back, with the state from before it, which is what the table is again.</para>
/// </remarks>
internal sealed class CandidateExplainer
{
    private readonly EntityKey[] _keys;
    private readonly List<ExplainStep>[] _steps;

    // The state after the last recorded step: physical row (-1 when not in the table), 1-based rank (0 when not live),
    // whether the target ever entered the table, and the node that last dropped it.
    private readonly int[] _rows;
    private readonly int[] _ranks;
    private readonly bool[] _present;
    private readonly string?[] _droppedBy;

    // The observed attempt's state, until it is recorded or dropped.
    private readonly int[] _pendingRows;
    private readonly int[] _pendingRanks;
    private readonly string?[] _reasons;
    private readonly List<ExplainValue>?[] _terms;
    private bool _observed;

    private ulong[]? _marks; // target rows, set during one pass over the ranking
    private List<ColumnBuffer>? _scratch;

    public CandidateExplainer(IReadOnlyList<EntityKey> ids)
    {
        var distinct = new List<EntityKey>(ids.Count);
        var seen = new HashSet<EntityKey>();
        for (var i = 0; i < ids.Count; i++)
        {
            if (seen.Add(ids[i]))
            {
                distinct.Add(ids[i]);
            }
        }

        _keys = distinct.ToArray();
        var n = _keys.Length;
        _steps = new List<ExplainStep>[n];
        for (var t = 0; t < n; t++)
        {
            _steps[t] = new List<ExplainStep>();
        }

        _rows = new int[n];
        _pendingRows = new int[n];
        Array.Fill(_rows, -1);
        _ranks = new int[n];
        _pendingRanks = new int[n];
        _present = new bool[n];
        _droppedBy = new string?[n];
        _reasons = new string?[n];
        _terms = new List<ExplainValue>?[n];
    }

    public int Count => _keys.Length;

    public EntityKey Key(int target) => _keys[target];

    public int RankBefore(int target) => _ranks[target];

    public int PendingRow(int target) => _pendingRows[target];

    public int PendingRank(int target) => _pendingRanks[target];

    /// <summary>A node that did not run: every target is as it was.</summary>
    public void Skipped(string nodeId, string? detail) => Unchanged(nodeId, ExplainStepKind.Skipped, detail);

    /// <summary>A failed attempt: the table is back where it was before it (design 4.6), and so is every target.</summary>
    public void RolledBack(string nodeId, string? detail)
    {
        _observed = false;
        ClearOperatorNotes();
        Unchanged(nodeId, ExplainStepKind.RolledBack, detail);
    }

    /// <summary>
    /// Takes the state an attempt left before it is committed: finds the targets a row source appended, their liveness and
    /// ranks, and lets the operator explain itself.
    /// </summary>
    public void Observe(RequestState state, CompiledNode node)
    {
        var table = state.Table;
        var rows = table.Rows;
        var appends = (node.Effects & Effects.RowSource) != 0;
        for (var t = 0; t < _keys.Length; t++)
        {
            var row = _rows[t];
            if (row < 0 && appends)
            {
                row = table.FindRow(_keys[t]); // a key of the other key type is never found
            }

            _pendingRows[t] = row;
            _pendingRanks[t] = row >= 0 && rows.IsAlive(row) ? _ranks[t] : 0;
        }

        if (node.HasRowEffects)
        {
            Rank(table);
        }

        _observed = true;
        ClearOperatorNotes();
        if (node.Operator is not IExplainableOperator explainable || !SawAny())
        {
            return;
        }

        try
        {
            explainable.Explain(new ExplainContext(state, this, node.NodeId));
        }
        catch (Exception ex)
        {
            // Explaining never changes the request: the reason says the explanation is incomplete.
            ClearOperatorNotes();
            for (var t = 0; t < _keys.Length; t++)
            {
                _reasons[t] = $"the operator could not explain itself: {ex.GetType().Name}: {ex.Message}";
            }
        }
    }

    /// <summary>Records a committed attempt observed by <see cref="Observe"/>, with the values of the columns the node read and wrote.</summary>
    public void Ran(RequestState state, CompiledNode node)
    {
        if (!_observed)
        {
            throw new InvalidOperationException($"Node '{node.NodeId}' was recorded without being observed.");
        }

        _observed = false;
        for (var t = 0; t < _keys.Length; t++)
        {
            var before = _ranks[t];
            var after = _pendingRanks[t];
            var row = _pendingRows[t];
            var change = Change(before > 0, after > 0);
            _present[t] |= row >= 0;
            if (change == ExplainChange.Dropped)
            {
                _droppedBy[t] = node.NodeId;
            }

            _steps[t].Add(new ExplainStep
            {
                Node = node.NodeId,
                Kind = ExplainStepKind.Ran,
                Change = change,
                Present = _present[t],
                RankBefore = before > 0 ? before : null,
                Rank = after > 0 ? after : null,
                Values = row >= 0 && (before > 0 || after > 0) ? Values(state, node, row) : Array.Empty<ExplainValue>(),
                Reason = _reasons[t],
                Terms = (IReadOnlyList<ExplainValue>?)_terms[t] ?? Array.Empty<ExplainValue>(),
            });
            _rows[t] = row;
            _ranks[t] = after;
        }

        ClearOperatorNotes();
    }

    /// <summary>
    /// The table was compacted (design 4.5): live rows moved to their rank's position, dropped rows were released. A
    /// released target stays present and dropped; a later row source may append its key again.
    /// </summary>
    public void Compacted()
    {
        for (var t = 0; t < _keys.Length; t++)
        {
            if (_rows[t] >= 0)
            {
                _rows[t] = _ranks[t] > 0 ? _ranks[t] - 1 : -1;
            }
        }
    }

    /// <summary>The explanations of the run: <paramref name="returned"/> items were returned when it <paramref name="succeeded"/>.</summary>
    public RunExplanation Build(string pipelineId, bool succeeded, int returned)
    {
        var candidates = new CandidateExplanation[_keys.Length];
        for (var t = 0; t < _keys.Length; t++)
        {
            var rank = _ranks[t];
            var verdict = !succeeded ? ExplainVerdict.NoResult
                : !_present[t] ? ExplainVerdict.NotRecalled
                : rank == 0 ? ExplainVerdict.Dropped
                : rank <= returned ? ExplainVerdict.Returned
                : ExplainVerdict.BeyondLimit;
            candidates[t] = new CandidateExplanation
            {
                Id = _keys[t],
                Verdict = verdict,
                Rank = verdict is ExplainVerdict.Returned or ExplainVerdict.BeyondLimit ? rank : null,
                DroppedBy = verdict == ExplainVerdict.Dropped ? _droppedBy[t] : null,
                Steps = _steps[t],
            };
        }

        return new RunExplanation { PipelineId = pipelineId, Candidates = candidates };
    }

    /// <summary>A buffer for evaluating an expression on target rows, one per result type per request (the capacity never grows).</summary>
    internal ColumnBuffer Scratch(ColumnType type, CandidateTable table)
    {
        _scratch ??= new List<ColumnBuffer>();
        foreach (var buffer in _scratch)
        {
            if (buffer.Type == type)
            {
                return buffer;
            }
        }

        var created = ColumnBuffer.Create(type, Math.Max(1, table.StorageCapacity), table.Memory);
        _scratch.Add(created);
        return created;
    }

    internal void SetReason(int target, string reason) => _reasons[target] = reason;

    internal void AddTerm(int target, string name, in ScalarValue value) => (_terms[target] ??= new List<ExplainValue>()).Add(new ExplainValue(name, value));

    private void Unchanged(string nodeId, ExplainStepKind kind, string? detail)
    {
        for (var t = 0; t < _keys.Length; t++)
        {
            var rank = _ranks[t];
            _steps[t].Add(new ExplainStep
            {
                Node = nodeId,
                Kind = kind,
                Detail = detail,
                Change = rank > 0 ? ExplainChange.Stayed : ExplainChange.Absent,
                Present = _present[t],
                RankBefore = rank > 0 ? rank : null,
                Rank = rank > 0 ? rank : null,
            });
        }
    }

    /// <summary>
    /// Sets the pending rank of every live target: one pass over the ranking, testing a bit per row and stopping at the
    /// last live target, so the work is the targets' depth in the ranking, at most the live rows.
    /// </summary>
    private void Rank(CandidateTable table)
    {
        var pending = 0;
        for (var t = 0; t < _keys.Length; t++)
        {
            var row = _pendingRows[t];
            _pendingRanks[t] = 0;
            if (row >= 0 && table.Rows.IsAlive(row))
            {
                // Rented at the first use: the table's storage capacity never grows, so every later row fits.
                Bits.Set(_marks ??= table.Memory.RentCleared<ulong>(Bits.WordCount(Math.Max(1, table.StorageCapacity))), row);
                pending++;
            }
        }

        var order = table.Rows.Order;
        for (var i = 0; i < order.Length && pending > 0; i++)
        {
            var row = order[i];
            if (!Bits.Get(_marks!, row))
            {
                continue;
            }

            Bits.Clear(_marks!, row);
            pending--;
            for (var t = 0; t < _keys.Length; t++)
            {
                if (_pendingRows[t] == row)
                {
                    _pendingRanks[t] = i + 1;
                }
            }
        }
    }

    private bool SawAny()
    {
        for (var t = 0; t < _keys.Length; t++)
        {
            if (_ranks[t] > 0 || _pendingRanks[t] > 0)
            {
                return true;
            }
        }

        return false;
    }

    private void ClearOperatorNotes()
    {
        Array.Clear(_reasons);
        Array.Clear(_terms);
    }

    private static ExplainChange Change(bool before, bool after) => (before, after) switch
    {
        (false, false) => ExplainChange.Absent,
        (false, true) => ExplainChange.Entered,
        (true, true) => ExplainChange.Stayed,
        (true, false) => ExplainChange.Dropped,
    };

    /// <summary>
    /// The row's values, after the node, of the columns it read (but the entity id) and then those it wrote that it did not
    /// read. A column resolves as a later reader would see it: a feature by its slot, an output by its committed version.
    /// </summary>
    private static ExplainValue[] Values(RequestState state, CompiledNode node, int row)
    {
        var columns = state.Plan.Schema.Columns;
        var values = new List<ExplainValue>(node.ReadColumns.Length + node.Outputs.Length);
        foreach (var logical in node.ReadColumns)
        {
            if (columns[logical].Kind != ColumnKind.EntityId)
            {
                values.Add(new ExplainValue(columns[logical].Name, ValueOf(state, columns[logical], row)));
            }
        }

        foreach (var output in node.Outputs)
        {
            if (Array.IndexOf(node.ReadColumns, output.LogicalId) < 0)
            {
                values.Add(new ExplainValue(output.Name, ValueOf(state, columns[output.LogicalId], row)));
            }
        }

        return values.ToArray();
    }

    private static ScalarValue ValueOf(RequestState state, LogicalColumn column, int row)
    {
        var slot = column.FixedSlot >= 0 ? column.FixedSlot : state.Bindings.Resolve(column.Id);
        return slot >= 0 && state.Table.TryGetSlot(slot) is { } buffer && row < buffer.Capacity ? ColumnValues.Get(buffer, row) : ScalarValue.Missing;
    }
}
