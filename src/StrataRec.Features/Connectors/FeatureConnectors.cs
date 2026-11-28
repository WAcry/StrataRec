using System.Numerics;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;

namespace StrataRec.Features.Connectors;

/// <summary>
/// The keys of one fetch: <see cref="Count"/> keys, each a tuple of <see cref="PartCount"/> parts in the
/// entity's declared order, deduplicated as tuples. Key i is staging row i.
/// </summary>
/// <remarks>
/// <para>Read a key's parts by part: <see cref="Part"/> gives part p of every key, one entry per key, or a single entry
/// for a shared part (<see cref="IsShared"/>), whose value every key of the batch has: a context part of a
/// candidate-scoped entity (the country of a request's <c>(item, country)</c> keys). <see cref="Keys"/> is the one-part
/// key's keys; <see cref="Key"/> one part of one key.</para>
/// <para>The keys live in the request's pooled buffers: they stay valid until the connector call completes,
/// even past the request's own timeout, and not after. A connector that needs keys later must copy them.</para>
/// </remarks>
public readonly struct EntityKeyBatch
{
    private readonly ReadOnlyMemory<EntityKey> _buffer;
    private readonly int _stride;
    private readonly int _start;
    private readonly int _count;
    private readonly byte _parts;
    private readonly byte _shared;

    /// <summary>A batch of one-part keys: key i is <c>keys[i]</c>.</summary>
    public EntityKeyBatch(ReadOnlyMemory<EntityKey> keys)
        : this(keys, keys.Length, 0, keys.Length, 1, 0)
    {
    }

    /// <summary>
    /// A window of a part-major key buffer: varying part v of key i at <c>v * stride + start + i</c>, then shared part s at
    /// <c>varyingCount * stride + s</c> (varying and shared parts each counted in part order).
    /// </summary>
    internal EntityKeyBatch(ReadOnlyMemory<EntityKey> buffer, int stride, int start, int count, int parts, byte sharedMask)
    {
        _buffer = buffer;
        _stride = stride;
        _start = start;
        _count = count;
        _parts = (byte)parts;
        _shared = sharedMask;
    }

    /// <summary>
    /// A batch of <paramref name="count"/> keys of <paramref name="layout"/>'s parts, from one memory per part
    /// (<paramref name="parts"/>[p]: <paramref name="count"/> keys, or one for a shared part), copied into a buffer of
    /// its own.
    /// </summary>
    public static EntityKeyBatch Create(EntityKeyLayout layout, int count, params ReadOnlyMemory<EntityKey>[] parts)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (parts.Length != layout.PartCount)
        {
            throw new ArgumentException($"{parts.Length} parts for a key of {layout.PartCount}.", nameof(parts));
        }

        var buffer = new EntityKey[(layout.VaryingCount * count) + (layout.PartCount - layout.VaryingCount)];
        int varying = 0, shared = 0;
        for (var p = 0; p < parts.Length; p++)
        {
            var expected = layout[p].IsShared ? 1 : count;
            if (parts[p].Length != expected)
            {
                throw new ArgumentException($"Part {p} ({layout[p].Reference}) has {parts[p].Length} keys; it needs {expected}.", nameof(parts));
            }

            parts[p].Span.CopyTo(buffer.AsSpan(layout[p].IsShared ? (layout.VaryingCount * count) + shared++ : count * varying++));
        }

        return new EntityKeyBatch(buffer, count, 0, count, layout.PartCount, layout.SharedMask);
    }

    /// <summary>Number of keys, i.e. staging rows.</summary>
    public int Count => _count;

    /// <summary>Parts of every key (1 to <see cref="EntityKeyLayout.MaxParts"/>), as the entity's key layout declares them.</summary>
    public int PartCount => _parts;

    /// <summary>Whether every key of the batch has the same value of part <paramref name="part"/> (a context part).</summary>
    public bool IsShared(int part)
    {
        CheckPart(part);
        return (_shared & (1 << part)) != 0;
    }

    /// <summary>
    /// Part <paramref name="part"/> of every key: <see cref="Count"/> entries, entry i of key i; or a single entry, the
    /// value of every key, when the part is shared (<see cref="IsShared"/>).
    /// </summary>
    public ReadOnlyMemory<EntityKey> Part(int part)
    {
        CheckPart(part);
        var below = (1 << part) - 1;
        if ((_shared & (1 << part)) != 0)
        {
            var varying = _parts - BitOperations.PopCount(_shared);
            return _buffer.Slice((varying * _stride) + BitOperations.PopCount((uint)(_shared & below)), 1);
        }

        return _buffer.Slice((BitOperations.PopCount((uint)(~_shared & below)) * _stride) + _start, _count);
    }

    /// <summary>The keys of a one-part key (<see cref="PartCount"/> 1), one per key even when the part is shared.</summary>
    /// <exception cref="InvalidOperationException">The keys have several parts: read them with <see cref="Part"/>.</exception>
    public ReadOnlyMemory<EntityKey> Keys
    {
        get
        {
            if (_parts != 1)
            {
                throw new InvalidOperationException($"The keys have {_parts} parts; read each with Part(p).");
            }

            return _shared == 0 || _count == 1 ? Part(0) : throw new InvalidOperationException("A shared one-part key has one value: read Part(0).");
        }
    }

    public EntityKey Key(int index, int part)
    {
        if ((uint)index >= (uint)_count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"The batch has {_count} keys.");
        }

        var keys = Part(part).Span;
        return keys.Length == 1 && IsShared(part) ? keys[0] : keys[index];
    }

    internal ReadOnlySpan<EntityKey> Buffer => _buffer.Span;

    /// <summary>
    /// Where part <paramref name="part"/> of key 0 is in <see cref="Buffer"/>, and how far apart the part's keys are:
    /// key i's at <c>Offset + Step * i</c>, the step 0 for a shared part.
    /// </summary>
    internal (int Offset, int Step) PartAt(int part)
    {
        CheckPart(part);
        var below = (1 << part) - 1;
        if ((_shared & (1 << part)) != 0)
        {
            var varying = _parts - BitOperations.PopCount(_shared);
            return ((varying * _stride) + BitOperations.PopCount((uint)(_shared & below)), 0);
        }

        return ((BitOperations.PopCount((uint)(~_shared & below)) * _stride) + _start, 1);
    }

    internal EntityKeyBatch Slice(int start, int count)
    {
        if ((uint)start > (uint)_count || (uint)count > (uint)(_count - start))
        {
            throw new ArgumentOutOfRangeException(nameof(start), start, $"Keys {start}..{start + count} of a batch of {_count}.");
        }

        return new EntityKeyBatch(_buffer, _stride, _start + start, count, _parts, _shared);
    }

    /// <summary>The batch in an array of its own, for a recording that outlives the request's buffers.</summary>
    internal EntityKeyBatch Copy()
    {
        var varying = _parts - BitOperations.PopCount(_shared);
        var buffer = new EntityKey[(varying * _count) + (_parts - varying)];
        CopyTo(buffer, varying);
        return new EntityKeyBatch(buffer, _count, 0, _count, _parts, _shared);
    }

    /// <summary>
    /// The keys <paramref name="keys"/> of this batch, in that order, laid out in <paramref name="buffer"/> (at least
    /// <c>varying parts × keys + shared parts</c> long).
    /// </summary>
    internal EntityKeyBatch Select(ReadOnlySpan<int> keys, EntityKey[] buffer)
    {
        var varying = _parts - BitOperations.PopCount(_shared);
        var source = _buffer.Span;
        for (var v = 0; v < varying; v++)
        {
            var from = source.Slice((v * _stride) + _start, _count);
            var to = buffer.AsSpan(v * keys.Length, keys.Length);
            for (var i = 0; i < keys.Length; i++)
            {
                to[i] = from[keys[i]];
            }
        }

        source.Slice(varying * _stride, _parts - varying).CopyTo(buffer.AsSpan(varying * keys.Length));
        return new EntityKeyBatch(buffer, keys.Length, 0, keys.Length, _parts, _shared);
    }

    private void CopyTo(EntityKey[] buffer, int varying)
    {
        var source = _buffer.Span;
        for (var v = 0; v < varying; v++)
        {
            source.Slice((v * _stride) + _start, _count).CopyTo(buffer.AsSpan(v * _count));
        }

        source.Slice(varying * _stride, _parts - varying).CopyTo(buffer.AsSpan(varying * _count));
    }

    private void CheckPart(int part)
    {
        if ((uint)part >= (uint)_parts)
        {
            throw new ArgumentOutOfRangeException(nameof(part), part, $"The keys have {_parts} parts.");
        }
    }
}

/// <summary>A physical field requested from a source and where its values go in the staging area.</summary>
/// <param name="Field">Physical field name in the store.</param>
/// <param name="FieldIndex">Index used with <see cref="IFeatureBatchWriter"/>.</param>
/// <param name="Type">Storage type the connector must write.</param>
public readonly record struct CompiledFieldBinding(string Field, int FieldIndex, ColumnType Type);

/// <summary>
/// Dense staging area the connector decodes into, ordered by the batch's keys and independent of table rows
///.
/// </summary>
/// <remarks>
/// <para>Validity is tracked per field and key, and every (field, key) starts as missing: only values the connector
/// actually wrote are ever read. A key absent from the response needs no call. Values written through
/// <see cref="GetValues{T}"/> count once <see cref="SetValid"/> is called for that field and key; call it after the
/// value is fully decoded. <see cref="SetString"/> and the list setters (<see cref="SetInt64List"/> and its siblings)
/// mark their value present themselves. A connector reporting <see cref="FetchOutcome.Partial"/> therefore needs no
/// extra bookkeeping.</para>
/// <para>A writer belongs to one connector call while that call runs (each <c>max_batch</c> chunk gets its own), so
/// implementations may write from any one thread at a time without synchronization. The chunks of one fetch share
/// buffers: the memory <see cref="GetValues{T}"/> returns is this call's window of a fetch-wide array, so write only
/// inside it. Once a call has completed, the engine may hand the same writer object to another chunk: a connector must
/// not keep it, nor write through it after returning (see <see cref="IFeatureSourceConnector.FetchAsync"/>).</para>
/// </remarks>
public interface IFeatureBatchWriter
{
    int KeyCount { get; }

    /// <summary>
    /// Native value buffer of a fixed-size scalar field, indexed by key: <c>bool</c>, <c>int</c> (int32), <c>long</c>,
    /// <c>float</c> (float32) or <c>double</c>. Writes become visible only through <see cref="SetValid"/>. Lists, of any
    /// length or of a fixed one (an embedding, <c>list&lt;float32,64&gt;</c>), are written with the list setters.
    /// </summary>
    Memory<T> GetValues<T>(int fieldIndex)
        where T : unmanaged;

    /// <summary>Marks the value of one field of one key, written through <see cref="GetValues{T}"/>, as present.</summary>
    void SetValid(int fieldIndex, int keyIndex);

    /// <summary>Marks one field of one key as absent (withdrawing a value set earlier, if any).</summary>
    void SetMissing(int fieldIndex, int keyIndex);

    /// <summary>Writes a <c>string</c> field value; never null. The request keeps the instance, it does not copy it.</summary>
    void SetString(int fieldIndex, int keyIndex, string value);

    /// <summary>
    /// A string with the characters of <paramref name="value"/>, for <see cref="SetString"/> or a
    /// <see cref="SetStringList"/> element: within this call, equal values return the same instance.
    /// </summary>
    /// <remarks>
    /// <para>The scope is the call: the table goes away with the writer, and nothing is kept process-wide. At most
    /// 4,096 distinct values are kept; past that each call returns a new string. A connector that wants reuse across
    /// requests holds its own pool, sized for its data, and passes its strings to <see cref="SetString"/>.</para>
    /// </remarks>
    string Intern(ReadOnlySpan<char> value);

    /// <summary>
    /// <see cref="Intern"/> for a value as UTF-8 bytes. Invalid UTF-8 becomes U+FFFD, as
    /// <see cref="System.Text.Encoding.UTF8"/> decodes it.
    /// </summary>
    string InternUtf8(ReadOnlySpan<byte> utf8);

    /// <remarks>A setter for another element type than the field's is a connector bug and throws. A fixed-length field
    /// (<c>list&lt;float32,64&gt;</c>, an embedding) takes the same setter; a value of another length is data, not a
    /// bug: the engine replaces it with the feature's default (or missing) where it stores it, and counts it
    /// (<c>column_out_of_range_total</c>), while recordings and the cache keep what the connector wrote.</remarks>
    void SetBoolList(int fieldIndex, int keyIndex, ReadOnlySpan<bool> value);

    void SetInt32List(int fieldIndex, int keyIndex, ReadOnlySpan<int> value);

    void SetInt64List(int fieldIndex, int keyIndex, ReadOnlySpan<long> value);

    void SetFloat32List(int fieldIndex, int keyIndex, ReadOnlySpan<float> value);

    void SetFloat64List(int fieldIndex, int keyIndex, ReadOnlySpan<double> value);

    /// <summary>Writes a <c>list&lt;string&gt;</c> field value; no element may be null.</summary>
    /// <inheritdoc cref="SetBoolList" path="/remarks"/>
    void SetStringList(int fieldIndex, int keyIndex, ReadOnlySpan<string> value);
}

public enum FetchOutcome : byte
{
    Ok = 1,

    /// <summary>Some keys failed; readers become Degraded.</summary>
    Partial,

    /// <summary>The source failed; nodes reading its features fail.</summary>
    Failed,
}

public readonly record struct FetchStatus(FetchOutcome Outcome, int FailedKeys = 0, string? Error = null)
{
    public static FetchStatus Ok => new(FetchOutcome.Ok);

    public static FetchStatus Failed(string error) => new(FetchOutcome.Failed, 0, error);
}

/// <summary>Implementations must be thread-safe.</summary>
public interface IFeatureSourceConnector
{
    /// <summary>Connector type and version, e.g. <c>redis_hash@1</c>.</summary>
    string ConnectorType { get; }

    /// <summary>
    /// Whether the connector can fetch an entity keyed by <paramref name="layout"/>: its parts' references, types and
    /// shared flags, in declared order. Asked once per (source, entity) when a plan is compiled; a connector that cannot is a
    /// configuration error naming the layout (<c>CONNECTOR_KEY</c>), never a failure on the request path.
    /// </summary>
    bool Supports(EntityKeyLayout layout);

    /// <summary>
    /// Must not write after it returns or after cancellation is acknowledged.
    /// </summary>
    /// <remarks>
    /// <paramref name="fields"/> holds only the fields the request needs at this point, so the same source may be
    /// called again later for other fields of the same keys. The engine stops waiting at the source timeout or the
    /// request deadline, whichever comes first, and discards whatever the call writes afterwards; the writer's buffers
    /// stay rented until the call actually completes. The same holds for the keys of <paramref name="keys"/>: neither
    /// may be kept past completion.
    /// </remarks>
    ValueTask<FetchStatus> FetchAsync(
        EntityKeyBatch keys,
        ReadOnlyMemory<CompiledFieldBinding> fields,
        IFeatureBatchWriter writer,
        CancellationToken cancellationToken);
}
