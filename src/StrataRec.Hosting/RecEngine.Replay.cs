using System.Diagnostics;
using Microsoft.Extensions.Logging;
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
    /// <exception cref="ObjectDisposedException">The engine is disposed.</exception>
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
        if (Enter(recordedRequest.PipelineId, out var disposed) is not { } pipeline)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(RecEngine), "The engine is disposed: it replays no request.");
            }

            var rejected = Rejected(request, $"Unknown pipeline '{recordedRequest.PipelineId}'.");
            return Outcome(bundle, rejected, null, recordedResult, recordedArms, null, primaryRecord, null, primaryReplay, null, notes, 0, "<none>");
        }

        // Entered like a request, though a replay calls no client (the bundle answers them): one rule for every run of a plan.
        LoadedPipeline? fallback = null;
        try
        {
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
                return Outcome(bundle, Rejected(request, ex.Message), null, recordedResult, recordedArms, null, primaryRecord, null, primaryReplay, null, notes, 0,
                    plan.ManifestId);
            }

            // The external arms the recorded request ignored as unknown are part of its assignment record; arms passed to
            // the replay replace that assignment, so it then has none.
            var unknown = options?.Arms is null ? recordedRequest.Assignment.UnknownArms : 0;
            var started = Stopwatch.GetTimestamp();
            var replayed = new RunCapture(); // the replayed runs' recordings, also of a run that stops where its recording was aborted
            RecResult result;
            string? aborted = null;
            try
            {
                var executed = await _executor.ExecuteAsync(plan, ReplayRequest(request, arms, primaryReplay, replayed, pipeline), cancellationToken)
                    .ConfigureAwait(false);
                if (executed.Succeeded || pipeline.FallbackPipeline is not { } fallbackId || fallbackId == request.PipelineId
                    || (fallback = Enter(fallbackId, out _)) is null)
                {
                    result = ToResult(request, pipeline, executed, unknown, request.PipelineId);
                }
                else if (fallbackRecord is null && primaryRecord is { Result.Succeeded: false } failed)
                {
                    // The recorded request failed too and did not run its fallback pipeline: no time was left (design 5.6), or
                    // it was aborted first.
                    notes.Add($"the recording did not run fallback pipeline '{fallbackId}' ({(failed.Result.Aborted ? "it was aborted" : "no time was left")}); " +
                        "neither does the replay");
                    result = ToResult(request, pipeline, executed, unknown, request.PipelineId);
                }
                else
                {
                    if (fallbackReplay is null)
                    {
                        notes.Add($"fallback pipeline '{fallbackId}' did not run in the recording: its external calls are gaps");
                        fallbackReplay = RunReplay.Empty();
                    }

                    var served = await _executor.ExecuteAsync(fallback.Plan, ReplayRequest(request, RequestArms.None, fallbackReplay, replayed, fallback), cancellationToken)
                        .ConfigureAwait(false);
                    result = Combine(request, executed, ToResult(request, fallback, served, unknown, fallbackId), served, unknown, fallbackId);
                }
            }
            catch (ReplayAbortedException ex)
            {
                // The recorded request was aborted: the replay stops where the recording does and reports that, not a result.
                aborted = ex.Message;
                result = AbortedResult(request, replayed.Runs, unknown, ex.Message);
            }

            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var primaryReplayed = replayed.Runs.Count > 0 ? replayed.Runs[0] : null;
            var fallbackReplayed = replayed.Runs.Count > 1 ? replayed.Runs[1] : null;
            return Outcome(bundle, result, aborted, recordedResult, recordedArms, pipeline, primaryRecord, primaryReplayed, primaryReplay,
                (fallbackReplayed, fallbackRecord, fallbackReplay), notes, elapsed, plan.ManifestId);
        }
        finally
        {
            pipeline.Bindings.RequestEnded();
            fallback?.Bindings.RequestEnded();
        }
    }

    /// <param name="replayed">Collects the replayed run's recording: its node decisions and parameters, for the report.</param>
    /// <param name="pipeline">Entered by the caller; the run joins its lease.</param>
    private static PipelineRequest ReplayRequest(RecRequest request, RequestArms arms, RunReplay replay, RunCapture replayed, LoadedPipeline pipeline) => new()
    {
        RequestId = request.RequestId,
        Inputs = request.Inputs,
        Arms = arms,
        Deadline = RunReplay.Deadline,
        Seed = request.Seed,
        TimeMs = request.TimeMs,
        CaptureLogs = true,
        OnRecording = replayed.Add,
        Replay = replay,
        OnEnd = pipeline.Bindings.Join(), // last: the executor, called next, ends it on every outcome
    };

    /// <summary>The result of a replay that stopped where its recording was aborted: a failure with the recorded reason and no items.</summary>
    private static RecResult AbortedResult(RecRequest request, List<ReplayRunRecord> replayed, int unknown, string reason)
    {
        var primary = replayed.Count > 0 ? replayed[0] : null;
        var last = replayed.Count > 0 ? replayed[^1] : null;
        return new RecResult
        {
            Succeeded = false,
            Error = reason,
            Items = Array.Empty<RankedItem>(),
            Experiments = new ExperimentRecord(primary?.Arms ?? new List<string>(), primary?.Result.TriggeredExperiments ?? new List<string>(), unknown),
            ManifestId = last?.ManifestId ?? string.Empty,
            ServedBy = last?.PipelineId ?? request.PipelineId,
        };
    }

    private static ReplayOutcome Outcome(
        ReplayBundle bundle,
        RecResult result,
        string? aborted,
        ComparableResult recordedResult,
        IReadOnlyList<string> recordedArms,
        LoadedPipeline? pipeline,
        ReplayRunRecord? primaryRecord,
        ReplayRunRecord? primaryReplayed,
        RunReplay primaryReplay,
        (ReplayRunRecord? Replayed, ReplayRunRecord? Record, RunReplay? Replay)? fallback,
        List<string> notes,
        double elapsedMs,
        string replayedManifest)
    {
        var nodes = CompareNodes(primaryRecord?.Nodes, primaryReplayed?.Nodes, null);
        var gaps = new List<ReplayGap>(primaryReplay.Gaps);
        var stats = new List<ReplayFieldStats>(primaryReplay.FieldStats);
        if (fallback is { } f)
        {
            var prefix = (f.Record?.PipelineId ?? f.Replayed?.PipelineId ?? "fallback") + ":";
            nodes.AddRange(CompareNodes(f.Record?.Nodes, f.Replayed?.Nodes, prefix));
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
            AbortReason = aborted,
            Report = new ReplayReport
            {
                RecordedManifest = bundle.Manifest.Id,
                ReplayedManifest = replayedManifest,
                Artifacts = CompareMaps(bundle.Manifest.Artifacts, pipeline?.ArtifactIds),
                RecordedArms = recordedArms,
                ReplayedArms = result.Experiments.Assigned,
                Notes = notes,
                Nodes = nodes,
                Params = CompareParams(primaryRecord?.Params, primaryReplayed?.Params),
                Gaps = gaps,
                FieldStats = stats,
                Diff = ResultComparison.Diff(recordedResult, ComparableResult.From(result, aborted: aborted is not null)),
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

    /// <summary>The result of a recorded request with its bundle, which also goes to <see cref="DebugOptions.OnRecording"/>.</summary>
    private RecResult Recorded(
        RecRequest request, LoadedPipeline pipeline, long seed, long timeMs, RecResult result, PipelineResult executed, PipelineResult? served)
    {
        if (executed.Recording is not { } primary)
        {
            return result;
        }

        var runs = new List<ReplayRunRecord> { primary };
        var serving = primary.Result;
        if (served?.Recording is { } fallbackRun)
        {
            fallbackRun.Role = "fallback_pipeline";
            runs.Add(fallbackRun);
            serving = fallbackRun.Result;
        }

        var bundle = new ReplayBundle
        {
            Format = ReplayBundle.CurrentFormat,
            Request = RequestRecord(request, pipeline, seed, timeMs, result.Experiments.Assigned, result.Experiments.UnknownArms),
            Manifest = ManifestRecord(pipeline),
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
        Deliver(request, bundle);
        return WithRecording(result, bundle);
    }

    /// <summary>
    /// The bundle of a request aborted while or after a pipeline ran: every run recorded so far (the last one aborted, as a
    /// snapshot taken at the abort) and an aborted result.
    /// </summary>
    private ReplayBundle AbortedBundle(RecRequest request, LoadedPipeline pipeline, long seed, long timeMs, List<ReplayRunRecord> runs, int unknown)
    {
        var primary = runs[0];
        var last = runs[^1];
        var elapsedMs = 0.0;
        for (var i = 0; i < runs.Count; i++)
        {
            runs[i].Role = i == 0 ? "primary" : "fallback_pipeline";
            elapsedMs += runs[i].Result.ElapsedMs;
        }

        return new ReplayBundle
        {
            Format = ReplayBundle.CurrentFormat,
            Request = RequestRecord(request, pipeline, seed, timeMs, primary.Arms, unknown),
            Manifest = ManifestRecord(pipeline),
            Runs = new List<ReplayRunRecord>(runs),
            Result = new ReplayResultRecord
            {
                ServedBy = last.PipelineId,
                Succeeded = false,
                Aborted = true,
                Error = last.Result.Error,
                ElapsedMs = elapsedMs,
                ColumnTypes = last.Result.ColumnTypes,
                TriggeredExperiments = new List<string>(primary.Result.TriggeredExperiments),
            },
        };
    }

    private ReplayRequestRecord RequestRecord(RecRequest request, LoadedPipeline pipeline, long seed, long timeMs, IReadOnlyList<string> arms, int unknown)
    {
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

        return new ReplayRequestRecord
        {
            PipelineId = request.PipelineId,
            RequestId = request.RequestId,
            Seed = seed,
            TimeMs = timeMs,
            DeadlineMs = request.Deadline.TotalMilliseconds,
            Inputs = inputs,
            Assignment = new ReplayAssignmentRecord
            {
                Source = AssignmentSource(pipeline, request),
                Arms = new List<string>(arms),
                UnknownArms = unknown,
            },
        };
    }

    private static ReplayManifestRecord ManifestRecord(LoadedPipeline pipeline) => new()
    {
        Id = pipeline.Plan.ManifestId,
        Sequence = pipeline.Sequence,
        Artifacts = new Dictionary<string, string>(pipeline.ArtifactIds, StringComparer.Ordinal),
    };

    /// <summary>Hands a bundle to <see cref="DebugOptions.OnRecording"/>; a callback that throws is logged and changes nothing else.</summary>
    private void Deliver(RecRequest request, ReplayBundle bundle)
    {
        if (request.Debug?.OnRecording is not { } deliver)
        {
            return;
        }

        try
        {
            deliver(bundle);
        }
        catch (Exception ex)
        {
            LogRecordingCallbackFailed(_logger, ex, request.PipelineId, request.RequestId);
        }
    }

    [LoggerMessage(EventId = 1101, Level = LogLevel.Warning,
        Message = "The recording callback of request '{RequestId}' (pipeline '{PipelineId}') threw; the request's outcome is unchanged")]
    private static partial void LogRecordingCallbackFailed(ILogger logger, Exception exception, string pipelineId, string requestId);

    /// <summary>The run recordings of one request (or replay), in execution order, as the executor delivers them on every exit.</summary>
    private sealed class RunCapture
    {
        public List<ReplayRunRecord> Runs { get; } = new(2);

        public void Add(ReplayRunRecord run) => Runs.Add(run);
    }
}
