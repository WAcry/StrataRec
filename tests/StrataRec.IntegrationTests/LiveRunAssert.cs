using StrataRec.Hosting;
using StrataRec.Pipeline.Execution;

namespace StrataRec.IntegrationTests;

/// <summary>
/// Asserts that two live runs, whose results a test compares, took the same decisions at every node. Call it before
/// comparing the results: when one run fell back, skipped or failed where the other did not (a budget hit on a slow
/// machine, say), the failure names the node and why, e.g. <c>ctr_ranker: Succeeded → Degraded by ctr_backup (timed out
/// after 50 ms; ...)</c>, instead of showing a truncated diff of scores.
/// </summary>
internal static class LiveRunAssert
{
    /// <param name="except">Nodes where the runs differ by design.</param>
    public static void SameNodeOutcomes(PipelineResult expected, PipelineResult actual, params string[] except) =>
        Same(Describe(expected.Outcomes), Describe(actual.Outcomes), except);

    /// <summary>The same for engine results, from their trace or, for a recorded request, from the recording.</summary>
    /// <inheritdoc cref="SameNodeOutcomes(PipelineResult, PipelineResult, string[])"/>
    public static void SameNodeOutcomes(RecResult expected, RecResult actual, params string[] except) =>
        Same(Describe(expected), Describe(actual), except);

    private static List<(string Node, string Outcome)> Describe(IReadOnlyList<NodeOutcome> outcomes) =>
        outcomes.Select(o => (o.NodeId, Outcome(o.Kind.ToString(), o.NodeId, o.ProducedBy, o.Detail))).ToList();

    /// <summary>
    /// The decisions of an engine result: which pipeline served it, and the outcome of every node of that pipeline's run.
    /// A trace carries only the served run, so a recording is compared by its served run too; a request whose primary
    /// pipeline failed differs from one it served in the first entry (review 15).
    /// </summary>
    private static List<(string Node, string Outcome)> Describe(RecResult result)
    {
        var served = new List<(string Node, string Outcome)> { ("(served by)", result.ServedBy) };
        if (result.Trace is { } trace)
        {
            served.AddRange(Describe(trace));
            return served;
        }

        Assert.True(result.Recording is not null, "compare the node outcomes of a request that traces (DebugOptions.Trace) or records");
        var run = result.Recording.Runs.Last(r => r.PipelineId == result.ServedBy);
        served.AddRange(run.Nodes.Select(n => (n.Node, Outcome(Kind(n.Outcome), n.Node, n.ProducedBy, n.Detail))));
        return served;
    }

    /// <summary>A recording keeps the outcome category (<c>fallback</c>, <c>skipped_when</c>); a trace reports its kind. The
    /// engine's own mapping makes the two comparable; the detail and the producer still tell the categories apart.</summary>
    private static string Kind(string category)
    {
        Assert.True(PlanTelemetry.TryParseCategory(category, out var parsed), $"unknown outcome category '{category}' in the recording");
        return PlanTelemetry.Kind(parsed).ToString();
    }

    private static string Outcome(string kind, string node, string? producedBy, string? detail) =>
        kind + (producedBy is null || producedBy == node ? string.Empty : " by " + producedBy) + (detail is null ? string.Empty : $" ({detail})");

    private static void Same(List<(string Node, string Outcome)> expected, List<(string Node, string Outcome)> actual, string[] except)
    {
        expected = expected.Where(o => !except.Contains(o.Node)).ToList();
        actual = actual.Where(o => !except.Contains(o.Node)).ToList();
        if (expected.SequenceEqual(actual))
        {
            return;
        }

        var nodes = expected.Select(o => o.Node).Concat(actual.Select(o => o.Node)).Distinct().ToList();
        var differences = nodes
            .Select(node => (node, Expected: Find(expected, node), Actual: Find(actual, node)))
            .Where(d => d.Expected != d.Actual)
            .Select(d => $"  {d.node}: {d.Expected} → {d.Actual}")
            .ToList();
        if (differences.Count == 0)
        {
            differences.Add("  the same outcomes in another order:\n    " + string.Join(", ", expected.Select(o => o.Node)) + "\n    " +
                string.Join(", ", actual.Select(o => o.Node)));
        }

        Assert.Fail("The runs took different decisions, so their results do not compare (did a budget run out on a slow machine?):\n" +
            string.Join("\n", differences));
    }

    private static string Find(List<(string Node, string Outcome)> outcomes, string node) =>
        outcomes.Where(o => o.Node == node).Select(o => o.Outcome).FirstOrDefault() ?? "(no outcome)";
}
