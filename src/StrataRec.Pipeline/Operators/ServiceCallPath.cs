using System.Runtime.ExceptionServices;
using StrataRec.Artifacts.Json;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Memory;
using StrataRec.Core.Services;
using StrataRec.Core.Threading;
using StrataRec.Pipeline.Replay;

namespace StrataRec.Pipeline.Operators;

/// <summary>
/// Who makes a service call: a node, or the feature prefetch for a source in mode <c>service</c>. The
/// recording says which, and a replay answers a caller's own recorded calls first.
/// </summary>
internal readonly struct ServiceCaller
{
    private ServiceCaller(string? node, string? source, string? entity)
    {
        Node = node;
        Source = source;
        Entity = entity;
    }

    public string? Node { get; }

    public string? Source { get; }

    /// <summary>
    /// The entity a source's call computes: a source may compute several, whose calls are each its own as two nodes'
    /// are.
    /// </summary>
    public string? Entity { get; }

    /// <summary>
    /// One string per caller, which a replay matches a recorded call's on, with whether it is a source's (a node built
    /// without the plan compiler's checks may spell a source's key): the node id, or
    /// <c>source:&lt;source&gt;/&lt;entity&gt;</c> with each name escaped as a JSON pointer's token, since a name may
    /// hold a slash.
    /// </summary>
    public string Key => KeyOf(Node, Source, Entity);

    public static ServiceCaller OfNode(string node) => new(node, null, null);

    public static ServiceCaller OfSource(string source, string entity) => new(null, source, entity);

    public static string KeyOf(string? node, string? source, string? entity) =>
        node ?? "source:" + JsonPointer.Escape(source ?? "") + "/" + JsonPointer.Escape(entity ?? "");
}

internal interface IServiceCallScope
{
    ServiceCaller Caller { get; }

    CallBound Budget { get; }

    RequestMemory Memory { get; }

    RunRecorder? Recorder { get; }

    RunReplay? Replay { get; }

    RequestReads? Reads { get; }

    /// <summary>The call was answered with stale values (its cache's), or was when it was recorded.</summary>
    void ReportDegraded(string reason);

    /// <summary>
    /// An external call of this scope (to the service, or its cache's distributed tier) did not complete synchronously;
    /// <paramref name="done"/> completes once it really has: a feature fetch tracks it, as its connectors' calls.
    /// The call holds the request memory until then either way.
    /// </summary>
    void Pending(Task done);
}

internal static class ServiceCallPath
{
    /// <summary>
    /// Calls the service; the call has been checked by its caller. Closes <paramref name="response"/>.
    /// </summary>
    public static async Task<ServiceStatus> CallAsync(IServiceCallScope scope, BoundService service, ServiceCall call, ServiceResponse response)
    {
        if (scope.Replay is { } replay)
        {
            try
            {
                var status = replay.Call(scope.Caller, service.Target, call, response, out var degraded);
                if (degraded is not null)
                {
                    scope.ReportDegraded(degraded);
                }

                return status;
            }
            finally
            {
                response.Close();
            }
        }

        // A cache that keeps nothing serves the one kind of call a request's other calls share: about no items, of a
        // run with reads to share through.
        if (service.Cache is { } cache && (cache.Kept || (call.Items.IsEmpty && scope.Reads is not null)))
        {
            return await CachedServiceCall.RunAsync(scope, service, cache, call, response).ConfigureAwait(false);
        }

        var recorder = scope.Recorder;
        var sequence = recorder?.BeginCall(scope.Caller, service.Target, call) ?? 0;
        var result = await BoundedCall.RunAsync<ServiceStatus, ServiceInvocation>(
            new ServiceInvocation(scope, service.Client, call, response, service.Target, recorder, sequence), scope.Budget, scope.Memory, service.Target,
            CallDeadline.After(service.TimeoutMs)).ConfigureAwait(false);
        // Every end named, no default arm: a new kind of end does not compile until it is decided here.
        return result.End switch
        {
            CallEnd.Answered => result.Value,
            CallEnd.TimedOut or CallEnd.Threw => ServiceStatus.Failed(result.Error ?? "failed"),
            CallEnd.BudgetRanOut or CallEnd.Cancelled => Rethrow(result.Exception!),
        };
    }

    /// <summary>
    /// Sends a call to the service's client, bounded and measured like every call, without recording it: the call of
    /// the items a service's cache missed (<see cref="CachedServiceCall"/>), whose caller records the whole call.
    /// </summary>
    public static ValueTask<CallResult<ServiceStatus>> SendAsync(
        IServiceCallScope scope, BoundService service, ServiceCall call, ServiceResponse response, in CallDeadline deadline) =>
        BoundedCall.RunAsync<ServiceStatus, ServiceInvocation>(
            new ServiceInvocation(scope, service.Client, call, response, service.Target, null, 0), scope.Budget, scope.Memory, service.Target, deadline);

    private static ServiceStatus Rethrow(Exception exception)
    {
        ExceptionDispatchInfo.Throw(exception);
        return default;
    }

    private readonly struct ServiceInvocation(
        IServiceCallScope scope, IServiceClient client, ServiceCall call, ServiceResponse response, IoTarget target, RunRecorder? recorder, int sequence)
        : IBoundedCall<ServiceStatus>
    {
        public ValueTask<ServiceStatus> Send(CancellationToken token) => client.CallAsync(call, response, token);

        // The rows of a call about rows (its items, or a pairwise model's pairs, which have no keys): what it sends.
        public int Keys => call.BatchSize > 0 ? call.BatchSize : -1;

        public IoOutcome Judge(ServiceStatus value, out string? error)
        {
            error = value.Error;
            return value.Succeeded ? IoOutcome.Ok : IoOutcome.Error;
        }

        public void Pending(Task done) => scope.Pending(done);

        public void Ended(in CallResult<ServiceStatus> result, TimeSpan duration)
        {
            // Closed before the memory is released: a client writing after it completed cannot write into returned buffers.
            // One still running may be in the middle of a write: the response reads as empty from now on.
            if (result.Abandoned)
            {
                response.Abandon();
            }
            else
            {
                response.Close();
            }

            var cutOff = result.End is CallEnd.BudgetRanOut or CallEnd.Cancelled;
            var answered = result.End == CallEnd.Answered;
            if (answered)
            {
                CountGenerated(result.Value, target);
            }

            recorder?.EndCall(sequence, result.Outcome, result.Error, cutOff, duration, response, answered ? result.Value : default);
        }

        // Each instrument has its own boundary, as the call's other metrics (IoCall.Complete): what a host's listener throws
        // loses that one measurement, and never the call's record or its answer.
        private static void CountGenerated(in ServiceStatus status, IoTarget target)
        {
            if (status.OutputTokens is { } tokens && StrataRecMetrics.IoOutputTokens.Enabled)
            {
                try
                {
                    StrataRecMetrics.IoOutputTokens.Add(tokens, target.Tags);
                }
                catch (Exception)
                {
                }
            }

            if (status.Truncated && StrataRecMetrics.IoTruncated.Enabled)
            {
                try
                {
                    StrataRecMetrics.IoTruncated.Add(1, target.Tags);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
