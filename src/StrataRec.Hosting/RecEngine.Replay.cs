using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Values;
using StrataRec.Experiments.Assignment;
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
    /// <para>A replay is not traffic: it writes no logs (taps are captured into the result), records no metrics, event logs
    /// or spans, and does not count as a request of the pipeline or its arms.</para>
    /// <para>A bundle of an aborted request is not replayed: nothing executes, and the outcome carries the recorded reason,
    /// the node that was running and the recorded node decisions (<see cref="ReplayOutcome.NotReplayed"/>).</para>
    /// </remarks>
    /// <exception cref="ReplayBundleException">The bundle is malformed or of another format (checked here too, for a bundle
    /// a host deserialized itself); the pointer says where.</exception>
    /// <exception cref="ObjectDisposedException">The engine is disposed (and the bundle is one to replay).</exception>
    public async ValueTask<ReplayOutcome> ReplayAsync(ReplayBundle bundle, ReplayOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (Aborted(bundle) is { } aborted)
        {
            return aborted;
        }

        var recordedRequest = bundle.Request;
        var (primaryRecord, primaryIndex) = FindRun(bundle, "primary");
        var (fallbackRecord, fallbackIndex) = FindRun(bundle, "fallback_pipeline");

        // A run with a configuration override records no arms (its plan merged them in); the request's assignment has them.
        var recordedArms = recordedRequest.ConfigOverride is null ? primaryRecord?.Arms ?? recordedRequest.Assignment.Arms : recordedRequest.Assignment.Arms;

        var notes = new List<string>();
        var recordedResult = ComparableResult.From(bundle.Result, "/result");
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
            Debug = new DebugOptions { Trace = true, CaptureLogs = true, ExplainIds = options?.ExplainIds },
        };
        if (Enter(recordedRequest.PipelineId, out var disposed) is not { } pipeline)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(RecEngine), "The engine is disposed: it replays no request.");
            }

            var rejected = Rejected(request, RecErrorKind.UnknownPipeline, $"Unknown pipeline '{recordedRequest.PipelineId}'.");
            return Outcome(bundle, rejected, recordedResult, recordedArms, null, primaryRecord, null, primaryReplay, null, notes, 0, "<none>");
        }

        // Entered like a request, though a replay calls no client (the bundle answers them): one rule for every run of a plan.
        LoadedPipeline? fallback = null;
        LoadedPipeline? overridden = null;
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

            // The caller's mistakes first, as ExecuteAsync checks them (review 33): inputs the replayed plan rejects are an
            // invalid request at their pointer, and no fallback pipeline serves them, so a replay on a changed schema
            // answers what the engine would answer the request now.
            if (plan.RequestSchema.Check(inputs) is { } invalid)
            {
                var rejected = Rejected(request, RecErrorKind.InvalidRequest, RequestSchema.Describe(invalid), pointer: invalid.Pointer);
                return Outcome(bundle, rejected, recordedResult, recordedArms, null, primaryRecord, null, primaryReplay, null, notes, 0, plan.ManifestId);
            }

            if (options?.ExplainIds is { } explainIds && ExplainTargets.Check(explainIds, plan.TableSchema.KeyType, request.PipelineId) is { } explainError)
            {
                return Outcome(bundle, Rejected(request, RecErrorKind.InvalidRequest, explainError), recordedResult, recordedArms, null, primaryRecord, null, primaryReplay, null,
                    notes, 0, plan.ManifestId);
            }

            // The arms to run, resolved over the replayed plan as the request's assignment would be now (reviews 96, 98).
            // A recording of the platform's arms (external, provider) holds every id the platform sent, those that ran and
            // those it ignored for any reason; they go through the platform's rule again, so a changed plan ignores (an
            // unknown arm, a layer conflict), runs or yields them as it would the request. Forced arms, and arms passed to
            // the replay, are strict, as forced arms are in a request: an arm the plan does not have is left out with a
            // note, two of one layer reject the replay. Yields come last, for every source. The input is the request's
            // assignment record (the arms of its primary run are what the report compares with): in a bundle the engine
            // wrote they agree, and an edited assignment shows as replayed arms that differ from the recorded ones.
            var recordedAssigned = recordedRequest.Assignment.Arms;
            var recordedIgnored = options?.Arms is null ? IgnoredArms(recordedRequest.Assignment) : Array.Empty<IgnoredArm>();
            RequestArms arms;
            IgnoredArm[] ignored;
            if (options?.Arms is null && recordedRequest.Assignment.Source is "external" or "provider")
            {
                var sent = new List<string>(recordedAssigned);
                foreach (var arm in recordedIgnored)
                {
                    sent.Add(arm.Id);
                }

                arms = ExternalArms.Resolve(plan.Layers.AsSpan(), plan.Arms.AsSpan(), sent, out ignored);
            }
            else
            {
                // A forced recording's yielded arms were forced too.
                var forced = new List<string>(options?.Arms ?? recordedAssigned);
                foreach (var arm in recordedIgnored)
                {
                    if (arm.Reason == IgnoredArmReason.Yielded)
                    {
                        forced.Add(arm.Id);
                    }
                }

                var known = new List<string>();
                foreach (var arm in forced)
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

                try
                {
                    arms = plan.ArmsFromIds(known);
                }
                catch (ArgumentException ex)
                {
                    return Outcome(bundle, Rejected(request, RecErrorKind.InvalidRequest, ex.Message), recordedResult, recordedArms, null, primaryRecord, null, primaryReplay, null,
                        notes, 0, plan.ManifestId);
                }

                ignored = Array.Empty<IgnoredArm>();
            }

            var assigned = arms;
            arms = ExternalArms.Yield(plan, assigned, ignored, out ignored);
            Notes(plan, recordedAssigned, recordedIgnored, assigned, arms, ignored, notes);

            // The recorded configuration override, applied again over the replayed plan with the replayed arms, as the
            // request applied it (DebugOptions.ConfigOverride); its run has the arms merged in.
            var running = pipeline;
            IReadOnlyList<string>? merged = null;
            if (recordedRequest.ConfigOverride is { } configOverride)
            {
                merged = plan.ArmIds(arms);
                var compiled = await CompileOverrideAsync(pipeline, configOverride, merged, RunReplay.Deadline, cancellationToken).ConfigureAwait(false);
                if (compiled.Pipeline is null)
                {
                    var rejected = Rejected(request, compiled.ErrorKind, compiled.Error!, new ExperimentRecord(merged, Array.Empty<string>(), ignored), plan.ManifestId,
                        compiled.Pointer);
                    return Outcome(bundle, rejected, recordedResult, recordedArms, pipeline, primaryRecord, null, primaryReplay, null, notes, 0, plan.ManifestId);
                }

                running = overridden = compiled.Pipeline;
                arms = RequestArms.None;
            }

            var started = Stopwatch.GetTimestamp();
            var replayed = new RunCapture(); // the replayed runs' recordings
            RecResult result;
            var executed = await _executor.ExecuteAsync(running.Plan, ReplayRequest(request, request.Inputs, arms, primaryReplay, replayed, running), cancellationToken)
                .ConfigureAwait(false);
            if (executed.Succeeded || pipeline.FallbackPipeline is not { } fallbackId || fallbackId == request.PipelineId
                || (fallback = Enter(fallbackId, out _)) is null)
            {
                if (!executed.Succeeded && fallbackRecord is not null)
                {
                    // Not a difference of the replayed configuration's behavior: what the request failed over to is absent.
                    notes.Add(pipeline.FallbackPipeline is { } configured && configured != request.PipelineId
                        ? $"the recording ran fallback pipeline '{fallbackRecord.PipelineId}' (manifest {fallbackRecord.ManifestId}), and this engine has not loaded '{configured}': the replay ends with the primary pipeline's failure"
                        : $"the recording ran fallback pipeline '{fallbackRecord.PipelineId}', and the replayed configuration has none");
                }

                result = ToResult(request, pipeline, executed, ignored, request.PipelineId, merged);
            }
            else if (fallbackRecord is null && primaryRecord is { Result.Succeeded: false })
            {
                // The recorded request failed too and did not run its fallback pipeline: no time was left (design 5.6).
                notes.Add($"the recording did not run fallback pipeline '{fallbackId}' (no time was left); neither does the replay");
                result = ToResult(request, pipeline, executed, ignored, request.PipelineId, merged);
            }
            else
            {
                if (fallbackReplay is null)
                {
                    notes.Add($"fallback pipeline '{fallbackId}' did not run in the recording: its external calls are gaps");
                    fallbackReplay = RunReplay.Empty();
                }

                var fallbackInputs = FallbackInputs(request.Inputs, fallback.Plan.RequestSchema); // as the request did (ExecuteAsync)
                var served = await _executor.ExecuteAsync(fallback.Plan, ReplayRequest(request, fallbackInputs, RequestArms.None, fallbackReplay, replayed, fallback), cancellationToken)
                    .ConfigureAwait(false);
                result = Combine(request, executed, ToResult(request, fallback, served, ignored, fallbackId), served, ignored, fallbackId, merged);
            }

            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var primaryReplayed = replayed.Runs.Count > 0 ? replayed.Runs[0] : null;
            var fallbackReplayed = replayed.Runs.Count > 1 ? replayed.Runs[1] : null;
            return Outcome(bundle, result, recordedResult, recordedArms, pipeline, primaryRecord, primaryReplayed, primaryReplay,
                (fallbackReplayed, fallbackRecord, fallbackReplay), notes, elapsed, plan.ManifestId);
        }
        finally
        {
            pipeline.Bindings.RequestEnded();
            fallback?.Bindings.RequestEnded();
            EndOverride(overridden);
        }
    }

    /// <param name="replayed">Collects the replayed run's recording: its node decisions and parameters, for the report.</param>
    /// <param name="pipeline">Entered by the caller; the run joins its lease.</param>
    private static PipelineRequest ReplayRequest(
        RecRequest request, RequestInputs inputs, RequestArms arms, RunReplay replay, RunCapture replayed, LoadedPipeline pipeline) => new()
    {
        RequestId = request.RequestId,
        Inputs = inputs,
        Arms = arms,
        Deadline = RunReplay.Deadline,
        Seed = request.Seed,
        TimeMs = request.TimeMs,
        CaptureLogs = true,
        ExplainIds = request.Debug?.ExplainIds,
        OnRecording = replayed.Add,
        Replay = replay,
        OnEnd = pipeline.Bindings.Join(), // last: the executor, called next, ends it on every outcome
    };

    /// <summary>
    /// What <see cref="ReplayAsync"/> gives for a bundle that records an aborted request, which needs no engine (nothing is
    /// replayed): the recorded reason, the node that was running and the recorded node decisions
    /// (<see cref="ReplayOutcome.NotReplayed"/>), and a placeholder failure. Null for a bundle with a result. A tool that
    /// finds a configuration for each bundle asks this first: an aborted request's configuration need not be available.
    /// </summary>
    /// <exception cref="ReplayBundleException">The bundle is malformed; the pointer says where.</exception>
    public static ReplayOutcome? Aborted(ReplayBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ReplayBundleFile.CheckFormat(bundle); // a bundle object a host deserialized itself is checked as a file is (review 89)
        if (!bundle.Result.Aborted)
        {
            return null;
        }

        var (primaryRecord, _) = FindRun(bundle, "primary");
        var (fallbackRecord, _) = FindRun(bundle, "fallback_pipeline");
        var recordedArms = bundle.Request.ConfigOverride is null ? primaryRecord?.Arms ?? bundle.Request.Assignment.Arms : bundle.Request.Assignment.Arms;
        return NotReplayed(bundle, recordedArms, primaryRecord, fallbackRecord);
    }

    private static ReplayOutcome NotReplayed(ReplayBundle bundle, IReadOnlyList<string> recordedArms, ReplayRunRecord? primary, ReplayRunRecord? fallback)
    {
        var nodes = CompareNodes(primary?.Nodes, null, null);
        if (fallback is not null)
        {
            nodes.AddRange(CompareNodes(fallback.Nodes, null, fallback.PipelineId + ":"));
        }

        string? running = null;
        foreach (var node in nodes)
        {
            if (node.Recorded == NodeOutcomeNames.Name(NodeOutcomeKind.Aborted))
            {
                running = node.Node; // the last run's: the request was aborted in it
            }
        }

        var reason = bundle.Result.Error ?? "aborted";
        return new ReplayOutcome
        {
            Bundle = bundle,
            Result = new RecResult
            {
                RequestId = bundle.Request.RequestId,
                Succeeded = false,
                ErrorKind = RecErrorKind.Failed,
                Error = $"not replayed: the recorded request was aborted ({reason}) and has no result to reproduce",
                Columns = ResultColumns.Empty,
                Experiments = new ExperimentRecord(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<IgnoredArm>()),
                ManifestId = string.Empty,
                ServedBy = bundle.Request.PipelineId,
            },
            NotReplayed = new RecordedAbort(reason, running),
            Report = new ReplayReport
            {
                RecordedManifest = bundle.Manifest.Id,
                ReplayedManifest = "<none>",
                Artifacts = Array.Empty<ValueChange>(),
                RecordedArms = recordedArms,
                ReplayedArms = Array.Empty<string>(),
                Notes = Array.Empty<string>(),
                Nodes = nodes,
                Params = Array.Empty<ValueChange>(),
                Gaps = Array.Empty<ReplayGap>(),
                FieldStats = Array.Empty<ReplayFieldStats>(),
                // Nothing ran, so nothing was reproduced: the placeholder is not compared with the recorded result.
                Diff = new ResultDiff
                {
                    Identical = false,
                    LeftSucceeded = false,
                    RightSucceeded = false,
                    LeftCount = bundle.Result.Items.Count,
                    RightCount = 0,
                    Overlap = 0,
                    KendallTau = null,
                    AddedColumns = Array.Empty<string>(),
                    RemovedColumns = Array.Empty<string>(),
                    Changes = Array.Empty<ItemChange>(),
                },
            },
        };
    }

    private static ReplayOutcome Outcome(
        ReplayBundle bundle,
        RecResult result,
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
            if (!plan.RequestSchema.Contains(name))
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
        : "none";

    private static RecResult WithRecording(RecResult result, ReplayBundle? bundle) => bundle is null ? result : new RecResult
    {
        RequestId = result.RequestId,
        Succeeded = result.Succeeded,
        ErrorKind = result.ErrorKind,
        Error = result.Error,
        ErrorPointer = result.ErrorPointer,
        Columns = result.Columns,
        Experiments = result.Experiments,
        ManifestId = result.ManifestId,
        ServedBy = result.ServedBy,
        ColumnProducers = result.ColumnProducers,
        Trace = result.Trace,
        LogBatches = result.LogBatches,
        Recording = bundle,
        Explanation = result.Explanation,
        ElapsedMs = result.ElapsedMs,
    };

    /// <summary>The result of a recorded request with its bundle, which also goes to <see cref="DebugOptions.OnRecording"/>.</summary>
    /// <param name="configOverride">The request's configuration override as its plan was compiled with it (<see cref="LoadedPipeline.ConfigOverride"/>); null without one.</param>
    private RecResult Recorded(
        RecRequest request, LoadedPipeline pipeline, long seed, long timeMs, RecResult result, PipelineResult executed, PipelineResult? served,
        JsonObject? configOverride)
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
            Request = RequestRecord(request, pipeline, seed, timeMs, result.Experiments.Assigned, result.Experiments.Ignored, configOverride),
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
    /// <param name="merged">The arms a configuration override's plan merged in; its run records none (see <see cref="ToResult"/>).</param>
    /// <param name="configOverride">That override as the plan was compiled with it (<see cref="LoadedPipeline.ConfigOverride"/>); null without one.</param>
    private ReplayBundle AbortedBundle(
        RecRequest request, LoadedPipeline pipeline, long seed, long timeMs, List<ReplayRunRecord> runs, IReadOnlyList<IgnoredArm> ignored,
        IReadOnlyList<string>? merged, JsonObject? configOverride)
    {
        var primary = runs[0];
        var last = runs[^1];
        var elapsedMs = 0.0;
        for (var i = 0; i < runs.Count; i++)
        {
            runs[i].Role = i == 0 ? "primary" : "fallback_pipeline";
            elapsedMs += runs[i].Result.ElapsedMs;

            // A run that finished (the primary, its fallback pipeline aborted) captured its batches for a result the caller
            // never gets: they went nowhere, as the aborted run's did (review 79).
            if (runs[i].Sampling.Logs == "captured")
            {
                runs[i].Sampling.Logs = "none";
            }
        }

        return new ReplayBundle
        {
            Format = ReplayBundle.CurrentFormat,
            Request = RequestRecord(request, pipeline, seed, timeMs, merged ?? primary.Arms, ignored, configOverride),
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

    /// <param name="configOverride">
    /// The request's configuration override as its plan was compiled with it, not the host's tree (<see cref="DebugOptions.ConfigOverride"/>):
    /// copying that would write the CLR values the host wrapped once more, and a value that writes something else the
    /// second time would be recorded as what did not run (review 83).
    /// </param>
    private ReplayRequestRecord RequestRecord(
        RecRequest request, LoadedPipeline pipeline, long seed, long timeMs, IReadOnlyList<string> arms, IReadOnlyList<IgnoredArm> ignored,
        JsonObject? configOverride)
    {
        var inputs = new Dictionary<string, ReplayTypedValue>(StringComparer.Ordinal);
        foreach (var (name, value) in request.Inputs)
        {
            if (value.IsMissing)
            {
                continue;
            }

            // The value as the host gave it, at its own (promoted) type, not the declared one: decoded, it is that value
            // again, and each pipeline that binds it converts it as it was served (a fallback pipeline may declare the
            // input otherwise; an int64 recorded as the primary's float32 lost its digits for an int64 fallback).
            inputs[name] = ReplayJson.Typed(ReplayJson.TypeOf(value), value);
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
                Ignored = IgnoredRecords(ignored),
            },
            ConfigOverride = configOverride?.DeepClone().AsObject(), // each bundle its own: the caller may change it
        };
    }

    private static List<ReplayIgnoredArm> IgnoredRecords(IReadOnlyList<IgnoredArm> ignored)
    {
        var records = new List<ReplayIgnoredArm>(ignored.Count);
        foreach (var arm in ignored)
        {
            records.Add(new ReplayIgnoredArm { Id = arm.Id, Reason = IgnoredArm.Name(arm.Reason) });
        }

        return records;
    }

    /// <summary>
    /// Notes for the arms a replay runs otherwise than the recording: one that ran and the replayed plan ignores (the
    /// strict sources note an unknown arm where they leave it out), and one the recording ignored that runs now.
    /// </summary>
    private static void Notes(
        ExecutionPlan plan, IReadOnlyList<string> recordedArms, IgnoredArm[] recordedIgnored, in RequestArms assigned, in RequestArms running, IgnoredArm[] ignored,
        List<string> notes)
    {
        foreach (var arm in ignored)
        {
            if (!Contains(recordedArms, arm.Id))
            {
                continue;
            }

            if (arm.Reason == IgnoredArmReason.Yielded && plan.TryGetArm(arm.Id, out var info))
            {
                notes.Add($"arm '{arm.Id}' yields to '{plan.Experiments[plan.Yields.YieldsTo(info.Index, assigned)].Id}' in the replayed plan; replayed without it");
            }
            else if (arm.Reason == IgnoredArmReason.Unknown)
            {
                notes.Add($"arm '{arm.Id}' is not in the replayed plan; replayed without it");
            }
            else if (arm.Reason == IgnoredArmReason.LayerConflict)
            {
                notes.Add($"arm '{arm.Id}' is in a layer with another arm of the request in the replayed plan (layer_conflict); replayed without it");
            }
        }

        foreach (var arm in recordedIgnored)
        {
            // An id listed twice (an edited bundle) runs once and is noted once.
            if (plan.TryGetArm(arm.Id, out var info) && running.ArmIn(info.LayerIndex) == info.Index && !Contains(notes, Ran(arm)))
            {
                notes.Add(Ran(arm));
            }
        }
    }

    private static string Ran(IgnoredArm arm) => $"arm '{arm.Id}' was ignored in the recording ({IgnoredArm.Name(arm.Reason)}) and runs in the replayed plan";

    private static bool Contains(IReadOnlyList<string> ids, string id)
    {
        foreach (var candidate in ids)
        {
            if (string.Equals(candidate, id, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The ignored arms a bundle recorded.</summary>
    /// <exception cref="ReplayBundleException">A reason this build does not know.</exception>
    private static IgnoredArm[] IgnoredArms(ReplayAssignmentRecord assignment)
    {
        if (assignment.Ignored.Count == 0)
        {
            return Array.Empty<IgnoredArm>();
        }

        var ignored = new IgnoredArm[assignment.Ignored.Count];
        for (var i = 0; i < ignored.Length; i++)
        {
            var record = assignment.Ignored[i];
            if (!IgnoredArm.TryParse(record.Reason, out var reason))
            {
                throw new ReplayBundleException($"/request/assignment/ignored/{i}/reason", $"unknown reason '{record.Reason}'", "a reason is unknown, layer_conflict or yielded");
            }

            ignored[i] = new IgnoredArm(record.Id, reason);
        }

        return ignored;
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
