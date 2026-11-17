using System.Globalization;
using System.Numerics;
using System.Text;

namespace StrataRec.Core.Keys;

/// <summary>Type of an entity key part (design 6.3: <c>int64</c> or <c>string</c>).</summary>
public enum EntityKeyType : byte
{
    Int64 = 1,
    String = 2,
}

/// <summary>One part of an entity key: an int64 or a string (design 6.5). A key of one part is one of these.</summary>
public readonly record struct EntityKey(long Int64Value, string? StringValue)
{
    public static EntityKey FromInt64(long value) => new(value, null);

    public static EntityKey FromString(string value) => new(0, value ?? throw new ArgumentNullException(nameof(value)));

    public bool IsString => StringValue is not null;

    public override string ToString() => StringValue ?? Int64Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// One part of an entity's key (design 6.2): where a request reads it, its type, and whether every key of a batch shares it.
/// </summary>
/// <param name="Reference">What the catalog keys the entity by: <c>ctx.request.country</c>, <c>col.entity_id</c>,
/// <c>col.item.author_id</c>.</param>
/// <param name="Type">The part's type (an <c>int32</c> reference is an <c>int64</c> part).</param>
/// <param name="IsShared">True for a context part (<c>ctx.*</c>): one value per request, so every key of a batch has the
/// same value. A store keyed by it can read one partition for the whole batch.</param>
public readonly record struct EntityKeyPart(string Reference, EntityKeyType Type, bool IsShared);

/// <summary>
/// The layout of an entity's key (design 6.2, 6.5): an ordered tuple of 1 to <see cref="MaxParts"/> parts, in the order
/// the catalog declares them. What a connector is asked to support (<c>IFeatureSourceConnector.Supports</c>), what a local
/// table and a cache record are keyed by, and what a fetch's keys are deduplicated as.
/// </summary>
/// <remarks>
/// <para>An entity is context-scoped (one key per request) when every part is shared, candidate-scoped (one key per row)
/// otherwise. A batch of keys stores each varying part once per key and each shared part once
/// (<c>EntityKeyBatch</c>).</para>
/// <para>What identifies the stored keys is the part types in order (<see cref="TypeText"/>): a table or a cache record is
/// keyed by values, and the references only say where a request reads them. Equality compares the whole layout.</para>
/// </remarks>
public sealed class EntityKeyLayout : IEquatable<EntityKeyLayout>
{
    /// <summary>The most parts a key has: enough for real composite keys (segment × age bucket × country …), small enough
    /// that a tuple fits on the stack and a batch keeps its shared flags in one byte.</summary>
    public const int MaxParts = 8;

    private readonly EntityKeyPart[] _parts;

    /// <exception cref="ArgumentException">Fewer than one or more than <see cref="MaxParts"/> parts, an empty or repeated
    /// reference, or a type that is not int64 or string.</exception>
    public EntityKeyLayout(IReadOnlyList<EntityKeyPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count is < 1 or > MaxParts)
        {
            throw new ArgumentException($"A key has 1 to {MaxParts} parts, got {parts.Count}.", nameof(parts));
        }

        _parts = new EntityKeyPart[parts.Count];
        for (var p = 0; p < _parts.Length; p++)
        {
            var part = parts[p];
            ArgumentException.ThrowIfNullOrEmpty(part.Reference, nameof(parts));
            if (part.Type is not (EntityKeyType.Int64 or EntityKeyType.String))
            {
                throw new ArgumentException($"Key part '{part.Reference}' has type {part.Type}; a key part is an int64 or a string.", nameof(parts));
            }

            for (var q = 0; q < p; q++)
            {
                if (string.Equals(_parts[q].Reference, part.Reference, StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Key part '{part.Reference}' is listed twice.", nameof(parts));
                }
            }

            _parts[p] = part;
            if (part.IsShared)
            {
                SharedMask |= (byte)(1 << p);
            }
        }

        VaryingCount = _parts.Length - BitOperations.PopCount(SharedMask);
        TypeText = DescribeTypes(_parts);
    }

    /// <summary>A key of one varying part: a table built outside a catalog (a test, a tool), whose reference is only a label.</summary>
    public static EntityKeyLayout Single(EntityKeyType type, string reference = "key") => new([new EntityKeyPart(reference, type, IsShared: false)]);

    /// <summary>A key of varying parts of <paramref name="types"/>, labelled <c>key/0</c>, <c>key/1</c>, … (tables built outside a catalog).</summary>
    public static EntityKeyLayout OfTypes(params EntityKeyType[] types)
    {
        ArgumentNullException.ThrowIfNull(types);
        var parts = new EntityKeyPart[types.Length];
        for (var p = 0; p < parts.Length; p++)
        {
            parts[p] = new EntityKeyPart(types.Length == 1 ? "key" : "key/" + p.ToString(CultureInfo.InvariantCulture), types[p], IsShared: false);
        }

        return new EntityKeyLayout(parts);
    }

    public IReadOnlyList<EntityKeyPart> Parts => _parts;

    public int PartCount => _parts.Length;

    public EntityKeyPart this[int part] => _parts[part];

    /// <summary>Bit p set when part p is shared.</summary>
    public byte SharedMask { get; }

    /// <summary>Parts that vary per key (candidate parts); 0 for a context-scoped entity.</summary>
    public int VaryingCount { get; }

    /// <summary>Every part is shared: one key per request.</summary>
    public bool IsContextScoped => VaryingCount == 0;

    /// <summary>
    /// The part types in order: <c>int64</c> or <c>string</c> for one part, <c>(int64,string)</c> for several. What keys of
    /// the layout are stored as: a local table's identity and a cache record's fingerprint take it.
    /// </summary>
    public string TypeText { get; }

    /// <summary>Whether keys of this layout and of <paramref name="other"/> are stored alike (the same part types in order).</summary>
    public bool HasSameTypes(EntityKeyLayout other) => string.Equals(TypeText, other?.TypeText, StringComparison.Ordinal);

    /// <summary>The name of a part type as configuration spells it.</summary>
    public static string TypeName(EntityKeyType type) => type == EntityKeyType.String ? "string" : "int64";

    public bool Equals(EntityKeyLayout? other)
    {
        if (other is null || other._parts.Length != _parts.Length)
        {
            return false;
        }

        for (var p = 0; p < _parts.Length; p++)
        {
            if (_parts[p] != other._parts[p])
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EntityKeyLayout other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var part in _parts)
        {
            hash.Add(part);
        }

        return hash.ToHashCode();
    }

    /// <summary>E.g. <c>[col.entity_id int64, ctx.request.country string (shared)]</c>: what a configuration error names.</summary>
    public override string ToString()
    {
        var text = new StringBuilder("[");
        for (var p = 0; p < _parts.Length; p++)
        {
            text.Append(p == 0 ? string.Empty : ", ").Append(_parts[p].Reference).Append(' ').Append(TypeName(_parts[p].Type));
            if (_parts[p].IsShared)
            {
                text.Append(" (shared)");
            }
        }

        return text.Append(']').ToString();
    }

    private static string DescribeTypes(EntityKeyPart[] parts)
    {
        if (parts.Length == 1)
        {
            return TypeName(parts[0].Type);
        }

        var text = new StringBuilder("(");
        for (var p = 0; p < parts.Length; p++)
        {
            text.Append(p == 0 ? string.Empty : ",").Append(TypeName(parts[p].Type));
        }

        return text.Append(')').ToString();
    }
}
