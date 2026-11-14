using StrataRec.Artifacts.Model;

namespace StrataRec.UnitTests.Artifacts;

/// <summary>
/// Members of an earlier format fail with what replaced them, not as an unknown member: a release or manifest written
/// for the built-in traffic assigner must say that allocation moved to the experiment platform.
/// </summary>
public sealed class RetiredMembersTests
{
    private const string Manifest = """
        { "pipeline_id": "p", "environment": "prod", "seq": 3,
          "artifacts": { "catalog": "sha256:c", "bindings": "sha256:b", "pipeline": "sha256:p" },
          %MEMBER%
          "experiments": { "exp.a-1": { "artifact": "sha256:e", "layer": "rerank" %ARMS% } } }
        """;

    [Fact]
    public void AManifestWithAnAssignmentFailsAtItWithAFix()
    {
        var json = Manifest.Replace("%MEMBER%", "\"assignment\": { \"buckets\": 1000 },").Replace("%ARMS%", string.Empty);

        var ex = Assert.Throws<ArtifactFormatException>(() => ArtifactSerializer.Deserialize<ReleaseManifest>(json, "manifest.json"));

        Assert.Equal("/assignment", ex.Pointer);
        Assert.Contains("experiment platform", ex.Reason);
        Assert.Contains("stratarec build", ex.Hint);
        Assert.StartsWith("manifest.json: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestWithArmRangesFailsAtTheExperimentsArms()
    {
        var json = Manifest.Replace("%MEMBER%", string.Empty).Replace("%ARMS%", ", \"arms\": { \"control\": [0, 499] }");

        var ex = Assert.Throws<ArtifactFormatException>(() => ArtifactSerializer.Deserialize<ReleaseManifest>(json));

        // Experiment ids may contain '.' and '-': the pointer still names the member exactly.
        Assert.Equal("/experiments/exp.a-1/arms", ex.Pointer);
        Assert.Contains("arm bucket ranges", ex.Reason);
        Assert.NotNull(ex.Hint);
    }

    [Fact]
    public void AReleaseNodeWithAnAssignmentFailsAtIt()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse("""{ "pipeline_id": "p", "environment": "local", "assignment": {} }""")!;

        var ex = Assert.Throws<ArtifactFormatException>(() => ArtifactSerializer.Deserialize<ReleaseArtifact>(node, "releases/p.local.jsonc"));

        Assert.Equal("/assignment", ex.Pointer);
        Assert.Contains("\"layer\"", ex.Hint);
    }

    [Fact]
    public void OtherUnknownMembersAreUnknownKeys()
    {
        var json = Manifest.Replace("%MEMBER%", "\"assignmnt\": {},").Replace("%ARMS%", string.Empty);

        var ex = Assert.Throws<ArtifactFormatException>(() => ArtifactSerializer.Deserialize<ReleaseManifest>(json));

        Assert.Equal("/assignmnt", ex.Pointer);
        Assert.Equal("Unknown key 'assignmnt' in the manifest.", ex.Reason);
        Assert.DoesNotContain("stratarec build", ex.Hint); // not the retired member's fix
        Assert.StartsWith("allowed keys: ", ex.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void ARetiredNameInAnotherArtifactIsJustUnknown()
    {
        // Experiments have arms of their own; only a release's or manifest's experiment entries had ranges.
        var ex = Assert.Throws<ArtifactFormatException>(() => ArtifactSerializer.Deserialize<PolicyArtifact>("""{ "assignment": {} }"""));

        Assert.Equal("Unknown key 'assignment' in the policy.", ex.Reason);
        Assert.DoesNotContain("experiment platform", ex.Hint);
    }
}
