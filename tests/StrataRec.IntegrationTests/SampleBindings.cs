using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;

namespace StrataRec.IntegrationTests;

internal static class SampleBindings
{
    public static void Rebind(string root, string source, string binding)
    {
        var replacement = (JsonObject)JsonNode.Parse(binding)!;
        Assert.True(replacement.ContainsKey("timeout_ms") || (replacement["mode"]?.GetValue<string>() ?? "fetch") != "fetch",
            $"give the fetched binding of '{source}' a timeout_ms: {HomeFeedTestHost.UntimedMs}, or the budget the test is about");
        Replace(root, "sources", source, replacement);
    }

    public static void RebindService(string root, string service, string binding) => Replace(root, "services", service, JsonNode.Parse(binding)!);

    public static void BindPipeline(string root, string pipeline, string binding, string bindings = "local")
    {
        var path = Path.Combine(root, "bindings", bindings + ".jsonc");
        var document = (JsonObject)Jsonc.Parse(File.ReadAllBytes(path), path)!;
        if (document["pipelines"] is not JsonObject pipelines)
        {
            document["pipelines"] = pipelines = new JsonObject();
        }

        pipelines[pipeline] = JsonNode.Parse(binding)!;
        File.WriteAllText(path, document.ToJsonString());
    }

    public static void CopyAs(string root, string bindings)
    {
        var path = Path.Combine(root, "bindings", "local.jsonc");
        var document = (JsonObject)Jsonc.Parse(File.ReadAllBytes(path), path)!;
        document["environment"] = bindings;
        File.WriteAllText(Path.Combine(root, "bindings", bindings + ".jsonc"), document.ToJsonString());
    }

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
