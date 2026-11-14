using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace StrataRec.Artifacts.Json;

/// <summary>
/// The first null in a document where the type it was bound to says an element cannot be null: an entry of a map or an
/// item of a list whose element type is not nullable (<c>"outputs": { "x": null }</c> for a
/// <c>Dictionary&lt;string, Output&gt;</c>). The serializer rejects a null member itself (<c>RespectNullableAnnotations</c>)
/// but not a null element, which then reached the code that read it as an exception without a location.
/// </summary>
/// <remarks>
/// <para>
/// Walks the document, guided by the serializer's own <see cref="JsonTypeInfo"/>, not the object it was bound to: the
/// location is where the null is written (a key as written, not as its type reads it back; an index in the array, not in
/// a set that dropped or reordered items; review 46), and no code of the bound object runs (a computed property returning
/// <c>this</c> recursed; review 45). Element nullability comes from the member's annotations, so a model member added
/// later is checked without being listed; at the root, and where annotations are unknown, a null element is let
/// through (a patch's null deletes). A polymorphic object is walked as the type its discriminator names; a member with
/// its own converter, or a type the serializer does not read as an object, list or map, is not walked into.
/// </para>
/// <para>
/// Runs when a document or a config is bound, and only when the document has a null at all; members are looked up once
/// per type.
/// </para>
/// </remarks>
internal static class NullElements
{
    private static readonly ConcurrentDictionary<(Type, JsonSerializerOptions), Members> ByType = new();

    /// <summary>Whether <paramref name="node"/> has a null anywhere (without one, nothing needs walking).</summary>
    public static bool AnyNull(JsonNode? node) => node switch
    {
        null => true,
        JsonObject o => AnyNullIn(o),
        JsonArray a => AnyNullIn(a),
        _ => false,
    };

    /// <param name="document">The document bound.</param>
    /// <param name="type">The type it was bound to.</param>
    public static JsonBindingError? Find(JsonElement document, Type type, JsonSerializerOptions options) =>
        Walk(document, options.GetTypeInfo(type), declared: null, [], options);

    private static bool AnyNullIn(JsonObject o)
    {
        foreach (var (_, value) in o)
        {
            if (AnyNull(value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AnyNullIn(JsonArray a)
    {
        foreach (var value in a)
        {
            if (AnyNull(value))
            {
                return true;
            }
        }

        return false;
    }

    /// <param name="declared">The annotations of the member or element <paramref name="value"/> is; null when unknown.</param>
    /// <param name="path">The steps to <paramref name="value"/>: a member's or key's name, or an item's index.</param>
    private static JsonBindingError? Walk(JsonElement value, JsonTypeInfo info, NullabilityInfo? declared, List<Step> path, JsonSerializerOptions options)
    {
        switch (info.Kind)
        {
            case JsonTypeInfoKind.Object when value.ValueKind == JsonValueKind.Object:
                var members = ByType.GetOrAdd((Concrete(value, info, options).Type, options), static key => Members.Of(key.Item1, key.Item2));
                foreach (var property in value.EnumerateObject())
                {
                    if (members.Named.TryGetValue(property.Name, out var member))
                    {
                        // A null member is the serializer's to reject (or allowed); a member with its own converter has
                        // its own shape.
                        if (property.Value.ValueKind != JsonValueKind.Null && member.Property.CustomConverter is null
                            && Into(property.Value, member.Property.PropertyType, member.Nullability, path, new Step(property.Name, false), options) is { } error)
                        {
                            return error;
                        }
                    }
                    else if (members.ExtensionData is { } extension)
                    {
                        // The object's other members, at its own level: entries of the extension map.
                        var entry = extension.Nullability?.GenericTypeArguments is { Length: 2 } pair ? pair[1] : null;
                        if (Element(property.Value, Value(extension.Property.PropertyType, options), entry, path, new Step(property.Name, false), options) is { } error)
                        {
                            return error;
                        }
                    }
                }

                return null;
            case JsonTypeInfoKind.Enumerable when value.ValueKind == JsonValueKind.Array && info.ElementType is { } itemType:
                if (itemType is { IsValueType: true } && Nullable.GetUnderlyingType(itemType) is null && options.GetTypeInfo(itemType).Kind == JsonTypeInfoKind.None)
                {
                    return null; // numbers, booleans, enums: none can be null, and nothing to walk into
                }

                var item = declared?.ElementType ?? (declared?.GenericTypeArguments is { Length: 1 } single ? single[0] : null);
                var itemInfo = options.GetTypeInfo(itemType);
                var index = 0;
                foreach (var element in value.EnumerateArray())
                {
                    if (Element(element, itemInfo, item, path, new Step(null, true, index++), options) is { } error)
                    {
                        return error;
                    }
                }

                return null;
            case JsonTypeInfoKind.Dictionary when value.ValueKind == JsonValueKind.Object && info.ElementType is { } entryType:
                var entryNullability = declared?.GenericTypeArguments is { Length: 2 } types ? types[1] : null;
                var entryInfo = options.GetTypeInfo(entryType);
                foreach (var property in value.EnumerateObject())
                {
                    if (Element(property.Value, entryInfo, entryNullability, path, new Step(property.Name, false), options) is { } error)
                    {
                        return error;
                    }
                }

                return null;
            default:
                return null;
        }
    }

    private static JsonBindingError? Into(JsonElement value, Type type, NullabilityInfo? declared, List<Step> path, Step step, JsonSerializerOptions options)
    {
        var info = options.GetTypeInfo(type);
        if (info.Kind == JsonTypeInfoKind.None)
        {
            return null;
        }

        path.Add(step);
        var error = Walk(value, info, declared, path, options);
        path.RemoveAt(path.Count - 1);
        return error;
    }

    private static JsonBindingError? Element(JsonElement value, JsonTypeInfo info, NullabilityInfo? declared, List<Step> path, Step step, JsonSerializerOptions options)
    {
        if (value.ValueKind != JsonValueKind.Null)
        {
            return Into(value, info.Type, declared, path, step, options);
        }

        // Nullable<T> and annotated references may be null; an element whose annotations are unknown is let through.
        if (declared is null || declared.Type.IsValueType || declared.ReadState != NullabilityState.NotNull)
        {
            return null;
        }

        var pointer = new StringBuilder();
        foreach (var at in path)
        {
            pointer.Append('/').Append(JsonPointer.Escape(at.Token));
        }

        pointer.Append('/').Append(JsonPointer.Escape(step.Token));
        var described = !step.Index ? $"'{step.Token}'" : path.Count > 0 ? $"Item {step.Token} of '{path[^1].Token}'" : $"Item {step.Token}";
        return new JsonBindingError(pointer.ToString(), $"{described} must be {JsonBindingErrors.Phrase(options, declared.Type)}, got null.", null);
    }

    /// <summary>The type info of the values of a map type (the extension data's), or of <c>object</c>.</summary>
    private static JsonTypeInfo Value(Type map, JsonSerializerOptions options) =>
        options.GetTypeInfo(options.GetTypeInfo(map).ElementType ?? typeof(object));

    /// <summary>The type a polymorphic object is read as: the derived type its discriminator names, else the type itself.</summary>
    private static JsonTypeInfo Concrete(JsonElement value, JsonTypeInfo info, JsonSerializerOptions options)
    {
        if (info.PolymorphismOptions is not { } polymorphism || !value.TryGetProperty(polymorphism.TypeDiscriminatorPropertyName, out var tag))
        {
            return info;
        }

        foreach (var derived in polymorphism.DerivedTypes)
        {
            var matches = derived.TypeDiscriminator switch
            {
                string name => tag.ValueKind == JsonValueKind.String && tag.ValueEquals(name),
                int number => tag.ValueKind == JsonValueKind.Number && tag.TryGetInt32(out var n) && n == number,
                _ => false,
            };
            if (matches)
            {
                return options.GetTypeInfo(derived.DerivedType);
            }
        }

        return info;
    }

    /// <summary>A step of the path: a member's or a key's name as written, or an item's index.</summary>
    private readonly record struct Step(string? Name, bool Index, int Position = 0)
    {
        public string Token => Name ?? Position.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>A type's members by their JSON names, with their annotations, and its extension data member.</summary>
    private sealed class Members
    {
        public required Dictionary<string, Member> Named { get; init; }

        public Member? ExtensionData { get; init; }

        public static Members Of(Type type, JsonSerializerOptions options)
        {
            var info = options.GetTypeInfo(type);
            var context = new NullabilityInfoContext();
            var named = new Dictionary<string, Member>(StringComparer.Ordinal);
            Member? extension = null;
            if (info.Kind == JsonTypeInfoKind.Object)
            {
                foreach (var property in info.Properties)
                {
                    var nullability = property.AttributeProvider switch
                    {
                        PropertyInfo p => context.Create(p),
                        FieldInfo f => context.Create(f),
                        _ => null,
                    };
                    var member = new Member(property, nullability);
                    if (property.IsExtensionData)
                    {
                        extension = member;
                    }
                    else
                    {
                        named[property.Name] = member;
                    }
                }
            }

            return new Members { Named = named, ExtensionData = extension };
        }
    }

    private sealed record Member(JsonPropertyInfo Property, NullabilityInfo? Nullability);
}
