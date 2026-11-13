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
        var replacement = (JsonObject)JsonNode.Parse(binding)!;
        Assert.True(replacement.ContainsKey("timeout_ms") || (replacement["mode"]?.GetValue<string>() ?? "fetch") != "fetch",
            $"give the fetched binding of '{source}' a timeout_ms: {HomeFeedTestHost.UntimedMs}, or the budget the test is about");
        Replace(root, "sources", source, replacement);
    }

    /// <summary>Replaces the binding of model <paramref name="model"/> in <c>bindings/local.jsonc</c>.</summary>
    public static void RebindModel(string root, string model, string binding) => Replace(root, "models", model, JsonNode.Parse(binding)!);

    /// <summary>Replaces the binding of candidate source <paramref name="source"/> in <c>bindings/local.jsonc</c>.</summary>
    public static void RebindCandidateSource(string root, string source, string binding) =>
        Replace(root, "candidate_sources", source, JsonNode.Parse(binding)!);

    private static void Replace(string root, string section, string name, JsonNode binding)
    {
        var path = Path.Combine(root, "bindings", "local.jsonc");
        var document = (JsonObject)Jsonc.Parse(File.ReadAllBytes(path), path)!;
        var bindings = (JsonObject)document[section]!;
        Assert.True(bindings.ContainsKey(name), $"the sample binds nothing named '{name}' in {section}");
        bindings[name] = binding;
        File.WriteAllText(path, document.ToJsonString());
    }
}
