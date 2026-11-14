using System.Text;
using StrataRec.Pipeline.Execution;

namespace StrataRec.CommandLine.Commands;

/// <summary>
/// Explained candidates (design 13.3) as lines for people: where each ended, then one line per node with what the node did
/// to it, the values it read and wrote, and the operator's reason. The text formats print this; JSON has
/// <see cref="Hosting.RecResultJson.WriteExplanation"/>.
/// </summary>
internal static class ExplanationText
{
    /// <param name="prefix">Starts each candidate's line (e.g. the report's label column); its steps are indented past it.</param>
    public static void Write(TextWriter output, RunExplanation explanation, string prefix)
    {
        var indent = new string(' ', prefix.Length + 2);
        output.WriteLine($"{prefix}pipeline {explanation.PipelineId}");
        foreach (var candidate in explanation.Candidates)
        {
            output.WriteLine($"{prefix}{candidate.Id}: {Verdict(candidate)}");
            foreach (var step in candidate.Steps)
            {
                output.WriteLine(indent + Step(step));
            }
        }
    }

    private static string Verdict(CandidateExplanation candidate) => candidate.Verdict switch
    {
        ExplainVerdict.Returned => $"returned at rank {candidate.Rank}",
        ExplainVerdict.BeyondLimit => $"live at rank {candidate.Rank}, past the output limit",
        ExplainVerdict.Dropped => $"dropped by {candidate.DroppedBy}",
        ExplainVerdict.NotRecalled => "never recalled",
        _ => "no result: the run failed",
    };

    private static string Step(ExplainStep step)
    {
        var line = new StringBuilder(step.Node).Append(": ");
        switch (step.Kind)
        {
            case ExplainStepKind.Skipped:
                line.Append("skipped (").Append(step.Detail).Append("), ").Append(State(step));
                break;
            case ExplainStepKind.RolledBack:
                line.Append("rolled back (").Append(step.Detail).Append("), ").Append(State(step));
                break;
            default:
                line.Append(step.Change switch
                {
                    ExplainChange.Entered => $"entered at rank {step.Rank}",
                    ExplainChange.Dropped => $"dropped (was rank {step.RankBefore})",
                    ExplainChange.Stayed when step.Rank != step.RankBefore => $"rank {step.Rank} (was {step.RankBefore})",
                    _ => State(step),
                });
                break;
        }

        Append(line, "; ", step.Values);
        if (step.Reason is not null)
        {
            line.Append("; ").Append(step.Reason);
        }
        else
        {
            Append(line, "; ", step.Terms);
        }

        return line.ToString();
    }

    private static string State(ExplainStep step) =>
        step.Rank is { } rank ? $"rank {rank}" : step.Present ? "not live" : "not in the table";

    private static void Append(StringBuilder line, string separator, IReadOnlyList<ExplainValue> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            line.Append(i == 0 ? separator : ", ").Append(values[i].Name).Append(' ').Append(values[i].Value.ToString());
        }
    }
}
