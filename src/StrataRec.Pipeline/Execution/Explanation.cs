using System.Globalization;
using StrataRec.Core.Keys;
using StrataRec.Core.Values;

namespace StrataRec.Pipeline.Execution;

/// <summary>What one node did with an explained candidate (design 13.3).</summary>
public enum ExplainStepKind : byte
{
    /// <summary>The node ran and succeeded; the step has the candidate's state after it.</summary>
    Ran = 1,

    /// <summary>
    /// The node's attempt failed (<see cref="ExplainStep.Detail"/> says why), so the table is as it was before the attempt
    /// (design 4.6): nothing it did to the candidate stays. A fallback that then ran has a step of its own.
    /// </summary>
    RolledBack,

    /// <summary>The node did not run (<see cref="ExplainStep.Detail"/> says why: <c>when</c>, time, a missing input).</summary>
    Skipped,
}

/// <summary>How a step changed an explained candidate's membership of the live rows.</summary>
public enum ExplainChange : byte
{
    /// <summary>Not live before the node and not after (never recalled yet, or dropped earlier).</summary>
    Absent = 1,

    /// <summary>The node appended it (a row source).</summary>
    Entered,

    /// <summary>Live before and after; its rank may have changed.</summary>
    Stayed,

    /// <summary>The node dropped it (a filter or a truncation).</summary>
    Dropped,
}

/// <summary>Where an explained candidate ended (design 13.3), so "why is it not in the result" has a one-word answer.</summary>
public enum ExplainVerdict : byte
{
    /// <summary>In the result, at <see cref="CandidateExplanation.Rank"/>.</summary>
    Returned = 1,

    /// <summary>Live after the last node, at <see cref="CandidateExplanation.Rank"/>, but past the output's limit.</summary>
    BeyondLimit,

    /// <summary>A node dropped it: <see cref="CandidateExplanation.DroppedBy"/>.</summary>
    Dropped,

    /// <summary>No row source appended it.</summary>
    NotRecalled,

    /// <summary>The run failed, so there is no result to be in; the steps show what happened up to the failure.</summary>
    NoResult,
}

/// <summary>A named value in an explanation: a column's value for the candidate, or a term an operator reports.</summary>
public readonly record struct ExplainValue(string Name, ScalarValue Value);

/// <summary>An explained candidate's state after one node (design 13.3 items 1–3).</summary>
public sealed class ExplainStep
{
    /// <summary>The node that ran (or was skipped): a position's node, or the fallback node that ran in its place.</summary>
    public required string Node { get; init; }

    public required ExplainStepKind Kind { get; init; }

    /// <summary>Why the node was skipped or its attempt rolled back; null when it ran.</summary>
    public string? Detail { get; init; }

    public required ExplainChange Change { get; init; }

    /// <summary>The candidate has entered the table by now (a row source appended it), whether or not it is still live.</summary>
    public required bool Present { get; init; }

    /// <summary>Its 1-based position among the live rows before the node; null when it was not live.</summary>
    public int? RankBefore { get; init; }

    /// <summary>Its 1-based position among the live rows after the node; null when it is not live.</summary>
    public int? Rank { get; init; }

    public bool Alive => Rank is not null;

    /// <summary>
    /// The candidate's values, after the node, of the columns the node read and wrote (the entity id left out), for a node
    /// that ran and saw or produced the candidate; empty otherwise. Read from what the executor knows of every node, so a
    /// custom operator is explained too.
    /// </summary>
    public IReadOnlyList<ExplainValue> Values { get; init; } = Array.Empty<ExplainValue>();

    /// <summary>The operator's own account of its decision, when it gives one (<see cref="Operators.IExplainableOperator"/>).</summary>
    public string? Reason { get; init; }

    /// <summary>Named values the operator's decision rests on (a threshold, a cut, the terms of a score); empty when it gives none.</summary>
    public IReadOnlyList<ExplainValue> Terms { get; init; } = Array.Empty<ExplainValue>();
}

/// <summary>One explained candidate: where it ended, and its state after every node of the run.</summary>
public sealed class CandidateExplanation
{
    public required EntityKey Id { get; init; }

    public required ExplainVerdict Verdict { get; init; }

    /// <summary>Its rank in the result (<see cref="ExplainVerdict.Returned"/>) or among the live rows past the limit (<see cref="ExplainVerdict.BeyondLimit"/>).</summary>
    public int? Rank { get; init; }

    /// <summary>The node that dropped it (<see cref="ExplainVerdict.Dropped"/>).</summary>
    public string? DroppedBy { get; init; }

    /// <summary>One step per node that ran, was rolled back or was skipped, in execution order.</summary>
    public required IReadOnlyList<ExplainStep> Steps { get; init; }
}

/// <summary>The explanations of one run, in the order the ids were asked for (each id once).</summary>
public sealed class RunExplanation
{
    /// <summary>The pipeline whose run is explained: the requested one, or the fallback pipeline that served (design 5.6).</summary>
    public required string PipelineId { get; init; }

    public required IReadOnlyList<CandidateExplanation> Candidates { get; init; }
}

/// <summary>
/// The rules for the ids a request asks to explain (<see cref="PipelineRequest.ExplainIds"/>), in one place for the engine,
/// which rejects a request that breaks them before anything runs, and for the tools that parse ids from text.
/// </summary>
public static class ExplainTargets
{
    /// <summary>
    /// The most ids one request explains. Explaining costs in proportion to targets × nodes, and every step is in the
    /// result: the bound keeps an explained request a debugging tool and not a second, unbounded result. A constant, so a
    /// caller knows it without asking a loaded plan.
    /// </summary>
    public const int MaxCount = 100;

    /// <summary>
    /// Null when <paramref name="ids"/> can be explained on a pipeline keyed by <paramref name="keyType"/>; otherwise what is
    /// wrong and how to fix it. Repeated ids are allowed (each is explained once).
    /// </summary>
    public static string? Check(IReadOnlyList<EntityKey> ids, EntityKeyType keyType, string pipelineId)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (TooMany(ids.Count) is { } tooMany)
        {
            return tooMany;
        }

        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            if (id.IsString != (keyType == EntityKeyType.String))
            {
                return $"explain id {Describe(id)} is {(id.IsString ? "a string" : "an int64")}, but pipeline '{pipelineId}' has {Name(keyType)} entity ids; "
                    + $"give its ids as {Name(keyType)}s.";
            }
        }

        return null;
    }

    /// <summary>The bound's error for <paramref name="count"/> ids, or null within it; the executor checks only this (it does not know the caller's key types).</summary>
    internal static string? TooMany(int count) => count > MaxCount
        ? $"explain_ids has {count} ids, but a request explains at most {MaxCount} candidates; ask about fewer ids per request."
        : null;

    /// <summary>Reads an id as a pipeline keyed by <paramref name="keyType"/> has them: an int64 in invariant digits, or any string.</summary>
    public static bool TryParse(string text, EntityKeyType keyType, out EntityKey key)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (keyType == EntityKeyType.String)
        {
            key = EntityKey.FromString(text);
            return true;
        }

        var ok = long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value);
        key = ok ? EntityKey.FromInt64(value) : default;
        return ok;
    }

    /// <summary><c>int64</c> or <c>string</c>, as <c>entity_key_type</c> names them.</summary>
    public static string Name(EntityKeyType keyType) => keyType == EntityKeyType.String ? "string" : "int64";

    private static string Describe(EntityKey id) => id.IsString ? "'" + id.StringValue + "'" : id.Int64Value.ToString(CultureInfo.InvariantCulture);
}
