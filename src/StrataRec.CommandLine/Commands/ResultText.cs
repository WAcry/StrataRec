using System.Globalization;
using StrataRec.Experiments.Assignment;
using StrataRec.Hosting;
using StrataRec.Pipeline.Execution;

namespace StrataRec.CommandLine.Commands;

/// <summary>
/// A result as lines for people (<c>stratarec run --format text</c>), laid out as <c>replay</c>'s report: how the request
/// ended, the arms, one line per item with its output columns, the output's request-level values, and what the request asked for (the trace, the column
/// producers, the explained candidates). The JSON document (<see cref="RecResultJson"/>) stays the default output: it is
/// what a service answers, exactly.
/// </summary>
internal static class ResultText
{
    /// <summary>Labels take this many characters, as in <c>replay</c>'s report; continuation lines are indented past them.</summary>
    private const string Continued = "            ";

    public static void Write(TextWriter output, RecResult result)
    {
        // run executes one request in a new process: its times include compiling the code it runs for the first time,
        // which a service pays once. Said where the times are, so they are not read as what a request costs.
        var elapsed = result.ElapsedMs.ToString("0.###", CultureInfo.InvariantCulture) + " ms (the process's first request: times include JIT)";
        output.WriteLine(result.Succeeded
            ? $"request {result.RequestId}: succeeded, served by {result.ServedBy}, {elapsed}"
            : $"request {result.RequestId}: failed ({RecResultJson.Name(result.ErrorKind)}" +
              (result.ErrorPointer is { } pointer ? $" at {pointer}" : string.Empty) + $"), {elapsed}");
        if (result.Error is { } error)
        {
            // A request served by its fallback pipeline succeeded, and the error says why the primary one did not serve.
            output.WriteLine($"  {(result.Succeeded ? "degraded" : "error"),-9} {error}");
        }

        output.WriteLine($"  manifest  {result.ManifestId}");
        var experiments = result.Experiments;
        output.WriteLine($"  arms      {(experiments.Assigned.Count == 0 ? "none (the baseline)" : string.Join(", ", experiments.Assigned))}" +
                         (experiments.Triggered.Count == 0 ? string.Empty : $"; triggered {string.Join(", ", experiments.Triggered)}"));
        if (experiments.Ignored.Count > 0)
        {
            var ignored = new List<string>(experiments.Ignored.Count);
            foreach (var arm in experiments.Ignored)
            {
                ignored.Add($"{arm.Id} ({IgnoredArm.Name(arm.Reason)})");
            }

            output.WriteLine($"  ignored   {string.Join(", ", ignored)}");
        }

        WriteItems(output, result);
        if (result.Values.Count > 0)
        {
            var values = new List<string>(result.Values.Count);
            foreach (var value in result.Values.All)
            {
                values.Add($"{value.Name} {value.Value}");
            }

            output.WriteLine($"  values    {string.Join(", ", values)}");
        }

        if (result.Trace is { } trace)
        {
            for (var i = 0; i < trace.Count; i++)
            {
                var node = trace[i];
                var detail = node.Detail is null ? string.Empty : $" ({node.Detail})";
                var producer = node.ProducedBy is { } by && by != node.NodeId ? $", produced by {by}" : string.Empty;
                output.WriteLine($"{(i == 0 ? "  trace     " : Continued)}{node.NodeId}: {NodeOutcomeNames.Name(node.Kind)}{detail}{producer}, " +
                                 $"{node.DurationMs.ToString("0.###", CultureInfo.InvariantCulture)} ms, {node.RowsIn} -> {node.RowsOut} rows");
            }
        }

        if (result.Calls is { } calls)
        {
            // Each run of a pipeline a node called, under who called it, with its own nodes.
            foreach (var call in calls)
            {
                var how = call switch
                {
                    { Cached: { Stale: true } stale } => $"failed ({call.Error}), served the cached result {Computed(stale)}, past its ttl_ms",
                    { Cached: { } origin } => $"not run, served the cached result {Computed(origin)}",
                    { Succeeded: true } => "succeeded",
                    _ => $"failed ({call.Error})",
                };
                var arms = call.Arms.Count == 0 ? string.Empty : $", arms {string.Join(", ", call.Arms)}";
                output.WriteLine($"  call      {call.CalledBy} -> {call.PipelineId}: {how}, " +
                                 $"{call.ElapsedMs.ToString("0.###", CultureInfo.InvariantCulture)} ms{arms}");
                foreach (var node in call.Trace)
                {
                    var detail = node.Detail is null ? string.Empty : $" ({node.Detail})";
                    output.WriteLine($"{Continued}  {node.NodeId}: {NodeOutcomeNames.Name(node.Kind)}{detail}, " +
                                     $"{node.DurationMs.ToString("0.###", CultureInfo.InvariantCulture)} ms, {node.RowsIn} -> {node.RowsOut} rows");
                }
            }
        }

        if (result.Trace is not null && result.ColumnProducers is { Count: > 0 } producers)
        {
            var pairs = new List<string>(producers.Count);
            foreach (var (column, node) in producers)
            {
                pairs.Add($"{column} <- {node}");
            }

            output.WriteLine($"  columns   {string.Join(", ", pairs)}");
        }

        if (result.LogBatches is { } batches)
        {
            output.WriteLine($"  logs      {Output.Count(batches.Count, "batch", "batches")} captured; --format json prints them");
        }

        if (result.Explanation is { } explanation)
        {
            ExplanationText.Write(output, explanation, "  explain   ");
        }
    }

    /// <summary>Which run computed a cached result: the request, the call that ran the pipeline in it, and when.</summary>
    private static string Computed(StrataRec.Pipeline.Operators.CachedFrom from) =>
        $"of request {from.RequestId} ({from.CalledBy}, {DateTimeOffset.FromUnixTimeMilliseconds(from.TimeMs).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)})";

    private static void WriteItems(TextWriter output, RecResult result)
    {
        if (result.Items.Count == 0)
        {
            output.WriteLine("  items     none");
            return;
        }

        // Ranks right-aligned and ids left-aligned to their widest, so the columns of every item start together.
        var rankWidth = result.Items[^1].Rank.ToString(CultureInfo.InvariantCulture).Length;
        var idWidth = 0;
        foreach (var item in result.Items)
        {
            idWidth = Math.Max(idWidth, item.Id.ToString().Length);
        }

        for (var i = 0; i < result.Items.Count; i++)
        {
            var item = result.Items[i];
            var columns = new List<string>(item.Columns.Count);
            foreach (var (name, value) in item.Columns)
            {
                columns.Add($"{name} {value}");
            }

            var rank = item.Rank.ToString(CultureInfo.InvariantCulture).PadLeft(rankWidth);
            output.WriteLine($"{(i == 0 ? "  items     " : Continued)}{rank}. {item.Id.ToString().PadRight(idWidth)}  {string.Join(", ", columns)}".TrimEnd());
        }
    }
}
