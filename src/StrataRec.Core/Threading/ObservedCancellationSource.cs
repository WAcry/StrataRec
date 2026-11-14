using StrataRec.Core.Diagnostics;

namespace StrataRec.Core.Threading;

/// <summary>
/// Which token the framework cancelled when callbacks registered on it threw: the <c>stratarec.token</c> tag of
/// <see cref="StrataRecMetrics.CancellationCallbackErrors"/>, and the subject of the request event.
/// </summary>
public enum CancellationSite : byte
{
    /// <summary>An assignment provider's token: cancelled at the request's deadline, or when the engine stops waiting.</summary>
    Assignment,

    /// <summary>A node attempt's token, held by its operator and its clients: cancelled at the node's budget.</summary>
    Node,

    /// <summary>A candidate source or model call's token: cancelled at the call's own timeout.</summary>
    Call,

    /// <summary>
    /// A feature fetch's token, shared by its chunks and by the read of a cache source's distributed tier: cancelled at
    /// the fetch's budget.
    /// </summary>
    Fetch,

    /// <summary>The log sink's token: cancelled when the log pipeline's drain timeout runs out.</summary>
    LogDrain,

    /// <summary>A binding instance's load: cancelled when no lease holds the instance any more.</summary>
    BindingLoad,

    /// <summary>A local table's source run: cancelled when the table stops.</summary>
    LocalTable,

    /// <summary>A plan's model signature lookups: cancelled when the lease gives up on them.</summary>
    SignatureLookup,

    /// <summary>A plan load's token: cancelled when the engine is disposed during the load.</summary>
    PlanLoad,

    /// <summary>The writer of a distributed cache tier: cancelled when the tier is disposed.</summary>
    CacheWriter,
}

/// <summary>
/// A token source whose token the framework hands to code it does not own (an operator, a connector, a model client, a
/// candidate source, an assignment provider, a cache store, a log sink). Cancelling a token runs the callbacks
/// registered on it synchronously, on the cancelling thread, and what they throw comes out of the cancellation: out of
/// the timer behind <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>, that ends the process. Every
/// cancellation of this token goes through <see cref="CancelObservedAsync"/> instead, whether the framework cancels it
/// at a time (<see cref="CancelAfterObserved"/>) or now, or the token it is linked to is cancelled: the token observes
/// the cancellation at once, the callbacks run on the thread pool, never on a timer's or a canceller's thread, and what
/// they throw is observed: counted (<see cref="StrataRecMetrics.CancellationCallbackErrors"/>), reported, and never
/// thrown or left in an unobserved task.
/// </summary>
/// <remarks>
/// <para>Linked to a token (the caller's, or the token of a framework source it nests in), it is cancelled with it, but
/// unlike <see cref="CancellationTokenSource.CreateLinkedTokenSource(CancellationToken)"/> not by running its callbacks
/// in that cancellation: a caller's own timer (a request timeout made with <c>CancelAfter</c>) would otherwise run an
/// extension's callback on the timer's thread all the same. So a caller's <c>Cancel</c> never throws what an extension's
/// callback threw. The framework never waits on these tokens' callbacks: its waits are bounded by the caller's token and
/// absolute deadlines (the shared waits' invariant 1), and a source is disposed only once whoever holds its token has
/// ended (invariant 3), so callbacks queued on the pool are never dropped by a disposal.</para>
/// <para>Costs as a linked source with <c>CancelAfter</c>: the source, its registration on the linked token, and a
/// timer (the same timer object as <c>CancelAfter</c>'s, in a 24 B wrapper). Nothing else unless the token is cancelled
/// while callbacks are registered on it: then the task that runs them, and one continuation until it has.</para>
/// <para>Reporting: the <see cref="RequestEventLog"/> given, if any, summarizes failures per window
/// (<see cref="RequestEvent.CancellationCallbacksFailed"/>, subject the site); a subclass with a logger of its own
/// overrides <see cref="CallbacksFailed"/>. Neither can throw out of here.</para>
/// </remarks>
public class ObservedCancellationSource : CancellationTokenSource
{
    private static readonly string[] SiteNames =
    {
        "assignment", "node", "call", "fetch", "log_drain", "binding_load", "local_table", "signature_lookup", "plan_load", "cache_writer",
    };

    /// <summary>One tag array per site, built once: counting a failure allocates nothing.</summary>
    private static readonly KeyValuePair<string, object?>[][] SiteTags = Tags();

    private static readonly TimerCallback Expire = static state => _ = ((ObservedCancellationSource)state!).CancelObservedAsync();

    private static readonly Action<object?> LinkedCancelled = static state => ((ObservedCancellationSource)state!).CancelLinked();

    private static readonly Action<Task, object?> Observe = static (callbacks, state) => ((ObservedCancellationSource)state!).Failed(callbacks.Exception!);

    private readonly CancellationTokenRegistration _link;
    private readonly RequestEventLog? _events;
    private readonly string? _requestId;
    private readonly CancellationSite _site;
    private ITimer? _timer;

    /// <param name="site">The token's site, for the metric and the request event.</param>
    /// <param name="linkedTo">A token this one is cancelled with; none by default.</param>
    /// <param name="events">Where a failure is summarized, if anywhere.</param>
    /// <param name="requestId">The request a failure is sampled with.</param>
    public ObservedCancellationSource(CancellationSite site, CancellationToken linkedTo = default, RequestEventLog? events = null, string? requestId = null)
    {
        _site = site;
        _events = events;
        _requestId = requestId;
        if (linkedTo.CanBeCanceled)
        {
            _link = linkedTo.UnsafeRegister(LinkedCancelled, this);
        }
    }

    /// <summary>Which token this is.</summary>
    public CancellationSite Site => _site;

    /// <summary>The tag value of <paramref name="site"/>.</summary>
    public static string NameOf(CancellationSite site) => SiteNames[(int)site];

    /// <summary>
    /// Cancels the token once <paramref name="delay"/> has passed, as <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>,
    /// but from the timer through <see cref="CancelObservedAsync"/>: the timer thread only queues the callbacks. Set once,
    /// before the token is handed out; disposing the source stops the timer.
    /// </summary>
    /// <param name="delay">At most <see cref="uint.MaxValue"/> − 1 ms, as for <c>CancelAfter</c>.</param>
    public void CancelAfterObserved(TimeSpan delay)
    {
        if (_timer is not null)
        {
            throw new InvalidOperationException("The cancellation's timer is set already.");
        }

        _timer = TimeProvider.System.CreateTimer(Expire, this, delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Cancels the token now. The token observes the cancellation before this returns; its callbacks run on the thread
    /// pool. Returns the task that runs them: it faults with what they threw, which is counted and reported whether or
    /// not anyone awaits it, so a caller that does not need to know when they are done can drop it. A source cancelled
    /// already, or disposed, is left as it is (a completed task).
    /// </summary>
    public Task CancelObservedAsync()
    {
        Task callbacks;
        try
        {
            callbacks = CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            return Task.CompletedTask; // see below
        }

        if (!callbacks.IsCompleted)
        {
            _ = callbacks.ContinueWith(Observe, this, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return callbacks;
        }

        if (callbacks.IsFaulted)
        {
            var failure = callbacks.Exception!; // observed here either way
            if (failure.InnerException is ObjectDisposedException)
            {
                // CancelAsync returns, rather than throws, the disposal of a source disposed already (between the timer's
                // due time and its callback, say): whoever held the token has finished with it. No callback ran.
                return Task.CompletedTask;
            }

            Failed(failure);
        }

        return callbacks;
    }

    /// <summary>
    /// Called once when the callbacks of this token's cancellation threw. A subclass with a logger logs it; the default
    /// does nothing more. Whatever it throws is dropped.
    /// </summary>
    protected virtual void CallbacksFailed(AggregateException errors)
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Interlocked.Exchange(ref _timer, null)?.Dispose();
            _link.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>The linked token was cancelled: so is this one, its callbacks on the pool (see the remarks).</summary>
    private void CancelLinked() => _ = CancelObservedAsync();

    private void Failed(AggregateException errors)
    {
        try
        {
            StrataRecMetrics.CancellationCallbackErrors.Add(1, SiteTags[(int)_site]);
            _events?.Report(RequestEvent.CancellationCallbacksFailed, SiteNames[(int)_site], _requestId, ExceptionText.Of(First(errors)));
            CallbacksFailed(errors);
        }
        catch (Exception)
        {
            // A failing listener or logger cannot report itself, and must not take the timer or the pool thread down.
        }
    }

    /// <summary>The first exception a callback threw, through the aggregates of linked cancellations.</summary>
    private static Exception First(AggregateException errors)
    {
        Exception first = errors;
        while (first is AggregateException { InnerExceptions.Count: > 0 } aggregate)
        {
            first = aggregate.InnerExceptions[0];
        }

        return first;
    }

    private static KeyValuePair<string, object?>[][] Tags()
    {
        var tags = new KeyValuePair<string, object?>[SiteNames.Length][];
        for (var i = 0; i < tags.Length; i++)
        {
            tags[i] = new[] { new KeyValuePair<string, object?>(StrataRecTags.Token, SiteNames[i]) };
        }

        return tags;
    }
}
