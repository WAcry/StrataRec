using StrataRec.Artifacts.Model;
using StrataRec.Hosting.Workspace;

namespace StrataRec.IntegrationTests;

public sealed class WorkspaceTests
{
    [Fact]
    public void SampleWorkspaceLoadsWithoutDiagnostics()
    {
        var diagnostics = new DiagnosticBag();
        var workspace = ConfigWorkspace.Load(HomeFeedTestHost.SampleRoot(SampleTiming.AsWritten), diagnostics);

        Assert.Empty(diagnostics.Items);
        Assert.Equal(new[] { "home_feed" }, workspace.Pipelines.Keys);
        Assert.Equal(3, workspace.Experiments.Count);
        Assert.StartsWith("sha256:", workspace.Pipelines["home_feed"].ContentId);

        var request = workspace.CreateCompileRequest("home_feed", "local", diagnostics);
        Assert.Empty(diagnostics.Items);
        Assert.NotNull(request);
        Assert.Equal(3, request!.Experiments.Count);
        Assert.Equal(new[] { "fusion", "params", "rerank" }, request.Experiments.Select(e => e.Layer).Order());
    }

    [Fact]
    public void AssemblyErrorsAbortTheRequestInsteadOfDroppingTheExperiment()
    {
        var diagnostics = new DiagnosticBag();
        var workspace = ConfigWorkspace.Load(HomeFeedTestHost.SampleRoot(SampleTiming.AsWritten), diagnostics);
        var request = workspace.CreateCompileRequest("home_feed", "local", diagnostics,
            extraExperiments: new Dictionary<string, string> { ["exp_missing"] = "rerank" });

        Assert.Null(request);
        Assert.Equal("EXPERIMENT_UNKNOWN", Assert.Single(diagnostics.Items).Code);
    }

    [Fact]
    public async Task ManifestRoundTripsAndRejectsMislabeledArtifacts()
    {
        var diagnostics = new DiagnosticBag();
        var workspace = ConfigWorkspace.Load(HomeFeedTestHost.SampleRoot(SampleTiming.AsWritten), diagnostics);
        var store = new StrataRec.Artifacts.Storage.InMemoryArtifactStore();
        var manifest = await StrataRec.Hosting.Manifests.ManifestBuilder.BuildAsync(workspace, "home_feed", "local", Tool.Environment with { BaseDirectory = workspace.Root }, store, diagnostics, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(manifest);
        Assert.Empty(diagnostics.Items);

        var loaded = await StrataRec.Hosting.Manifests.ManifestBuilder.LoadAsync(manifest!, "sha256:test", store, diagnostics, TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.Experiments.Count);

        // An experiment entry pointing at another experiment's (valid) blob.
        var swapped = new Dictionary<string, ManifestExperiment>(manifest!.Experiments)
        {
            ["exp_mmr_diversity"] = new ManifestExperiment
            {
                Artifact = manifest.Experiments["exp_quality_weight"].Artifact,
                Layer = "rerank",
            },
        };
        var tampered = new ReleaseManifest
        {
            PipelineId = manifest.PipelineId,
            Environment = manifest.Environment,
            Artifacts = manifest.Artifacts,
            Experiments = swapped,
        };
        var errors = new DiagnosticBag();
        Assert.Null(await StrataRec.Hosting.Manifests.ManifestBuilder.LoadAsync(tampered, "sha256:tampered", store, errors, TestContext.Current.CancellationToken));
        var error = Assert.Single(errors.Items);
        Assert.Equal("MANIFEST_IDENTITY", error.Code);
        Assert.Equal("/experiments/exp_mmr_diversity/artifact", error.Pointer);
    }

    [Fact]
    public void FileNameMustMatchId()
    {
        var root = Directory.CreateTempSubdirectory("stratarec-ws-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "experiments"));
            File.WriteAllText(Path.Combine(root, "experiments", "exp_a.jsonc"), """
                { "experiment_id": "exp_b", "pipeline_id": "p", "owner": "o", "description": "d", "expires_at": "2099-01-01", "arms": { "control": {} } }
                """);

            var diagnostics = new DiagnosticBag();
            ConfigWorkspace.Load(root, diagnostics);

            var error = Assert.Single(diagnostics.Items, d => d.Code != "WORKSPACE_NO_PIPELINE"); // the file has its one error
            Assert.Equal("FILE_NAME_MISMATCH", error.Code);
            Assert.Equal("experiments/exp_a.jsonc", error.Artifact);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SchemaErrorsPointIntoTheFile()
    {
        var root = Directory.CreateTempSubdirectory("stratarec-ws-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "experiments"));
            File.WriteAllText(Path.Combine(root, "experiments", "exp_a.jsonc"), """
                // a comment is fine
                { "experiment_id": "exp_a", "pipeline_id": "p", "owner": "o", "description": "d", "expires_at": "2099-01-01",
                  "arms": { "control": {} }, "arm": {} }
                """);

            var diagnostics = new DiagnosticBag();
            ConfigWorkspace.Load(root, diagnostics);

            var error = Assert.Single(diagnostics.Items, d => d.Code != "WORKSPACE_NO_PIPELINE"); // the file has its one error
            Assert.Equal("ARTIFACT_SCHEMA", error.Code);
            Assert.Contains("arm", error.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Two files that define one artifact (its id with either extension) are one error, at the later file, naming both,
    /// for every kind the workspace reads: assigning by id kept the last silently, so a workspace validated while the
    /// other file was ignored. The policy is read as <c>policy.json</c> too, as every other file is.
    /// </summary>
    [Theory]
    [InlineData("pipelines/home_feed.jsonc", "pipeline 'home_feed'", "/pipeline_id")]
    [InlineData("catalog/home_feed.jsonc", "catalog 'home_feed'", "/catalog_id")]
    [InlineData("bindings/local.jsonc", "the bindings of environment 'local'", "/environment")]
    [InlineData("experiments/exp_quality_weight.jsonc", "experiment 'exp_quality_weight'", "/experiment_id")]
    [InlineData("releases/home_feed.local.jsonc", "the release of pipeline 'home_feed' in environment 'local'", "/environment")]
    [InlineData("tables/boost.jsonc", "table 'boost'", "/table_id")]
    [InlineData("policy.jsonc", "the policy", "")]
    public void TwoFilesOfOneArtifact_AreAnError_NamingBoth(string file, string what, string pointer)
    {
        var root = CopyOfSample();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "tables"));
            File.WriteAllText(Path.Combine(root, "tables", "boost.jsonc"), """{ "table_id": "boost", "key_type": "int64", "value_type": "float64", "entries": { "1": 0.5 } }""");
            var json = file[..^1];
            File.Copy(Path.Combine(root, file), Path.Combine(root, json));

            var diagnostics = new DiagnosticBag();
            ConfigWorkspace.Load(root, diagnostics);

            var error = Assert.Single(diagnostics.Items);
            Assert.Equal(("ARTIFACT_DUPLICATE", file, pointer, true), (error.Code, error.Artifact, error.Pointer, error.IsError));
            Assert.Equal($"Both {json} and {file} define {what}; only one file may.", error.Message);
            Assert.Equal($"keep one of the two files and delete the other ({json} or {file})", error.Hint);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The assessment's case: <c>pipelines/home_feed.json</c>, whose final_top_k is out of range, beside the sample's
    /// <c>.jsonc</c>. Validate, build and a host read the one workspace index, and each refuses the pair; validate used to
    /// pass on the <c>.jsonc</c>, and report PARAM_OUT_OF_RANGE only once the <c>.jsonc</c> was removed.
    /// </summary>
    [Fact]
    public async Task ADuplicatePipeline_FailsValidateBuildAndTheHost()
    {
        var root = CopyOfSample();
        var store = Directory.CreateTempSubdirectory("stratarec-store-").FullName;
        try
        {
            var pipeline = File.ReadAllText(Path.Combine(root, "pipelines", "home_feed.jsonc"));
            File.WriteAllText(Path.Combine(root, "pipelines", "home_feed.json"), pipeline.Replace("\"final_top_k\": 10,", "\"final_top_k\": 1000,", StringComparison.Ordinal));

            var validate = new StringWriter();
            Assert.Equal(1, StrataRec.CommandLine.Commands.ValidateCommand.Run(Tool.Environment, root, "local", null, "text", warningsAsErrors: false, validate));
            Assert.Contains($"{root}/pipelines/home_feed.jsonc:", validate.ToString());
            Assert.Contains("error ARTIFACT_DUPLICATE /pipeline_id: Both pipelines/home_feed.json and pipelines/home_feed.jsonc define pipeline 'home_feed'", validate.ToString());

            var build = new StringWriter();
            Assert.Equal(1, await StrataRec.CommandLine.Commands.BuildCommand.RunAsync(
                Tool.Environment, root, "local", null, store, null, null, TextWriter.Null, CancellationToken.None, build));
            Assert.Contains("error ARTIFACT_DUPLICATE", build.ToString());

            await using var engine = new StrataRec.Hosting.RecEngine(Tool.Environment with { BaseDirectory = root });
            var host = StrataRec.Hosting.ServiceCollectionExtensions.LoadWorkspace(engine, root, "local");
            Assert.Equal("ARTIFACT_DUPLICATE", Assert.Single(host.Items, d => d.IsError).Code);
            Assert.Empty(engine.PipelineIds());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(store, recursive: true);
        }
    }

    /// <summary>
    /// A directory without a pipeline is an error of validate and build, naming the directory and the layout expected:
    /// validate reported 0 errors and 0 warnings, and a host started on it serving nothing. <c>stratarec init</c>'s
    /// workspace validates (InitCommandTests).
    /// </summary>
    [Fact]
    public async Task AWorkspaceWithoutAPipeline_FailsValidateAndBuild()
    {
        var root = Directory.CreateTempSubdirectory("stratarec-ws-").FullName;
        var store = Directory.CreateTempSubdirectory("stratarec-store-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "pipelines")); // empty, as an empty directory is

            var validate = new StringWriter();
            Assert.Equal(1, StrataRec.CommandLine.Commands.ValidateCommand.Run(Tool.Environment, root, "local", null, "json", warningsAsErrors: false, validate));
            using (var report = System.Text.Json.JsonDocument.Parse(validate.ToString()))
            {
                var error = Assert.Single(report.RootElement.GetProperty("diagnostics").EnumerateArray());
                Assert.Equal("WORKSPACE_NO_PIPELINE", error.GetProperty("code").GetString());
                Assert.Equal($"The workspace {root} has no pipeline: {root}/pipelines holds no .json or .jsonc file.", error.GetProperty("message").GetString());
                Assert.StartsWith("add pipelines/<pipeline_id>.jsonc", error.GetProperty("hint").GetString(), StringComparison.Ordinal);
            }

            var build = new StringWriter();
            Assert.Equal(1, await StrataRec.CommandLine.Commands.BuildCommand.RunAsync(
                Tool.Environment, root, "local", "home_feed", store, null, null, TextWriter.Null, CancellationToken.None, build));
            Assert.Contains("error WORKSPACE_NO_PIPELINE", build.ToString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(store, recursive: true);
        }
    }

    /// <summary>A copy of the sample as written, in a directory of its own.</summary>
    private static string CopyOfSample()
    {
        var root = Directory.CreateTempSubdirectory("stratarec-ws-").FullName;
        var sample = HomeFeedTestHost.SampleRoot(SampleTiming.AsWritten);
        foreach (var file in Directory.GetFiles(sample, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(root, Path.GetRelativePath(sample, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return root;
    }
}
