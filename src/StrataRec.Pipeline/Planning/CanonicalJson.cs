using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;

namespace StrataRec.Pipeline.Planning;

/// <summary>Content hash of a JSON definition (RFC 8785 + sha256), used to deduplicate node variants (design 12.3).</summary>
internal static class CanonicalJson
{
    public static string Hash(JsonNode node) => ContentHash.Compute(node);
}
