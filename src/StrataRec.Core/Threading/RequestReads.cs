using System.Runtime.InteropServices;

namespace StrataRec.Core.Threading;

internal sealed class RequestReads
{
    private readonly Group _group;
    private readonly List<SharedRead>? _made; // of a request of a host's group: the reads it made there, which leave with it
    private bool _ended;

    public RequestReads() => _group = new Group();

    /// <summary>The reads of one request of <paramref name="group"/>, which must <see cref="End"/> them when it ends.</summary>
    public RequestReads(Group group)
    {
        _group = group;
        _made = new List<SharedRead>();
    }

    /// <param name="reader">The caller asks the origin, and must end the read (<see cref="SharedRead.Withdraw"/> at the least).</param>
    public SharedRead Join(object table, UInt128 key, long limit, out bool reader, object? plan = null)
    {
        var slot = new Slot(table, plan, key, limit);
        lock (_group.Gate)
        {
            if (_ended)
            {
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
    /// those already waiting for it get what it gets.
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
                _group.Reads.Clear();
                return;
            }

            foreach (var read in _made)
            {
                _group.Leave(read);
            }

            _made.Clear();
        }
    }

    internal sealed class Group
    {
        internal Lock Gate { get; } = new();

        internal Dictionary<Slot, SharedRead> Reads { get; } = new();

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
        /// Under <see cref="Gate"/>.
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

        public void Complete(object record) => _answer.TrySetResult(new SharedAnswer(SharedEnd.Record, record));

        /// <summary>
        /// Does nothing once the read has ended, so a reader calls it on every way out.
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
        /// A read whose time is up takes nothing, an answer that is there no more than one it would wait for: the call
        /// it stands for would not be sent.
        /// </summary>
        public async ValueTask<SharedAnswer> WaitAsync(CallBound bound, CallDeadline deadline)
        {
            var most = CallBound.Until(bound.Deadline);
            var left = deadline.Remaining;
            var own = deadline.IsSet && left < most;
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

internal enum SharedEnd : byte
{
    /// <summary>The reader withdrew: ask the origin yourself.</summary>
    Withdrawn = 0,

    Record,

    TimedOut,

    CutOff,
}

internal readonly record struct SharedAnswer(SharedEnd End, object? Record);
