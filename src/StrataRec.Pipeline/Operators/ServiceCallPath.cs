using System.Runtime.ExceptionServices;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Memory;
using StrataRec.Core.Services;
using StrataRec.Core.Threading;
using StrataRec.Pipeline.Replay;

namespace StrataRec.Pipeline.Operators;

/// <summary>
/// Who makes a service call: a node, or the feature prefetch for a source in mode <c>service</c> (IO step 5). The
/// recording says which, and a replay answers a caller's own recorded calls first.
/// </summary>
internal readonly struct ServiceCaller
{
    private ServiceCaller(string? node, string? source, string key)
    {
        Node = node;
        Source = source;
        Key = key;
    }

    /// <summary>The calling node; null for a feature source's call.</summary>
    public string? Node { get; }

    /// <summary>The feature source whose features the call is for; null for a node's call.</summary>
    public string? Source { get; }

    /// <summary>One string per caller, which a replay matches a recorded call's on, with whether it is a source's (a node
    /// built without the plan compiler's checks may spell a source's key): the node id, or <c>source:&lt;name&gt;</c>.</summary>
    public string Key { get; }

    public static ServiceCaller OfNode(string node) => new(node, null, node);

    /// <summary>A source's caller, made once per source (its key is a string of its own).</summary>
    public static ServiceCaller OfSource(string source) => new(null, source, KeyOf(null, source));

    /// <summary>The key of a recorded call's caller (<see cref="Artifacts.Model.ReplayServiceCall"/>'s node or source).</summary>
    public static string KeyOf(string? node, string? source) => node ?? "source:" + source;
}

/// <summary>
/// What a service call needs of whoever makes it (<see cref="ServiceCallPath"/>): a node's context, or a feature source's
/// call of one request.
/// </summary>
internal interface IServiceCallScope
{
    ServiceCaller Caller { get; }

    /// <summary>The bound the call is waited for within, with the caller's token: a node's budget, a fetch's.</summary>
    CallBound Budget { get; }

    RequestMemory Memory { get; }

    RunRecorder? Recorder { get; }

    RunReplay? Replay { get; }

    /// <summary>The call was answered with stale values (its cache's), or was when it was recorded.</summary>
    void ReportDegraded(string reason);

    /// <summary>
    /// An external call of this scope (to the service, or its cache's distributed tier) did not complete synchronously;
    /// <paramref name="done"/> completes once it really has: a feature fetch tracks it, as its connectors' calls (review of
    /// IO step 5). The call holds the request memory until then either way.
    /// </summary>
    void Pending(Task done);
}

/// <summary>
/// The one path of a service call (design 6.9, IO steps 2–5), whoever makes it: a replay answers it from the recording;
/// otherwise its cache, then its client through <see cref="BoundedCall"/>, recorded when the request records. A node's
/// calls (<see cref="OperatorContext.CallAsync"/>) and a feature source's (<see cref="FeatureServiceSupply"/>) go through
/// it, so both are bounded, cached, measured, recorded and replayed alike.
/// </summary>
internal static class ServiceCallPath
{
    /// <summary>
    /// Calls the service; the call has been checked by its caller. Closes <paramref name="response"/>. Returns the client's
    /// status; throws <see cref="OperationCanceledException"/> when the scope's bound ran out or the caller cancelled (in a
    /// replay, where the recorded call was).
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
                    scope.ReportDegraded(degraded); // the call was Degraded when it was recorded
                }

                return status;
            }
            finally
            {
                response.Close();
            }
        }

        if (service.Cache is { } cache)
        {
            return await CachedServiceCall.RunAsync(scope, service, cache, call, response).ConfigureAwait(false);
        }

        var recorder = scope.Recorder;
        var sequence = recorder?.BeginCall(scope.Caller, service.Target, call) ?? 0;
        var result = await BoundedCall.RunAsync<ServiceStatus, ServiceInvocation>(
            new ServiceInvocation(scope, service.Client, call, response, service.Target, recorder, sequence), scope.Budget, scope.Memory, service.Target,
            CallDeadline.After(service.TimeoutMs)).ConfigureAwait(false);
        // Every end named, no default arm: a new kind of end does not compile until it is decided here (architecture audit).
        return result.End switch
        {
            CallEnd.Answered => result.Value,
            CallEnd.TimedOut or CallEnd.Threw => ServiceStatus.Failed(result.Error ?? "failed"),
            CallEnd.BudgetRanOut or CallEnd.Cancelled => Rethrow(result.Exception!), // the bound's or the caller's
        };
    }

    /// <summary>
    /// Sends a call to the service's client, bounded and measured like every call, without recording it: the call of the
    /// items a service's cache missed (<see cref="CachedServiceCall"/>), whose caller records the whole call. The deadline
    /// is what is left of the service's <c>timeout_ms</c>; a call whose deadline has passed is not sent.
    /// </summary>
    public static ValueTask<CallResult<ServiceStatus>> SendAsync(
        IServiceCallScope scope, BoundService service, ServiceCall call, ServiceResponse response, in CallDeadline deadline) =>
        BoundedCall.RunAsync<ServiceStatus, ServiceInvocation>(
            new ServiceInvocation(scope, service.Client, call, response, service.Target, null, 0), scope.Budget, scope.Memory, service.Target, deadline);

    /// <summary>Throws <paramref name="exception"/> with the stack it was thrown with.</summary>
    private static ServiceStatus Rethrow(Exception exception)
    {
        ExceptionDispatchInfo.Throw(exception);
        return default;
    }

    /// <summary>
    /// A service call: closes or abandons the response when the call ends; counts what a generative client reported
    /// (<see cref="StrataRecMetrics.IoOutputTokens"/>, <see cref="StrataRecMetrics.IoTruncated"/>) for every call that
    /// answered, in this one place for every caller; and records what the request saw, with the outputs only of a call
    /// that answered. A replay never gets here: it is answered from the recording before the call is sent, so it counts
    /// nothing.
    /// </summary>
    private readonly struct ServiceInvocation(
        IServiceCallScope scope, IServiceClient client, ServiceCall call, ServiceResponse response, IoTarget target, RunRecorder? recorder, int sequence)
        : IBoundedCall<ServiceStatus>
    {
        public ValueTask<ServiceStatus> Send(CancellationToken token) => client.CallAsync(call, response, token);

        public int Keys => call.Items.IsEmpty ? -1 : call.Items.Length;

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

            // Whether the caller is thrown cancellation, so a replay throws it too (a failure is a status, not a throw).
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
                    // A metric listener threw: see above.
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
                    // As above.
                }
            }
        }
    }
}
