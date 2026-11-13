using System.ComponentModel;
using System.Text.Json;

namespace StrataRec.Artifacts.Model;

/// <summary>
/// Everything needed to re-execute one request deterministically (design 13.4): the request, its assignment, every
/// external response with its completion status and duration, every node's execution decision, the effective sampling
/// and the original result to diff against.
/// </summary>
/// <remarks>
/// <para>A plain JSON document with the same strict serializer as artifacts, so its JSON Schema comes from these types
/// (<c>stratarec schema replay_bundle</c>) and a malformed bundle is reported with a JSON pointer.</para>
/// <para>Values use the log encoding (design 8.7: missing and non-finite are null, bool is 0 or 1) and are decoded with
/// the type recorded next to them, never with the type of whatever configuration replays the bundle. Bundles contain
/// request inputs and feature values; who may read them is the environment's business (design 8.9).</para>
/// </remarks>
[Description("A recorded request for deterministic replay (StrataRec design 13.4).")]
public sealed class ReplayBundle
{
    /// <summary>The format this build reads and writes.</summary>
    public const string CurrentFormat = "stratarec.replay_bundle/1";

    [Description("Format and version: \"stratarec.replay_bundle/1\".")]
    public required string Format { get; init; }

    public required ReplayRequestRecord Request { get; init; }

    public required ReplayManifestRecord Manifest { get; init; }

    [Description("Pipeline executions of the request: the primary pipeline, then its fallback_pipeline if that ran (design 5.6).")]
    public required List<ReplayRunRecord> Runs { get; init; }

    [Description("What the caller received.")]
    public required ReplayResultRecord Result { get; init; }
}

/// <summary>The request as the engine saw it, with seed and time resolved.</summary>
public sealed class ReplayRequestRecord
{
    public required string PipelineId { get; init; }

    public required string RequestId { get; init; }

    [Description("ctx.request.seed as used (explicit or derived from the request id).")]
    public required long Seed { get; init; }

    [Description("ctx.request.time_ms as used.")]
    public required long TimeMs { get; init; }

    [Description("End-to-end deadline of the original request. Replay does not enforce it: recorded timing decisions are replayed instead.")]
    public required double DeadlineMs { get; init; }

    [Description("Request inputs that were set, with their request_schema types.")]
    public Dictionary<string, ReplayTypedValue> Inputs { get; init; } = new();

    public required ReplayAssignmentRecord Assignment { get; init; }
}

/// <summary>A value and the type it was recorded with.</summary>
public sealed class ReplayTypedValue
{
    [Description("Type spelling, e.g. \"int64?\" or \"vec<float32,4>\".")]
    public required string Type { get; init; }

    [Description("Log encoding (design 8.7): null is missing, bool is 0 or 1, lists and vectors are arrays.")]
    public JsonElement? Value { get; init; }
}

/// <summary>How the request's arms were chosen.</summary>
public sealed class ReplayAssignmentRecord
{
    [Description("forced (debug force_arms), external (the caller's assignment), provider, builtin or none.")]
    public required string Source { get; init; }

    [Description("Assigned arms as experiment/arm.")]
    public List<string> Arms { get; init; } = new();

    [Description("External arm ids that were not in the plan and were ignored.")]
    public int UnknownArms { get; init; }
}

/// <summary>The configuration the request ran on.</summary>
public sealed class ReplayManifestRecord
{
    [Description("Manifest content id, or a local label such as workspace:<pipeline>/<env>.")]
    public required string Id { get; init; }

    [Description("Release order of the plan (manifest seq, or the engine's load ticket for local loads).")]
    public long? Sequence { get; init; }

    [Description("Artifact (e.g. pipeline:home_feed, experiment:exp_x) → content id, when the loader knew them.")]
    public Dictionary<string, string> Artifacts { get; init; } = new();
}

/// <summary>One pipeline execution within the request.</summary>
public sealed class ReplayRunRecord
{
    public required string PipelineId { get; init; }

    public required string ManifestId { get; init; }

    [Description("primary or fallback_pipeline.")]
    public required string Role { get; set; }

    [Description("Arms this execution ran with (a fallback pipeline runs without experiments).")]
    public List<string> Arms { get; init; } = new();

    [Description("The request's parameter snapshot: baseline plus the assigned arms' overrides (design 12.2).")]
    public Dictionary<string, ReplayTypedValue> Params { get; init; } = new();

    [Description("One entry per timeline position that had a node, in execution order.")]
    public List<ReplayNodeRecord> Nodes { get; init; } = new();

    public List<ReplayCandidateCall> CandidateCalls { get; init; } = new();

    public List<ReplayFeatureCall> FeatureCalls { get; init; } = new();

    public List<ReplayModelCall> ModelCalls { get; init; } = new();

    public required ReplaySamplingRecord Sampling { get; init; }

    public required ReplayResultRecord Result { get; init; }
}

/// <summary>The execution decision at one timeline position (design 5.6).</summary>
public sealed class ReplayNodeRecord
{
    public required string Node { get; init; }

    [Description("succeeded, degraded, fallback, skipped_when, skipped_deadline, skipped_input or failed.")]
    public required string Outcome { get; init; }

    public string? Detail { get; init; }

    [Description("Node that produced the outputs: the node itself, or its fallback.")]
    public string? ProducedBy { get; init; }

    public double DurationMs { get; init; }

    public int RowsIn { get; init; }

    public int RowsOut { get; init; }

    [Description("Time left before the deadline when the node passed its when condition: what min_remaining_ms is compared with on replay.")]
    public double? RemainingMs { get; init; }

    [Description("The node's run and its fallback's, with how each ended relative to its time budget.")]
    public List<ReplayAttemptRecord> Attempts { get; init; } = new();
}

/// <summary>How one node attempt ended relative to its time budget.</summary>
public sealed class ReplayAttemptRecord
{
    public required string Node { get; init; }

    [Description("ran (finished within its budget, successfully or not), deadline_exceeded (no budget left to start) or timed_out (cancelled at its budget).")]
    public required string Timing { get; init; }

    public double? BudgetMs { get; init; }
}

/// <summary>One candidate source call.</summary>
public sealed class ReplayCandidateCall
{
    [Description("Start order of the call within its run, across all call kinds.")]
    public int Seq { get; init; }

    public string? Node { get; init; }

    public required string Source { get; init; }

    [Description("Candidates requested (the source's quota).")]
    public int Limit { get; init; }

    [Description("ok, timeout, error or cancelled.")]
    public required string Status { get; init; }

    public string? Error { get; init; }

    public double DurationMs { get; init; }

    [Description("Returned entity ids in rank order (int64 numbers or strings), as accepted (at most limit).")]
    public JsonElement? Ids { get; init; }

    [Description("Score of each returned id.")]
    public List<double> Scores { get; init; } = new();
}

/// <summary>One feature connector call (one max_batch chunk).</summary>
public sealed class ReplayFeatureCall
{
    public int Seq { get; init; }

    public required string Source { get; init; }

    public required string Entity { get; init; }

    [Description("single, many, cross or context_map (design 6.5).")]
    public required string Shape { get; init; }

    [Description("Context key for single, cross and context_map shapes.")]
    public JsonElement? ContextKey { get; init; }

    [Description("Candidate (or foreign) keys in staging order, for many, cross and context_map shapes.")]
    public JsonElement? Keys { get; init; }

    [Description("False when the request had no time left to send the call.")]
    public bool Sent { get; init; } = true;

    [Description("ok, partial, timeout, error or cancelled.")]
    public required string Status { get; init; }

    public string? Error { get; init; }

    public int FailedKeys { get; init; }

    public double DurationMs { get; init; }

    [Description("Requested fields with their storage types.")]
    public required List<ReplayField> Fields { get; init; }

    [Description("Field → one value per staging row (keys, or the context key); null where the source returned no value. Absent unless status is ok or partial.")]
    public Dictionary<string, JsonElement>? Values { get; init; }
}

/// <summary>A requested physical field.</summary>
public sealed class ReplayField
{
    public required string Name { get; init; }

    public required string Type { get; init; }
}

/// <summary>One model call (one batch).</summary>
public sealed class ReplayModelCall
{
    public int Seq { get; init; }

    public string? Node { get; init; }

    public required string Model { get; init; }

    public string? Version { get; init; }

    public int BatchSize { get; init; }

    [Description("ok, timeout, error or cancelled.")]
    public required string Status { get; init; }

    public string? Error { get; init; }

    [Description("The client threw instead of returning a failed status; replay throws too.")]
    public bool Threw { get; init; }

    public double DurationMs { get; init; }

    [Description("Entity id of each batch row, when the operator scored rows; outputs are matched by it on replay.")]
    public JsonElement? Keys { get; init; }

    [Description("XXH3 of each row's inputs (one per key), or of the whole call when there are no keys; hex.")]
    public List<string> InputDigests { get; init; } = new();

    [Description("Output → flat float32 values, batch_size × values per row; null where the value was not finite.")]
    public Dictionary<string, JsonElement> Outputs { get; init; } = new();

    public Dictionary<string, string> Texts { get; init; } = new();
}

/// <summary>Effective log sampling of the run (design 8.2, 8.4).</summary>
public sealed class ReplaySamplingRecord
{
    [Description("none (no log consumer, so nothing was sampled), pipeline (written to the log pipeline) or captured (returned to the caller).")]
    public required string Logs { get; init; }

    public List<ReplayGroupSampling> Groups { get; init; } = new();

    public List<ReplayTapSampling> Taps { get; init; } = new();
}

/// <summary>A funnel group's request-level sampling.</summary>
public sealed class ReplayGroupSampling
{
    public required string Group { get; init; }

    [Description("Configured rate.")]
    public double Rate { get; init; }

    [Description("Probability this request had of being sampled: the rate, or 0 when nothing consumed logs.")]
    public double EffectiveRate { get; init; }

    [Description("The decision; null when no tap of the group was active or nothing consumed logs.")]
    public bool? Sampled { get; init; }
}

/// <summary>A tap variant's sampling for the request.</summary>
public sealed class ReplayTapSampling
{
    public required string Tap { get; init; }

    public required string Group { get; init; }

    [Description("Row sampling rate within a sampled request.")]
    public double RowRate { get; init; }

    public int? MaxRows { get; init; }

    [Description("The tap captured for this request (its group sampled it).")]
    public bool Active { get; init; }
}

/// <summary>A result: what a run returned, or (with served_by) what the caller received.</summary>
public sealed class ReplayResultRecord
{
    [Description("Pipeline whose result the caller received; top-level result only.")]
    public string? ServedBy { get; init; }

    public required bool Succeeded { get; init; }

    public string? Error { get; init; }

    public bool Degraded { get; init; }

    public double ElapsedMs { get; init; }

    [Description("Output column → type, to decode item values.")]
    public Dictionary<string, string> ColumnTypes { get; init; } = new();

    public List<ReplayItem> Items { get; init; } = new();

    public List<string> TriggeredExperiments { get; init; } = new();

    [Description("Output column → node that produced it (lineage).")]
    public Dictionary<string, string> ColumnProducers { get; init; } = new();
}

/// <summary>A returned item.</summary>
public sealed class ReplayItem
{
    [Description("Entity id: an int64 number or a string.")]
    public required JsonElement Id { get; init; }

    public required int Rank { get; init; }

    public Dictionary<string, JsonElement?> Columns { get; init; } = new();
}
