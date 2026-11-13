using System.Globalization;

namespace StrataRec.Core.Keys;

/// <summary>Type of an entity key (design 6.3: <c>int64</c> or <c>string</c>).</summary>
public enum EntityKeyType : byte
{
    Int64 = 1,
    String = 2,
}

/// <summary>An entity key: either an int64 or a string (design 6.5).</summary>
public readonly record struct EntityKey(long Int64Value, string? StringValue)
{
    public static EntityKey FromInt64(long value) => new(value, null);

    public static EntityKey FromString(string value) => new(0, value ?? throw new ArgumentNullException(nameof(value)));

    public bool IsString => StringValue is not null;

    public override string ToString() => StringValue ?? Int64Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Key shape of a feature fetch (design 6.5).</summary>
public enum KeyShape : byte
{
    /// <summary>One context key, e.g. a user id.</summary>
    Single = 1,

    /// <summary>A deduplicated batch of candidate or foreign keys.</summary>
    Many,

    /// <summary>One context key × a batch of candidate keys.</summary>
    Cross,

    /// <summary>One context key; the response carries its own candidate keys.</summary>
    ContextMap,
}
