using System.Runtime.InteropServices;
using StrataRec.Core.Hashing;

namespace StrataRec.Core.Threading;

/// <summary>
/// What the cached reads of one request share while it runs (docs/decisions/external-calls.md). The runs of a request,
/// its own pipeline's and those of the pipelines its nodes call, often read the same thing: a user's features, a call of
/// a service with the same inputs, a call of a pipeline. The in-process tier does not have what one of them is still
/// reading, nor yet what it has just read (records are written back off the request path), and a cache without that tier
/// never has it. So a read the in-process tier did not answer joins the read of the same record here: the first is its
/// reader and reads on as it would alone, with its own bound, recording and metrics; the others wait for what it gets,
/// within their own bounds, and send nothing.
/// </summary>
/// <remarks>
/// <para><b>Whole reads only</b>: one record, found by its cache's table and its key, which a cache computes already. A
/// context entity's features, a service call about no items, a pipeline call. Reads of many keys (the candidates'
/// features, a service call about items) are not joined: two runs' candidates overlap in part, and a wait per key is a
/// cost per row.</para>
/// <para><b>Only reads that ask the same</b> (<see cref="Asked"/>): the same record, judged by the same times, to the
/// same limit. What a tier holds is one read's hit and another's miss when their times differ, and a service refuses a
/// limit it would have answered a smaller one of; a read given another's answer then would lose one it gets alone.</para>
/// <para><b>The reader ends its read once</b> (<see cref="SharedRead"/>): with the record, which stays for the request so
/// that later reads take it at once; with the origin's failure, which those waiting get as their own and which is not
/// kept (the next read asks again); or by withdrawing, when it was cut off by its own bound or got what a cache does not
/// keep. A read whose reader withdrew reads on its own and does not join again: each read waits for one reader at
/// most, so a reader that is slow to get nothing holds the others up once, and reads never queue behind one another.
/// So nobody loses an answer it would have got alone: the worst case is the calls of today, one wait later.</para>
/// <para>One per request that has more than one run; a request of one run has none and pays nothing.</para>
/// </remarks>
internal sealed class RequestReads
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Slot, SharedRead> _reads = new();

    /// <summary>
    /// What reads of one record join under: the record's key with what the read asks of it, the times it takes the
    /// record as fresh and as stale by, and how much of it it asks for (a call's limit; 0 for a record that has none).
    /// </summary>
    public static UInt128 Asked(UInt128 record, long ttlMs, long staleIfErrorMs, long limit = 0)
    {
        using var builder = new StableHash.Builder();
        builder.Add(unchecked((long)(ulong)(record >> 64)));
        builder.Add(unchecked((long)(ulong)record));
        builder.Add(ttlMs);
        builder.Add(staleIfErrorMs);
        builder.Add(limit);
        return builder.Finish128();
    }

    /// <summary>
    /// The read of the record <paramref name="key"/> of <paramref name="table"/>'s cache: the one under way or done, or
    /// a new one whose reader the caller is.
    /// </summary>
    /// <param name="table">The cache's table: records of one table are of one layout, whichever plan reads them.</param>
    /// <param name="key">The record and what the read asks of it (<see cref="Asked"/>).</param>
    /// <param name="reader">The caller reads, and must end the read (<see cref="SharedRead.Withdraw"/> at the least).</param>
    public SharedRead Join(object table, UInt128 key, out bool reader)
    {
        lock (_gate)
        {
            ref var read = ref CollectionsMarshal.GetValueRefOrAddDefault(_reads, new Slot(table, key), out var joined);
            reader = !joined;
            return read ??= new SharedRead(this, table, key);
        }
    }

    private void Remove(SharedRead read, object table, UInt128 key)
    {
        lock (_gate)
        {
            var slot = new Slot(table, key);
            if (_reads.TryGetValue(slot, out var current) && ReferenceEquals(current, read))
            {
                _reads.Remove(slot);
            }
        }
    }

    private readonly struct Slot(object table, UInt128 key) : IEquatable<Slot>
    {
        private readonly object _table = table;
        private readonly UInt128 _key = key;

        public bool Equals(Slot other) => ReferenceEquals(_table, other._table) && _key == other._key;

        public override bool Equals(object? obj) => obj is Slot other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_table), _key);
    }

    /// <summary>One record's read, shared by the reads of a request (<see cref="RequestReads"/>).</summary>
    internal sealed class SharedRead(RequestReads reads, object table, UInt128 key)
    {
        private readonly TaskCompletionSource<SharedAnswer> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The reader got the record: those waiting take it, and so does every later read of the request.</summary>
        public void Complete(object record) => _answer.TrySetResult(new SharedAnswer(SharedEnd.Record, record, null));

        /// <summary>The origin failed the reader: those waiting fail as it did; the next read asks again.</summary>
        /// <param name="stale">
        /// The record past its time to live the reader held from a tier those waiting did not read; null when it held
        /// none. Each judges, when it falls back, whether the record still serves, as it would have had it read the tier.
        /// </param>
        public void Fail(string error, object? stale = null) => End(new SharedAnswer(SharedEnd.Failed, stale, error));

        /// <summary>
        /// The reader got nothing to share (cut off by its own bound, or an answer no cache keeps): those waiting read on
        /// their own. Does nothing once the read has ended, so a reader calls it on every way out.
        /// </summary>
        public void Withdraw() => End(new SharedAnswer(SharedEnd.Withdrawn, null, null));

        /// <summary>
        /// What the reader got, waited for no longer than <paramref name="bound"/> and <paramref name="own"/> allow:
        /// <see cref="SharedEnd.CutOff"/> when the bound ran out or the caller cancelled, <see cref="SharedEnd.TimedOut"/>
        /// at the wait's own time. Nothing was sent for it, so nothing is abandoned: the reader's call is the reader's.
        /// </summary>
        /// <param name="own">The read's own time, as its call's would be; <see cref="Timeout.InfiniteTimeSpan"/> for none.</param>
        public async ValueTask<SharedAnswer> WaitAsync(CallBound bound, TimeSpan own)
        {
            if (_answer.Task.IsCompletedSuccessfully)
            {
                return _answer.Task.Result;
            }

            if (own != Timeout.InfiniteTimeSpan && own <= TimeSpan.Zero)
            {
                return new SharedAnswer(SharedEnd.TimedOut, null, null);
            }

            try
            {
                return await _answer.Task.WaitAsync(own == Timeout.InfiniteTimeSpan || own > CallBound.MaxWait ? Timeout.InfiniteTimeSpan : own, bound.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return new SharedAnswer(SharedEnd.TimedOut, null, null);
            }
            catch (OperationCanceledException)
            {
                return new SharedAnswer(SharedEnd.CutOff, null, null);
            }
        }

        private void End(in SharedAnswer answer)
        {
            if (_answer.TrySetResult(answer))
            {
                reads.Remove(this, table, key);
            }
        }
    }
}

/// <summary>How a shared read ended for one that waited for it (<see cref="RequestReads.SharedRead.WaitAsync"/>).</summary>
internal enum SharedEnd : byte
{
    /// <summary>The reader withdrew: read on your own.</summary>
    Withdrawn = 0,

    /// <summary>The reader got the record (<see cref="SharedAnswer.Record"/>).</summary>
    Record,

    /// <summary>The origin failed the reader (<see cref="SharedAnswer.Error"/>; <see cref="SharedAnswer.Record"/> is a stale record or null).</summary>
    Failed,

    /// <summary>The wait's own time passed before the reader ended.</summary>
    TimedOut,

    /// <summary>The waiting read's bound ran out, or its caller cancelled.</summary>
    CutOff,
}

/// <summary>
/// What a shared read gave: the record as its cache keeps it, or the origin's error, with the stale record the reader
/// held then, if it held one.
/// </summary>
internal readonly record struct SharedAnswer(SharedEnd End, object? Record, string? Error);
