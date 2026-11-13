using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;

namespace StrataRec.IntegrationTests;

/// <summary>Edits the bindings of a copied sample workspace.</summary>
internal static class SampleBindings
{
    /// <summary>Replaces the binding of feature source <paramref name="source"/> in <c>bindings/local.jsonc</c>.</summary>
    /// <remarks>
    /// A fetched binding must say how long its calls may take: without <c>timeout_ms</c> it gets the framework's default of
    /// 50 ms, which a slow machine can hit even with an in-memory connector (an answer after the bound is a timeout
    /// however it arrived), and the test would race the clock (see <see cref="HomeFeedTestHost"/>).
    /// </remarks>
    public static void Rebind(string root, string source, string binding)
    {
        var path = Path.Combine(root, "bindings", "local.jsonc");
        var document = (JsonObject)Jsonc.Parse(File.ReadAllBytes(path), path)!;
        var sources = (JsonObject)document["sources"]!;
        Assert.True(sources.ContainsKey(source), $"the sample binds no source '{source}'");
        var replacement = (JsonObject)JsonNode.Parse(binding)!;
        Assert.True(replacement.ContainsKey("timeout_ms") || (replacement["mode"]?.GetValue<string>() ?? "fetch") != "fetch",
            $"give the fetched binding of '{source}' a timeout_ms: {HomeFeedTestHost.UntimedMs}, or the budget the test is about");
        sources[source] = replacement;
        File.WriteAllText(path, document.ToJsonString());
    }

    /// <summary>Replaces the binding of model <paramref name="model"/> in <c>bindings/local.jsonc</c>.</summary>
    public static void RebindModel(string root, string model, string binding)
    {
        var path = Path.Combine(root, "bindings", "local.jsonc");
        var document = (JsonObject)Jsonc.Parse(File.ReadAllBytes(path), path)!;
        var models = (JsonObject)document["models"]!;
        Assert.True(models.ContainsKey(model), $"the sample binds no model '{model}'");
        models[model] = JsonNode.Parse(binding);
        File.WriteAllText(path, document.ToJsonString());
    }
}
