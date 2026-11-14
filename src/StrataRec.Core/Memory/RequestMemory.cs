using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using StrataRec.Core.Diagnostics;

namespace StrataRec.Core.Memory;

/// <summary>
/// Source of large reusable buffers (design 4.8). The default implementation wraps <see cref="ArrayPool{T}.Shared"/>;
/// hosts can provide tiered pools (for example a dedicated pool for embedding columns).
/// </summary>
public interface IBufferPool
{
    T[] Rent<T>(int minimumLength);

    void Return<T>(T[] array, bool clearArray);
}

/// <summary><see cref="IBufferPool"/> backed by <see cref="ArrayPool{T}.Shared"/>.</summary>
public sealed class SharedArrayBufferPool : IBufferPool
{
    public static SharedArrayBufferPool Instance { get; } = new();

    public T[] Rent<T>(int minimumLength) => ArrayPool<T>.Shared.Rent(Math.Max(1, minimumLength));

    public void Return<T>(T[] array, bool clearArray) => ArrayPool<T>.Shared.Return(array, clearArray);
}

/// <summary>
/// Told once that a request and every external call it started have ended: its <see cref="RequestMemory"/> has returned
/// its buffers. Until then a call the request abandoned (a timeout) may still be running inside a client, so whatever the
/// request borrowed from its plan (the plan's model clients, sources and connectors) must stay usable.
/// </summary>
public interface IRequestEndObserver
{
    /// <summary>Called on the thread of the last <see cref="RequestMemory.Release"/>, which may be a client's; must not block or throw.</summary>
    void RequestEnded();
}

/// <summary>
/// Per-request buffer ledger. Operators rent through it and never return buffers themselves; everything is
/// returned at once when the request and every external I/O it started have finished (design 4.8).
/// </summary>
/// <remarks>
/// <para>The holder count starts at 1 (the request itself). Each external I/O calls <see cref="Hold"/> when it starts
/// and <see cref="Release"/> when it completes or acknowledges cancellation; the request calls <see cref="Release"/>
/// after it has produced its result. The last release returns all buffers and then tells the
/// <see cref="IRequestEndObserver"/>, if any: the one moment from which nothing of the request can still run.</para>
/// <para>The ledger is itself a pooled array of struct entries, so renting allocates nothing per buffer.</para>
/// <para>A buffer the pool will not take back (a host's <see cref="IBufferPool"/> throws) is left to the GC, the others are
/// still returned, and the failure goes to the event log as <see cref="RequestEvent.BufferReturnFailed"/> (design 13.2).
/// <see cref="Release"/> never throws for the pool: the last release often runs on a client's thread, as the continuation
/// of a call the request abandoned, where an exception would reach no one.</para>
/// </remarks>
public sealed class RequestMemory
{
    private const int InitialLedgerLength = 64;

    private readonly IBufferPool _pool;
    private readonly IRequestEndObserver? _ended;
    private readonly RequestEventLog? _events;
    private readonly string _subject;
    private readonly string? _requestId;
    private readonly object _gate = new();
    private Lease[] _leases = Array.Empty<Lease>(); // guarded by _gate until the last Release
    private int _leaseCount;
    private int _holders = 1;
    private bool _returned;

    /// <param name="ended">Told once, after the last <see cref="Release"/> has returned every buffer.</param>
    /// <param name="events">Told of buffers the pool would not take back; null reports nothing.</param>
    /// <param name="subject">The event's subject (the pipeline).</param>
    /// <param name="requestId">The event's sample request.</param>
    public RequestMemory(
        IBufferPool? pool = null, IRequestEndObserver? ended = null, RequestEventLog? events = null, string? subject = null, string? requestId = null)
    {
        _ended = ended;
        _pool = pool ?? SharedArrayBufferPool.Instance;
        _events = events;
        _subject = subject ?? string.Empty;
        _requestId = requestId;
    }

    /// <summary>Total bytes currently rented (approximate, for metrics).</summary>
    public long RentedBytes { get; private set; }

    /// <summary>Rents an array of at least <paramref name="minimumLength"/> elements. Contents are unspecified.</summary>
    public T[] Rent<T>(int minimumLength)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_returned, this);
        }

        var array = _pool.Rent<T>(minimumLength);
        lock (_gate)
        {
            if (_returned)
            {
                // Lost a race with the final Release: give the lease back instead of leaking it.
                _pool.Return(array, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
                throw new ObjectDisposedException(nameof(RequestMemory));
            }

            if (_leaseCount == _leases.Length)
            {
                GrowLedger();
            }

            _leases[_leaseCount++] = new Lease(array, Returner<T>.Instance);
            if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                RentedBytes += (long)array.Length * Unsafe.SizeOf<T>();
            }
        }

        return array;
    }

    /// <summary>Rents an array and zero-fills the first <paramref name="length"/> elements.</summary>
    public T[] RentCleared<T>(int length)
    {
        var array = Rent<T>(length);
        Array.Clear(array, 0, length);
        return array;
    }

    /// <summary>Registers an in-flight external operation that may still write into rented buffers.</summary>
    public void Hold()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_returned, this);
            _holders++;
        }
    }

    /// <summary>Releases one holder; the last release returns every rented buffer to the pool.</summary>
    /// <returns>
    /// The holders left. When the request releases its own hold, these are external operations still running after it
    /// returned (metric <c>stratarec.io.abandoned</c>).
    /// </returns>
    public int Release()
    {
        lock (_gate)
        {
            if (_holders <= 0)
            {
                throw new InvalidOperationException("RequestMemory released more times than it was held.");
            }

            _holders--;
            if (_holders > 0)
            {
                return _holders;
            }

            _returned = true;
            RentedBytes = 0;
        }

        // Once _returned is set no Rent touches the ledger (it checks under the lock and gives a late lease straight back),
        // so the ledger can be walked outside the lock without copying it.
        var leases = _leases;
        var count = _leaseCount;
        _leases = Array.Empty<Lease>();
        _leaseCount = 0;
        Exception? failure = null;
        var failed = 0;
        for (var i = 0; i < count; i++)
        {
            try
            {
                leases[i].Returner.Return(_pool, leases[i].Array);
            }
            catch (Exception ex)
            {
                failure ??= ex;
                failed++;
            }
        }

        if (leases.Length > 0)
        {
            ArrayPool<Lease>.Shared.Return(leases, clearArray: true);
        }

        if (failure is not null)
        {
            _events?.Report(RequestEvent.BufferReturnFailed, _subject, _requestId,
                $"the buffer pool did not take back {failed} of {count} buffer(s), left to the GC: {ExceptionText.Of(failure)}");
        }

        _ended?.RequestEnded(); // also when the pool threw: what the request borrowed must not stay borrowed
        return 0;
    }

    /// <summary>Doubles the ledger. The ledger comes from the shared pool, not from <see cref="IBufferPool"/>: it is bookkeeping.</summary>
    private void GrowLedger()
    {
        var next = ArrayPool<Lease>.Shared.Rent(Math.Max(InitialLedgerLength, _leases.Length * 2));
        _leases.AsSpan(0, _leaseCount).CopyTo(next);
        if (_leases.Length > 0)
        {
            ArrayPool<Lease>.Shared.Return(_leases, clearArray: true);
        }

        _leases = next;
    }

    public bool IsReturned
    {
        get
        {
            lock (_gate)
            {
                return _returned;
            }
        }
    }

    /// <summary>One ledger entry. A struct plus a per-type singleton returner, so renting allocates no wrapper object.</summary>
    private readonly record struct Lease(Array Array, ArrayReturner Returner);

    private abstract class ArrayReturner
    {
        public abstract void Return(IBufferPool pool, Array array);
    }

    private sealed class Returner<T> : ArrayReturner
    {
        public static readonly Returner<T> Instance = new();

        public override void Return(IBufferPool pool, Array array)
        {
            var typed = (T[])array;
            var containsRefs = RuntimeHelpers.IsReferenceOrContainsReferences<T>();
#if STRATAREC_POISON_BUFFERS
            if (!containsRefs)
            {
                // Debug/test builds fill returned buffers with a fixed pattern so use-after-return surfaces quickly.
                Poison(typed);
            }
#endif
            pool.Return(typed, containsRefs);
        }

#if STRATAREC_POISON_BUFFERS
        private static void Poison(T[] buffer)
        {
            ref var first = ref Unsafe.As<T, byte>(ref MemoryMarshal.GetArrayDataReference(buffer));
            MemoryMarshal.CreateSpan(ref first, buffer.Length * Unsafe.SizeOf<T>()).Fill(0xCD);
        }
#endif
    }
}
