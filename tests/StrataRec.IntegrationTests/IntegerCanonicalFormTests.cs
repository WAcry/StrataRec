using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;
using StrataRec.CommandLine.Commands;
using StrataRec.Hosting.Plans;

namespace StrataRec.IntegrationTests;

public sealed class IntegerCanonicalFormTests
{
    private const string Pipeline = "pipelines/home_feed.jsonc";

    private const string TwoPow60 = "1152921504606846976.0";

    [Fact]
    public void Validate_ReportsTheInteger_AtTheValue()
    {
        var root = Workspace(TwoPow60);
        var output = new StringWriter();

        Assert.Equal(1, ValidateCommand.Run(Tool.Environment, root, "local", null, "json", warningsAsErrors: false, output));

        using var report = JsonDocument.Parse(output.ToString());
        var error = Assert.Single(report.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal(("PARAM_BAD_VALUE", "/params/big"), (error.GetProperty("code").GetString(), error.GetProperty("pointer").GetString()));
        Assert.Equal(
            $"Parameter 'big' must be an integer (int64) that RFC 8785 represents exactly, got the number {TwoPow60}, whose canonical form would be " +
            "1152921504606847000, a different integer; write integers beyond ±2^53 (9007199254740992) as strings, of a string type.",
            error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ALegalInteger_RunsAlike_FromTheWorkspaceAndFromItsManifest()
    {
        var root = Workspace("1e18");
        var store = ManifestStores.NewStore();
        var (id, _) = await ManifestStores.BuildAsync(root, store);
        var request = Path.Combine(root, "requests", "user_1001.json");

        var fromWorkspace = await Run(request, config: root);
        var fromManifest = await Run(request, manifest: id, store: store);

        Assert.Equal(1_000_000_000_000_000_000L, fromWorkspace);
        Assert.Equal(fromWorkspace, fromManifest);
    }

    [Fact]
    public async Task Diff_ReportsTheInteger_AsADiagnostic()
    {
        var output = new StringWriter();
        var errors = new StringWriter();

        Assert.Equal(2, await DiffCommand.RunAsync(Tool.Environment, HomeFeedTestHost.SampleRoot(), Workspace(TwoPow60), null, "local", null, Array.Empty<string>(), "json",
            output, CancellationToken.None, errors));

        Assert.Equal(string.Empty, output.ToString());
        var error = Assert.Single(ManifestStores.Errors(errors.ToString()));
        Assert.Equal(("PARAM_BAD_VALUE", "--to pipeline:home_feed", "/params/big"), (error.Code, error.Artifact, error.Pointer));
    }

    [Fact]
    public void APlanHoldingAValueRfc8785CannotHold_IsReportedWithItsSideAndPointer()
    {
        var host = HomeFeedTestHost.Load();
        var request = host.Request;
        request.Pipeline.ParamSchema["big"] = new ParamSpec { Type = "int64" };
        request.Pipeline.Params["big"] = JsonValue.Create(1152921504606846976L); // plain digits, never hashed on this way
        var compilation = host.Compile(request);
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));

        var thrown = Assert.Throws<PlanDiffCanonicalizationException>(() => PlanDiff.Compare(compilation.Plan!, compilation.Plan!));

        Assert.Equal(("from", "baseline", "/params/big"), (thrown.Side, thrown.Combination, thrown.Canonicalization.Pointer.ToString()));
        Assert.Equal("write integers beyond ±2^53 (9007199254740992) as strings", thrown.Canonicalization.Hint);
    }

    /// <summary>A copy of the sample (with absolute fixture paths, so a manifest built from it runs) with an int64 parameter <c>big</c> the output returns.</summary>
    private static string Workspace(string big) => ManifestStores.Workspace(root =>
    {
        ReplayTests.Edit(root, Pipeline, "\"quality_weight\": { \"type\": \"float64\", \"min\": 0, \"max\": 1 }",
            "\"quality_weight\": { \"type\": \"float64\", \"min\": 0, \"max\": 1 }, \"big\": { \"type\": \"int64\" }");
        ReplayTests.Edit(root, Pipeline, "\"quality_weight\": 0.3", $"\"quality_weight\": 0.3, \"big\": {big}");
        ReplayTests.Edit(root, Pipeline, "\"category\": \"col.item.category\",", "\"category\": \"col.item.category\", \"big\": \"param.big\",");
        var bindings = Path.Combine(root, "bindings", "local.jsonc");
        File.WriteAllText(bindings, File.ReadAllText(bindings)
            .Replace("\"fixtures/author_snapshot\"", JsonSerializer.Serialize(Path.Combine(root, "fixtures", "author_snapshot")), StringComparison.Ordinal)
            .Replace("\"fixtures/item_country_snapshot\"", JsonSerializer.Serialize(Path.Combine(root, "fixtures", "item_country_snapshot")), StringComparison.Ordinal));
    });

    private static async Task<long> Run(string request, string? config = null, string? manifest = null, string? store = null)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var code = await RunCommand.RunAsync(Tool.Environment, config, "local", request, Array.Empty<string>(), false, output, CancellationToken.None,
            errors: errors, manifest: manifest, store: store);
        Assert.True(code == 0, errors.ToString());
        using var result = JsonDocument.Parse(output.ToString());
        return result.RootElement.GetProperty("items")[0].GetProperty("columns").GetProperty("big").GetInt64();
    }
}
