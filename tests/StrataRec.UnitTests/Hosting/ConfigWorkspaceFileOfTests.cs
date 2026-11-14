using StrataRec.Hosting.Workspace;

namespace StrataRec.UnitTests.Hosting;

/// <summary>
/// <see cref="ConfigWorkspace.FileOf"/>: a diagnostic's artifact label names the workspace file it came from, by the
/// workspace's naming convention, and never a file outside the workspace.
/// </summary>
public sealed class ConfigWorkspaceFileOfTests : IDisposable
{
    private readonly string _parent = Directory.CreateTempSubdirectory("stratarec-file-of-").FullName;
    private readonly string _root;

    public ConfigWorkspaceFileOfTests()
    {
        _root = Path.Combine(_parent, "workspace");
        foreach (var file in new[]
                 {
                     "pipelines/p.jsonc", "experiments/e.json", "catalog/c.jsonc", "bindings/local.jsonc", "tables/t.jsonc", "policy.jsonc",
                     "releases/p.local.jsonc",
                 })
        {
            var path = Path.Combine(_root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{}");
        }

        File.WriteAllText(Path.Combine(_parent, "outside.jsonc"), "{}");
    }

    public void Dispose() => Directory.Delete(_parent, recursive: true);

    [Theory]
    [InlineData("pipeline:p", "pipelines/p.jsonc")]
    [InlineData("experiment:e", "experiments/e.json")] // .json when there is no .jsonc
    [InlineData("catalog:c", "catalog/c.jsonc")]
    [InlineData("bindings:local", "bindings/local.jsonc")]
    [InlineData("table:t", "tables/t.jsonc")]
    [InlineData("policy", "policy.jsonc")]
    [InlineData("releases/p.local.jsonc", "releases/p.local.jsonc")] // a label that is a path
    public void ALabel_NamesItsFile(string label, string file)
    {
        Assert.Equal(file, ConfigWorkspace.FileOf(_root, label, out var exact));
        Assert.True(exact);
    }

    [Fact]
    public void ALabelThatSaysMore_NamesTheFile_NotExactly()
    {
        // The arms of a combination: the file is right, the label says more than it.
        Assert.Equal("pipelines/p.jsonc", ConfigWorkspace.FileOf(_root, "pipeline:p with exp/a+exp2/b", out var exact));
        Assert.False(exact);
    }

    [Theory]
    [InlineData("pipeline:missing")]
    [InlineData("pipeline:../policy")]
    [InlineData("pipeline:")]
    [InlineData("manifest:abc")]
    [InlineData("workspace")]
    [InlineData("arms")]
    [InlineData("../outside.jsonc")]
    [InlineData("missing.jsonc")]
    public void ALabelWithoutAFileInTheWorkspace_HasNone(string label)
    {
        Assert.Null(ConfigWorkspace.FileOf(_root, label, out _));
    }
}
