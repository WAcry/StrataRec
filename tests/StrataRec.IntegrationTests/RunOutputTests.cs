using System.Text.Json;
using StrataRec.CommandLine;
using StrataRec.Hosting;
using StrataRec.Testing;

namespace StrataRec.IntegrationTests;

/// <summary>
/// <c>stratarec run</c> writes the document a service answers with (<see cref="RecResultJson"/>), and the CLI's entry point
/// (<see cref="StrataRecCommandLine"/>) writes where its caller says, so a host binary runs the commands with its own
/// components and a test reads what they wrote.
/// </summary>
public sealed class RunOutputTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("stratarec-run-output-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Run_WritesTheResultDocument_ItemsWithTheirColumnsApart()
    {
        var (code, json, error) = await Run("""{ "user_id": 1001, "session_id": "s-1", "surface": "home" }""", "--trace");

        Assert.True(code == 0, error);
        var root = json.RootElement;
        Assert.Equal(
            new[] { "request_id", "succeeded", "served_by", "manifest", "experiments", "items", "elapsed_ms", "column_producers", "trace" },
            root.EnumerateObject().Select(m => m.Name));
        Assert.Equal(("req-out", true, "home_feed"), (root.GetProperty("request_id").GetString(), root.GetProperty("succeeded").GetBoolean(), root.GetProperty("served_by").GetString()));
        Assert.Equal(new[] { "assigned", "triggered", "ignored" }, root.GetProperty("experiments").EnumerateObject().Select(m => m.Name));

        var items = root.GetProperty("items").EnumerateArray().ToList();
        Assert.NotEmpty(items);
        Assert.Equal(Enumerable.Range(1, items.Count), items.Select(i => i.GetProperty("rank").GetInt32()));
        Assert.All(items, item =>
        {
            Assert.Equal(new[] { "rank", "id", "columns" }, item.EnumerateObject().Select(m => m.Name));
            Assert.Equal(JsonValueKind.Number, item.GetProperty("id").ValueKind); // an int64 entity id, written exactly
            Assert.Equal(new[] { "category", "primary_source", "score" }, item.GetProperty("columns").EnumerateObject().Select(m => m.Name).Order(StringComparer.Ordinal));
        });
        Assert.Equal("succeeded", root.GetProperty("trace")[0].GetProperty("outcome").GetString());
        Assert.True(root.GetProperty("column_producers").TryGetProperty("score", out _));
    }

    [Fact]
    public async Task Run_ARequestTheEngineRejects_WritesItsKind_AndExits1()
    {
        var (code, json, _) = await Run("""{ "user_id": 1001, "session_id": "s-1", "surface": "home" }""", "--arms", "exp_gone/treatment");

        Assert.Equal(1, code);
        var root = json.RootElement;
        Assert.False(root.GetProperty("succeeded").GetBoolean());
        Assert.Equal("invalid_request", root.GetProperty("error_kind").GetString());
        Assert.Contains("exp_gone/treatment", root.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, root.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task TheEntryPoint_WritesToTheWritersItIsGiven()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var validated = await StrataRecCommandLine.InvokeAsync(["validate", "-c", HomeFeedTestHost.SampleRoot(), "--format", "json"], Tool.Environment, output, error);
        Assert.Equal(0, validated);
        using (var report = JsonDocument.Parse(output.ToString()))
        {
            Assert.Equal(0, report.RootElement.GetProperty("errors").GetInt32());
        }

        output.GetStringBuilder().Clear();
        var unknown = await StrataRecCommandLine.InvokeAsync(["schema", "operator", "Nope@1"], Tool.Environment, output, error);
        Assert.Equal(2, unknown);
        Assert.Contains("Unknown operator 'Nope@1'", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, output.ToString());

        // The operators of the environment it was given: the stock tool's include StrataRec.Llm's.
        var listed = await StrataRecCommandLine.InvokeAsync(["operators", "--format", "json"], Tool.Environment, output, error);
        Assert.Equal(0, listed);
        using var operators = JsonDocument.Parse(output.ToString());
        Assert.Contains(operators.RootElement.GetProperty("operators").EnumerateArray(), o => o.GetProperty("id").GetString() == "LlmReranker@1");
    }

    private async Task<(int Code, JsonDocument Json, string Error)> Run(string inputs, params string[] options)
    {
        var request = Path.Combine(_directory, "request.json");
        await File.WriteAllTextAsync(request, $$"""
            { "pipeline_id": "home_feed", "request_id": "req-out", "inputs": {{inputs}}, "time_ms": 1767225600000 }
            """);
        var output = new StringWriter();
        var error = new StringWriter();
        string[] args = ["run", "-c", HomeFeedTestHost.SampleRoot(), "-r", request, "--deadline-ms", HomeFeedTestHost.UntimedMs.ToString(System.Globalization.CultureInfo.InvariantCulture), .. options];

        var code = await StrataRecCommandLine.InvokeAsync(args, Tool.Environment, output, error);

        return (code, JsonDocument.Parse(output.ToString()), error.ToString());
    }
}
