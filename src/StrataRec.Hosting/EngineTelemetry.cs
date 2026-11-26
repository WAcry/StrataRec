using System.Diagnostics;
using Microsoft.Extensions.Logging;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Diagnostics;
using StrataRec.Experiments.Assignment;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Hosting;

/// <summary>
/// Request-level metric tags of one served plan: the pipeline, pipeline × arm for every arm of the plan,
/// and pipeline × reason for ignored arms. Built once when the plan is loaded, so recording a request passes cached
/// arrays.
/// </summary>
internal sealed class RequestTelemetry
{
    /// <summary>Requests for a pipeline the engine does not serve: no pipeline tag (the id is caller input, unbounded).</summary>
    public static readonly RequestTelemetry Unknown = new([], [], [], [], []);

    private readonly KeyValuePair<string, object?>[] _ignoredUnknown;
    private readonly KeyValuePair<string, object?>[] _ignoredConflict;
    private readonly KeyValuePair<string, object?>[] _ignoredYielded;

    private RequestTelemetry(
        KeyValuePair<string, object?>[] pipeline, KeyValuePair<string, object?>[][] arms, KeyValuePair<string, object?>[] ignoredUnknown,
        KeyValuePair<string, object?>[] ignoredConflict, KeyValuePair<string, object?>[] ignoredYielded)
    {
        Pipeline = pipeline;
        Arms = arms;
        _ignoredUnknown = ignoredUnknown;
        _ignoredConflict = ignoredConflict;
        _ignoredYielded = ignoredYielded;
    }

    public KeyValuePair<string, object?>[] Pipeline { get; }

    /// <summary>
    /// Starts the request span (<see cref="StrataRecTracing.RequestSpanName"/>); null when nobody listens or the sampler
    /// drops it. Starting it costs a listener check otherwise, so it is started once per request, not per node. The engine
    /// starts it for every request it runs; nothing else runs a pipeline.
    /// </summary>
    public static Activity? StartRequest(string pipelineId, string requestId)
    {
        var activity = StrataRecTelemetry.ActivitySource.StartActivity(StrataRecTracing.RequestSpanName, ActivityKind.Internal);
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag(StrataRecTags.Pipeline, pipelineId);
            activity.SetTag(StrataRecTags.RequestId, requestId);
        }

        return activity;
    }

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

        return new RequestTelemetry(
            [pipeline], arms, Ignored(pipeline, IgnoredArmReason.Unknown), Ignored(pipeline, IgnoredArmReason.LayerConflict), Ignored(pipeline, IgnoredArmReason.Yielded));

        static KeyValuePair<string, object?>[] Ignored(KeyValuePair<string, object?> pipeline, IgnoredArmReason reason) =>
            [pipeline, new(StrataRecTags.Outcome, IgnoredArm.Name(reason))];
    }

    /// <summary>Counts the platform's arm ids a request ignored, by reason; nothing to do (and nothing recorded) when none were.</summary>
    public void RecordIgnored(IgnoredArm[] ignored)
    {
        foreach (var arm in ignored)
        {
            StrataRecMetrics.RequestArmsIgnored.Add(1, arm.Reason switch
            {
                IgnoredArmReason.Unknown => _ignoredUnknown,
                IgnoredArmReason.LayerConflict => _ignoredConflict,
                _ => _ignoredYielded,
            });
        }
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
    /// <param name="plan">The plan the request ran on, when known.</param>
    /// <param name="arms">The arms assigned before execution.</param>
    public static void EndSpan(Activity activity, RecResult? result, bool degraded, ExecutionPlan? plan, in RequestArms arms)
    {
        if (activity.IsAllDataRequested)
        {
            if (result is null)
            {
                // Aborted (caller cancellation, defect): no result, but the release and the arms were known before
                // execution, and slow treatment requests are exactly the ones worth finding by arm (review 6).
                activity.SetStatus(ActivityStatusCode.Error, "aborted");
                if (plan is not null)
                {
                    activity.SetTag(StrataRecTags.ManifestId, plan.ManifestId);
                    activity.SetTag(StrataRecTags.ArmsAssigned, plan.ArmIds(arms).ToArray());
                }
            }
            else
            {
                activity.SetTag(StrataRecTags.ServedBy, result.ServedBy);
                activity.SetTag(StrataRecTags.ManifestId, result.ManifestId);
                activity.SetTag(StrataRecTags.Items, result.Columns.Count);
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

/// <summary>
/// Plan lifecycle metrics and event logs: loads are rare, so tags are built per load. None of
/// it throws: a metric listener or a logger that throws loses its record, and a load's outcome (a plan published, a
/// release's status and retries) never depends on it.
/// </summary>
internal static partial class PlanLoadTelemetry
{
    public static void Applied(ExecutionPlan plan, long sequence)
    {
        try
        {
            var pipeline = new KeyValuePair<string, object?>(StrataRecTags.Pipeline, plan.PipelineId);
            StrataRecMetrics.PlanLoads.Add(1, pipeline, new KeyValuePair<string, object?>(StrataRecTags.Outcome, "applied"));
            StrataRecMetrics.PlanVariants.Record(plan.Nodes.Length, pipeline);
            StrataRecMetrics.PlanSequence.Record(sequence, pipeline);
        }
        catch (Exception)
        {
            // See the summary.
        }
    }

    public static void LogApplied(ILogger logger, ExecutionPlan plan, long sequence)
    {
        try
        {
            LogApplied(logger, plan.PipelineId, plan.ManifestId, sequence, plan.Nodes.Length, Math.Round(plan.CompileTime.TotalMilliseconds, 1));
        }
        catch (Exception)
        {
            // See the summary.
        }
    }

    public static void CompileFailed(ILogger logger, string pipelineId, string manifestId, DiagnosticBag diagnostics)
    {
        try
        {
            Count(pipelineId, "compile_failed");
            LogCompileFailed(logger, pipelineId, manifestId, diagnostics.ErrorCount, FirstError(diagnostics));
        }
        catch (Exception)
        {
            // See the summary.
        }
    }

    public static void BindingsFailed(ILogger logger, string pipelineId, string manifestId, DiagnosticBag diagnostics)
    {
        try
        {
            Count(pipelineId, "bindings_failed");
            LogBindingsFailed(logger, pipelineId, manifestId, diagnostics.ErrorCount, FirstError(diagnostics));
        }
        catch (Exception)
        {
            // See the summary.
        }
    }

    public static void Stale(ILogger logger, string pipelineId, string manifestId, long sequence, long current)
    {
        try
        {
            Count(pipelineId, "stale");
            LogStale(logger, pipelineId, manifestId, sequence, current);
        }
        catch (Exception)
        {
            // See the summary.
        }
    }

    public static void Incompatible(ILogger logger, string pipelineId, string manifestId, long sequence, IReadOnlyList<string> missing)
    {
        try
        {
            Count(pipelineId, "incompatible");
            LogIncompatible(logger, pipelineId, manifestId, sequence, string.Join(", ", missing));
        }
        catch (Exception)
        {
            // See the summary.
        }
    }

    /// <summary>The manifest, or an artifact it names, could not be read: missing, damaged, of another identity.</summary>
    public static void ArtifactsFailed(ILogger logger, string pipelineId, string manifestId, DiagnosticBag diagnostics)
    {
        try
        {
            Count(pipelineId, "artifacts_failed");
            LogArtifactsFailed(logger, pipelineId, manifestId, diagnostics.ErrorCount, FirstError(diagnostics));
        }
        catch (Exception)
        {
            // See the summary.
        }
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

    [LoggerMessage(EventId = 1005, Level = LogLevel.Error,
        Message = "Plan of pipeline '{PipelineId}' from manifest '{ManifestId}' not applied: {ErrorCount} local table(s) or instance(s) it binds failed to load; the current plan stays. First: {FirstError}")]
    private static partial void LogBindingsFailed(ILogger logger, string pipelineId, string manifestId, int errorCount, string? firstError);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Error,
        Message = "Manifest '{ManifestId}' (sequence {Sequence}) for pipeline '{PipelineId}' is incompatible: this binary lacks {MissingCapabilities}; the current plan stays")]
    private static partial void LogIncompatible(ILogger logger, string pipelineId, string manifestId, long sequence, string missingCapabilities);

    // EventId 1007 (a manifest below min_accepted_seq) is retired: a revocation is a newer release like any other.

    [LoggerMessage(EventId = 1008, Level = LogLevel.Error,
        Message = "Manifest '{ManifestId}' for pipeline '{PipelineId}' not applied: {ErrorCount} error(s) reading it or its artifacts; the current plan stays. First: {FirstError}")]
    private static partial void LogArtifactsFailed(ILogger logger, string pipelineId, string manifestId, int errorCount, string? firstError);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Information,
        Message = "Manifest '{ManifestId}' for pipeline '{PipelineId}' not applied: its sequence {Sequence} is not newer than the served {CurrentSequence}")]
    private static partial void LogStale(ILogger logger, string pipelineId, string manifestId, long sequence, long currentSequence);
}
