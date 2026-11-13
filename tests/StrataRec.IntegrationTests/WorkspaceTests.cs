using StrataRec.Artifacts.Model;
using StrataRec.Hosting.Workspace;

namespace StrataRec.IntegrationTests;

public sealed class WorkspaceTests
{
    [Fact]
    public void SampleWorkspaceLoadsWithoutDiagnostics()
    {
        var diagnostics = new DiagnosticBag();
        var workspace = ConfigWorkspace.Load(Samples.HomeFeed, diagnostics);

        Assert.Empty(diagnostics.Items);
        Assert.Equal(new[] { "home_feed" }, workspace.Pipelines.Keys);
        Assert.Equal(3, workspace.Experiments.Count);
        Assert.StartsWith("sha256:", workspace.Pipelines["home_feed"].ContentId);

        var request = workspace.CreateCompileRequest("home_feed", "local", diagnostics);
        Assert.Empty(diagnostics.Items);
        Assert.NotNull(request);
        Assert.Equal(3, request!.Experiments.Count);
        Assert.Equal(3, workspace.ArmRanges("home_feed", "local").Count);
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
