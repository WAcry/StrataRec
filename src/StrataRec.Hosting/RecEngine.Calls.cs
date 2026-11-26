using System.Diagnostics;
using StrataRec.Core.Hashing;
using StrataRec.Core.Threading;
using StrataRec.Core.Values;
using StrataRec.Experiments.Assignment;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Hosting;

public sealed partial class RecEngine
{
    /// <summary>
    /// How deep the pipelines a request's nodes call may nest. A workspace's compile refuses calls that lead back to their
    /// pipeline; pipelines loaded each from its own release are not compiled together, so a cycle among them is stopped
    /// here, as a failed call.
    /// </summary>
    internal const int MaxCallDepth = 8;

    /// <summary>
    /// What the pipelines a request's nodes called ran, for the request's result: the arms they ran and the experiments
    /// they triggered, the platform's arms they ignored, and their captured log batches. One per request that calls any,
    /// shared by its runs; calls may end on any thread.
    /// </summary>
    private sealed class RequestCalls(RecEngine engine, RecRequest request)
    {
        private readonly Lock _gate = new();
        private readonly SortedSet<string> _assigned = new(StringComparer.Ordinal);
        private readonly SortedSet<string> _triggered = new(StringComparer.Ordinal);
        private List<IgnoredArm>? _ignored;
        private List<Logging.LogBatch>? _batches;
        private List<CalledRun>? _runs;

        public RecEngine Engine => engine;

        public RecRequest Request => request;

        /// <summary>The request asked for its trace: the called runs are kept with theirs (<see cref="RecResult.Calls"/>).</summary>
        public bool Traces => request.Debug?.Trace == true;

        public void Add(CalledRun run)
        {
            lock (_gate)
            {
                (_runs ??= new List<CalledRun>()).Add(run);
            }
        }

        public void Add(PipelineResult result, IgnoredArm[] ignored)
        {
            lock (_gate)
            {
                foreach (var arm in result.AssignedArms)
                {
                    _assigned.Add(arm);
                }

                foreach (var experiment in result.TriggeredExperiments)
                {
                    _triggered.Add(experiment);
                }

                if (ignored.Length > 0)
                {
                    (_ignored ??= new List<IgnoredArm>()).AddRange(ignored);
                }

                if (result.LogBatches.Count > 0)
                {
                    (_batches ??= new List<Logging.LogBatch>()).AddRange(result.LogBatches);
                }
            }
        }

        /// <summary>
        /// The request's result with what its called pipelines ran: their arms and triggered experiments after its own (in
        /// ordinal order, each once), the arms they ignored, and their captured batches after its own.
        /// </summary>
        public RecResult Merge(RecResult result)
        {
            lock (_gate)
            {
                if (_assigned.Count == 0 && _triggered.Count == 0 && _ignored is null && _batches is null && _runs is null)
                {
                    return result;
                }

                // Calls end in any order: listed by who called, so the same request lists them alike.
                _runs?.Sort(static (a, b) => string.CompareOrdinal(a.Caller, b.Caller) is not 0 and var byCaller ? byCaller
                    : string.CompareOrdinal(a.Node, b.Node) is not 0 and var byNode ? byNode
                    : a.Sequence.CompareTo(b.Sequence));

                var ignored = Ignored(result.Experiments.Ignored);

                IReadOnlyList<Logging.LogBatch>? batches = result.LogBatches;
                if (batches is not null && _batches is not null)
                {
                    var all = new List<Logging.LogBatch>(batches);
                    all.AddRange(_batches);
                    batches = all;
                }

                return new RecResult
                {
                    RequestId = result.RequestId,
                    Succeeded = result.Succeeded,
                    ErrorKind = result.ErrorKind,
                    Error = result.Error,
                    ErrorPointer = result.ErrorPointer,
                    Columns = result.Columns,
                    Values = result.Values,
                    Experiments = new ExperimentRecord(With(result.Experiments.Assigned, _assigned), With(result.Experiments.Triggered, _triggered), ignored),
                    ManifestId = result.ManifestId,
                    ServedBy = result.ServedBy,
                    ColumnProducers = result.ColumnProducers,
                    Trace = result.Trace,
                    Calls = _runs is null ? result.Calls : new List<CalledRun>(_runs),
                    LogBatches = batches,
                    Recording = result.Recording,
                    Explanation = result.Explanation,
                    ElapsedMs = result.ElapsedMs,
                };
            }
        }

        /// <summary>
        /// The request's ignored arms with those of its called runs: each id and reason once (a pipeline called twice
        /// ignores the same arm twice), by id as the record has them, and without an arm the request's pipeline did not
        /// know that a called run ran (a pipeline its plan does not name, called by a configuration override).
        /// </summary>
        private IReadOnlyList<IgnoredArm> Ignored(IReadOnlyList<IgnoredArm> own)
        {
            if (_ignored is null && (_assigned.Count == 0 || own.Count == 0))
            {
                return own;
            }

            var all = new SortedSet<IgnoredArm>(ByIdThenReason);
            foreach (var arm in own)
            {
                if (arm.Reason != IgnoredArmReason.Unknown || !_assigned.Contains(arm.Id))
                {
                    all.Add(arm);
                }
            }

            if (_ignored is not null)
            {
                foreach (var arm in _ignored)
                {
                    all.Add(arm);
                }
            }

            return [.. all];
        }

        private static readonly Comparer<IgnoredArm> ByIdThenReason = Comparer<IgnoredArm>.Create(static (a, b) =>
            string.CompareOrdinal(a.Id, b.Id) is not 0 and var byId ? byId : a.Reason.CompareTo(b.Reason));

        private static IReadOnlyList<string> With(IReadOnlyList<string> own, SortedSet<string> called)
        {
            if (called.Count == 0)
            {
                return own;
            }

            var all = new List<string>(own.Count + called.Count);
            all.AddRange(own);
            foreach (var id in called)
            {
                if (!Contains(own, id))
                {
                    all.Add(id);
                }
            }

            return all;
        }
    }

    /// <summary>
    /// The calls of one run (<see cref="OperatorContext.CallPipelineAsync"/>): each runs the pipeline this engine serves
    /// under that id, for the same request.
    /// </summary>
    /// <param name="pipelineId">The pipeline of the run that calls.</param>
    /// <param name="depth">How many calls led to that run: 0 for a run of the request's own pipeline.</param>
    /// <param name="parent">The calls of the run that called it; null at depth 0.</param>
    private sealed class RunCalls(RequestCalls scope, string pipelineId, int depth, RunCalls? parent = null) : IPipelineCalls
    {
        public async ValueTask<PipelineCallResult> CallAsync(PipelineCallRequest call, CancellationToken callerCancellation)
        {
            var started = Stopwatch.GetTimestamp();
            var id = call.Call.PipelineId;
            PipelineResult? ran = null;
            var answer = await RunAsync().ConfigureAwait(false);
            if (scope.Traces)
            {
                scope.Add(new CalledRun(pipelineId, call.Call.NodeId, call.Sequence, id, answer.Succeeded, answer.Error,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds, ran?.AssignedArms ?? [], ran?.Outcomes ?? []));
            }

            return answer;

            async ValueTask<PipelineCallResult> RunAsync()
            {
                if (Chain(id) is { } chain)
                {
                    return PipelineCallResult.Failed(id, $"pipeline '{id}' was not called: the calls lead back to it ({chain})");
                }

                if (depth + 1 > MaxCallDepth)
                {
                    return PipelineCallResult.Failed(id, $"pipeline '{id}' was not called: calls nest deeper than {MaxCallDepth}");
                }

                var engine = scope.Engine;
                var request = scope.Request;
                if (engine.Enter(id, out _) is not { } callee)
                {
                    return PipelineCallResult.Failed(id, $"pipeline '{id}' is not served by this engine");
                }

                // Counted as a request of the called pipeline and of its arms from here on, as the host's requests of it are:
                // its experiments' health is read from these, and a call that fails before it runs (inputs it rejects, an
                // assignment that fails) is an error of it, as such a request is. An aborted one counts as an error too.
                var arms = RequestArms.None;
                PipelineResult? result = null;
                try
                {
                    var plan = callee.Plan;
                    if (call.Call.MismatchOf(plan) is { } mismatch)
                    {
                        return PipelineCallResult.Failed(id, $"the call does not fit pipeline '{id}': {mismatch}");
                    }

                    var inputs = CallInputs(call.Given, call.CallerInputs, plan.RequestSchema);
                    if (plan.RequestSchema.Check(inputs) is { } invalid)
                    {
                        return PipelineCallResult.Failed(id, $"pipeline '{id}' rejects the call's inputs: {RequestSchema.Describe(invalid)}");
                    }

                    var deadline = CallBound.DeadlineAfter(started, call.Budget);
                    var assignment = await engine.AssignCallAsync(callee, request, deadline, callerCancellation).ConfigureAwait(false);
                    if (assignment.Error is { } assignmentError)
                    {
                        return PipelineCallResult.Failed(id, $"pipeline '{id}' was not called: {assignmentError}");
                    }

                    // An arm the pipeline does not have, in a list every pipeline of the request shares, is another
                    // pipeline's; in the provider's answer for this pipeline it is a mistake, and reported.
                    arms = ExternalArms.Yield(plan, assignment.Arms, assignment.Ignored, out var ignored);
                    ignored = assignment.Shared ? NotUnknown(ignored) : ignored;
                    callee.Telemetry.RecordIgnored(ignored);
                    var remaining = call.Budget - Stopwatch.GetElapsedTime(started);
                    if (remaining <= TimeSpan.Zero)
                    {
                        return PipelineCallResult.Failed(id, $"pipeline '{id}' was not called: nothing was left of the node's budget after its assignment");
                    }

                    result = await engine._executor.ExecuteAsync(plan, new PipelineRequest
                    {
                        RequestId = request.RequestId,
                        Inputs = inputs,
                        Arms = arms,
                        Deadline = remaining,
                        Seed = CallSeed(call.Seed, call.Call.NodeId, id, call.Sequence),
                        TimeMs = call.TimeMs,
                        CaptureLogs = request.Debug?.CaptureLogs == true,
                        WriteLogs = WritesLogs(request),
                        Calls = plan.Calls.IsEmpty ? null : new RunCalls(scope, id, depth + 1, this),
                        CalledBy = $"{pipelineId}/{call.Call.NodeId}#{call.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                        OnEnd = callee.Bindings.Join(), // last: the executor, called next, ends it on every outcome
                    }, callerCancellation).ConfigureAwait(false);
                    ran = result;
                    scope.Add(result, ignored);
                    return result.Succeeded
                        ? new PipelineCallResult(id, true, null, result.Columns, result.Values)
                        : PipelineCallResult.Failed(id, $"pipeline '{id}' failed: {result.Error}");
                }
                finally
                {
                    var failed = result is not { Succeeded: true };
                    callee.Telemetry.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, arms, failed, !failed && result!.Columns.Count == 0,
                        !failed && result!.Degraded);
                    callee.Bindings.RequestEnded();
                }
            }
        }

        /// <summary>The pipelines from <paramref name="id"/> back to it, when a run of it is among the runs that led here; null otherwise.</summary>
        private string? Chain(string id)
        {
            for (var run = this; run is not null; run = run.Parent)
            {
                if (run.PipelineId == id)
                {
                    var names = new List<string> { id };
                    for (var back = this; !ReferenceEquals(back, run); back = back.Parent!)
                    {
                        names.Insert(0, back.PipelineId);
                    }

                    names.Insert(0, id);
                    return string.Join(" → ", names);
                }
            }

            return null;
        }

        private string PipelineId => pipelineId;

        private RunCalls? Parent => parent;

        /// <summary>
        /// The inputs of a called run: those the operator passes, and for every other input the pipeline declares, the
        /// calling run's input of that name, when it has one.
        /// </summary>
        private static RequestInputs CallInputs(RequestInputs? given, RequestInputs callerInputs, RequestSchema callee)
        {
            var inputs = new RequestInputs(callee.Count);
            if (given is not null)
            {
                foreach (var (name, value) in given)
                {
                    inputs.Set(name, value);
                }
            }

            foreach (var (name, value) in callerInputs)
            {
                if (callee.Contains(name) && !inputs.ContainsKey(name))
                {
                    inputs.Set(name, value);
                }
            }

            return inputs;
        }

        /// <summary>
        /// The ignored arms that are the called pipeline's to report: an arm it does not have is another pipeline's (the
        /// request's own, or another one it calls), not a mistake.
        /// </summary>
        private static IgnoredArm[] NotUnknown(IgnoredArm[] ignored)
        {
            var kept = 0;
            foreach (var arm in ignored)
            {
                kept += arm.Reason == IgnoredArmReason.Unknown ? 0 : 1;
            }

            if (kept == ignored.Length)
            {
                return ignored;
            }

            var own = new IgnoredArm[kept];
            var next = 0;
            foreach (var arm in ignored)
            {
                if (arm.Reason != IgnoredArmReason.Unknown)
                {
                    own[next++] = arm;
                }
            }

            return own;
        }
    }

    /// <summary>
    /// The seed of a called run: of the calling run's seed, the node, the pipeline and the call's place among the node's
    /// calls, so the same request calls with the same seeds and two calls of one node differ.
    /// </summary>
    internal static long CallSeed(long seed, string nodeId, string pipelineId, int sequence) =>
        unchecked((long)StableHash.Hash(seed, StableHash.SeedFromSalt(nodeId + "\u001f" + pipelineId), sequence));

    /// <summary>
    /// The arms of a called run, from where the request's own came: the debugger's forced arms, the platform's answer the
    /// caller passed, or the assignment provider, asked for the called pipeline's experiments. A list the request's
    /// pipelines share (<c>Shared</c>: forced, or passed) is taken as the platform's is: an id the called pipeline does
    /// not have is another pipeline's. A provider that fails, fails the call.
    /// </summary>
    private async ValueTask<(RequestArms Arms, IgnoredArm[] Ignored, string? Error, bool Shared)> AssignCallAsync(
        LoadedPipeline callee, RecRequest request, long deadline, CancellationToken cancellationToken)
    {
        var plan = callee.Plan;
        if (plan.Arms.IsEmpty)
        {
            return (RequestArms.None, Array.Empty<IgnoredArm>(), null, true);
        }

        if ((request.Debug?.ForceArms ?? request.Assignment) is { } ids)
        {
            var arms = ExternalArms.Resolve(plan.Layers.AsSpan(), plan.Arms.AsSpan(), ids, out var ignored);
            return (arms, ignored, null, true);
        }

        var asked = await AskProviderAsync(callee, request, deadline, cancellationToken, called: true).ConfigureAwait(false);
        return (asked.Arms, asked.Ignored, asked.Error, false);
    }

    /// <summary>
    /// The plans served now of the pipelines <paramref name="plan"/>'s nodes call, directly or through the pipelines those
    /// call, each once.
    /// </summary>
    private List<ExecutionPlan> CalledPlans(ExecutionPlan plan)
    {
        var plans = new List<ExecutionPlan>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { plan.PipelineId };
        var pending = new Stack<string>();
        foreach (var called in plan.Calls)
        {
            pending.Push(called);
        }

        while (pending.TryPop(out var id))
        {
            if (!seen.Add(id) || !_pipelines.TryGetValue(id, out var loaded))
            {
                continue;
            }

            plans.Add(loaded.Plan);
            foreach (var called in loaded.Plan.Calls)
            {
                pending.Push(called);
            }
        }

        return plans;
    }

    /// <summary>
    /// Forced arms are strict for the pipelines the request calls too: two arms of one layer of a called pipeline are the
    /// debugger's mistake, as two of the request's own are, not a layer that quietly runs its baseline.
    /// </summary>
    /// <exception cref="ArgumentException">A called pipeline cannot run the arms of it that were forced.</exception>
    private void CheckForcedOfCalled(ExecutionPlan plan, IReadOnlyList<string> forced)
    {
        if (plan.Calls.IsEmpty || forced.Count < 2)
        {
            return;
        }

        foreach (var called in CalledPlans(plan))
        {
            var own = new List<string>();
            foreach (var id in forced)
            {
                if (called.TryGetArm(id, out _))
                {
                    own.Add(id);
                }
            }

            if (own.Count > 1)
            {
                _ = called.ArmsFromIds(own);
            }
        }
    }

    /// <summary>
    /// Whether a pipeline <paramref name="plan"/>'s nodes call, directly or through the pipelines those call, has arm
    /// <paramref name="armId"/> in the plan it serves now: such an arm is not the request's pipeline's to run, and not a
    /// mistake either.
    /// </summary>
    private bool KnownToCalled(ExecutionPlan plan, string armId)
    {
        if (plan.Calls.IsEmpty)
        {
            return false;
        }

        foreach (var called in CalledPlans(plan))
        {
            if (called.TryGetArm(armId, out _))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The ids of <paramref name="ignored"/> that no pipeline the plan calls has: what the request really ignored.</summary>
    private IgnoredArm[] WithoutCalled(ExecutionPlan plan, IgnoredArm[] ignored)
    {
        if (plan.Calls.IsEmpty || ignored.Length == 0)
        {
            return ignored;
        }

        var kept = new List<IgnoredArm>(ignored.Length);
        foreach (var arm in ignored)
        {
            if (arm.Reason != IgnoredArmReason.Unknown || !KnownToCalled(plan, arm.Id))
            {
                kept.Add(arm);
            }
        }

        return kept.Count == ignored.Length ? ignored : kept.ToArray();
    }
}
