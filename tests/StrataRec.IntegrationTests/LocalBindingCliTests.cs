using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Cli.Commands;

namespace StrataRec.IntegrationTests;

/// <summary><c>stratarec validate</c> and <c>stratarec schema bindings</c> on local feature source bindings (design 6.6).</summary>
public sealed class LocalBindingCliTests
{
    [Fact]
    public void Validate_ReportsEveryProblemOfALocalBinding_WithItsPointerAndAHint()
    {
        var root = HomeFeedTestHost.LoadModified(root => SampleBindings.Rebind(root, "author_store", """
            { "mode": "local", "max_bytes": "16MB", "timeout_ms": 20, "bootstrap": { "source": "snapshot_files@1", "url": "fixtures/author_snapshot" } }
            """)).Root;
        var output = new StringWriter();

        Assert.Equal(1, ValidateCommand.Run(root, "local", null, "json", warningsAsErrors: false, output));

        using var report = JsonDocument.Parse(output.ToString());
        var errors = report.RootElement.GetProperty("diagnostics").EnumerateArray()
            .Where(d => d.GetProperty("severity").GetString() == "error")
            .Select(d => (Code: d.GetProperty("code").GetString(), Pointer: d.GetProperty("pointer").GetString(), Hint: d.TryGetProperty("hint", out var h) ? h.GetString() : null))
            .ToList();
        Assert.Equal(
            new[]
            {
                ("LOCAL_MAX_BYTES", "/sources/author_store/max_bytes"),
                ("SOURCE_SETTING_WRONG_MODE", "/sources/author_store/timeout_ms"),
                ("UPDATE_SOURCE_CONFIG", "/sources/author_store/bootstrap/url"),
            },
            errors.Select(e => (e.Code!, e.Pointer!)).Order());
        Assert.All(errors, e => Assert.False(string.IsNullOrEmpty(e.Hint)));
    }

    [Fact]
    public void Validate_DoesNotLoadTables_SoTheSampleValidatesWithoutReadingItsSnapshot()
    {
        var root = HomeFeedTestHost.LoadModified(root => Directory.Delete(Path.Combine(root, "fixtures", "author_snapshot"), recursive: true)).Root;

        Assert.Equal(0, ValidateCommand.Run(root, "local", null, "text", warningsAsErrors: false, TextWriter.Null));
    }

    [Fact]
    public void BindingsSchema_DescribesTheModesAndTheirSettings_AndLeavesSourceSettingsOpen()
    {
        var output = new StringWriter();

        Assert.Equal(0, SchemaCommand.Run("bindings", null, output, TextWriter.Null));

        var schema = JsonNode.Parse(output.ToString())!;
        var binding = FindObjectWithProperty(schema, "max_bytes")!;
        var properties = binding["properties"]!;
        Assert.Equal("""["fetch","local",null]""", properties["mode"]!["enum"]!.ToJsonString());
        Assert.Equal("^[0-9]+(B|KiB|MiB|GiB|TiB)$", properties["max_bytes"]!["pattern"]!.GetValue<string>());
        Assert.Contains("Mode local", properties["bootstrap"]!["description"]!.GetValue<string>());
        Assert.Null(binding["additionalProperties"]); // connector settings
        Assert.Null(properties["bootstrap"]!["additionalProperties"]); // the update source's own settings
    }

    private static JsonObject? FindObjectWithProperty(JsonNode? node, string property)
    {
        switch (node)
        {
            case JsonObject obj when obj["properties"] is JsonObject props && props.ContainsKey(property):
                return obj;
            case JsonObject obj:
                foreach (var (_, child) in obj)
                {
                    if (FindObjectWithProperty(child, property) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            case JsonArray array:
                foreach (var child in array)
                {
                    if (FindObjectWithProperty(child, property) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            default:
                return null;
        }
    }
}
