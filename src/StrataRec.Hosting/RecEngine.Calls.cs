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
    /// A workspace's compile refuses calls that lead back to their pipeline; pipelines loaded each from its own release
    /// are not compiled together, so a cycle among them is stopped here, as a failed call.
    /// </summary>
    internal const int MaxCallDepth = 8;

    /// <summary>
    /// One per request that calls any, shared by its runs; calls may end on any thread.
    /// </summary>
    private sealed class RequestCalls(RecEngine engine, RecRequest request, RequestReads reads)
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

        public RequestReads Reads { get; } = reads;

        public bool Traces => request.Debug?.Trace == true;

        public void Add(CalledRun run)
        {
            lock (_gate)
            {
                (_runs ??= new List<CalledRun>()).Add(run);
            }
        }

        /// <summary>
        /// The arms a cached call's key was built with are the arms the runs under it run. Calls that start before the
        /// first answer each ask, and all take the answer that came first.
        /// </summary>
        public async ValueTask<(CallAssignment? Assignment, string? Error)> AssignAsync(LoadedPipeline callee, long deadline, CancellationToken cancellationToken)
        {
            if (callee.Plan.Arms.IsEmpty)
            {
                return (CallAssignment.None, null);
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

        public RecResult Merge(RecResult result)
        {
            lock (_gate)
            {
                if (_assigned.Count == 0 && _triggered.Count == 0 && _ignored is null && _batches is null && _runs is null)
                {
                    return result;
                }

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
        /// The request's ignored arms with those of its called runs: each id and reason once, by id as the record has
        /// them, and without an arm the request's pipeline did not know that a called run ran (a pipeline its plan does
        /// not name, called by a configuration override).
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

    private sealed record CallAssignment(RequestArms Arms, IgnoredArm[] Ignored)
    {
        public static readonly CallAssignment None = new(RequestArms.None, []);
    }

    /// <summary>
    /// Runs end on any thread.
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

    /// <param name="Key">The call's key; null when the call is not cached, or cannot be now.</param>
    /// <param name="Keyed">The plans of the pipelines under the called one that the key was built with.</param>
    /// <param name="Stale">A result past its time to live that may still serve if the run fails.</param>
    /// <param name="CutOff">The read was cut off by the node's budget or the caller.</param>
    private readonly record struct CacheLookup(
        UInt128? Key, List<(LoadedPipeline Pipeline, RequestArms Arms)>? Keyed, CachedCall? Fresh, CachedCall? Stale, bool StaleShared, Exception? CutOff);

    /// <param name="calledBy">Who called that run, as its logs say it (<see cref="Logging.LogBatch.CalledBy"/>); null at depth 0.</param>
    /// <param name="capture">What the call that started that run keeps of the runs under it, when its result will be cached.</param>
    private sealed class RunCalls(RequestCalls scope, string pipelineId, int depth, RunCalls? parent = null, string? calledBy = null, CallCapture? capture = null)
        : IPipelineCalls
    {
        public async ValueTask<PipelineCallResult> CallAsync(PipelineCallRequest call, CancellationToken callerCancellation)
        {
            // The call reads the request's memory (for its cache's distributed tier) after waits of its own (its
            // assignment), which may be after the request has returned: a call its operator did not wait for. So it
            // holds the memory from before its first wait to its end.
            call.Memory.Hold();
            try
            {
                return await HeldAsync(call, callerCancellation).ConfigureAwait(false);
            }
            finally
            {
                call.Memory.Release();
            }
        }

        private async ValueTask<PipelineCallResult> HeldAsync(PipelineCallRequest call, CancellationToken callerCancellation)
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

                // Counted as a request of the called pipeline and of its arms from here on, as the host's requests of
                // it are: its experiments' health is read from these, and a call that fails before it runs (inputs it
                // rejects, an assignment that fails) is an error of it, as such a request is. Not a call its cache
                // answers: the pipeline did not run, and its health is that of its runs.
                var arms = RequestArms.None;
                PipelineResult? result = null;
                var answered = false;
                RequestReads.SharedRead? reading = null;
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

                    var cache = request.Debug?.ConfigOverride is null ? call.Call.Cache : null;
                    var lookup = cache is null ? default : await LookupAsync(cache, callee, arms, inputs, deadline).ConfigureAwait(false);
                    if (lookup.CutOff is not null)
                    {
                        callerCancellation.ThrowIfCancellationRequested();
                    }

                    var hit = lookup.Fresh;
                    if (hit is null && lookup is { Key: { } asked, CutOff: null })
                    {
                        // The same record of the same cache, by a run of this plan: a caller that keeps the pipeline's
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

                            callerCancellation.ThrowIfCancellationRequested();
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
                            var exposure = new CallExposure(id, plan.ManifestId, null, result.AssignedArms, result.TriggeredExperiments);
                            runCapture?.Add(exposure);
                            Expose(exposure, path);
                        }

                        if (result.Succeeded)
                        {
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
                    reading?.Withdraw();
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

                // Null key when the arms of a pipeline the run may call cannot be told now, and the call then runs as
                // one without a cache.
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

                        return shared.Stale is { } other && (memory.Stale is null || other.WrittenMs > memory.Stale.WrittenMs)
                            ? new CacheLookup(key, called, null, other, true, null)
                            : new CacheLookup(key, called, null, memory.Stale, false, null);
                    }
                    finally
                    {
                        bound.Release();
                    }
                }

                PipelineCallResult Serve(CachedCall cached, bool stale)
                {
                    var from = stale ? cached.From with { Stale = true } : cached.From;
                    var captures = request.Debug?.CaptureLogs == true;
                    var captured = Captured;
                    foreach (var exposure in cached.Exposures)
                    {
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
        /// Such a call has no cache: what its run returns depends on where the circle was entered, which no key says,
        /// and calls that waited for each other's result around the circle (<see cref="RequestReads"/>) would wait
        /// until their time was up.
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
    /// <paramref name="callee"/> may call now, no other and no more: only then is what the run returned a result of
    /// that key.
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
    /// Not the request's id, seed or time, nor who calls: whoever declares the cache takes one result for all of those.
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
                builder.AddAny(inputs[name]);
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

    internal static long CallSeed(long seed, string nodeId, string pipelineId, int sequence) =>
        unchecked((long)StableHash.Hash(seed, StableHash.SeedFromSalt(nodeId + "\u001f" + pipelineId), sequence));

    /// <summary>
    /// A list the request's pipelines share (<c>Shared</c>: forced, or passed) is taken as the platform's is: an id the
    /// called pipeline does not have is another pipeline's.
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
