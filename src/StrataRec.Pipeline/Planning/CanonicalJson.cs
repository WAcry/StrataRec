using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;

namespace StrataRec.Pipeline.Planning;

internal static class CanonicalJson
{
    public static string Hash(JsonNode node) => ContentHash.Compute(node);
}
