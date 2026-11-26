using StrataRec.Artifacts.Model;
using StrataRec.Hosting.Manifests;

namespace StrataRec.IntegrationTests;

/// <summary><c>policy.jsonc</c> owners: review only.</summary>
public sealed class PolicyOwnersTests
{
    /// <summary>
    /// An arm that changes the safety filter's predicate is reviewed by its owners; one that only changes a parameter is
    /// not. The owners are reported, and they are no part of a release: the manifest names no policy.
    /// </summary>
    [Fact]
    public void AnArmTouchingAnOwnedPath_ReportsItsOwners()
    {
        var compilation = HomeFeedTestHost.LoadModified(root => InteractionWarningTests.Experiment(root, "exp_safety", "strict",
            """{ "nodes": { "safety_filter": { "config": { "predicate": "col.item.is_policy_safe ?? false" } } } }""")).Compile();

        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        var arms = compilation.Report.Arms;
        Assert.Equal(["trust-safety"], arms.Single(a => a.Id == "exp_safety/strict").Owners);
        Assert.Empty(arms.Single(a => a.Id == "exp_safety/control").Owners);
        Assert.All(arms.Where(a => !a.Id.StartsWith("exp_safety/", StringComparison.Ordinal)), a => Assert.DoesNotContain("trust-safety", a.Owners));
    }

    [Fact]
    public async Task AReleaseDoesNotCarryThePolicy()
    {
        var diagnostics = new DiagnosticBag();
        var workspace = Hosting.Workspace.ConfigWorkspace.Load(HomeFeedTestHost.SampleRoot(), diagnostics);
        Assert.NotNull(workspace.Policy); // the sample has one

        var manifest = await ManifestBuilder.BuildAsync(workspace, "home_feed", "local", Tool.Environment with { BaseDirectory = workspace.Root },
            new Artifacts.Storage.InMemoryArtifactStore(), diagnostics, seq: 1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(manifest);
        Assert.DoesNotContain("policy", ArtifactSerializer.ToNode(manifest!).ToJsonString(), StringComparison.Ordinal);
    }
}
