using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;

namespace StrataRec.IntegrationTests;

/// <summary>Edits the bindings of a copied sample workspace.</summary>
internal static class SampleBindings
{
    /// <summary>Replaces the binding of feature source <paramref name="source"/> in <c>bindings/local.jsonc</c>.</summary>
    public static void Rebind(string root, string source, string binding)
    {
        var path = Path.Combine(root, "bindings", "local.jsonc");
        var document = (JsonObject)Jsonc.Parse(File.ReadAllBytes(path), path)!;
        var sources = (JsonObject)document["sources"]!;
        Assert.True(sources.ContainsKey(source), $"the sample binds no source '{source}'");
        sources[source] = JsonNode.Parse(binding);
        File.WriteAllText(path, document.ToJsonString());
    }
}
