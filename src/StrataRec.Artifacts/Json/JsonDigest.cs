using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Core.Hashing;

namespace StrataRec.Artifacts.Json;

/// <summary>
/// A digest of a JSON value that every value has, for telling two configurations apart: an identity derived from typed
/// configuration that compiled must not be able to fail. Not an id of content: <see cref="ContentHash"/> is, over the
/// RFC 8785 form, which a value it cannot hold exactly (an integer beyond 2^53, which a typed host may well pass as a
/// parameter) does not have.
/// </summary>
/// <remarks>
/// Members are taken in ordinal order of their names, so the order they were written in does not matter. A string is
/// its characters and a number its text: <c>1</c> and <c>1.0</c> differ, which makes two digests of the same
/// configuration written two ways, never one digest of two configurations. The same in every process of a build.
/// </remarks>
public static class JsonDigest
{
    /// <summary>The digest of <paramref name="node"/>, as 32 lowercase hex digits.</summary>
    public static string Of(JsonNode? node)
    {
        var hash = default(StableHash.Builder);
        try
        {
            Add(ref hash, node);
            return hash.Finish128().ToString("x32", CultureInfo.InvariantCulture);
        }
        finally
        {
            hash.Dispose();
        }
    }

    private static void Add(ref StableHash.Builder hash, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject members:
                hash.Add('{');
                hash.Add(members.Count);
                var names = new List<string>(members.Count);
                foreach (var member in members)
                {
                    names.Add(member.Key);
                }

                names.Sort(StringComparer.Ordinal);
                foreach (var name in names)
                {
                    hash.Add(name);
                    Add(ref hash, members[name]);
                }

                break;
            case JsonArray items:
                hash.Add('[');
                hash.Add(items.Count);
                foreach (var item in items)
                {
                    Add(ref hash, item);
                }

                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                hash.Add('"');
                hash.Add(value.GetValue<string>());
                break;
            case JsonValue value when JsonNodeUtilities.IsNonFiniteNumber(value, out var number):
                hash.Add('#');
                hash.Add(number.ToString(CultureInfo.InvariantCulture)); // JSON has no text for it; told apart all the same
                break;
            case JsonValue value when !JsonNodeUtilities.IsJsonNull(value):
                hash.Add('#');
                hash.Add(value.ToJsonString()); // a number as written, true or false
                break;
            default:
                hash.Add('0');
                break;
        }
    }
}
