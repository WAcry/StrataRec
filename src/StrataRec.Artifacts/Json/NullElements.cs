using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace StrataRec.Artifacts.Json;

/// <summary>
/// The serializer rejects a null member itself (<c>RespectNullableAnnotations</c>) but not a null element.
/// </summary>
/// <remarks>
/// <para>
/// Walks the document, guided by the serializer's own <see cref="JsonTypeInfo"/>, not the object it was bound to: the
/// location is where the null is written (a key as written, not as its type reads it back; an index in the array, not
/// in a set that dropped or reordered items), and no code of the bound object runs.
/// </para>
/// </remarks>
internal static class NullElements
{
    private static readonly ConcurrentDictionary<(Type, JsonSerializerOptions), Members> ByType = new();

    public static bool AnyNull(JsonNode? node) => node switch
    {
        null => true,
        JsonObject o => AnyNullIn(o),
        JsonArray a => AnyNullIn(a),
        // A value can hold a CLR list or object (JsonValue.Create(array)), written as JSON that may have a null: the
        // same JSON is checked however the tree was built.
        JsonValue v => v.GetValueKind() is JsonValueKind.Null or JsonValueKind.Array or JsonValueKind.Object,
        _ => false,
    };

    public static JsonBindingError? Find(JsonElement document, Type type, JsonSerializerOptions options) =>
        Walk(document, options.GetTypeInfo(Nullable.GetUnderlyingType(type) ?? type), declared: null, [], options);

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
                        // its own shape; one the serializer does not read (ignored, computed) is skipped, as it skips it.
                        if (member.Read && property.Value.ValueKind != JsonValueKind.Null && member.Property.CustomConverter is null
                            && Into(property.Value, member.Property.PropertyType, member.Nullability, path, new Step(property.Name, false), options) is { } error)
                        {
                            return error;
                        }
                    }
                    else if (members.ExtensionData is { Read: true } extension)
                    {
                        var entry = extension.Nullability?.GenericTypeArguments is { Length: 2 } pair ? pair[1] : null;
                        if (Element(property.Value, Value(extension.Property.PropertyType, options), entry, path, new Step(property.Name, false), options) is { } error)
                        {
                            return error;
                        }
                    }
                }

                return null;
            case JsonTypeInfoKind.Enumerable when value.ValueKind == JsonValueKind.Array && info.ElementType is { } itemType:
                if ((Nullable.GetUnderlyingType(itemType) ?? itemType) is { IsValueType: true } plain && options.GetTypeInfo(plain).Kind == JsonTypeInfoKind.None)
                {
                    return null;
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
        // A present Nullable<T> is read as its T (its own type info is opaque). The annotations of a T? already describe
        // T's type arguments.
        var info = options.GetTypeInfo(Nullable.GetUnderlyingType(type) ?? type);
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

    private static JsonTypeInfo Value(Type map, JsonSerializerOptions options) =>
        options.GetTypeInfo(options.GetTypeInfo(map).ElementType ?? typeof(object));

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

    private readonly record struct Step(string? Name, bool Index, int Position = 0)
    {
        public string Token => Name ?? Position.ToString(CultureInfo.InvariantCulture);
    }

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
                    // The document is read into a constructor parameter, when there is one: its annotations are the
                    // contract (a constructor may take nulls and drop them).
                    var parameter = property.AssociatedParameter?.AttributeProvider as ParameterInfo;
                    var nullability = parameter is not null ? context.Create(parameter) : property.AttributeProvider switch
                    {
                        PropertyInfo p => context.Create(p),
                        FieldInfo f => context.Create(f),
                        _ => null,
                    };

                    // An ignored member has no accessors, and its constructor parameter is not read either; a computed
                    // one has only a getter.
                    var ignored = property.Get is null && property.Set is null;
                    var read = !ignored && (parameter is not null || property.Set is not null || PopulatedWithoutSetter(property, info, options));
                    var member = new Member(property, nullability, read);
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

    /// <summary>
    /// Whether the serializer fills a member without a setter through its getter's object. Whether it can is the
    /// serializer's converter's, which it does not expose: this follows the shapes its converters populate.
    /// </summary>
    private static bool PopulatedWithoutSetter(JsonPropertyInfo property, JsonTypeInfo declaring, JsonSerializerOptions options)
    {
        if (property.Get is null)
        {
            return false;
        }

        if (property.ObjectCreationHandling is { } handling)
        {
            return handling == JsonObjectCreationHandling.Populate;
        }

        if ((declaring.PreferredPropertyObjectCreationHandling ?? options.PreferredObjectCreationHandling) != JsonObjectCreationHandling.Populate
            || property.PropertyType.IsValueType)
        {
            return false;
        }

        var value = options.GetTypeInfo(property.PropertyType);
        return value.Kind switch
        {
            JsonTypeInfoKind.Object => true,
            // A collection the serializer adds to: one it can create (a read-only interface, an array or an immutable
            // collection it cannot), or one it adds to though it cannot create one.
            JsonTypeInfoKind.Enumerable or JsonTypeInfoKind.Dictionary => value.CreateObject is not null || AddedTo(property.PropertyType),
            _ => false,
        };
    }

    /// <summary>
    /// A collection the serializer fills through what it can add to (a list, a collection, a dictionary, a queue or a
    /// stack), whether or not it could create one. Never an array or an immutable collection, which also implement the
    /// list interfaces.
    /// </summary>
    private static bool AddedTo(Type type)
    {
        if (type.IsArray || type.Namespace == "System.Collections.Immutable")
        {
            return false;
        }

        if (typeof(System.Collections.IList).IsAssignableFrom(type) || typeof(System.Collections.IDictionary).IsAssignableFrom(type))
        {
            return true;
        }

        foreach (var contract in type.GetInterfaces())
        {
            if (contract.IsGenericType && contract.GetGenericTypeDefinition() is var definition
                && (definition == typeof(ICollection<>) || definition == typeof(IDictionary<,>)))
            {
                return true;
            }
        }

        for (var t = type; t is not null; t = t.BaseType)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() is var definition
                && (definition == typeof(Queue<>) || definition == typeof(Stack<>) || definition == typeof(ConcurrentQueue<>) || definition == typeof(ConcurrentStack<>)))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record Member(JsonPropertyInfo Property, NullabilityInfo? Nullability, bool Read);
}
