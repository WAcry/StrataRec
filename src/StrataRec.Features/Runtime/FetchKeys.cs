using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Features.Connectors;

namespace StrataRec.Features.Runtime;

/// <remarks>
/// <para>A copy shares the indexes: take one only once the keys are added, to read them.</para>
/// </remarks>
internal struct FetchKeys
{
    private EntityKeyIndex? _single;
    private EntityKeyTupleIndex? _tuples;
    private EntityKey[]? _shared; // rented: the shared parts' values in part order, for the batch
    private EntityKeyLayout? _layout;

    public static FetchKeys Prepared(RequestMemory memory, int tupleParts) => new()
    {
        _single = new EntityKeyIndex(EntityKeyType.Int64, 0, memory),
        _tuples = tupleParts >= 2 ? new EntityKeyTupleIndex(tupleParts, 0, memory) : null,
    };

    /// <summary>Whether the keys were reset for a group (a fetch with no key never is).</summary>
    public readonly bool IsSet => _layout is not null;

    public readonly EntityKeyLayout Layout => _layout ?? throw new InvalidOperationException("The keys were not reset for a group.");

    public readonly int Count => Layout.VaryingCount == 1 ? _single!.Count : _tuples!.Count;

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

    public readonly int GetOrAdd(in EntityKey varying) => _single!.GetOrAdd(varying, _single.Count, out _);

    public readonly bool TryGetValue(ReadOnlySpan<EntityKey> varying, out int entry) =>
        varying.Length == 1 ? TryGetValue(varying[0], out entry) : _tuples!.TryGetValue(varying, out entry);

    public readonly bool TryGetValue(in EntityKey varying, out int entry)
    {
        if (varying.IsString != (_single!.KeyType == EntityKeyType.String))
        {
            entry = 0;
            return false;
        }

        return _single.TryGetValue(varying, out entry);
    }

    /// <summary>The keys as a batch, in a buffer rented from <paramref name="memory"/>: valid while the request holds it.</summary>
    public readonly EntityKeyBatch Batch(RequestMemory memory) => Fill(memory.Rent<EntityKey>(BufferLength()));

    /// <summary>The keys as a batch in an array of its own, for a recording, which outlives the request's memory.</summary>
    public readonly EntityKeyBatch Copy() => Fill(new EntityKey[BufferLength()]);

    private readonly int BufferLength() => (Layout.VaryingCount * Count) + (Layout.PartCount - Layout.VaryingCount);

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
