using StrataRec.Hosting;
using StrataRec.Pipeline.Execution;

namespace StrataRec.IntegrationTests;

/// <summary>
/// Call it before comparing the results: when one run fell back, skipped or failed where the other did not, the failure
/// names the node and why, instead of showing a truncated diff of scores.
/// </summary>
internal static class LiveRunAssert
{
    public static void SameNodeOutcomes(PipelineResult expected, PipelineResult actual, params string[] except) =>
        Same(Describe(expected.Outcomes), Describe(actual.Outcomes), except);

    public static void SameNodeOutcomes(RecResult expected, RecResult actual, params string[] except) =>
        Same(Describe(expected), Describe(actual), except);

    private static List<(string Node, string Outcome)> Describe(IReadOnlyList<NodeOutcome> outcomes) =>
        outcomes.Select(o => (o.NodeId, Outcome(NodeOutcomeNames.Name(o.Kind), o.NodeId, o.ProducedBy, o.Detail))).ToList();

    /// <summary>
    /// A trace carries only the served run, so a recording is compared by its served run too.
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
        served.AddRange(run.Nodes.Select(n => (n.Node, Outcome(Known(n.Outcome), n.Node, n.ProducedBy, n.Detail))));
        return served;
    }

    private static string Known(string outcome)
    {
        Assert.True(NodeOutcomeNames.TryParse(outcome, out _), $"unknown node outcome '{outcome}' in the recording");
        return outcome;
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
