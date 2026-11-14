using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace StrataRec.Artifacts.Json;

/// <summary>Where and why a JSON document does not bind to the type that reads it, in the document's words.</summary>
/// <param name="Pointer">RFC 6901 pointer of the value at fault, relative to the document that was bound.</param>
/// <param name="Message">What is wrong, e.g. <c>'order' must be an integer, got the string "350".</c></param>
/// <param name="Hint">How to fix it when the type says (a close key, the allowed keys, what a required key means); null otherwise.</param>
public sealed record JsonBindingError(string Pointer, string Message, string? Hint);

/// <summary>
/// Turns a <see cref="JsonException"/> of System.Text.Json into a <see cref="JsonBindingError"/>: an unknown key with a
/// "did you mean", a value of the wrong kind (<c>must be an integer, got the string "350"</c>), a missing required key with
/// what it means, an integer out of range. One translator for every artifact and operator config, so they all report a
/// mistake alike.
/// </summary>
/// <remarks>
/// <para>The serializer's own messages name .NET types (<c>System.Nullable`1[System.Int32]</c>) and positions in a
/// re-serialized copy of the document. Instead of parsing them, the translator walks the error's path through the bound
/// type's <see cref="JsonTypeInfo"/> (from the serializer options) and through the document, and describes what it finds
/// at the end: the expected kind comes from the type, the actual value from the document. Only a converter of our own
/// (one outside System.Text.Json) is trusted to word its message for the reader.</para>
/// <para>Error path only: nothing here runs while a valid document binds.</para>
/// </remarks>
public static class JsonBindingErrors
{
    private static readonly Assembly SerializerAssembly = typeof(JsonSerializer).Assembly;

    /// <summary>Describes why <paramref name="document"/> did not bind to <paramref name="type"/>.</summary>
    /// <param name="exception">What the serializer threw; its <see cref="JsonException.Path"/> locates the problem.</param>
    /// <param name="document">The document that was bound.</param>
    /// <param name="type">The type it was bound to.</param>
    /// <param name="options">The options it was bound with.</param>
    /// <param name="subject">The document as a sentence names it, e.g. <c>the pipeline</c> or <c>the config of TopK@1</c>.</param>
    public static JsonBindingError Describe(JsonException exception, JsonNode? document, Type type, JsonSerializerOptions options, string subject)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(subject);

        var at = new Location(TypeInfo(options, type), document, null, Capitalize(subject), subject);
        var pointer = new StringBuilder();
        var segments = PathSegments(exception.Path, document);
        var ownKey = false; // the last segment is a key read by a converter of our own, which may be what failed
        foreach (var segment in segments)
        {
            var info = at.Info is null ? null : Unwrap(options, at.Info);
            ownKey = false;
            var child = Child(at.Node, segment);
            pointer.Append('/').Append(JsonPointer.Escape(segment.Token));
            switch (info?.Kind)
            {
                case JsonTypeInfoKind.Object when !segment.IsIndex:
                    var property = FindProperty(info, segment.Token);
                    if (property is null)
                    {
                        if (TakesUnknownKeys(info, options))
                        {
                            at = new Location(null, child, null, $"'{segment.Token}'", $"'{segment.Token}'");
                            continue;
                        }

                        return UnknownKey(pointer.ToString(), segment.Token, info, at.Where);
                    }

                    at = new Location(TypeInfo(options, property.PropertyType), child, property, $"'{segment.Token}'", $"'{segment.Token}'");
                    break;
                case JsonTypeInfoKind.Dictionary:
                    ownKey = info.KeyType is { } keyType && keyType != typeof(string) && IsOwn(options.GetTypeInfo(keyType).Converter);
                    at = new Location(TypeInfo(options, info.ElementType!), child, null,
                        $"Entry '{segment.Token}' of {at.Where}", $"entry '{segment.Token}' of {at.Where}");
                    break;
                case JsonTypeInfoKind.Enumerable:
                    at = new Location(TypeInfo(options, info.ElementType!), child, null,
                        $"Element {segment.Token} of {at.Where}", $"element {segment.Token} of {at.Where}");
                    break;
                default:
                    // Into a value the type does not describe (a JsonObject member, an extension member): only the
                    // document is left to walk.
                    at = new Location(null, child, null, $"'{segment.Token}'", $"'{segment.Token}'");
                    break;
            }
        }

        return Explain(at, pointer.ToString(), exception, options, ownKey);
    }

    /// <summary>A document that is JSON <c>null</c> where <paramref name="type"/> needs a value.</summary>
    public static JsonBindingError Null(Type type, JsonSerializerOptions options, string subject)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(subject);
        return new JsonBindingError(string.Empty, $"{Capitalize(subject)} must be {Phrase(Expect(Unwrap(options, TypeInfo(options, type))))}, got null.", null);
    }

    /// <summary>The problem at the end of the path: a value of the wrong kind, a required key missing, a number out of range.</summary>
    private static JsonBindingError Explain(Location at, string pointer, JsonException exception, JsonSerializerOptions options, bool ownKey)
    {
        if (at.Info is null)
        {
            return new JsonBindingError(pointer, $"{at.Name} has an invalid value: {JsonWords.Value(at.Node)}.", null);
        }

        var nullable = Nullable.GetUnderlyingType(at.Info.Type) is not null || (!at.Info.Type.IsValueType && IsNullableReference(at.Property));
        var info = Unwrap(options, at.Info);
        var expected = Expect(info);
        if (expected == Kind.Custom)
        {
            // A converter of our own words its message for the reader; the serializer's generic one does not.
            return IsOwn(info.Converter) && !string.IsNullOrEmpty(exception.Message)
                ? new JsonBindingError(pointer, $"{at.Name}: {exception.Message}", null)
                : new JsonBindingError(pointer, $"{at.Name} has an invalid value: {JsonWords.Value(at.Node)}.", null);
        }

        if (!Matches(expected, at.Node))
        {
            var or = nullable && at.Node is not null ? " or null" : string.Empty;
            return new JsonBindingError(pointer, $"{at.Name} must be {Phrase(expected)}{or}, got {JsonWords.Value(at.Node)}.", null);
        }

        if (expected == Kind.Integer && at.Node is JsonValue number && IntegerRange(info.Type) is var (min, max))
        {
            var text = number.ToJsonString();
            var integral = decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && decimal.Truncate(d) == d;
            if (!integral)
            {
                return new JsonBindingError(pointer, $"{at.Name} must be an integer, got {JsonWords.Value(at.Node)}.", null);
            }

            if (d < min || d > max)
            {
                return new JsonBindingError(pointer,
                    $"{at.Name} must be an integer from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}, got {JsonWords.Value(at.Node)}.",
                    null);
            }
        }

        if (info.Kind == JsonTypeInfoKind.Object && at.Node is JsonObject obj)
        {
            var missing = new List<JsonPropertyInfo>();
            foreach (var property in info.Properties)
            {
                if (property.IsRequired && !obj.ContainsKey(property.Name))
                {
                    missing.Add(property);
                }
            }

            if (missing.Count > 0)
            {
                var names = new List<string>(missing.Count);
                foreach (var property in missing)
                {
                    names.Add(property.Name);
                }

                var verb = missing.Count == 1 ? "is" : "are";
                return new JsonBindingError(pointer, $"{JsonWords.List(names)} {verb} required in {at.Where}.", Meaning(missing[0], options));
            }
        }

        return ownKey && !string.IsNullOrEmpty(exception.Message)
            ? new JsonBindingError(pointer, $"{at.Name}: {exception.Message}", null)
            : new JsonBindingError(pointer, $"{at.Name} has an invalid value: {JsonWords.Value(at.Node)}.", null);
    }

    /// <summary>A converter outside System.Text.Json, ours, whose messages are written for the reader.</summary>
    private static bool IsOwn(JsonConverter converter) => converter.GetType().Assembly != SerializerAssembly;

    private static JsonBindingError UnknownKey(string pointer, string key, JsonTypeInfo info, string where)
    {
        var keys = new List<string>(info.Properties.Count);
        foreach (var property in info.Properties)
        {
            if (!property.IsExtensionData)
            {
                keys.Add(property.Name);
            }
        }

        var hint = NameSuggestions.FindClosest(key, keys) is { } close
            ? $"did you mean '{close}'?"
            : keys.Count == 0 ? "it takes no keys" : "allowed keys: " + string.Join(", ", keys);
        return new JsonBindingError(pointer, $"Unknown key '{key}' in {where}.", hint);
    }

    /// <summary>What a required member means, from its description or documentation, as a hint.</summary>
    private static string? Meaning(JsonPropertyInfo property, JsonSerializerOptions options) =>
        property.AttributeProvider is MemberInfo member && MemberDocs.Describe(member, options.PropertyNamingPolicy) is { } text
            ? $"'{property.Name}': {text}"
            : null;

    private static bool TakesUnknownKeys(JsonTypeInfo info, JsonSerializerOptions options)
    {
        foreach (var property in info.Properties)
        {
            if (property.IsExtensionData)
            {
                return true;
            }
        }

        return (info.UnmappedMemberHandling ?? options.UnmappedMemberHandling) == JsonUnmappedMemberHandling.Skip;
    }

    private static JsonPropertyInfo? FindProperty(JsonTypeInfo info, string name)
    {
        foreach (var property in info.Properties)
        {
            if (string.Equals(property.Name, name, StringComparison.Ordinal))
            {
                return property;
            }
        }

        return null;
    }

    private static bool IsNullableReference(JsonPropertyInfo? property)
    {
        if (property?.AttributeProvider is not PropertyInfo member)
        {
            return false;
        }

        return new NullabilityInfoContext().Create(member).WriteState == NullabilityState.Nullable;
    }

    private static JsonTypeInfo TypeInfo(JsonSerializerOptions options, Type type) => options.GetTypeInfo(type);

    private static JsonTypeInfo Unwrap(JsonSerializerOptions options, JsonTypeInfo info) =>
        Nullable.GetUnderlyingType(info.Type) is { } underlying ? options.GetTypeInfo(underlying) : info;

    private enum Kind
    {
        Any,
        String,
        Boolean,
        Integer,
        Number,
        Object,
        Array,
        Value,
        Custom,
    }

    private static Kind Expect(JsonTypeInfo info)
    {
        switch (info.Kind)
        {
            case JsonTypeInfoKind.Object or JsonTypeInfoKind.Dictionary:
                return Kind.Object;
            case JsonTypeInfoKind.Enumerable:
                return Kind.Array;
        }

        var type = info.Type;
        if (type == typeof(string) || type == typeof(char) || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly)
            || type == typeof(TimeOnly) || type == typeof(TimeSpan) || type == typeof(Guid) || type == typeof(Uri))
        {
            return Kind.String;
        }

        if (type == typeof(bool))
        {
            return Kind.Boolean;
        }

        if (IntegerRange(type) is not null || type.IsEnum)
        {
            return Kind.Integer;
        }

        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal) || type == typeof(Half))
        {
            return Kind.Number;
        }

        if (type == typeof(JsonObject))
        {
            return Kind.Object;
        }

        if (type == typeof(JsonArray))
        {
            return Kind.Array;
        }

        if (type == typeof(JsonValue))
        {
            return Kind.Value;
        }

        return type == typeof(JsonNode) || type == typeof(JsonElement) || type == typeof(JsonDocument) || type == typeof(object) ? Kind.Any : Kind.Custom;
    }

    private static string Phrase(Kind kind) => kind switch
    {
        Kind.String => "a string",
        Kind.Boolean => "true or false",
        Kind.Integer => "an integer",
        Kind.Number => "a number",
        Kind.Object => "an object",
        Kind.Array => "an array",
        Kind.Value => "a string, a number, true or false",
        _ => "a JSON value",
    };

    private static bool Matches(Kind kind, JsonNode? node)
    {
        if (kind == Kind.Any)
        {
            return true;
        }

        var valueKind = node is JsonValue v ? v.GetValueKind() : JsonValueKind.Undefined;
        return kind switch
        {
            Kind.String => valueKind == JsonValueKind.String,
            Kind.Boolean => valueKind is JsonValueKind.True or JsonValueKind.False,
            Kind.Integer or Kind.Number => valueKind == JsonValueKind.Number,
            Kind.Object => node is JsonObject,
            Kind.Array => node is JsonArray,
            Kind.Value => valueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False,
            _ => true,
        };
    }

    private static (decimal Min, decimal Max)? IntegerRange(Type type)
    {
        if (type.IsEnum)
        {
            type = Enum.GetUnderlyingType(type);
        }

        return Type.GetTypeCode(type) switch
        {
            TypeCode.SByte => (sbyte.MinValue, sbyte.MaxValue),
            TypeCode.Byte => (byte.MinValue, byte.MaxValue),
            TypeCode.Int16 => (short.MinValue, short.MaxValue),
            TypeCode.UInt16 => (ushort.MinValue, ushort.MaxValue),
            TypeCode.Int32 => (int.MinValue, int.MaxValue),
            TypeCode.UInt32 => (uint.MinValue, uint.MaxValue),
            TypeCode.Int64 => (long.MinValue, long.MaxValue),
            TypeCode.UInt64 => (ulong.MinValue, ulong.MaxValue),
            _ => null,
        };
    }

    private static JsonNode? Child(JsonNode? node, Segment segment) => node switch
    {
        JsonObject obj => obj.TryGetPropertyValue(segment.Token, out var value) ? value : null,
        JsonArray array when segment.IsIndex && int.TryParse(segment.Token, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < array.Count => array[i],
        _ => null,
    };

    private static string Capitalize(string text) => text.Length == 0 || !char.IsLower(text[0]) ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>Where the walk is: the type and value there, the member that holds it, and how a sentence names it.</summary>
    /// <param name="Name">The value as the subject of a sentence: <c>'order'</c>, <c>Element 1 of 'key'</c>, <c>The pipeline</c>.</param>
    /// <param name="Where">The value as a place, after "in": <c>'coarse_top_k'</c>, <c>the config of TopK@1</c>.</param>
    private readonly record struct Location(JsonTypeInfo? Info, JsonNode? Node, JsonPropertyInfo? Property, string Name, string Where);

    internal readonly record struct Segment(string Token, bool IsIndex);

    /// <summary>
    /// The segments of a System.Text.Json error path (<c>$.nodes['a.b'].order</c>, <c>$.key[1]</c>). A name in brackets is
    /// written without escaping, so one holding <c>']</c> is ambiguous; the document decides, by the key that exists.
    /// </summary>
    internal static List<Segment> PathSegments(string? path, JsonNode? document)
    {
        var segments = new List<Segment>();
        if (string.IsNullOrEmpty(path))
        {
            return segments;
        }

        var node = document;
        var i = path[0] == '$' ? 1 : 0;
        while (i < path.Length)
        {
            Segment segment;
            if (path[i] == '.')
            {
                var end = i + 1;
                while (end < path.Length && path[end] != '.' && path[end] != '[')
                {
                    end++;
                }

                segment = new Segment(path[(i + 1)..end], false);
                i = end;
            }
            else if (path[i] == '[' && i + 1 < path.Length && path[i + 1] == '\'')
            {
                var start = i + 2;
                var chosen = -1;
                for (var close = path.IndexOf("']", start, StringComparison.Ordinal); close >= 0; close = path.IndexOf("']", close + 1, StringComparison.Ordinal))
                {
                    var next = close + 2;
                    if (next < path.Length && path[next] != '.' && path[next] != '[')
                    {
                        continue; // not the end of a segment
                    }

                    if (chosen < 0)
                    {
                        chosen = close;
                    }

                    if (node is JsonObject obj && obj.ContainsKey(path[start..close]))
                    {
                        chosen = close;
                        break;
                    }
                }

                if (chosen < 0)
                {
                    break;
                }

                segment = new Segment(path[start..chosen], false);
                i = chosen + 2;
            }
            else if (path[i] == '[')
            {
                var end = path.IndexOf(']', i + 1);
                if (end < 0)
                {
                    break;
                }

                segment = new Segment(path[(i + 1)..end], true);
                i = end + 1;
            }
            else
            {
                break;
            }

            segments.Add(segment);
            node = Child(node, segment);
        }

        return segments;
    }
}
