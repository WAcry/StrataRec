using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StrataRec.Pipeline.Planning;

/// <summary>
/// Content hash of a JSON definition, used to deduplicate node variants (design 12.3).
/// </summary>
/// <remarks>TODO(lead): switch to <c>StrataRec.Artifacts.Json.ContentHash</c> (full RFC 8785) once merged.</remarks>
internal static class CanonicalJson
{
    public static string Hash(JsonNode node)
    {
        var builder = new StringBuilder();
        Write(node, builder);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static void Write(JsonNode? node, StringBuilder builder)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;
            case JsonObject obj:
                var keys = new List<string>(obj.Count);
                foreach (var (key, _) in obj)
                {
                    keys.Add(key);
                }

                keys.Sort(StringComparer.Ordinal);
                builder.Append('{');
                for (var i = 0; i < keys.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    builder.Append(JsonSerializer.Serialize(keys[i])).Append(':');
                    Write(obj[keys[i]], builder);
                }

                builder.Append('}');
                break;
            case JsonArray array:
                builder.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    Write(array[i], builder);
                }

                builder.Append(']');
                break;
            default:
                builder.Append(node.ToJsonString());
                break;
        }
    }
}
