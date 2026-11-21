using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace StrataRec.Artifacts.Model;

/// <summary>
/// Everything needed to re-execute one request deterministically (design 13.4): the request, its assignment, every
/// external response with its completion status and duration, every node's execution decision, the effective sampling
/// and the original result to diff against.
/// </summary>
/// <remarks>
/// <para>A plain JSON document with the same strict serializer as artifacts, so its JSON Schema comes from these types
/// (<c>stratarec schema replay_bundle</c>) and a malformed bundle is reported with a JSON pointer.</para>
/// <para>Values use the log encoding (design 8.7: missing is null, bool is 0 or 1) and are decoded with the type recorded
/// next to them, never with the type of whatever configuration replays the bundle. Unlike logs, a bundle keeps the NaN
/// and ±∞ the request observed, as the strings <c>"NaN"</c>, <c>"Infinity"</c> and <c>"-Infinity"</c> (per element in
/// float lists): a replay must see the same values to make the same decisions. Bundles contain request inputs and feature
/// values; who may read them is the environment's business (design 8.9).</para>
/// </remarks>
[Description("A recorded request for deterministic replay (StrataRec design 13.4).")]
public sealed class ReplayBundle
{
    /// <summary>The format this build reads and writes.</summary>
    /// <remarks>
    /// A format version is the date its layout changed and that day's sequence (<c>2026-11-14.1</c>, then
    /// <c>2026-11-14.2</c>): it says when a bundle was laid out, and sorts as text. It changes also when what a bundle
    /// records means something else for the same layout: 2026-11-20.2 batches calls evenly (Batching.Size), so a bundle
    /// recorded before holds calls cut at other boundaries, which a replay could not answer as recorded: a model call is
    /// matched as a whole and a recorded timeout belongs to its batch (review 86). 2026-11-20.3 adds the ignored arm's
    /// reason <c>yielded</c> (design 11.1): a build before cannot read it, and refuses such a bundle at its format, with
    /// a hint to record again, instead of at a reason it does not know (review 96). 2026-11-21.1 records each model output
    /// with its element type, and model inputs of every declared type (their digests include it): tensors follow the
    /// declared column types (design 6.9). 2026-11-21.2 records the result's columns at their declared types, and a
    /// float32 request input recorded as the host gave it now replays as the nearest float, as it is served: the same
    /// bundle would replay to other values in a build before. 2026-11-21.3 records each request input at the type of the
    /// value the host gave (a fallback pipeline may declare it otherwise), and a computation the binder folds to a name
    /// (<c>max(col.x)</c>) is no longer a reference: result types and model input digests of a bundle before can differ.
    /// 2026-11-21.4 records every call but a feature fetch as one service call (<c>service_calls</c>, design 6.9), a
    /// model's and a recall's alike: a recall's query is a digest of its inputs, as a model's, and only the outputs the
    /// operator reads are recorded. 2026-11-21.5 has the same layout, but a service sees only its call's inputs: in a bundle
    /// before, a recall service could pick its answer by a request field that no digest covers, and a replay that no longer
    /// sends it would still find that answer, and call the run identical. 2026-11-21.6 digests a call about no items that
    /// reads named outputs with its limit, a missing input of a call about items by its rows when its shape is per item,
    /// and records why a call left its node Degraded; a recall query of a negative zero or of a declared float32 is sent
    /// as served since: a bundle before could replay identical to a run whose answers changed. 2026-11-21.7 digests every
    /// call about no items without its limit again (a recorded call answers the named outputs a call reads only at the
    /// call's limit), and records the items a service added past a call's limit. 2026-11-21.8 records the service call of
    /// a feature source in mode <c>service</c> (IO step 5) with its source and no node. 2026-11-21.9 records the outputs each
    /// call asked for (an output the service left unwritten is replayed so, and a failure answers only a call asking for the
    /// same), and a feature source's call carries the request's seed: a bundle before replayed a source's failure for a
    /// call asking for other outputs, and the answer of a call made without the seed. 2026-11-21.10 records the entity a
    /// source's call computed: a source's two entities of the same arguments and outputs took each other's answers.
    /// 2026-11-21.11 digests an item by what it saw of each input: a shared <c>[1, d]</c> input left the digest of the
    /// same item in a batch of one another. 2026-11-21.12 adds what an item saw of each input to its digest as a digest of
    /// its own, so a shared input is hashed once for a call, not once for every item.
    /// </remarks>
    public const string CurrentFormat = "stratarec.replay_bundle/2026-11-21.12";

    [Description("Format and version: \"stratarec.replay_bundle/2026-11-21.12\" (the date the layout changed, and that day's sequence).")]
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

    [Description("Request inputs that were set, each with the type of the value the host gave (int64, float64, bool, string or a list of them), not a pipeline's declared type: each pipeline that binds it converts it as it was served.")]
    public Dictionary<string, ReplayTypedValue> Inputs { get; init; } = new();

    public required ReplayAssignmentRecord Assignment { get; init; }

    [Description("The request's configuration override (an RFC 7396 merge patch of the pipeline, applied after the arms), when it had one; a replay applies it again.")]
    public JsonObject? ConfigOverride { get; init; }
}

/// <summary>A value and the type it was recorded with.</summary>
public sealed class ReplayTypedValue
{
    [Description("Type spelling, e.g. \"int64?\" or \"list<float32,4>\".")]
    public required string Type { get; init; }

    [Description("Log encoding (design 8.7): null is missing, bool is 0 or 1, lists are arrays; NaN and ±∞ floats (and float list elements) are the strings \"NaN\", \"Infinity\" and \"-Infinity\".")]
    public JsonElement? Value { get; init; }
}

/// <summary>How the request's arms were chosen.</summary>
public sealed class ReplayAssignmentRecord
{
    [Description("forced (debug force_arms), external (the platform's answer the caller passed), provider (the engine asked the platform) or none (the baseline).")]
    public required string Source { get; init; }

    [Description("Assigned arms as experiment/arm.")]
    public List<string> Arms { get; init; } = new();

    [Description("Arm ids from the platform the request did not run with, and why, sorted by id.")]
    public List<ReplayIgnoredArm> Ignored { get; init; } = new();
}

/// <summary>An arm id from the platform that the request ignored.</summary>
public sealed class ReplayIgnoredArm
{
    [Description("The id as the platform sent it (experiment/arm).")]
    public required string Id { get; init; }

    [Description("unknown (the served plan has no such arm), layer_conflict (another arm of its layer came too, so that layer ran its baseline) or yielded (its experiment yields to another experiment the request was in, so that layer ran its baseline).")]
    [AllowedValues("unknown", "layer_conflict", "yielded")]
    public required string Reason { get; init; }
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

    [Description("Calls of services (models, recall sources, any other), in start order.")]
    public List<ReplayServiceCall> ServiceCalls { get; init; } = new();

    public List<ReplayFeatureCall> FeatureCalls { get; init; } = new();

    public required ReplaySamplingRecord Sampling { get; init; }

    public required ReplayResultRecord Result { get; init; }
}

/// <summary>The execution decision at one timeline position (design 5.6).</summary>
public sealed class ReplayNodeRecord
{
    public required string Node { get; init; }

    [Description("succeeded, degraded, fallback, skipped_when, skipped_deadline, skipped_input, failed, or aborted (the position running when the request was aborted).")]
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

    [Description("ran (finished within its budget, successfully or not), deadline_exceeded (no budget left to start) or timed_out (ran out of its budget: cut off at it, or returned only after it).")]
    public required string Timing { get; init; }

    public double? BudgetMs { get; init; }
}

/// <summary>One feature connector call (one max_batch chunk).</summary>
public sealed class ReplayFeatureCall
{
    public int Seq { get; init; }

    public required string Source { get; init; }

    public required string Entity { get; init; }

    [Description("The call's keys in staging order (design 6.5): each an int64 number or a string for a one-part key, or an array of its parts in the entity's key order (a shared part, a request input or a context feature, repeated in every key).")]
    public required JsonElement Keys { get; init; }

    [Description("False when the request had no time left to send the call.")]
    public bool Sent { get; init; } = true;

    [Description("ok, partial, timeout, error, cancelled, or outstanding (still in flight when the request was aborted).")]
    public required string Status { get; init; }

    public string? Error { get; init; }

    public int FailedKeys { get; init; }

    public double DurationMs { get; init; }

    [Description("Requested fields with their storage types.")]
    public required List<ReplayField> Fields { get; init; }

    [Description("Field → one value per staging row (keys, or the context key), in the encoding of request inputs; null where the source returned no value. Absent unless status is ok or partial.")]
    public Dictionary<string, JsonElement>? Values { get; init; }
}

/// <summary>A requested physical field.</summary>
public sealed class ReplayField
{
    public required string Name { get; init; }

    public required string Type { get; init; }
}

/// <summary>One tensor output of a recorded service call.</summary>
public sealed class ReplayOutput
{
    [Description("Element type, as the service wrote it: bool, int32, int64, float32, float64 or string.")]
    public required string Type { get; init; }

    [Description("The elements, flat: batch_size × values per item for a call about items. A bool is 0 or 1; NaN and ±∞ floats are the strings \"NaN\", \"Infinity\" and \"-Infinity\".")]
    public required JsonElement Values { get; init; }
}

/// <summary>One service call (one batch): a model's, a recall source's, any other service's (design 6.9).</summary>
public sealed class ReplayServiceCall
{
    [Description("Start order of the call within its run, across all calls.")]
    public int Seq { get; init; }

    [Description("The node that made the call; absent for a feature source's call (source).")]
    public string? Node { get; init; }

    [Description("The feature source in mode 'service' whose features the call asked for (the request's prefetch made it); absent for a node's call.")]
    public string? Source { get; init; }

    [Description("The entity a feature source's call computed (a source may compute several, each with calls of its own); absent for a node's call.")]
    public string? Entity { get; init; }

    [Description("The service's logical name, as the bindings name it.")]
    public required string Service { get; init; }

    [Description("The role it was called in: model, recall, an operator's own, or fetch for a feature source's call.")]
    public required string Role { get; init; }

    public string? Version { get; init; }

    public int BatchSize { get; init; }

    [Description("The most items the call could add (a recall's quota); 0 for a call that adds none.")]
    public int Limit { get; init; }

    [Description("ok, timeout, error, cancelled, or outstanding (still in flight when the request was aborted).")]
    public required string Status { get; init; }

    public string? Error { get; init; }

    [Description("The node's budget or the caller cut the call off, which the operator saw as cancellation (an OperationCanceledException); replay throws it too. " +
                 "A client's own failure, thrown or returned, is a failed status, never this.")]
    public bool Threw { get; init; }

    public double DurationMs { get; init; }

    [Description("Entity id of each item of a call about items (a model scoring rows): its outputs are matched per item on replay.")]
    public JsonElement? Keys { get; init; }

    [Description("XXH3 of the inputs: one per item of a call about items (its slice of the inputs whose leading dimension is the batch size, and every other input whole), " +
                 "else one of the whole call; each input with its name, element type and shape, and max_output_tokens when set.")]
    public List<string> InputDigests { get; init; } = new();

    [Description("The outputs the call asked for (what its operator or feature source reads). One it asked for and the service did not write is in neither " +
        "outputs nor texts: the service left it unanswered, and a replay of the call leaves it so.")]
    public List<string> Requested { get; init; } = new();

    [Description("Output → the tensor the service returned, of the outputs the call asked for (requested).")]
    public Dictionary<string, ReplayOutput> Outputs { get; init; } = new();

    [Description("Output → text, for services that generate text (a recorded answer is replayed as it was, design 13.4 item 7).")]
    public Dictionary<string, string> Texts { get; init; } = new();

    [Description("Items the service added, in rank order (int64 numbers or strings), as accepted (at most limit).")]
    public JsonElement? Ids { get; init; }

    [Description("Score of each added item; NaN and ±∞ are the strings \"NaN\", \"Infinity\" and \"-Infinity\".")]
    [JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)]
    public List<double> Scores { get; init; } = new();

    [Description("Tokens the service generated, when its client reported them; absent otherwise.")]
    public int? OutputTokens { get; init; }

    [Description("True when the service stopped at the call's max_output_tokens, so its text may be cut short; absent otherwise.")]
    public bool? Truncated { get; init; }

    [Description("Why the call left its node Degraded (its cache answered with values past their TTL after the service failed); absent otherwise.")]
    public string? Degraded { get; init; }

    [Description("Items the service added past the call's limit, which its response dropped; absent when none.")]
    public int? Dropped { get; init; }
}

/// <summary>Effective log sampling of the run (design 8.2, 8.4).</summary>
public sealed class ReplaySamplingRecord
{
    [Description("Where the run's log batches went: none (neither written nor returned: no log consumer, so nothing was sampled, or a request that " +
                 "captured them was aborted, so nothing was returned), pipeline (written to the log pipeline) or captured (returned to the caller).")]
    public required string Logs { get; set; }

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

    [Description("The run, or the request, was aborted (cancelled by its caller, or stopped by a defect) and returned no result. The recording is a snapshot taken at the abort: calls still in flight are outstanding, and what they returned afterwards is not recorded. A replay does not execute an aborted request's bundle: there is no result to reproduce.")]
    public bool Aborted { get; init; }

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

    [Description("Output column → value, in the encoding of request inputs (non-finite floats as \"NaN\", \"Infinity\", \"-Infinity\").")]
    public Dictionary<string, JsonElement?> Columns { get; init; } = new();
}
