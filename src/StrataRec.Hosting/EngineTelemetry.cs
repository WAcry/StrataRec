using System.Diagnostics;
using Microsoft.Extensions.Logging;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Diagnostics;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Hosting;

/// <summary>
/// Request-level metric tags of one served plan (design 13.1): the pipeline, and pipeline × arm for every arm of the
/// plan. Built once when the plan is loaded, so recording a request passes cached arrays.
/// </summary>
internal sealed class RequestTelemetry
{
    /// <summary>Requests for a pipeline the engine does not serve: no pipeline tag (the id is caller input, unbounded).</summary>
    public static readonly RequestTelemetry Unknown = new([], []);

    private RequestTelemetry(KeyValuePair<string, object?>[] pipeline, KeyValuePair<string, object?>[][] arms)
    {
        Pipeline = pipeline;
        Arms = arms;
    }

    public KeyValuePair<string, object?>[] Pipeline { get; }

    /// <summary>Indexed by global arm index.</summary>
    public KeyValuePair<string, object?>[][] Arms { get; }

    public static RequestTelemetry Create(ExecutionPlan plan)
    {
        var pipeline = new KeyValuePair<string, object?>(StrataRecTags.Pipeline, plan.PipelineId);
        var arms = new KeyValuePair<string, object?>[plan.Arms.Length][];
        for (var i = 0; i < arms.Length; i++)
        {
            arms[i] = [pipeline, new(StrataRecTags.Arm, plan.Arms[i].Id)];
        }

        return new RequestTelemetry([pipeline], arms);
    }

    /// <summary>Records one request once for its pipeline and once per assigned arm.</summary>
    public void Record(double seconds, in RequestArms arms, bool failed, bool empty, bool degraded)
    {
        Record(seconds, Pipeline, failed, empty, degraded, arm: false);
        for (var layer = 0; layer < arms.LayerCount; layer++)
        {
            var arm = arms.ArmIn(layer);
            if (arm >= 0 && arm < Arms.Length)
            {
                Record(seconds, Arms[arm], failed, empty, degraded, arm: true);
            }
        }
    }

    private static void Record(double seconds, KeyValuePair<string, object?>[] tags, bool failed, bool empty, bool degraded, bool arm)
    {
        (arm ? StrataRecMetrics.ArmRequestDuration : StrataRecMetrics.RequestDuration).Record(seconds, tags);
        if (failed)
        {
            (arm ? StrataRecMetrics.ArmRequestErrors : StrataRecMetrics.RequestErrors).Add(1, tags);
        }

        if (empty)
        {
            (arm ? StrataRecMetrics.ArmRequestEmpty : StrataRecMetrics.RequestEmpty).Add(1, tags);
        }

        if (degraded)
        {
            (arm ? StrataRecMetrics.ArmRequestDegraded : StrataRecMetrics.RequestDegraded).Add(1, tags);
        }
    }

    /// <summary>Completes the request span with the request's outcome; tags are only built for recorded spans.</summary>
    public static void EndSpan(Activity activity, RecResult? result, bool degraded)
    {
        if (activity.IsAllDataRequested)
        {
            if (result is null)
            {
                activity.SetStatus(ActivityStatusCode.Error, "aborted");
            }
            else
            {
                activity.SetTag(StrataRecTags.ServedBy, result.ServedBy);
                activity.SetTag(StrataRecTags.ManifestId, result.ManifestId);
                activity.SetTag(StrataRecTags.Items, result.Items.Count);
                activity.SetTag(StrataRecTags.Degraded, degraded);
                activity.SetTag(StrataRecTags.ArmsAssigned, ToArray(result.Experiments.Assigned));
                activity.SetTag(StrataRecTags.ExperimentsTriggered, ToArray(result.Experiments.Triggered));
                if (!result.Succeeded)
                {
                    activity.SetStatus(ActivityStatusCode.Error, result.Error);
                }
            }
        }

        activity.Dispose();
    }

    /// <summary>Exporters recognize arrays (not lists) as array attributes.</summary>
    private static string[] ToArray(IReadOnlyList<string> values)
    {
        var array = new string[values.Count];
        for (var i = 0; i < array.Length; i++)
        {
            array[i] = values[i];
        }

        return array;
    }
}

/// <summary>Plan lifecycle metrics and event logs (design 13.1 "plan", 13.2): loads are rare, so tags are built per load.</summary>
internal static partial class PlanLoadTelemetry
{
    public static void Applied(ExecutionPlan plan, long sequence)
    {
        var pipeline = new KeyValuePair<string, object?>(StrataRecTags.Pipeline, plan.PipelineId);
        StrataRecMetrics.PlanLoads.Add(1, pipeline, new KeyValuePair<string, object?>(StrataRecTags.Outcome, "applied"));
        StrataRecMetrics.PlanVariants.Record(plan.Nodes.Length, pipeline);
        StrataRecMetrics.PlanSequence.Record(sequence, pipeline);
    }

    public static void LogApplied(ILogger logger, ExecutionPlan plan, long sequence) =>
        LogApplied(logger, plan.PipelineId, plan.ManifestId, sequence, plan.Nodes.Length, Math.Round(plan.CompileTime.TotalMilliseconds, 1));

    public static void CompileFailed(ILogger logger, string pipelineId, string manifestId, DiagnosticBag diagnostics)
    {
        Count(pipelineId, "compile_failed");
        LogCompileFailed(logger, pipelineId, manifestId, diagnostics.ErrorCount, FirstError(diagnostics));
    }

    public static void Rejected(ILogger logger, string pipelineId, string manifestId, DiagnosticBag diagnostics)
    {
        Count(pipelineId, "rejected");
        LogRejected(logger, pipelineId, manifestId, diagnostics.ErrorCount, FirstError(diagnostics));
    }

    public static void Stale(ILogger logger, string pipelineId, string manifestId, long sequence, long current)
    {
        Count(pipelineId, "stale");
        LogStale(logger, pipelineId, manifestId, sequence, current);
    }

    private static void Count(string pipelineId, string outcome) =>
        StrataRecMetrics.PlanLoads.Add(1, new KeyValuePair<string, object?>(StrataRecTags.Pipeline, pipelineId), new KeyValuePair<string, object?>(StrataRecTags.Outcome, outcome));

    private static string? FirstError(DiagnosticBag diagnostics)
    {
        foreach (var diagnostic in diagnostics.Items)
        {
            if (diagnostic.IsError)
            {
                return diagnostic.ToString();
            }
        }

        return null;
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information,
        Message = "Pipeline '{PipelineId}' now serves manifest '{ManifestId}' (sequence {Sequence}, {Variants} node variant(s), compiled in {CompileMs} ms)")]
    private static partial void LogApplied(ILogger logger, string pipelineId, string manifestId, long sequence, int variants, double compileMs);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error,
        Message = "Plan of pipeline '{PipelineId}' from manifest '{ManifestId}' failed to compile with {ErrorCount} error(s); the current plan stays. First: {FirstError}")]
    private static partial void LogCompileFailed(ILogger logger, string pipelineId, string manifestId, int errorCount, string? firstError);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Error,
        Message = "Manifest '{ManifestId}' for pipeline '{PipelineId}' rejected with {ErrorCount} error(s); the current plan stays. First: {FirstError}")]
    private static partial void LogRejected(ILogger logger, string pipelineId, string manifestId, int errorCount, string? firstError);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Information,
        Message = "Manifest '{ManifestId}' for pipeline '{PipelineId}' not applied: its sequence {Sequence} is not newer than the served {CurrentSequence}")]
    private static partial void LogStale(ILogger logger, string pipelineId, string manifestId, long sequence, long currentSequence);
}
