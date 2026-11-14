using System.Collections;
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
/// The first null in a bound value where its model says an element cannot be null: an entry of a map or an item of a
/// list whose element type is not nullable (<c>"outputs": { "x": null }</c> for a <c>Dictionary&lt;string, Output&gt;</c>).
/// The serializer rejects a null member itself (<c>RespectNullableAnnotations</c>) but not a null element, which then
/// reached the code that read it as an exception without a location.
/// </summary>
/// <remarks>
/// <para>
/// Walks what the document wrote: the members the serializer fills (a setter, a constructor parameter, or populated),
/// never a computed property (one returning <c>this</c> would recurse; one that throws would throw here), each object
/// once. Element nullability comes from the member's annotations, so a model member added later is checked without
/// being listed; at the root, and where annotations are unknown, a null element is let through (a patch's null deletes).
/// </para>
/// <para>
/// Runs when a document or a config is bound, never on a request path. Members are looked up once per type, and the
/// pointer and the message are only formatted for the error: the walk itself keeps a stack of steps.
/// </para>
/// </remarks>
internal sealed class NullElements
{
    private static readonly ConcurrentDictionary<(Type, JsonSerializerOptions), Member[]> Members = new();

    private readonly JsonSerializerOptions _options;
    private readonly List<Step> _path = [];
    private HashSet<object>? _visited;

    private NullElements(JsonSerializerOptions options) => _options = options;

    public static JsonBindingError? Find(object value, JsonSerializerOptions options) => new NullElements(options).Value(value, declared: null);

    /// <param name="declared">The annotations of the member or element <paramref name="value"/> is; null when unknown.</param>
    private JsonBindingError? Value(object value, NullabilityInfo? declared)
    {
        if (value is string or JsonNode or JsonElement || value.GetType() is { IsPrimitive: true } or { IsEnum: true })
        {
            return null;
        }

        if (!value.GetType().IsValueType && !(_visited ??= new HashSet<object>(ReferenceEqualityComparer.Instance)).Add(value))
        {
            return null; // walked already: shared, or a cycle through a default
        }

        switch (value)
        {
            case IDictionary map:
                var entryType = declared?.GenericTypeArguments is { Length: 2 } pair ? pair[1] : null;
                foreach (DictionaryEntry entry in map)
                {
                    _path.Add(new Step(null, entry.Key, -1));
                    var error = Element(entry.Value, entryType);
                    _path.RemoveAt(_path.Count - 1);
                    if (error is not null)
                    {
                        return error;
                    }
                }

                return null;
            case IEnumerable sequence:
                var itemType = declared?.ElementType ?? (declared?.GenericTypeArguments is { Length: 1 } single ? single[0] : null);
                if (itemType?.Type is { IsPrimitive: true } or { IsEnum: true })
                {
                    return null; // nothing in it can be null, and nothing to walk
                }

                var index = 0;
                foreach (var item in sequence)
                {
                    _path.Add(new Step(null, null, index++));
                    var error = Element(item, itemType);
                    _path.RemoveAt(_path.Count - 1);
                    if (error is not null)
                    {
                        return error;
                    }
                }

                return null;
            default:
                return Object(value);
        }
    }

    private JsonBindingError? Object(object value)
    {
        foreach (var member in Members.GetOrAdd((value.GetType(), _options), static key => Describe(key.Item1, key.Item2)))
        {
            // A null member is the serializer's to reject (or allowed); only what it holds is walked.
            if (member.Get(value) is not { } held)
            {
                continue;
            }

            // Extension data holds the object's other members: its entries are at the object's own level.
            if (!member.ExtensionData)
            {
                _path.Add(new Step(member.Name, null, -1));
            }

            var error = Value(held, member.Nullability);
            if (!member.ExtensionData)
            {
                _path.RemoveAt(_path.Count - 1);
            }

            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }

    private JsonBindingError? Element(object? value, NullabilityInfo? declared)
    {
        if (value is not null)
        {
            return Value(value, declared);
        }

        // Nullable<T> and annotated references may be null; an element whose annotations are unknown is let through.
        if (declared is null || declared.Type.IsValueType || declared.ReadState != NullabilityState.NotNull)
        {
            return null;
        }

        var pointer = new StringBuilder();
        foreach (var step in _path)
        {
            pointer.Append('/').Append(JsonPointer.Escape(Token(step)));
        }

        var last = _path[^1];
        var described = last.Index < 0
            ? $"'{Token(last)}'"
            : _path.Count > 1 ? $"Item {last.Index} of '{Token(_path[^2])}'" : $"Item {last.Index}";
        return new JsonBindingError(pointer.ToString(), $"{described} must be {JsonBindingErrors.Phrase(_options, declared.Type)}, got null.", null);
    }

    /// <summary>A step as the document spells it: a member's JSON name, a map's key as the serializer writes it, an index.</summary>
    private string Token(Step step)
    {
        if (step.Member is { } name)
        {
            return name;
        }

        if (step.Index >= 0)
        {
            return step.Index.ToString(CultureInfo.InvariantCulture);
        }

        if (step.Key is string key)
        {
            return key;
        }

        // A key of another type (a date, a number, a pointer) is written by its converter, not by ToString.
        var written = JsonSerializer.SerializeToElement(step.Key, step.Key!.GetType(), _options);
        return written.ValueKind == JsonValueKind.String ? written.GetString()! : written.GetRawText();
    }

    private static Member[] Describe(Type type, JsonSerializerOptions options)
    {
        var info = options.GetTypeInfo(type);
        if (info.Kind != JsonTypeInfoKind.Object)
        {
            return [];
        }

        var populated = info.PreferredPropertyObjectCreationHandling ?? options.PreferredObjectCreationHandling;
        var context = new NullabilityInfoContext();
        var members = new List<Member>(info.Properties.Count);
        foreach (var property in info.Properties)
        {
            // Only what reading a document fills: a get-only property the serializer writes but never reads is code's.
            var filled = property.Set is not null
                || property.AssociatedParameter is not null
                || (property.ObjectCreationHandling ?? populated) == JsonObjectCreationHandling.Populate;
            if (property.Get is null || !filled)
            {
                continue;
            }

            var nullability = property.AttributeProvider switch
            {
                PropertyInfo p => context.Create(p),
                FieldInfo f => context.Create(f),
                _ => null,
            };
            members.Add(new Member(property.Name, property.Get, nullability, property.IsExtensionData));
        }

        return members.ToArray();
    }

    /// <summary>One step of the path: a member's JSON name, a map's key, or an item's index (−1 when not an item).</summary>
    private readonly record struct Step(string? Member, object? Key, int Index);

    private sealed record Member(string Name, Func<object, object?> Get, NullabilityInfo? Nullability, bool ExtensionData);
}
