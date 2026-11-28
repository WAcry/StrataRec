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
    /// <param name="reads">The request's reads among those of its group; null for a request on its own, whose runs share among themselves.</param>
    private sealed class RequestCalls(RecEngine engine, RecRequest request, RequestReads? reads)
    {
        private readonly Lock _gate = new();
        private readonly SortedSet<string> _assigned = new(StringComparer.Ordinal);
        private readonly SortedSet<string> _triggered = new(StringComparer.Ordinal);
        private List<IgnoredArm>? _ignored;
        private List<Logging.LogBatch>? _batches;
        private List<CalledRun>? _runs;
        private Dictionary<LoadedPipeline, CallAssignment>? _assignments;

        public RecEngine Engine => engine;

        public RecRequest Request => request;

        /// <summary>What the cached reads of the request's runs share: every run of the request is handed it.</summary>
        public RequestReads Reads { get; } = reads ?? new RequestReads();

        /// <summary>The request asked for its trace: the called runs are kept with theirs (<see cref="RecResult.Calls"/>).</summary>
        public bool Traces => request.Debug?.Trace == true;

        public void Add(CalledRun run)
        {
            lock (_gate)
            {
                (_runs ??= new List<CalledRun>()).Add(run);
            }
        }

        /// <summary>
        /// The request's arms in a pipeline its nodes call (<see cref="AssignCallAsync"/>): one answer for the request and
        /// the plan. The arms a cached call's key was built with are the arms the runs under it run, a pipeline called
        /// again is not asked about again, and what the plan ignores of the answer is reported once, whether its calls
        /// are then run or answered by a cache. Calls that start before the first answer each ask, and all take the
        /// answer that came first: a provider that answered two of them differently had one call's key built with arms
        /// the runs under it did not run. A failed assignment is not kept, and the next call asks again.
        /// </summary>
        public async ValueTask<(CallAssignment? Assignment, string? Error)> AssignAsync(LoadedPipeline callee, long deadline, CancellationToken cancellationToken)
        {
            if (callee.Plan.Arms.IsEmpty)
            {
                return (CallAssignment.None, null); // nothing to ask about, nothing to keep
            }

            lock (_gate)
            {
                if (_assignments is not null && _assignments.TryGetValue(callee, out var known))
                {
                    return (known, null);
                }
            }

            var asked = await engine.AssignCallAsync(callee, request, deadline, cancellationToken).ConfigureAwait(false);
            if (asked.Error is not null)
            {
                return (null, asked.Error);
            }

            // An arm the pipeline does not have, in a list every pipeline of the request shares, is another pipeline's;
            // in the provider's answer for this pipeline it is a mistake, and reported.
            var arms = ExternalArms.Yield(callee.Plan, asked.Arms, asked.Ignored, out var ignored);
            var assignment = new CallAssignment(arms, asked.Shared ? NotUnknown(ignored) : ignored);
            lock (_gate)
            {
                _assignments ??= new(ReferenceEqualityComparer.Instance);
                if (_assignments.TryGetValue(callee, out var first))
                {
                    return (first, null);
                }

                _assignments.Add(callee, assignment);
                if (assignment.Ignored.Length > 0)
                {
                    (_ignored ??= new List<IgnoredArm>()).AddRange(assignment.Ignored);
                }
            }

            return (assignment, null);
        }

        /// <summary>
        /// A run that did not happen because a cache answered its call: the request has its arms and triggered its
        /// experiments as the request that computed the result did, and <paramref name="batch"/> is its captured record
        /// of that, when the request captures its logs.
        /// </summary>
        public void Add(CallExposure exposure, Logging.LogBatch? batch)
        {
            lock (_gate)
            {
                foreach (var arm in exposure.Arms)
                {
                    _assigned.Add(arm);
                }

                foreach (var experiment in exposure.Triggered)
                {
                    _triggered.Add(experiment);
                }

                if (batch is not null)
                {
                    (_batches ??= new List<Logging.LogBatch>()).Add(batch);
                }
            }
        }

        public void Add(PipelineResult result)
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
                _runs?.Sort(static (a, b) =>
                {
                    // By who called, then by the call's number: as text, a node's tenth call would come before its third.
                    var byCaller = a.CalledBy.AsSpan(0, a.CalledBy.LastIndexOf('#')).CompareTo(b.CalledBy.AsSpan(0, b.CalledBy.LastIndexOf('#')), StringComparison.Ordinal);
                    return byCaller != 0 ? byCaller : a.Sequence.CompareTo(b.Sequence);
                });

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

    /// <summary>The request's arms in a plan its nodes call: what the plan runs of the answer, and what it reports as ignored.</summary>
    private sealed record CallAssignment(RequestArms Arms, IgnoredArm[] Ignored)
    {
        public static readonly CallAssignment None = new(RequestArms.None, []);
    }

    /// <summary>
    /// The runs under one cached call that had experiments, gathered while its pipeline runs: what the record of its
    /// result keeps of them (<see cref="CallExposure"/>). Runs end on any thread.
    /// </summary>
    private sealed class CallCapture
    {
        private readonly List<CallExposure> _exposures = new();

        public void Add(CallExposure exposure)
        {
            lock (_exposures)
            {
                _exposures.Add(exposure);
            }
        }

        public CallExposure[] Snapshot()
        {
            lock (_exposures)
            {
                return [.. _exposures];
            }
        }
    }

    /// <summary>What the tiers of a call's cache had of it.</summary>
    /// <param name="Key">The call's key; null when the call is not cached, or cannot be now.</param>
    /// <param name="Keyed">The plans of the pipelines under the called one that the key was built with.</param>
    /// <param name="Stale">A result past its time to live that may still serve if the run fails.</param>
    /// <param name="StaleShared">The stale result is the distributed tier's.</param>
    /// <param name="CutOff">The read was cut off by the node's budget or the caller.</param>
    private readonly record struct CacheLookup(
        UInt128? Key, List<(LoadedPipeline Pipeline, RequestArms Arms)>? Keyed, CachedCall? Fresh, CachedCall? Stale, bool StaleShared, Exception? CutOff);

    /// <summary>
    /// The calls of one run (<see cref="OperatorContext.CallPipelineAsync"/>): each runs the pipeline this engine serves
    /// under that id, for the same request, unless the node's cache has its result
    /// (<see cref="OperatorCompileContext.TryCallPipeline"/>).
    /// </summary>
    /// <param name="pipelineId">The pipeline of the run that calls.</param>
    /// <param name="depth">How many calls led to that run: 0 for a run of the request's own pipeline.</param>
    /// <param name="parent">The calls of the run that called it; null at depth 0.</param>
    /// <param name="calledBy">Who called that run, as its logs say it (<see cref="Logging.LogBatch.CalledBy"/>); null at depth 0.</param>
    /// <param name="capture">What the call that started that run keeps of the runs under it, when its result will be cached.</param>
    private sealed class RunCalls(RequestCalls scope, string pipelineId, int depth, RunCalls? parent = null, string? calledBy = null, CallCapture? capture = null)
        : IPipelineCalls
    {
        public async ValueTask<PipelineCallResult> CallAsync(PipelineCallRequest call, CancellationToken callerCancellation)
        {
            var started = Stopwatch.GetTimestamp();
            var id = call.Call.PipelineId;
            PipelineResult? ran = null;
            IReadOnlyList<string>? served = null; // the arms of a result the cache answered
            string? failure = null; // why the run failed, when a stale result answered for it

            // The calls from the request's pipeline down to this one: a pipeline called twice numbers its own calls alike,
            // and only the whole path tells its two runs' calls apart.
            var path = (calledBy is null ? string.Empty : calledBy + " > ")
                + $"{pipelineId}/{call.Call.NodeId}#{call.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            var answer = await RunAsync().ConfigureAwait(false);
            answer.CalledBy = path;
            if (scope.Traces)
            {
                scope.Add(new CalledRun(path, pipelineId, call.Call.NodeId, call.Sequence, id, answer.Succeeded, answer.Error ?? failure,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds, ran?.AssignedArms ?? served ?? [], ran?.Outcomes ?? [])
                {
                    Cached = answer.Cached,
                });
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
                // Not a call its cache answers: the pipeline did not run, and its health is that of its runs.
                var arms = RequestArms.None;
                PipelineResult? result = null;
                var answered = false;
                RequestReads.SharedRead? reading = null; // the request's read of this call's result, when this call is its reader
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
                    var (assignment, assignmentError) = await scope.AssignAsync(callee, deadline, callerCancellation).ConfigureAwait(false);
                    if (assignment is null)
                    {
                        return PipelineCallResult.Failed(id, $"pipeline '{id}' was not called: {assignmentError}");
                    }

                    arms = assignment.Arms;
                    callee.Telemetry.RecordIgnored(assignment.Ignored);

                    // A request that runs a configuration of its own (DebugOptions.ConfigOverride) neither reads nor
                    // writes: what it computes is not what the served configuration computes.
                    var cache = request.Debug?.ConfigOverride is null ? call.Call.Cache : null;
                    var lookup = cache is null ? default : await LookupAsync(cache, callee, arms, inputs, deadline).ConfigureAwait(false);
                    if (lookup.CutOff is not null)
                    {
                        callerCancellation.ThrowIfCancellationRequested(); // the caller gave up: so does the node, as in a run
                    }

                    var hit = lookup.Fresh;
                    if (hit is null && lookup is { Key: { } asked, CutOff: null })
                    {
                        // The tiers did not have the result: another call of the request for it may be running the
                        // pipeline, or have run it, and this one takes what that one computes (RequestReads). The
                        // same record of the same cache, by a run of this plan: a caller that keeps the pipeline's
                        // results elsewhere runs it and keeps its own, and a call of a pipeline published since the
                        // other's began does not wait for the run of the plan before.
                        var joined = scope.Reads.Join(cache!.Table, asked, limit: 0, out var reader, plan: callee);
                        if (reader)
                        {
                            reading = joined;
                        }
                        else
                        {
                            var bound = new CallBound(CancellationSite.Call, callerCancellation, deadline, engine._executor.Events, request.RequestId);
                            SharedAnswer shared;
                            try
                            {
                                shared = await joined.WaitAsync(bound, CallDeadline.None).ConfigureAwait(false);
                            }
                            finally
                            {
                                bound.Release();
                            }

                            callerCancellation.ThrowIfCancellationRequested(); // the caller gave up: so does the node, as in a run
                            if (shared.Record is CachedCall computed && cache!.TakeShared(asked, computed))
                            {
                                hit = computed;
                            }
                        }
                    }

                    if (hit is not null)
                    {
                        answered = true;
                        return Serve(hit, stale: false);
                    }

                    var remaining = call.Budget - Stopwatch.GetElapsedTime(started);
                    if (remaining <= TimeSpan.Zero || lookup.CutOff is not null)
                    {
                        failure = $"pipeline '{id}' was not called: nothing was left of the call's time after its assignment" + (cache is null ? string.Empty : " and the read of its cache");
                    }
                    else
                    {
                        var runCapture = lookup.Key is null ? null : new CallCapture();
                        var startedMs = cache?.NowMs() ?? 0;
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
                            Calls = plan.Calls.IsEmpty ? null : new RunCalls(scope, id, depth + 1, this, path, runCapture),
                            Reads = scope.Reads,
                            CalledBy = path,
                            OnEnd = callee.Bindings.Join(), // last: the executor, called next, ends it on every outcome
                        }, callerCancellation).ConfigureAwait(false);
                        ran = result;
                        scope.Add(result);
                        if (plan.Experiments.Length > 0 && (runCapture is not null || Captured))
                        {
                            // Kept by the cached calls this run is under, its own included; a run under none makes none.
                            var exposure = new CallExposure(id, plan.ManifestId, null, result.AssignedArms, result.TriggeredExperiments);
                            runCapture?.Add(exposure);
                            Expose(exposure, path);
                        }

                        if (result.Succeeded)
                        {
                            // Only what a run computed whole is kept: a list one recall source was missing from is not
                            // served for an hour, and a degraded run under this one made this one degraded; nor is the
                            // result of a run with a call that was not answered, which its node may go on without. Only of a
                            // request whose runs write the logs (not one with forced arms or captured logs): a result
                            // served later says it comes from this request, and what the run logged must be there under
                            // it. And only while the plans the key was built with are the ones served: a pipeline under
                            // this one that was replaced since may have run as the new one, and its result is not the
                            // old key's, for this instance or for the others that share the record.
                            // The request's other calls for the same result take it whether or not its runs write the
                            // logs: what they are told it comes from is a run of their own request.
                            if (lookup.Key is { } key && !result.Degraded && !result.CallsUnanswered && engine.Serves(callee, lookup.Keyed!))
                            {
                                var from = new CachedFrom(request.RequestId, call.TimeMs, path);
                                var exposures = runCapture!.Snapshot();
                                if (WritesLogs(request))
                                {
                                    cache!.Write(key, result.Columns, result.Values, from, exposures, startedMs);
                                }

                                reading?.Complete(new CachedCall(result.Columns, result.Values, from, startedMs, exposures));
                            }

                            return new PipelineCallResult(id, true, null, result.Columns, result.Values)
                            {
                                Degraded = result.Degraded,
                                CallsUnanswered = result.CallsUnanswered,
                            };
                        }

                        failure = $"pipeline '{id}' failed: {result.Error}";
                    }

                    // The window is judged now, when the stale result is chosen, not when it was read.
                    if (lookup is { Stale: { } stale, Key: { } staleKey } && cache!.Usable(staleKey, stale))
                    {
                        cache.CountStale(lookup.StaleShared);
                        return Serve(stale, stale: true);
                    }

                    return PipelineCallResult.Failed(id, failure);
                }
                finally
                {
                    reading?.Withdraw(); // unless it ended with the result: the calls waiting for it run the pipeline themselves
                    try
                    {
                        if (!answered)
                        {
                            var failed = result is not { Succeeded: true };
                            callee.Telemetry.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, arms, failed, !failed && result!.Columns.Count == 0,
                                !failed && result!.Degraded);
                        }
                    }
                    finally
                    {
                        callee.Bindings.RequestEnded(); // whatever a host's metric listener throws: the plan's lease is not left entered
                    }
                }

                // What the tiers have of the call: memory first, then the distributed tier. Null key when the arms of a
                // pipeline the run may call cannot be told now, and the call then runs as one without a cache.
                async ValueTask<CacheLookup> LookupAsync(PipelineCallCache cache, LoadedPipeline callee, RequestArms arms, RequestInputs inputs, long deadline)
                {
                    // Told before the request's arms in those pipelines are asked for: a call without a cache asks for
                    // the arms of the pipelines its run reaches, as they are reached.
                    var reached = engine.Reached(callee);
                    if (LeadsBack(reached) || await CalledArmsAsync(scope, reached, deadline, callerCancellation).ConfigureAwait(false) is not { } called)
                    {
                        return default;
                    }

                    var shape = callee.CallIdentity.Shape;
                    var key = CallKey(cache, callee, arms, called, inputs);
                    var memory = cache.ReadMemory(key, shape);
                    if (memory.Fresh is not null || !cache.HasDistributed)
                    {
                        return new CacheLookup(key, called, memory.Fresh, memory.Stale, false, null);
                    }

                    var bound = new CallBound(CancellationSite.Call, callerCancellation, deadline, engine._executor.Events, request.RequestId);
                    try
                    {
                        var shared = await cache.ReadDistributedAsync(key, shape, bound, call.Memory).ConfigureAwait(false);
                        if (shared.CutOff is not null || shared.Fresh is not null)
                        {
                            return new CacheLookup(key, called, shared.Fresh, memory.Stale, false, shared.CutOff);
                        }

                        // Kept for a failure of the run: the newer of the two, which stays within the window longer.
                        return shared.Stale is { } other && (memory.Stale is null || other.WrittenMs > memory.Stale.WrittenMs)
                            ? new CacheLookup(key, called, null, other, true, null)
                            : new CacheLookup(key, called, null, memory.Stale, false, null);
                    }
                    finally
                    {
                        bound.Release();
                    }
                }

                // A result the cache answers: the runs that computed it are this request's exposure too, each logged with
                // the run it comes from and listed in the request's result.
                PipelineCallResult Serve(CachedCall cached, bool stale)
                {
                    var from = stale ? cached.From with { Stale = true } : cached.From;
                    var captures = request.Debug?.CaptureLogs == true;
                    var captured = Captured;
                    foreach (var exposure in cached.Exposures)
                    {
                        // The run of a pipeline under the called one: the computing request's, under the call that
                        // computed the record, unless a cache answered it there too and it is an earlier request's.
                        var by = exposure.Path is null ? path : path + " > " + exposure.Path;
                        var computed = exposure.From ?? (exposure.Path is null ? cached.From : cached.From with { CalledBy = cached.From.CalledBy + " > " + exposure.Path });
                        scope.Add(exposure, engine._executor.PublishCachedExposure(
                            request.RequestId, call.TimeMs, by, exposure, stale ? computed with { Stale = true } : computed, captures, WritesLogs(request)));
                        if (captured)
                        {
                            Expose(exposure with { From = computed }, path);
                        }

                        served = exposure.Path is null ? exposure.Arms : served;
                    }

                    return new PipelineCallResult(id, true, null, cached.Columns, cached.Values, from);
                }
            }
        }

        /// <summary>
        /// A run under the call at <paramref name="at"/> (the call's own run, or one its record holds), for every cached
        /// call this run is under: each keeps it with the calls from its own pipeline down.
        /// </summary>
        private void Expose(CallExposure exposure, string at)
        {
            for (var run = this; run is not null; run = run.Parent)
            {
                if (run.Capture is { } enclosing)
                {
                    var below = at[(run.CalledBy!.Length + 3)..]; // after "<the call that started the run> > "
                    enclosing.Add(exposure with { Path = exposure.Path is null ? below : below + " > " + exposure.Path });
                }
            }
        }

        private CallCapture? Capture => capture;

        /// <summary>This run is under a call whose result will be cached: the runs under it are kept for that record.</summary>
        private bool Captured
        {
            get
            {
                for (var run = this; run is not null; run = run.Parent)
                {
                    if (run.Capture is not null)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        private string? CalledBy => calledBy;

        /// <summary>
        /// A pipeline a run of the callee may call is one whose run led to this call: the calls lead back, and the run
        /// will have one refused (<see cref="Chain"/>). Such a call has no cache: what its run returns depends on where
        /// the circle was entered, which no key says, and calls that waited for each other's result around the circle
        /// (<see cref="RequestReads"/>) would wait until their time was up.
        /// </summary>
        private bool LeadsBack(List<LoadedPipeline> called)
        {
            for (var run = this; run is not null; run = run.Parent)
            {
                foreach (var pipeline in called)
                {
                    if (pipeline.Plan.PipelineId == run.PipelineId)
                    {
                        return true;
                    }
                }
            }

            return false;
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

    /// <summary>
    /// The pipelines a run may call, directly or through those (<see cref="Reached"/>), each with the arms the request
    /// has in it, by pipeline id: with the called pipeline's own, what its run could run. Null when one's arms cannot be
    /// told now (its assignment failed or timed out): the call is then not cached, and the run finds out as it would.
    /// </summary>
    /// <remarks>
    /// A pipeline replaced between this and the run is run as the new one: what the run returns is then not kept
    /// (<see cref="Serves"/>).
    /// </remarks>
    private static async ValueTask<List<(LoadedPipeline Pipeline, RequestArms Arms)>?> CalledArmsAsync(
        RequestCalls scope, List<LoadedPipeline> reached, long deadline, CancellationToken cancellationToken)
    {
        var called = new List<(LoadedPipeline Pipeline, RequestArms Arms)>(reached.Count);
        foreach (var loaded in reached)
        {
            var (assignment, _) = await scope.AssignAsync(loaded, deadline, cancellationToken).ConfigureAwait(false);
            if (assignment is null)
            {
                return null;
            }

            called.Add((loaded, assignment.Arms));
        }

        return called;
    }

    /// <summary>
    /// The pipelines a run of <paramref name="callee"/> may call, and those they may, as served now, by id. One not served
    /// is not among them: its call fails as a result, and it is among them once it is served.
    /// </summary>
    private List<LoadedPipeline> Reached(LoadedPipeline callee)
    {
        var reached = new List<LoadedPipeline>();
        if (callee.Plan.Calls.IsEmpty)
        {
            return reached;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal) { callee.Plan.PipelineId };
        var pending = new Stack<string>(callee.Plan.Calls);
        while (pending.TryPop(out var id))
        {
            if (!seen.Add(id) || !_pipelines.TryGetValue(id, out var loaded))
            {
                continue;
            }

            reached.Add(loaded);
            foreach (var next in loaded.Plan.Calls)
            {
                pending.Push(next);
            }
        }

        reached.Sort(static (a, b) => string.CompareOrdinal(a.Plan.PipelineId, b.Plan.PipelineId));
        return reached;
    }

    /// <summary>
    /// Whether the plans a call's key was built with (<see cref="CalledArmsAsync"/>) are the ones a run of
    /// <paramref name="callee"/> may call now, no other and no more: only then is what the run returned a result of that
    /// key. A pipeline loaded again, to the same configuration too, is another plan, and one first served while the run
    /// was under way is one the key does not have: either way one result is computed once more.
    /// </summary>
    private bool Serves(LoadedPipeline callee, List<(LoadedPipeline Pipeline, RequestArms Arms)> called)
    {
        var now = Reached(callee);
        if (now.Count != called.Count)
        {
            return false;
        }

        for (var i = 0; i < now.Count; i++)
        {
            if (!ReferenceEquals(now[i], called[i].Pipeline))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The key of a cached call (docs/decisions/pipeline-calls.md): the format, the pipeline and what a result of it
    /// holds, what would run (the pipeline's and its called pipelines' configuration, or the node's <c>version</c>, and
    /// the request's arms in each), and every input the pipeline receives. Not the request's id, seed or time, nor who
    /// calls: whoever declares the cache takes one result for all of those. 128 bits, so very many results do not collide.
    /// </summary>
    private static UInt128 CallKey(
        PipelineCallCache cache, LoadedPipeline callee, RequestArms arms, List<(LoadedPipeline Pipeline, RequestArms Arms)> called, RequestInputs inputs)
    {
        var builder = default(StableHash.Builder);
        try
        {
            var derived = cache.Version is null;
            builder.Add(PipelineCallCache.Format);
            builder.Add(callee.Plan.PipelineId);
            builder.Add(callee.CallIdentity.ShapeHash);
            builder.Add(derived);
            builder.Add(cache.Version ?? string.Empty);
            AddRun(ref builder, callee, callee.Plan.ArmIds(arms), derived);
            foreach (var (pipeline, itsArms) in called)
            {
                // With a version, a pipeline the request has no arm in adds nothing: an experiment that adds a call in
                // an arm, released, leaves the results of the requests outside it where they are.
                var ids = pipeline.Plan.ArmIds(itsArms);
                if (derived || ids.Count > 0)
                {
                    builder.Add(pipeline.Plan.PipelineId);
                    AddRun(ref builder, pipeline, ids, derived);
                }
            }

            builder.Add(string.Empty); // no pipeline has this id: the inputs' names start after it

            foreach (var name in callee.Plan.RequestSchema.Names)
            {
                builder.Add(name);
                builder.AddAny(inputs[name]); // an input without a value is one of the inputs
            }

            return builder.Finish128();
        }
        finally
        {
            builder.Dispose();
        }

        static void AddRun(ref StableHash.Builder builder, LoadedPipeline pipeline, List<string> ids, bool derived)
        {
            var identity = derived ? pipeline.CallIdentity : null;
            if (identity is not null)
            {
                builder.Add(identity.Baseline);
            }

            builder.Add(ids.Count);
            foreach (var id in ids)
            {
                builder.Add(id);
                if (identity is not null && pipeline.Plan.TryGetArm(id, out var arm))
                {
                    builder.Add(identity.Experiments[arm.ExperimentIndex]);
                }
            }
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
    /// The pipelines a request of <paramref name="plan"/> may call: those its nodes call, and those the nodes of its
    /// fallback pipeline call (the fallback itself runs without experiments; what it calls runs the request's arms).
    /// </summary>
    private bool Calls(ExecutionPlan plan, out LoadedPipeline? fallback)
    {
        fallback = _pipelines.TryGetValue(plan.PipelineId, out var served) && served.FallbackPipeline is { } id && id != plan.PipelineId
            && _pipelines.TryGetValue(id, out var loaded) && !loaded.Plan.Calls.IsEmpty ? loaded : null;
        return !plan.Calls.IsEmpty || fallback is not null;
    }

    /// <summary>
    /// The plans served now of the pipelines a request of <paramref name="plan"/> may call (<see cref="Calls"/>),
    /// directly or through the pipelines those call, each once.
    /// </summary>
    private List<ExecutionPlan> CalledPlans(ExecutionPlan plan)
    {
        var plans = new List<ExecutionPlan>();
        if (!Calls(plan, out var fallback))
        {
            return plans;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal) { plan.PipelineId };
        var pending = new Stack<string>();
        foreach (var called in plan.Calls)
        {
            pending.Push(called);
        }

        if (fallback is not null)
        {
            // What the fallback pipeline's nodes call, of any of its arms (it runs none, so this takes a few more for
            // callable than are). The fallback itself is not marked seen: a node may call it too, and then it runs its arms.
            foreach (var called in fallback.Plan.Calls)
            {
                pending.Push(called);
            }
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
        if (forced.Count < 2 || !Calls(plan, out _))
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
        if (ignored.Length == 0 || !Calls(plan, out _))
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
