using System.Runtime.InteropServices;

namespace StrataRec.Core.Threading;

/// <summary>
/// What the cached reads of one request share while it runs (docs/decisions/external-calls.md). The runs of a request,
/// its own pipeline's and those of the pipelines its nodes call, often read the same thing: a user's features, a call of
/// a service with the same inputs, a call of a pipeline. The tiers do not have what one of them is still getting from the
/// origin, nor yet what it has just got (records are written back off the request path). So a read the tiers did not
/// answer joins the request's read of the same record here before it asks the origin (the connector, the service, a
/// run of the pipeline): the first is its reader and asks, as it would alone; the others wait for what it gets, within
/// their own bounds, and ask nothing.
/// </summary>
/// <remarks>
/// <para><b>Only the origin is asked once.</b> Each read reads the tiers itself, by its own plan and times, and so holds
/// the stale record it falls back to; what is shared is the one thing a tier cannot give.</para>
/// <para><b>Only a record is shared</b>, the one the reader's cache keeps: it stays for the request, and a read takes it
/// as it takes a tier's, when it is fresh by its own times and answers what it asks. A reader that got anything else (a
/// failure, an answer in part, one its cache does not keep, its own time running out) withdraws, and those waiting ask
/// the origin themselves: a failure is the word of one call, sent by one plan within one budget, and is not another
/// call's. A read whose reader withdrew does not join again: each read waits for one reader at most, so a reader that is
/// slow to get nothing holds the others up once, and reads never queue behind one another.</para>
/// <para><b>Whole reads only</b>: one record, found by its cache's table and its key, which a cache computes already. A
/// context entity's features, a service call about no items, a pipeline call. Reads of many keys (the candidates'
/// features, a service call about items) are not joined: two runs' candidates overlap in part, and a wait per key is a
/// cost per row.</para>
/// <para>One per request, whatever its plan: whether two reads of a request share does not depend on what else the
/// request runs. A replay has none (it reads no cache).</para>
/// <para><b>Requests of one group</b>. The requests a host sends for one thing at the same time (a page's feed and its
/// promoted row) read the same things as the runs of one request do, and share the same way: each has its own of this
/// over the group's reads (<see cref="Group"/>). A read is the group's for as long as the request that made it is
/// under way, and leaves with it (<see cref="End"/>): no read that comes later finds it, so a record lives no longer
/// than the request that read it, however many others took it and however long the host keeps the group. A request
/// that comes later reads for itself (the tiers are what serve requests that follow one another). A read still under
/// way when its request ends (of a call its operator did not wait for) goes on as that call does, to its own bound:
/// those already waiting for it get what it gets, and the group keeps nothing of it.</para>
/// </remarks>
internal sealed class RequestReads
{
    private readonly Group _group;
    private readonly List<SharedRead>? _made; // of a request of a host's group: the reads it made there, which leave with it
    private bool _ended;

    /// <summary>The reads of a request on its own: gone with it.</summary>
    public RequestReads() => _group = new Group();

    /// <summary>The reads of one request of <paramref name="group"/>, which must <see cref="End"/> them when it ends.</summary>
    public RequestReads(Group group)
    {
        _group = group;
        _made = new List<SharedRead>();
    }

    /// <summary>
    /// The request's read of the record <paramref name="key"/> of <paramref name="table"/>'s cache from its origin: the
    /// one under way or done, or a new one whose reader the caller is.
    /// </summary>
    /// <param name="table">The cache's table: records of one table are of one layout, whichever plan reads them.</param>
    /// <param name="key">The record's key in its cache.</param>
    /// <param name="limit">
    /// How much of the record the read asks its origin for, when that is not in the key (a service call's limit: a longer
    /// answer serves a shorter call, so one record is kept for both); 0 for a record that has none. Reads that ask for
    /// different amounts are not one read: the origin may refuse the larger and answer the smaller, and a short answer
    /// kept for the request would leave those that ask for more nothing to share.
    /// </param>
    /// <param name="reader">The caller asks the origin, and must end the read (<see cref="SharedRead.Withdraw"/> at the least).</param>
    /// <param name="plan">
    /// For a record whose origin is a run of a plan (a call of a pipeline): that plan. A call of the plan published
    /// since is not the same read as one of the plan before, whose run may itself make calls that wait for this one's.
    /// Null for a record a connector or a service answers, which is the same whichever plan asks.
    /// </param>
    public SharedRead Join(object table, UInt128 key, long limit, out bool reader, object? plan = null)
    {
        var slot = new Slot(table, plan, key, limit);
        lock (_group.Gate)
        {
            if (_ended)
            {
                // A read that outlived its request (a call the request stopped waiting for) reads for itself: what it
                // gets is nobody else's.
                reader = true;
                return new SharedRead(null, slot);
            }

            ref var read = ref CollectionsMarshal.GetValueRefOrAddDefault(_group.Reads, slot, out var joined);
            reader = !joined;
            if (reader)
            {
                read = new SharedRead(_group, slot);
                _made?.Add(read);
            }

            return read!;
        }
    }

    /// <summary>
    /// The request ended: the reads it made leave the group, so no later read finds them, and a read it starts from now
    /// on reads for itself. A read still under way is not ended here: its reader ends it, as it ends every read, and
    /// those already waiting for it get what it gets. Ending again changes nothing.
    /// </summary>
    public void End()
    {
        lock (_group.Gate)
        {
            // Either way the request no longer holds its reads, nor their records: a call it did not wait for may
            // keep this object long after.
            _ended = true;
            if (_made is null)
            {
                _group.Reads.Clear(); // a request on its own: the group is its own, and nothing joins it any more
                return;
            }

            foreach (var read in _made)
            {
                _group.Leave(read);
            }

            _made.Clear();
        }
    }

    /// <summary>
    /// The reads under way or done that the requests of one group share: one request's own, or those of the requests a
    /// host sends as one.
    /// </summary>
    internal sealed class Group
    {
        internal Lock Gate { get; } = new();

        internal Dictionary<Slot, SharedRead> Reads { get; } = new();

        /// <summary>The reads the group holds now; for tests.</summary>
        internal int Count
        {
            get
            {
                lock (Gate)
                {
                    return Reads.Count;
                }
            }
        }

        /// <summary>
        /// <paramref name="read"/> leaves its place, if it still has it: the next read of the record asks the origin. Under
        /// <see cref="Gate"/>.
        /// </summary>
        internal void Leave(SharedRead read)
        {
            if (Reads.TryGetValue(read.Slot, out var current) && ReferenceEquals(current, read))
            {
                Reads.Remove(read.Slot);
            }
        }
    }

    internal readonly struct Slot(object table, object? plan, UInt128 key, long limit) : IEquatable<Slot>
    {
        private readonly object _table = table;
        private readonly object? _plan = plan;
        private readonly UInt128 _key = key;
        private readonly long _limit = limit;

        public bool Equals(Slot other) =>
            ReferenceEquals(_table, other._table) && ReferenceEquals(_plan, other._plan) && _key == other._key && _limit == other._limit;

        public override bool Equals(object? obj) => obj is Slot other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_table), _key, _limit);
    }

    /// <summary>One record's read from its origin, shared by the reads of a request (<see cref="RequestReads"/>).</summary>
    internal sealed class SharedRead
    {
        private readonly Group? _group; // null for a read no other can join

        internal SharedRead(Group? group, in Slot slot)
        {
            _group = group;
            Slot = slot;
        }

        internal Slot Slot { get; }

        private readonly TaskCompletionSource<SharedAnswer> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The reader got the record its cache keeps: those waiting are given it, and so is every later read of the request.</summary>
        public void Complete(object record) => _answer.TrySetResult(new SharedAnswer(SharedEnd.Record, record));

        /// <summary>
        /// The reader got no record to share: those waiting ask the origin themselves, and the next read of the record is
        /// a reader. Does nothing once the read has ended, so a reader calls it on every way out.
        /// </summary>
        public void Withdraw()
        {
            if (!_answer.Task.IsCompleted)
            {
                if (_group is { } group)
                {
                    lock (group.Gate)
                    {
                        group.Leave(this); // first: a read that sees this one ended finds its place free
                    }
                }

                _answer.TrySetResult(default);
            }
        }

        /// <summary>
        /// What the reader got, waited for as a call of the waiting read's own would be (<see cref="BoundedCall"/>): for
        /// what is left of <paramref name="bound"/> and of <paramref name="deadline"/>, by a timer, and until the caller
        /// cancels. A read whose time is up takes nothing, an answer that is there no more than one it would wait for:
        /// the call it stands for would not be sent. Nothing was sent for it, so nothing is abandoned.
        /// </summary>
        /// <param name="deadline">The read's own deadline, as its call's would be (a service's <c>timeout_ms</c>); none for a read that has none.</param>
        /// <returns>
        /// <see cref="SharedEnd.TimedOut"/> when the read's own deadline came first, <see cref="SharedEnd.CutOff"/> when
        /// the bound ran out or the caller cancelled.
        /// </returns>
        public async ValueTask<SharedAnswer> WaitAsync(CallBound bound, CallDeadline deadline)
        {
            var most = CallBound.Until(bound.Deadline);
            var left = deadline.Remaining;
            var own = deadline.IsSet && left < most; // which comes first is told now, while both are ahead
            if (own)
            {
                most = left;
            }

            if (bound.Passed || most <= TimeSpan.Zero)
            {
                return new SharedAnswer(own && !bound.Passed ? SharedEnd.TimedOut : SharedEnd.CutOff, null);
            }

            try
            {
                return await _answer.Task.WaitAsync(most, bound.Caller).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return new SharedAnswer(own ? SharedEnd.TimedOut : SharedEnd.CutOff, null);
            }
            catch (OperationCanceledException)
            {
                return new SharedAnswer(SharedEnd.CutOff, null);
            }
        }
    }
}

/// <summary>How a shared read ended for one that waited for it (<see cref="RequestReads.SharedRead.WaitAsync"/>).</summary>
internal enum SharedEnd : byte
{
    /// <summary>The reader withdrew: ask the origin yourself.</summary>
    Withdrawn = 0,

    /// <summary>The reader got the record (<see cref="SharedAnswer.Record"/>).</summary>
    Record,

    /// <summary>The waiting read's own deadline passed before the reader ended.</summary>
    TimedOut,

    /// <summary>The waiting read's bound ran out, or its caller cancelled.</summary>
    CutOff,
}

/// <summary>What a shared read gave: the record as its cache keeps it, when the reader got one.</summary>
internal readonly record struct SharedAnswer(SharedEnd End, object? Record);
