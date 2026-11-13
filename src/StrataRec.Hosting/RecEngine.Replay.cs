using System.Diagnostics;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Values;
using StrataRec.Hosting.Replay;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Replay;

namespace StrataRec.Hosting;

/// <summary>Recording requests into replay bundles and replaying them (design 13.4, 10.8).</summary>
public sealed partial class RecEngine
{
    /// <summary>
    /// Re-executes a recorded request on the plan this engine currently serves for its pipeline (design 13.4). Recorded
    /// responses replace every feature, candidate and model call, and recorded timing facts replace the clock, so a call
    /// that timed out times out again and every node makes the same skip, fallback or failure decision, without waiting.
    /// On the recording's own configuration the result is identical; on another, data the bundle lacks is missing and
    /// listed in <see cref="ReplayReport.Gaps"/>.
    /// </summary>
    /// <remarks>
    /// A replay is not traffic: it writes no logs (taps are captured into the result), records no metrics, event logs or
    /// spans, and does not count as a request of the pipeline or its arms.
    /// </remarks>
    /// <exception cref="ReplayBundleException">The bundle is malformed; the pointer says where.</exception>
    public async ValueTask<ReplayOutcome> ReplayAsync(ReplayBundle bundle, ReplayOptions? options = null, CancellationToken cancellationToken = default)
    {
        var recordedRequest = bundle.Request;
        var notes = new List<string>();
        var recordedResult = ComparableResult.From(bundle.Result, "/result");
        var (primaryRecord, primaryIndex) = FindRun(bundle, "primary");
        var (fallbackRecord, fallbackIndex) = FindRun(bundle, "fallback_pipeline");
        var primaryReplay = primaryRecord is null ? RunReplay.Empty() : RunReplay.Create(primaryRecord, $"/runs/{primaryIndex}");
        var fallbackReplay = fallbackRecord is null ? null : RunReplay.Create(fallbackRecord, $"/runs/{fallbackIndex}");
        if (primaryRecord is null)
        {
            notes.Add("the bundle has no primary run: every external call is a gap");
        }

        var request = new RecRequest
        {
            PipelineId = recordedRequest.PipelineId,
            RequestId = recordedRequest.RequestId,
            Inputs = new RequestInputs(),
            Deadline = RunReplay.Deadline,
            Seed = recordedRequest.Seed,
            TimeMs = recordedRequest.TimeMs,
            Debug = new DebugOptions { Trace = true, CaptureLogs = true },
        };
        var recordedArms = primaryRecord?.Arms ?? recordedRequest.Assignment.Arms;
        if (!_pipelines.TryGetValue(recordedRequest.PipelineId, out var pipeline))
        {
            var unknown = Rejected(request, $"Unknown pipeline '{recordedRequest.PipelineId}'.");
            return Outcome(bundle, unknown, recordedResult, recordedArms, null, primaryRecord, null, primaryReplay, null, notes, 0, "<none>");
        }

        var plan = pipeline.Plan;
        var inputs = DecodeInputs(recordedRequest, plan, notes);
        request = new RecRequest
        {
            PipelineId = request.PipelineId,
            RequestId = request.RequestId,
            Inputs = inputs,
            Deadline = request.Deadline,
            Seed = request.Seed,
            TimeMs = request.TimeMs,
            Debug = request.Debug,
        };

        var known = new List<string>();
        foreach (var arm in options?.Arms ?? recordedArms)
        {
            if (plan.TryGetArm(arm, out _))
            {
                known.Add(arm);
            }
            else
            {
                notes.Add($"arm '{arm}' is not in the replayed plan; replayed without it");
            }
        }

        RequestArms arms;
        try
        {
            arms = plan.ArmsFromIds(known);
        }
        catch (ArgumentException ex)
        {
            return Outcome(bundle, Rejected(request, ex.Message), recordedResult, recordedArms, null, primaryRecord, null, primaryReplay, null, notes, 0,
                plan.ManifestId);
        }

        var started = Stopwatch.GetTimestamp();
        var executed = await _executor.ExecuteAsync(plan, ReplayRequest(request, arms, primaryReplay), cancellationToken).ConfigureAwait(false);
        RecResult result;
        PipelineResult? served = null;
        if (executed.Succeeded || pipeline.FallbackPipeline is not { } fallbackId || fallbackId == request.PipelineId
            || !_pipelines.TryGetValue(fallbackId, out var fallback))
        {
            result = ToResult(request, pipeline, executed, 0, request.PipelineId);
        }
        else if (fallbackRecord is null && primaryRecord is { Result.Succeeded: false })
        {
            // The recorded request failed too and did not run its fallback pipeline: no time was left (design 5.6).
            notes.Add($"the recording did not run fallback pipeline '{fallbackId}' (no time was left); neither does the replay");
            result = ToResult(request, pipeline, executed, 0, request.PipelineId);
        }
        else
        {
            if (fallbackReplay is null)
            {
                notes.Add($"fallback pipeline '{fallbackId}' did not run in the recording: its external calls are gaps");
                fallbackReplay = RunReplay.Empty();
            }

            served = await _executor.ExecuteAsync(fallback.Plan, ReplayRequest(request, RequestArms.None, fallbackReplay), cancellationToken).ConfigureAwait(false);
            result = Combine(request, executed, ToResult(request, fallback, served, 0, fallbackId), served, 0, fallbackId);
        }

        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return Outcome(bundle, result, recordedResult, recordedArms, pipeline, primaryRecord, executed, primaryReplay, (served, fallbackRecord, fallbackReplay),
            notes, elapsed, plan.ManifestId);
    }

    private static PipelineRequest ReplayRequest(RecRequest request, RequestArms arms, RunReplay replay) => new()
    {
        RequestId = request.RequestId,
        Inputs = request.Inputs,
        Arms = arms,
        Deadline = RunReplay.Deadline,
        Seed = request.Seed,
        TimeMs = request.TimeMs,
        CaptureLogs = true,
        Record = true, // keeps the replayed node decisions and parameters for the report
        Replay = replay,
    };

    private static ReplayOutcome Outcome(
        ReplayBundle bundle,
        RecResult result,
        ComparableResult recordedResult,
        IReadOnlyList<string> recordedArms,
        LoadedPipeline? pipeline,
        ReplayRunRecord? primaryRecord,
        PipelineResult? executed,
        RunReplay primaryReplay,
        (PipelineResult? Served, ReplayRunRecord? Record, RunReplay? Replay)? fallback,
        List<string> notes,
        double elapsedMs,
        string replayedManifest)
    {
        var nodes = CompareNodes(primaryRecord?.Nodes, executed?.Recording?.Nodes, null);
        var gaps = new List<ReplayGap>(primaryReplay.Gaps);
        var stats = new List<ReplayFieldStats>(primaryReplay.FieldStats);
        if (fallback is { } f)
        {
            var prefix = (f.Record?.PipelineId ?? f.Served?.Recording?.PipelineId ?? "fallback") + ":";
            nodes.AddRange(CompareNodes(f.Record?.Nodes, f.Served?.Recording?.Nodes, prefix));
            if (f.Replay is { } fallbackReplay)
            {
                gaps.AddRange(fallbackReplay.Gaps);
                stats.AddRange(fallbackReplay.FieldStats);
            }
        }

        return new ReplayOutcome
        {
            Bundle = bundle,
            Result = result,
            Report = new ReplayReport
            {
                RecordedManifest = bundle.Manifest.Id,
                ReplayedManifest = replayedManifest,
                Artifacts = CompareMaps(bundle.Manifest.Artifacts, pipeline?.ArtifactIds),
                RecordedArms = recordedArms,
                ReplayedArms = result.Experiments.Assigned,
                Notes = notes,
                Nodes = nodes,
                Params = CompareParams(primaryRecord?.Params, executed?.Recording?.Params),
                Gaps = gaps,
                FieldStats = stats,
                Diff = ResultComparison.Diff(recordedResult, ComparableResult.From(result)),
                ElapsedMs = elapsedMs,
            },
        };
    }

    private static (ReplayRunRecord? Run, int Index) FindRun(ReplayBundle bundle, string role)
    {
        for (var i = 0; i < bundle.Runs.Count; i++)
        {
            if (bundle.Runs[i].Role == role)
            {
                return (bundle.Runs[i], i);
            }
        }

        return (null, -1);
    }

    /// <summary>Decodes the recorded inputs with their recorded types; inputs the replayed request_schema does not declare are dropped.</summary>
    private static RequestInputs DecodeInputs(ReplayRequestRecord recorded, ExecutionPlan plan, List<string> notes)
    {
        var inputs = new RequestInputs();
        foreach (var (name, typed) in recorded.Inputs)
        {
            var pointer = "/request/inputs/" + JsonPointer.Escape(name);
            var value = ReplayJson.Value(typed.Value, ReplayJson.Type(typed.Type, pointer + "/type"), pointer + "/value");
            if (!plan.RequestInputs.ContainsKey(name))
            {
                notes.Add($"input '{name}' is not declared in the replayed request_schema; replayed without it");
                continue;
            }

            inputs.Set(name, value);
        }

        return inputs;
    }

    private static List<NodeComparison> CompareNodes(List<ReplayNodeRecord>? recorded, List<ReplayNodeRecord>? replayed, string? prefix)
    {
        var comparisons = new List<NodeComparison>();
        var replayedById = new Dictionary<string, ReplayNodeRecord>(StringComparer.Ordinal);
        foreach (var node in replayed ?? new List<ReplayNodeRecord>())
        {
            replayedById.TryAdd(node.Node, node);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in recorded ?? new List<ReplayNodeRecord>())
        {
            if (!seen.Add(node.Node))
            {
                continue;
            }

            replayedById.TryGetValue(node.Node, out var other);
            comparisons.Add(new NodeComparison(prefix + node.Node, node.Outcome, other?.Outcome, node.Detail, other?.Detail));
        }

        foreach (var node in replayed ?? new List<ReplayNodeRecord>())
        {
            if (seen.Add(node.Node))
            {
                comparisons.Add(new NodeComparison(prefix + node.Node, null, node.Outcome, null, node.Detail));
            }
        }

        return comparisons;
    }

    private static List<ValueChange> CompareParams(Dictionary<string, ReplayTypedValue>? recorded, Dictionary<string, ReplayTypedValue>? replayed)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        names.UnionWith(recorded?.Keys ?? (IEnumerable<string>)Array.Empty<string>());
        names.UnionWith(replayed?.Keys ?? (IEnumerable<string>)Array.Empty<string>());
        var changes = new List<ValueChange>();
        foreach (var name in names)
        {
            ReplayTypedValue? before = null;
            ReplayTypedValue? after = null;
            recorded?.TryGetValue(name, out before);
            replayed?.TryGetValue(name, out after);
            var typesDiffer = before is not null && after is not null && before.Type != after.Type;
            var left = Text(before, typesDiffer);
            var right = Text(after, typesDiffer);
            if (left != right)
            {
                changes.Add(new ValueChange(name, left, right));
            }
        }

        return changes;

        static string? Text(ReplayTypedValue? value, bool withType) => value is null
            ? null
            : (value.Value?.GetRawText() ?? "null") + (withType ? " (" + value.Type + ")" : string.Empty);
    }

    private static List<ValueChange> CompareMaps(IReadOnlyDictionary<string, string> recorded, IReadOnlyDictionary<string, string>? replayed)
    {
        var names = new SortedSet<string>(recorded.Keys, StringComparer.Ordinal);
        if (replayed is not null)
        {
            names.UnionWith(replayed.Keys);
        }

        var changes = new List<ValueChange>();
        foreach (var name in names)
        {
            recorded.TryGetValue(name, out var before);
            string? after = null;
            replayed?.TryGetValue(name, out after);
            if (before != after)
            {
                changes.Add(new ValueChange(name, before, after));
            }
        }

        return changes;
    }

    /// <summary>How the request's arms were chosen, as a bundle records it.</summary>
    private string AssignmentSource(LoadedPipeline pipeline, RecRequest request) =>
        request.Debug?.ForceArms is not null ? "forced"
        : request.Assignment is not null ? "external"
        : _assignmentProvider is not null ? "provider"
        : pipeline.Assigner is not null ? "builtin"
        : "none";

    private static RecResult WithRecording(RecResult result, ReplayBundle? bundle) => bundle is null ? result : new RecResult
    {
        Succeeded = result.Succeeded,
        Error = result.Error,
        Items = result.Items,
        Experiments = result.Experiments,
        ManifestId = result.ManifestId,
        ServedBy = result.ServedBy,
        ColumnProducers = result.ColumnProducers,
        Trace = result.Trace,
        LogBatches = result.LogBatches,
        Recording = bundle,
        ElapsedMs = result.ElapsedMs,
    };

    /// <summary>Assembles the bundle of a recorded request from its runs' recordings.</summary>
    private static ReplayBundle? Bundle(
        RecRequest request, LoadedPipeline pipeline, string assignmentSource, long seed, long timeMs, RecResult result, PipelineResult executed, PipelineResult? served)
    {
        if (executed.Recording is not { } primary)
        {
            return null;
        }

        var runs = new List<ReplayRunRecord> { primary };
        var serving = primary.Result;
        if (served?.Recording is { } fallbackRun)
        {
            fallbackRun.Role = "fallback_pipeline";
            runs.Add(fallbackRun);
            serving = fallbackRun.Result;
        }

        var inputs = new Dictionary<string, ReplayTypedValue>(StringComparer.Ordinal);
        foreach (var (name, value) in request.Inputs)
        {
            if (value.IsMissing)
            {
                continue;
            }

            // The declared type when the value has it; otherwise its own (the request failed validation, as recorded).
            var type = pipeline.Plan.RequestInputs.TryGetValue(name, out var field) && value.TryConvert(field.InputType, out _)
                ? field.InputType
                : ReplayJson.TypeOf(value);
            inputs[name] = ReplayJson.Typed(type, value);
        }

        return new ReplayBundle
        {
            Format = ReplayBundle.CurrentFormat,
            Request = new ReplayRequestRecord
            {
                PipelineId = request.PipelineId,
                RequestId = request.RequestId,
                Seed = seed,
                TimeMs = timeMs,
                DeadlineMs = request.Deadline.TotalMilliseconds,
                Inputs = inputs,
                Assignment = new ReplayAssignmentRecord
                {
                    Source = assignmentSource,
                    Arms = new List<string>(result.Experiments.Assigned),
                    UnknownArms = result.Experiments.UnknownArms,
                },
            },
            Manifest = new ReplayManifestRecord
            {
                Id = pipeline.Plan.ManifestId,
                Sequence = pipeline.Sequence,
                Artifacts = new Dictionary<string, string>(pipeline.ArtifactIds, StringComparer.Ordinal),
            },
            Runs = runs,
            Result = new ReplayResultRecord
            {
                ServedBy = result.ServedBy,
                Succeeded = result.Succeeded,
                Error = result.Error,
                Degraded = executed.Degraded || served is not null,
                ElapsedMs = result.ElapsedMs,
                ColumnTypes = serving.ColumnTypes,
                Items = serving.Items,
                TriggeredExperiments = new List<string>(result.Experiments.Triggered),
                ColumnProducers = serving.ColumnProducers,
            },
        };
    }
}
