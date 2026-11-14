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
        var manifest = await StrataRec.Hosting.Manifests.ManifestBuilder.BuildAsync(workspace, "home_feed", "local", store, diagnostics);
        Assert.NotNull(manifest);
        Assert.Empty(diagnostics.Items);

        var loaded = await StrataRec.Hosting.Manifests.ManifestBuilder.LoadAsync(manifest!, "sha256:test", store, diagnostics);
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
        Assert.Null(await StrataRec.Hosting.Manifests.ManifestBuilder.LoadAsync(tampered, "sha256:tampered", store, errors));
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

            var error = Assert.Single(diagnostics.Items);
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

            var error = Assert.Single(diagnostics.Items);
            Assert.Equal("ARTIFACT_SCHEMA", error.Code);
            Assert.Contains("arm", error.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
