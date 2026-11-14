using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace StrataRec.Artifacts.Json;

/// <summary>How a value differs between two JSON documents (<see cref="JsonDiff"/>).</summary>
public enum JsonChangeKind : byte
{
    /// <summary>The member exists only in the second document.</summary>
    Added = 1,

    /// <summary>The member exists only in the first document.</summary>
    Removed,

    /// <summary>Both documents have a value here and the values differ.</summary>
    Changed,
}

/// <summary>One difference between two JSON documents.</summary>
/// <param name="Pointer">RFC 6901 pointer of the value that differs, relative to the compared roots.</param>
/// <param name="Kind">Whether the value was added, removed or changed.</param>
/// <param name="Before">The value in the first document: a detached copy; <see langword="null"/> when added (or JSON <c>null</c>).</param>
/// <param name="After">The value in the second document: a detached copy; <see langword="null"/> when removed (or JSON <c>null</c>).</param>
public sealed record JsonChange(string Pointer, JsonChangeKind Kind, JsonNode? Before, JsonNode? After);

/// <summary>
/// The semantic difference of two JSON documents, leaf by leaf (design 13.4 item 3): what a reviewer needs to see of a
/// configuration change, without the noise of a line diff (member order, whitespace, comments, <c>1</c> vs <c>1.0</c>).
/// </summary>
/// <remarks>
/// <para>
/// Objects are compared member by member, recursively, so a changed setting deep in a node's config is reported at its
/// own pointer. A member present on one side only is one change carrying its whole value: an added node is one entry,
/// not one per field. Every other value, arrays included, is compared as a whole, by RFC 8785 equality (numbers by
/// value, member order irrelevant): arrays have no stable element identity (an inserted element would shift every later
/// index), so element-wise changes would be guesses. A JSON <c>null</c> member is a value, distinct from an absent one.
/// </para>
/// <para>Changes come out in pointer order: members are visited in ordinal order of their names, whatever order the
/// documents list them in, so equal inputs always give the same report.</para>
/// </remarks>
public static class JsonDiff
{
    /// <summary>Compares <paramref name="before"/> with <paramref name="after"/>; an empty list means they are equal.</summary>
    /// <param name="before">The first document; <see langword="null"/> is JSON <c>null</c>.</param>
    /// <param name="after">The second document; <see langword="null"/> is JSON <c>null</c>.</param>
    /// <exception cref="JsonCanonicalizationException">A value has no canonical form (e.g. a non-finite number).</exception>
    public static List<JsonChange> Compare(JsonNode? before, JsonNode? after)
    {
        var changes = new List<JsonChange>();
        Compare(before, after, string.Empty, changes);
        return changes;
    }

    /// <summary>True when the two values are equal by RFC 8785: the same canonical form.</summary>
    public static bool ValueEquals(JsonNode? a, JsonNode? b) =>
        string.Equals(JsonCanonicalizer.CanonicalizeToString(a), JsonCanonicalizer.CanonicalizeToString(b), StringComparison.Ordinal);

    private static void Compare(JsonNode? before, JsonNode? after, string pointer, List<JsonChange> changes)
    {
        if (before is JsonObject left && after is JsonObject right)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var member in left)
            {
                names.Add(member.Key);
            }

            foreach (var member in right)
            {
                names.Add(member.Key);
            }

            foreach (var name in names)
            {
                var child = pointer + "/" + JsonPointer.Escape(name);
                var inLeft = JsonNodeUtilities.TryGetMember(left, name, out var l);
                var inRight = JsonNodeUtilities.TryGetMember(right, name, out var r);
                if (!inRight)
                {
                    changes.Add(new JsonChange(child, JsonChangeKind.Removed, l?.DeepClone(), null));
                }
                else if (!inLeft)
                {
                    changes.Add(new JsonChange(child, JsonChangeKind.Added, null, r?.DeepClone()));
                }
                else
                {
                    Compare(l, r, child, changes);
                }
            }

            return;
        }

        if (!ValueEquals(before, after))
        {
            changes.Add(new JsonChange(pointer, JsonChangeKind.Changed, before?.DeepClone(), after?.DeepClone()));
        }
    }
}
