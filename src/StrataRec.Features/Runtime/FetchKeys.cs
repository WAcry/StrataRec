using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Features.Connectors;

namespace StrataRec.Features.Runtime;

/// <summary>
/// The distinct keys of one fetch or read of a candidate-scoped group: each row's key deduplicated as a
/// tuple, in first-seen order (entry k is the k-th distinct key), and their <see cref="EntityKeyBatch"/> for a connector,
/// a table or a recording. The one place that knows how a key's parts are deduplicated.
/// </summary>
/// <remarks>
/// <para>Only the parts that vary per row are indexed. The shared parts (request inputs) have one value for the whole
/// fetch, read once (<see cref="Reset"/>), so a row costs no hash or comparison of them, and a batch carries each once.</para>
/// <para>One varying part (a one-part key, or one column with shared parts beside it) is an <see cref="EntityKeyIndex"/> on
/// that part: one <c>long</c> or string hashed and compared per row, as before keys had parts. Several varying parts are an
/// <see cref="EntityKeyTupleIndex"/>, which hashes them together and compares them part by part on a hash match. Every
/// array is rented from the request's memory, so deduplicating thousands of rows allocates nothing per row.</para>
/// <para>A struct, so that a fetch costs what it did with one-part keys: the one index object. A fetch holds its own (in
/// its pending state); a request's local reads share one, reset by each read, whose indexes are made with the request
/// (<see cref="Prepared"/>). A copy shares the indexes: take one only once the keys are added, to read them.</para>
/// </remarks>
internal struct FetchKeys
{
    private EntityKeyIndex? _single;
    private EntityKeyTupleIndex? _tuples;
    private EntityKey[]? _shared; // rented: the shared parts' values in part order, for the batch
    private EntityKeyLayout? _layout;

    /// <summary>
    /// Keys whose indexes exist already, so no read costs an object: the one-part index, and a tuple index when the plan
    /// reads a table keyed by <paramref name="tupleParts"/> (2 or more) varying parts.
    /// </summary>
    public static FetchKeys Prepared(RequestMemory memory, int tupleParts) => new()
    {
        _single = new EntityKeyIndex(EntityKeyType.Int64, 0, memory),
        _tuples = tupleParts >= 2 ? new EntityKeyTupleIndex(tupleParts, 0, memory) : null,
    };

    /// <summary>Whether the keys were reset for a group (a fetch with no key never is).</summary>
    public readonly bool IsSet => _layout is not null;

    public readonly EntityKeyLayout Layout => _layout ?? throw new InvalidOperationException("The keys were not reset for a group.");

    /// <summary>Number of distinct keys.</summary>
    public readonly int Count => Layout.VaryingCount == 1 ? _single!.Count : _tuples!.Count;

    /// <summary>
    /// The one-part index when the key is exactly one part, which varies: a table looks its keys up as the index holds them
    /// (<see cref="EntityKeyIndex.Int64Keys"/>, <see cref="EntityKeyIndex.StringKeys"/>), without a batch. Null otherwise.
    /// </summary>
    public readonly EntityKeyIndex? Plain => Layout.PartCount == 1 ? _single : null;

    /// <summary>
    /// Starts a group's keys: <paramref name="shared"/> are the values of the layout's shared parts in part order, and at
    /// most <paramref name="capacity"/> distinct keys follow (the live rows, an upper bound: the index never grows). For one
    /// varying part, <paramref name="first"/> is the first key's: every key of a column has its type.
    /// </summary>
    public void Reset(RequestMemory memory, EntityKeyLayout layout, ReadOnlySpan<EntityKey> shared, int capacity, in EntityKey first)
    {
        if (layout.VaryingCount == 0)
        {
            throw new ArgumentException("A context-scoped key has one value per request; it is not deduplicated.", nameof(layout));
        }

        if (shared.Length != layout.PartCount - layout.VaryingCount)
        {
            throw new ArgumentException($"{shared.Length} shared values for a key with {layout.PartCount - layout.VaryingCount} shared parts.", nameof(shared));
        }

        _layout = layout;
        if (!shared.IsEmpty)
        {
            if (_shared is null || _shared.Length < shared.Length)
            {
                _shared = memory.Rent<EntityKey>(EntityKeyLayout.MaxParts);
            }

            shared.CopyTo(_shared);
        }

        capacity = Math.Max(1, capacity);
        if (layout.VaryingCount == 1)
        {
            var type = first.IsString ? EntityKeyType.String : EntityKeyType.Int64;
            if (_single is null)
            {
                _single = new EntityKeyIndex(type, capacity, memory);
            }
            else
            {
                _single.Reset(type, capacity);
            }
        }
        else if (_tuples is null)
        {
            _tuples = new EntityKeyTupleIndex(layout.VaryingCount, capacity, memory);
        }
        else
        {
            _tuples.Reset(layout.VaryingCount, capacity);
        }
    }

    /// <summary>The entry of a key given by its varying parts (in part order), adding it first when it is new.</summary>
    public readonly int GetOrAdd(ReadOnlySpan<EntityKey> varying) =>
        varying.Length == 1 ? GetOrAdd(varying[0]) : _tuples!.GetOrAdd(varying, _tuples.Count, out _);

    /// <summary>The entry of a key whose one varying part is <paramref name="varying"/>, adding it first when it is new.</summary>
    public readonly int GetOrAdd(in EntityKey varying) => _single!.GetOrAdd(varying, _single.Count, out _);

    /// <summary>The entry of a key given by its varying parts, if it is one of the keys.</summary>
    public readonly bool TryGetValue(ReadOnlySpan<EntityKey> varying, out int entry) =>
        varying.Length == 1 ? TryGetValue(varying[0], out entry) : _tuples!.TryGetValue(varying, out entry);

    /// <summary>The entry of a key whose one varying part is <paramref name="varying"/>, if it is one of the keys.</summary>
    public readonly bool TryGetValue(in EntityKey varying, out int entry)
    {
        if (varying.IsString != (_single!.KeyType == EntityKeyType.String))
        {
            entry = 0;
            return false; // a key of the other type, which is not among them
        }

        return _single.TryGetValue(varying, out entry);
    }

    /// <summary>The keys as a batch, in a buffer rented from <paramref name="memory"/>: valid while the request holds it.</summary>
    public readonly EntityKeyBatch Batch(RequestMemory memory) => Fill(memory.Rent<EntityKey>(BufferLength()));

    /// <summary>The keys as a batch in an array of its own, for a recording, which outlives the request's memory.</summary>
    public readonly EntityKeyBatch Copy() => Fill(new EntityKey[BufferLength()]);

    private readonly int BufferLength() => (Layout.VaryingCount * Count) + (Layout.PartCount - Layout.VaryingCount);

    /// <summary>Lays the keys out part-major (see <see cref="EntityKeyBatch"/>): each varying part's keys, then the shared values.</summary>
    private readonly EntityKeyBatch Fill(EntityKey[] buffer)
    {
        var layout = Layout;
        var count = Count;
        var varying = layout.VaryingCount;
        for (var v = 0; v < varying; v++)
        {
            var run = buffer.AsSpan(v * count, count);
            if (varying == 1)
            {
                for (var k = 0; k < count; k++)
                {
                    run[k] = _single!.KeyAt(k);
                }
            }
            else
            {
                _tuples!.PartKeys(v).CopyTo(run);
            }
        }

        _shared.AsSpan(0, layout.PartCount - varying).CopyTo(buffer.AsSpan(varying * count));
        return new EntityKeyBatch(buffer, count, 0, count, layout.PartCount, layout.SharedMask);
    }
}
