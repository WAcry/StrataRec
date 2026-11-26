using System.Runtime.CompilerServices;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Services;

namespace StrataRec.Pipeline.Operators;

/// <summary>
/// The response an operator passes to <see cref="OperatorContext.CallAsync"/>: tensor outputs into buffers rented from
/// the request, of the element type the client asks for; text outputs as the strings it sets; and the items it adds, in
/// rank order, capped at <see cref="Limit"/> (excess is dropped and counted). Read it only after the call
/// succeeded.
/// </summary>
/// <remarks>
/// <para>Closed by the framework when the call ends, before the call releases the request memory: a client writing after
/// it returned (which it must not) changes nothing and never writes into returned buffers. A call the framework stopped
/// waiting for (its timeout, the node's budget, the caller) is abandoned: the client may still be writing, so from then on
/// the response reads as empty, and nobody reads the buffers it may be writing; they stay rented until it completes (the
/// call holds the request memory).</para>
/// <para>Being a framework type lets the call record what a service returned, and answer it from a recording, for every
/// operator. Public so that operators outside this assembly read answers the way the built-in ones do.</para>
/// </remarks>
public sealed class ServiceResponse : IServiceResponse
{
    private readonly RequestMemory _memory;
    private readonly object _gate = new();
    private readonly EntityKey[] _keys;
    private readonly double[] _scores;
    private List<(string Name, TensorValues Values)>? _tensors;
    private List<(string Name, string Text)>? _texts;
    private int _count;
    private int _dropped;
    private bool _closed;
    private bool _abandoned;

    /// <param name="limit">The most items the call may add (its <see cref="ServiceCall.Limit"/>); 0 for a call that adds none.</param>
    public ServiceResponse(RequestMemory memory, int limit = 0)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        _memory = memory;
        Limit = limit;
        (_keys, _scores) = limit > 0 ? (memory.Rent<EntityKey>(limit), memory.Rent<double>(limit)) : ([], []);
    }

    /// <summary>The most items kept; also the <see cref="ServiceCall.Limit"/> to ask for.</summary>
    public int Limit { get; }

    /// <summary>Items added; 0 once the call was abandoned.</summary>
    public int Count => Volatile.Read(ref _abandoned) ? 0 : _count;

    public ReadOnlySpan<EntityKey> Keys => _keys.AsSpan(0, Count);

    /// <summary>Scores of <see cref="Keys"/>; a non-finite score is stored as 0.</summary>
    public ReadOnlySpan<double> Scores => _scores.AsSpan(0, Count);

    /// <summary>
    /// Items the service added past the call's limit, dropped because the response was full; 0 once the call was abandoned.
    /// Not <see cref="ServiceStatus.Truncated"/>, which says a generation was cut short.
    /// </summary>
    public int Dropped => Volatile.Read(ref _abandoned) ? 0 : _dropped;

    public Memory<T> GetValues<T>(string name, int elementCount)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        _ = TensorValues.TypeOf<T>(); // an element type, before anything is rented
        lock (_gate)
        {
            if (_closed)
            {
                return new T[elementCount]; // after the call: written nowhere, and the request memory may be gone
            }

            // A string buffer starts cleared, so an element the client left unwritten reads as null, not as another call's string.
            var length = Math.Max(1, elementCount);
            var array = RuntimeHelpers.IsReferenceOrContainsReferences<T>() ? _memory.RentCleared<T>(length) : _memory.Rent<T>(length);
            var buffer = array.AsMemory(0, elementCount);
            Set(ref _tensors, name, TensorValues.Of<T>(buffer));
            return buffer;
        }
    }

    /// <summary>Keeps the text; a name set twice keeps the last.</summary>
    public void SetText(string name, string text)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            if (!_closed)
            {
                Set(ref _texts, name, text);
            }
        }
    }

    public void AddItem(long entityId, double score) => AddItem(EntityKey.FromInt64(entityId), score);

    public void AddItem(string entityId, double score) => AddItem(EntityKey.FromString(entityId), score);

    /// <summary>The output the client wrote under <paramref name="name"/>, of the element type it chose; a name asked for twice keeps the last buffer.</summary>
    public bool TryGetValues(string name, out TensorValues values)
    {
        if (Volatile.Read(ref _abandoned))
        {
            values = default;
            return false;
        }

        lock (_gate)
        {
            if (!_abandoned && Find(_tensors, name) is >= 0 and var i)
            {
                values = _tensors![i].Values;
                return true;
            }
        }

        values = default;
        return false;
    }

    public bool TryGetText(string name, out string text)
    {
        if (Volatile.Read(ref _abandoned))
        {
            text = string.Empty;
            return false;
        }

        lock (_gate)
        {
            if (!_abandoned && Find(_texts, name) is >= 0 and var i)
            {
                text = _texts![i].Text;
                return true;
            }
        }

        text = string.Empty;
        return false;
    }

    /// <summary>
    /// Counts items a service added past the limit, which a cache's answer and a recording carry (<see cref="Dropped"/>);
    /// at most <see cref="int.MaxValue"/>.
    /// </summary>
    internal void AddDropped(int count)
    {
        if (!Volatile.Read(ref _closed) && count > 0)
        {
            _dropped = (int)Math.Min(int.MaxValue, (long)_dropped + count);
        }
    }

    internal void AddItem(EntityKey key, double score)
    {
        if (Volatile.Read(ref _closed))
        {
            return;
        }

        if (_count >= Limit)
        {
            _dropped = _dropped == int.MaxValue ? _dropped : _dropped + 1;
            return;
        }

        _keys[_count] = key;
        _scores[_count] = double.IsFinite(score) ? score : 0.0;
        _count++;
    }

    /// <summary>The tensor outputs written, for the recording: read after <see cref="Close"/> of a call that answered.</summary>
    internal IReadOnlyList<(string Name, TensorValues Values)> Tensors => (IReadOnlyList<(string, TensorValues)>?)_tensors ?? [];

    /// <summary>The text outputs set, for the recording.</summary>
    internal IReadOnlyList<(string Name, string Text)> Texts => (IReadOnlyList<(string, string)>?)_texts ?? [];

    internal void Close()
    {
        lock (_gate)
        {
            Volatile.Write(ref _closed, true);
        }
    }

    /// <summary>
    /// The call was abandoned with the client still running: closes the response and hides what it holds, without waiting
    /// for a write in progress (the wait it ends must not depend on one: renting from a host's buffer pool may block). A
    /// write in progress completes into buffers the call keeps rented until the client completes, and nobody reads them.
    /// </summary>
    internal void Abandon()
    {
        Volatile.Write(ref _abandoned, true);
        Volatile.Write(ref _closed, true);
    }

    private static void Set<T>(ref List<(string Name, T Value)>? entries, string name, T value)
    {
        entries ??= new List<(string Name, T Value)>(2);
        if (Find(entries, name) is >= 0 and var i)
        {
            entries[i] = (name, value);
        }
        else
        {
            entries.Add((name, value));
        }
    }

    private static int Find<T>(List<(string Name, T Value)>? entries, string name)
    {
        if (entries is not null)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                if (string.Equals(entries[i].Name, name, StringComparison.Ordinal))
                {
                    return i;
                }
            }
        }

        return -1;
    }
}
