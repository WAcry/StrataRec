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
                     "pipelines/p.jsonc", "pipelines/a.jsonc", "pipelines/a b.jsonc", "experiments/e.json", "catalog/c.jsonc", "bindings/local.jsonc", "tables/t.jsonc", "policy.jsonc",
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
    [InlineData("pipeline:a b", "pipelines/a b.jsonc")] // an id may contain spaces (review 32 #7: it named pipelines/a.jsonc)
    [InlineData("pipelines/a b.jsonc", "pipelines/a b.jsonc")]
    [InlineData("pipeline:a", "pipelines/a.jsonc")]
    public void ALabel_NamesItsFile(string label, string file)
    {
        Assert.Equal(file, ConfigWorkspace.FileOf(_root, label, out var exact));
        Assert.True(exact);
    }

    [Theory]
    [InlineData("pipeline:p with exp/a", "pipelines/p.jsonc")]
    [InlineData("pipeline:p with exp/a + exp2/b", "pipelines/p.jsonc")]
    [InlineData("pipeline:a b with exp/a + exp2/b", "pipelines/a b.jsonc")]
    public void ACombinationsLabel_NamesThePipelinesFile_NotExactly(string label, string file)
    {
        // The arms of a combination: the file is right, the label says more than it.
        Assert.Equal(file, ConfigWorkspace.FileOf(_root, label, out var exact));
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
    [InlineData("pipeline:a c")] // never the file of a prefix of the id
    [InlineData("pipeline:a with b")] // "with b" is not an arm combination: no arm id without '/'
    [InlineData("pipeline:a with exp/x y")]
    public void ALabelWithoutAFileInTheWorkspace_HasNone(string label)
    {
        Assert.Null(ConfigWorkspace.FileOf(_root, label, out _));
    }
}
