namespace StrataRec.Pipeline.Execution;

/// <summary>What happened at one timeline position (design 5.6).</summary>
/// <param name="NodeId">Primary node id at the position.</param>
/// <param name="Kind">Final outcome of the position.</param>
/// <param name="Detail">Reason for skip/failure/degradation.</param>
/// <param name="ProducedBy">Node that produced the outputs (the fallback when it ran), or null.</param>
/// <param name="DurationMs">Wall time spent at the position.</param>
/// <param name="RowsIn">Live rows before the node.</param>
/// <param name="RowsOut">Live rows after the node.</param>
public readonly record struct NodeOutcome(string NodeId, NodeOutcomeKind Kind, string? Detail, string? ProducedBy, double DurationMs, int RowsIn, int RowsOut);

/// <summary>
/// What happened at one timeline position (design 5.6). One vocabulary everywhere a node outcome appears: results and
/// traces, the <c>stratarec.outcome</c> tag of metrics and spans, log envelopes, recordings and replay reports, all spelled
/// by <see cref="NodeOutcomeNames.Name"/>. It used to be four: these eight categories for metrics and recordings, four
/// coarser kinds in results and traces, PascalCase kinds in log envelopes with a fallback written as two entries, and a
/// mapping between them that every comparison had to go through.
/// </summary>
public enum NodeOutcomeKind : byte
{
    /// <summary>The node ran and committed its outputs.</summary>
    Succeeded = 1,

    /// <summary>The node committed its outputs from partial or stale inputs; the detail says which (design 5.6).</summary>
    Degraded,

    /// <summary>The node failed and its fallback node served (<see cref="NodeOutcome.ProducedBy"/>); the detail has both.</summary>
    Fallback,

    /// <summary><c>when</c> was false.</summary>
    SkippedWhen,

    /// <summary>Less than <c>min_remaining_ms</c> was left.</summary>
    SkippedDeadline,

    /// <summary>A column the node reads was not produced, or a non-nullable request column it reads not written.</summary>
    SkippedInput,

    /// <summary>The node failed, and was skipped (<c>on_failure: skip</c>) or failed the request.</summary>
    Failed,

    /// <summary>The request was cancelled by its caller, or hit a defect, while the node was running.</summary>
    Aborted,
}

/// <summary>The snake_case names of <see cref="NodeOutcomeKind"/>, as JSON, metrics, logs and recordings spell them.</summary>
public static class NodeOutcomeNames
{
    private static readonly string[] Names =
        { string.Empty, "succeeded", "degraded", "fallback", "skipped_when", "skipped_deadline", "skipped_input", "failed", "aborted" };

    /// <summary>The highest value of <see cref="NodeOutcomeKind"/>, for tables indexed by it.</summary>
    internal const int MaxKind = (int)NodeOutcomeKind.Aborted;

    /// <summary>The name of <paramref name="kind"/>, e.g. <c>skipped_when</c>.</summary>
    public static string Name(NodeOutcomeKind kind) => kind is >= NodeOutcomeKind.Succeeded and <= NodeOutcomeKind.Aborted
        ? Names[(int)kind]
        : throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a node outcome");

    /// <summary>The kind a <see cref="Name"/> spells (a recording's node outcome); false for an unknown name.</summary>
    public static bool TryParse(string name, out NodeOutcomeKind kind)
    {
        for (var i = 1; i < Names.Length; i++)
        {
            if (string.Equals(Names[i], name, StringComparison.Ordinal))
            {
                kind = (NodeOutcomeKind)i;
                return true;
            }
        }

        kind = default;
        return false;
    }

    /// <summary>True for the kinds of a node that did not run: <c>skipped_when</c>, <c>skipped_deadline</c>, <c>skipped_input</c>.</summary>
    public static bool IsSkipped(NodeOutcomeKind kind) => kind is NodeOutcomeKind.SkippedWhen or NodeOutcomeKind.SkippedDeadline or NodeOutcomeKind.SkippedInput;
}
