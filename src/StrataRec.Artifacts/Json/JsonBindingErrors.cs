using System.Buffers;
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
/// re-serialized copy of the document. Instead of parsing them, the translator walks the error's path through the
/// contract the serializer used (<see cref="JsonTypeInfo"/> from the options; for a polymorphic type, the derived type its
/// discriminator selects) and through the document, and describes what it finds at the end: the expected kind comes from
/// the contract, the actual value from the document.</para>
/// <para>A converter decides what JSON it takes. The kind is inferred from the .NET type only where the serializer's own
/// converter for that type reads it; a converter of our own words its message for the reader; a string enum converter
/// is asked for its names; any other converter gets a message that does not guess its shape.</para>
/// <para>What the translator cannot be sure of it does not claim: a path that two readings of the document fit is
/// reported at the place both share. Describing never throws for a bad document: an error about a value is told even
/// when the value cannot be read (a string that is not valid Unicode).</para>
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

        if (Unreadable(document, subject) is { } unreadable)
        {
            return unreadable; // walking it would fail on the same thing
        }

        var at = new Location(TypeInfo(options, type), document, null, Capitalize(subject), subject);
        var pointer = new StringBuilder();
        var segments = PathSegments(exception.Path, document, out var certain);
        var ownKey = false; // the last segment is a key read by a converter of our own, which may be what failed
        foreach (var segment in segments)
        {
            var info = at.Info is null ? null : Contract(options, at.Info, at.Node);
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

        if (!certain)
        {
            // The error's path fits the document in more than one way (keys that contain "']"): say only what is sure.
            return new JsonBindingError(pointer.ToString(),
                $"A value inside {at.Where} is not valid; a key there contains \"']\", so the error cannot say which value.", null);
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
        var or = nullable && at.Node is not null ? " or null" : string.Empty;
        var info = Contract(options, at.Info, at.Node);
        if (Discriminator(info, at, pointer) is { } discriminator)
        {
            return discriminator;
        }

        // A converter the serializer was told to use decides what JSON the value takes, not the .NET type.
        var converter = Converter(at.Property, info, options);
        if (info.Type.IsEnum && converter is not null && EnumNames(info.Type, converter, options) is { } enumNames)
        {
            return new JsonBindingError(pointer, $"{at.Name} must be one of {string.Join(", ", enumNames)}{or}, got {JsonWords.Value(at.Node)}.", null);
        }

        var expected = Expect(info);
        if (expected == Kind.Custom || HasConfiguredConverter(at.Property, info.Type, options))
        {
            // A converter of our own words its message for the reader; one we did not write gets no guess at its shape.
            return IsOwn(at.Property, converter) && !string.IsNullOrEmpty(exception.Message)
                ? new JsonBindingError(pointer, $"{at.Name}: {exception.Message}", null)
                : new JsonBindingError(pointer, $"{at.Name} has an invalid value: {JsonWords.Value(at.Node)}.", null);
        }

        if (!Matches(expected, at.Node))
        {
            return new JsonBindingError(pointer, $"{at.Name} must be {Phrase(expected)}{or}, got {JsonWords.Value(at.Node)}.", null);
        }

        if (expected == Kind.Integer && at.Node is JsonValue number && IntegerRange(info.Type) is var (min, max))
        {
            var text = number.ToJsonString();
            if (!JsonIntegers.IsIntegral(text))
            {
                return new JsonBindingError(pointer, $"{at.Name} must be an integer, got {JsonWords.Value(at.Node)}.", null);
            }

            // An integral number decimal cannot hold (1e40) is beyond every integer type; one it holds, it holds exactly.
            if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || d < min || d > max)
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

    // ------------------------------------------------------------------ the contract the serializer used

    /// <summary>
    /// The contract that read <paramref name="node"/>: the type's own, or for a polymorphic type the derived type its
    /// discriminator selects (the base's when the discriminator is absent or unknown).
    /// </summary>
    private static JsonTypeInfo Contract(JsonSerializerOptions options, JsonTypeInfo info, JsonNode? node)
    {
        info = Unwrap(options, info);
        if (info.PolymorphismOptions is not { } polymorphism || node is not JsonObject obj
            || !obj.TryGetPropertyValue(polymorphism.TypeDiscriminatorPropertyName, out var id))
        {
            return info;
        }

        foreach (var derived in polymorphism.DerivedTypes)
        {
            if (SameDiscriminator(derived.TypeDiscriminator, id))
            {
                return options.GetTypeInfo(derived.DerivedType);
            }
        }

        return info;
    }

    /// <summary>The error for a polymorphic object whose discriminator names no derived type; null otherwise.</summary>
    private static JsonBindingError? Discriminator(JsonTypeInfo info, Location at, string pointer)
    {
        if (info.PolymorphismOptions is not { DerivedTypes.Count: > 0 } polymorphism || at.Node is not JsonObject obj
            || !obj.TryGetPropertyValue(polymorphism.TypeDiscriminatorPropertyName, out var id))
        {
            return null;
        }

        foreach (var derived in polymorphism.DerivedTypes)
        {
            if (SameDiscriminator(derived.TypeDiscriminator, id))
            {
                return null;
            }
        }

        var name = polymorphism.TypeDiscriminatorPropertyName;
        return new JsonBindingError(pointer + "/" + JsonPointer.Escape(name), $"'{name}' must be one of {Discriminators(polymorphism)}, got {JsonWords.Value(id)}.", null);
    }

    /// <summary>
    /// For a <see cref="NotSupportedException"/> of the serializer: the first object of an abstract polymorphic type
    /// (one it cannot create) whose discriminator is missing, found by walking the document through the contract. Null
    /// when there is none: the exception is then not about the document.
    /// </summary>
    public static JsonBindingError? MissingDiscriminator(JsonNode? document, Type type, JsonSerializerOptions options, string subject)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(subject);
        return Unreadable(document, subject) ?? Missing(TypeInfo(options, type), document, string.Empty, subject);

        JsonBindingError? Missing(JsonTypeInfo info, JsonNode? node, string pointer, string where)
        {
            info = Unwrap(options, info);
            if (node is JsonObject obj && info.PolymorphismOptions is { DerivedTypes.Count: > 0 } polymorphism && (info.Type.IsAbstract || info.Type.IsInterface)
                && !obj.ContainsKey(polymorphism.TypeDiscriminatorPropertyName))
            {
                var name = polymorphism.TypeDiscriminatorPropertyName;
                return new JsonBindingError(pointer, $"'{name}' is required in {where}.", $"'{name}' names the kind of object: one of {Discriminators(polymorphism)}");
            }

            info = Contract(options, info, node);
            switch (info.Kind)
            {
                case JsonTypeInfoKind.Object when node is JsonObject members:
                    foreach (var property in info.Properties)
                    {
                        if (members.TryGetPropertyValue(property.Name, out var child)
                            && Missing(TypeInfo(options, property.PropertyType), child, pointer + "/" + JsonPointer.Escape(property.Name), $"'{property.Name}'") is { } found)
                        {
                            return found;
                        }
                    }

                    break;
                case JsonTypeInfoKind.Dictionary when node is JsonObject entries:
                    foreach (var (key, child) in entries)
                    {
                        if (Missing(TypeInfo(options, info.ElementType!), child, pointer + "/" + JsonPointer.Escape(key), $"entry '{key}' of {where}") is { } found)
                        {
                            return found;
                        }
                    }

                    break;
                case JsonTypeInfoKind.Enumerable when node is JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                    {
                        var index = i.ToString(CultureInfo.InvariantCulture);
                        if (Missing(TypeInfo(options, info.ElementType!), array[i], pointer + "/" + index, $"element {index} of {where}") is { } found)
                        {
                            return found;
                        }
                    }

                    break;
            }

            return null;
        }
    }

    /// <summary>The discriminators of a polymorphic type as JSON writes them: <c>"circle", "square"</c>.</summary>
    private static string Discriminators(JsonPolymorphismOptions polymorphism)
    {
        var ids = new List<string>(polymorphism.DerivedTypes.Count);
        foreach (var derived in polymorphism.DerivedTypes)
        {
            switch (derived.TypeDiscriminator)
            {
                case string text:
                    ids.Add(JsonWords.Quote(text));
                    break;
                case int number:
                    ids.Add(number.ToString(CultureInfo.InvariantCulture));
                    break;
            }
        }

        return string.Join(", ", ids);
    }

    private static bool SameDiscriminator(object? id, JsonNode? value) => id switch
    {
        string s => value is JsonValue v && v.GetValueKind() == JsonValueKind.String && JsonWords.TryText(v, out var text) && text == s,
        int n => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue(out int m) && m == n,
        _ => false,
    };

    /// <summary>
    /// The converter that read the value: the member's own, else the one of its (non-nullable) type, from the options, an
    /// attribute on the type, or built in.
    /// </summary>
    private static JsonConverter? Converter(JsonPropertyInfo? property, JsonTypeInfo info, JsonSerializerOptions options)
    {
        var converter = property?.CustomConverter ?? info.Converter;
        if (converter is JsonConverterFactory factory)
        {
            var type = property?.PropertyType is { } declared && factory.CanConvert(declared) ? declared : info.Type;
            converter = factory.CanConvert(type) ? factory.CreateConverter(type, options) : null;
        }

        return converter;
    }

    /// <summary>
    /// True when the converter is ours: the type named by the member's <see cref="JsonConverterAttribute"/> when it has one
    /// (the serializer may wrap it, e.g. for a nullable member), else the converter's own type.
    /// </summary>
    private static bool IsOwn(JsonPropertyInfo? property, JsonConverter? converter)
    {
        if (property?.AttributeProvider is MemberInfo member && member.GetCustomAttribute<JsonConverterAttribute>(inherit: true) is { ConverterType: { } declared })
        {
            return declared.Assembly != SerializerAssembly;
        }

        return converter is not null && IsOwn(converter);
    }

    /// <summary>True when the serializer was told to read <paramref name="type"/> with a converter: on the member, on the type, or in the options.</summary>
    private static bool HasConfiguredConverter(JsonPropertyInfo? property, Type type, JsonSerializerOptions options)
    {
        if (property?.CustomConverter is not null || type.IsDefined(typeof(JsonConverterAttribute), inherit: false))
        {
            return true;
        }

        foreach (var converter in options.Converters)
        {
            if (converter.CanConvert(type))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The names a string enum converter writes for <paramref name="enumType"/>'s values, quoted as JSON (its naming
    /// policy and member names applied, since the converter itself writes them), or null when it writes numbers.
    /// </summary>
    private static List<string>? EnumNames(Type enumType, JsonConverter converter, JsonSerializerOptions options)
    {
        if (converter.Type is not { } converted)
        {
            return null;
        }

        try
        {
            var write = typeof(JsonBindingErrors).GetMethod(nameof(WriteString), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(converted);
            var names = new List<string>();
            foreach (var value in Enum.GetValues(enumType))
            {
                if (write.Invoke(null, new[] { converter, value, options }) is not string name)
                {
                    return null;
                }

                var quoted = JsonWords.Quote(name);
                if (!names.Contains(quoted))
                {
                    names.Add(quoted);
                }
            }

            return names.Count > 0 ? names : null;
        }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException or ArgumentException or NotSupportedException or InvalidCastException)
        {
            return null; // a converter that cannot write a value alone: no names to offer
        }
    }

    /// <summary>What <paramref name="converter"/> writes for <paramref name="value"/> when that is a string; null otherwise.</summary>
    private static string? WriteString<T>(JsonConverter converter, object value, JsonSerializerOptions options)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            ((JsonConverter<T>)converter).Write(writer, (T)value, options);
        }

        var reader = new Utf8JsonReader(buffer.WrittenSpan);
        return reader.Read() && reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
    }

    // ------------------------------------------------------------------ kinds

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

    // ------------------------------------------------------------------ the document

    /// <summary>
    /// The first part of a document that cannot be read as written: an object with a key written twice, or a key or string
    /// that is not valid Unicode (an unpaired surrogate escape). A document parsed leniently holds such parts unread until
    /// something reads them, and then the serializer or the translator would fail on them with an exception of its own;
    /// this finds them first and says what and where they are. Null for a document every part of which can be read.
    /// </summary>
    /// <param name="subject">The document as a sentence names it, e.g. <c>the pipeline</c>.</param>
    public static JsonBindingError? Unreadable(JsonNode? document, string subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return Find(document, string.Empty);

        JsonBindingError? Find(JsonNode? node, string pointer)
        {
            var place = pointer.Length == 0 ? Capitalize(subject) : "This value";
            switch (node)
            {
                case JsonObject obj:
                    var children = new List<(string Key, JsonNode? Child)>();
                    try
                    {
                        foreach (var (key, child) in obj)
                        {
                            children.Add((key, child));
                        }
                    }
                    catch (ArgumentException)
                    {
                        return new JsonBindingError(pointer, $"{place} has a key written twice.",
                            "write each key once: a later value would silently replace an earlier one");
                    }
                    catch (InvalidOperationException)
                    {
                        return new JsonBindingError(pointer, $"{place} has a key that is not valid Unicode.", UnicodeHint);
                    }

                    foreach (var (key, child) in children)
                    {
                        if (Find(child, pointer + "/" + JsonPointer.Escape(key)) is { } error)
                        {
                            return error;
                        }
                    }

                    return null;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (Find(array[i], pointer + "/" + i.ToString(CultureInfo.InvariantCulture)) is { } error)
                        {
                            return error;
                        }
                    }

                    return null;
                case JsonValue value when value.GetValueKind() == JsonValueKind.String && !JsonWords.TryText(value, out _):
                    return new JsonBindingError(pointer, $"{place} is a string that is not valid Unicode.", UnicodeHint);
                default:
                    return null;
            }
        }
    }

    private const string UnicodeHint = "escape characters as UTF-16: a \\uD800-\\uDBFF escape must be followed by a \\uDC00-\\uDFFF one, or write the character itself";

    private static JsonNode? Child(JsonNode? node, Segment segment) => node switch
    {
        JsonObject obj => obj.TryGetPropertyValue(segment.Token, out var value) ? value : null,
        JsonArray array when Index(segment) is var i && i >= 0 && i < array.Count => array[i],
        _ => null,
    };

    /// <summary>True when the document has the segment: a key of an object, or an index within an array.</summary>
    private static bool Has(JsonNode? node, Segment segment) => node switch
    {
        JsonObject obj => !segment.IsIndex && obj.ContainsKey(segment.Token),
        JsonArray array => Index(segment) is var i && i >= 0 && i < array.Count,
        _ => false,
    };

    private static int Index(Segment segment) =>
        segment.IsIndex && int.TryParse(segment.Token, NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : -1;

    private static string Capitalize(string text) => text.Length == 0 || !char.IsLower(text[0]) ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>Where the walk is: the type and value there, the member that holds it, and how a sentence names it.</summary>
    /// <param name="Name">The value as the subject of a sentence: <c>'order'</c>, <c>Element 1 of 'key'</c>, <c>The pipeline</c>.</param>
    /// <param name="Where">The value as a place, after "in": <c>'coarse_top_k'</c>, <c>the config of TopK@1</c>.</param>
    private readonly record struct Location(JsonTypeInfo? Info, JsonNode? Node, JsonPropertyInfo? Property, string Name, string Where);

    internal readonly record struct Segment(string Token, bool IsIndex);

    // ------------------------------------------------------------------ the error's path

    /// <summary>
    /// The segments of a System.Text.Json error path (<c>$.nodes['a.b'].order</c>, <c>$.key[1]</c>). A name in brackets is
    /// written without escaping, so one holding <c>']</c> can end in more than one place. The document decides: of the
    /// readings, the one whose every segment exists in it. When none or more than one does, <paramref name="certain"/> is
    /// false and the segments are those before the first name that cannot be told apart.
    /// </summary>
    internal static List<Segment> PathSegments(string? path, JsonNode? document, out bool certain)
    {
        certain = true;
        if (string.IsNullOrEmpty(path))
        {
            return new List<Segment>();
        }

        var start = path[0] == '$' ? 1 : 0;
        var prefix = new List<Segment>();
        if (!Lexical(path, start, prefix))
        {
            return prefix; // no name is ambiguous: the path reads one way
        }

        var readings = new List<List<Segment>>(2);
        Readings(path, start, document, new List<Segment>(), readings);
        if (readings.Count == 1)
        {
            return readings[0];
        }

        certain = false;
        return prefix;
    }

    /// <summary>Reads the path's segments while each has one reading; true when it stops at a bracketed name with several.</summary>
    private static bool Lexical(string path, int i, List<Segment> segments)
    {
        while (i < path.Length)
        {
            if (path[i] == '[' && i + 1 < path.Length && path[i + 1] == '\'')
            {
                var ends = NameEnds(path, i + 2);
                if (ends.Count > 1)
                {
                    return true;
                }

                if (ends.Count == 0)
                {
                    return false;
                }

                segments.Add(new Segment(path[(i + 2)..ends[0]], false));
                i = ends[0] + 2;
            }
            else if (Plain(path, i, out var segment, out var next))
            {
                segments.Add(segment);
                i = next;
            }
            else
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Collects (at most two) readings of the path from <paramref name="i"/> whose every segment exists in the document.</summary>
    private static void Readings(string path, int i, JsonNode? node, List<Segment> current, List<List<Segment>> readings)
    {
        if (readings.Count > 1)
        {
            return;
        }

        if (i == path.Length)
        {
            readings.Add(new List<Segment>(current));
            return;
        }

        if (path[i] == '[' && i + 1 < path.Length && path[i + 1] == '\'')
        {
            foreach (var end in NameEnds(path, i + 2))
            {
                Next(new Segment(path[(i + 2)..end], false), end + 2);
            }
        }
        else if (Plain(path, i, out var segment, out var next))
        {
            Next(segment, next);
        }

        void Next(Segment segment, int next)
        {
            if (!Has(node, segment))
            {
                return;
            }

            current.Add(segment);
            Readings(path, next, Child(node, segment), current, readings);
            current.RemoveAt(current.Count - 1);
        }
    }

    /// <summary>Where a bracketed name starting at <paramref name="start"/> may end: each <c>']</c> followed by the end, '.' or '['.</summary>
    private static List<int> NameEnds(string path, int start)
    {
        var ends = new List<int>();
        for (var close = path.IndexOf("']", start, StringComparison.Ordinal); close >= 0; close = path.IndexOf("']", close + 1, StringComparison.Ordinal))
        {
            var next = close + 2;
            if (next == path.Length || path[next] == '.' || path[next] == '[')
            {
                ends.Add(close);
            }
        }

        return ends;
    }

    /// <summary>A segment that reads one way: <c>.name</c> (no '.', '[' or quote in it) or <c>[index]</c>.</summary>
    private static bool Plain(string path, int i, out Segment segment, out int next)
    {
        segment = default;
        next = i;
        if (path[i] == '.')
        {
            var end = i + 1;
            while (end < path.Length && path[end] != '.' && path[end] != '[')
            {
                end++;
            }

            segment = new Segment(path[(i + 1)..end], false);
            next = end;
            return true;
        }

        if (path[i] == '[' && (i + 1 >= path.Length || path[i + 1] != '\''))
        {
            var end = path.IndexOf(']', i + 1);
            if (end < 0)
            {
                return false;
            }

            segment = new Segment(path[(i + 1)..end], true);
            next = end + 1;
            return true;
        }

        return false;
    }
}
