using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace StrataRec.Artifacts.Json;

/// <summary>
/// The first null in a bound value where its model says an element cannot be null: an entry of a map or an item of a
/// list whose element type is not nullable (<c>"outputs": { "x": null }</c> for a <c>Dictionary&lt;string, Output&gt;</c>).
/// The serializer rejects a null member itself (<c>RespectNullableAnnotations</c>) but not a null element, which then
/// reached the code that read it as an exception without a location.
/// </summary>
/// <remarks>
/// Walks the members the serializer binds (its <see cref="JsonTypeInfo"/>, so the JSON names and the members are the
/// serializer's own) and reads each collection's element nullability from the member's annotations, so a model member
/// added later is checked without being listed. Runs when a document or a config is bound, never on a request path; the
/// members of a type are looked up once.
/// </remarks>
internal static class NullElements
{
    private static readonly ConcurrentDictionary<(Type, JsonSerializerOptions), Member[]> Members = new();

    public static JsonBindingError? Find(object value, JsonSerializerOptions options) =>
        Object(value, string.Empty, options);

    private static JsonBindingError? Object(object value, string pointer, JsonSerializerOptions options)
    {
        foreach (var member in Members.GetOrAdd((value.GetType(), options), static key => Describe(key.Item1, key.Item2)))
        {
            // A null member is the serializer's to reject (or allowed); only what it holds is walked.
            if (member.Get(value) is { } held
                && Value(held, member.Nullability, pointer + "/" + JsonPointer.Escape(member.Name), member.Name, options) is { } error)
            {
                return error;
            }
        }

        return null;
    }

    /// <param name="declared">The annotations of the member or element <paramref name="value"/> is; null when unknown.</param>
    /// <param name="name">The member's JSON name or the map entry's key, for an item's message.</param>
    private static JsonBindingError? Value(object value, NullabilityInfo? declared, string pointer, string name, JsonSerializerOptions options)
    {
        switch (value)
        {
            case string or ValueType or JsonNode:
                return null;
            case IDictionary map:
                var entryType = declared?.GenericTypeArguments is { Length: 2 } pair ? pair[1] : null;
                foreach (DictionaryEntry entry in map)
                {
                    var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty;
                    if (Element(entry.Value, entryType, pointer + "/" + JsonPointer.Escape(key), key, $"'{key}'", options) is { } error)
                    {
                        return error;
                    }
                }

                return null;
            case IList list:
                var itemType = declared?.ElementType ?? (declared?.GenericTypeArguments is { Length: 1 } single ? single[0] : null);
                for (var i = 0; i < list.Count; i++)
                {
                    var index = i.ToString(CultureInfo.InvariantCulture);
                    if (Element(list[i], itemType, pointer + "/" + index, index, $"Item {index} of '{name}'", options) is { } error)
                    {
                        return error;
                    }
                }

                return null;
            default:
                return Object(value, pointer, options); // a type the serializer does not bind as an object has no members here
        }
    }

    /// <param name="described">How the message names the element: <c>'x'</c>, <c>Item 2 of 'xs'</c>.</param>
    private static JsonBindingError? Element(
        object? value, NullabilityInfo? declared, string pointer, string name, string described, JsonSerializerOptions options)
    {
        if (value is not null)
        {
            return Value(value, declared, pointer, name, options);
        }

        // Nullable<T> and annotated references may be null; an element whose annotations are unknown is let through.
        if (declared is null || declared.Type.IsValueType || declared.ReadState != NullabilityState.NotNull)
        {
            return null;
        }

        return new JsonBindingError(pointer, $"{described} must be {JsonBindingErrors.Phrase(options, declared.Type)}, got null.", null);
    }

    private static Member[] Describe(Type type, JsonSerializerOptions options)
    {
        var info = options.GetTypeInfo(type);
        if (info.Kind != JsonTypeInfoKind.Object)
        {
            return [];
        }

        var context = new NullabilityInfoContext();
        var members = new List<Member>(info.Properties.Count);
        foreach (var property in info.Properties)
        {
            if (property.Get is null)
            {
                continue;
            }

            var nullability = property.AttributeProvider switch
            {
                PropertyInfo p => context.Create(p),
                FieldInfo f => context.Create(f),
                _ => null,
            };
            members.Add(new Member(property.Name, property.Get, nullability));
        }

        return members.ToArray();
    }

    private sealed record Member(string Name, Func<object, object?> Get, NullabilityInfo? Nullability);
}
