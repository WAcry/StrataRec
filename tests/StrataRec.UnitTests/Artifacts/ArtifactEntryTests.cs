using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;

namespace StrataRec.UnitTests.Artifacts;

public sealed class ArtifactEntryTests
{
    public static TheoryData<string, string> Documents => new()
    {
        { """{ "owners": {}, "owners": { "/nodes": [] } }""", "Duplicate key 'owners'" },
        { """{ "owners": { "/nodes": ["t\uD800s"] } }""", "unpaired UTF-16 surrogate" },
        { """{ "owners": {} """, "" },
        { """{ "log_bytes_per_request_budget": 1 }""", "Unknown key 'log_bytes_per_request_budget'" },
        { """{ "owners": null }""", "'owners' must be an object, got null." },
        { """{ "owners": { "nodes/safety_filter": [] } }""", "nodes/safety_filter" },
        { """{ "experiment_patchable": ["/params"] }""", "Unknown key 'experiment_patchable'" },
        { """{ "ownres": {} }""", "Unknown key 'ownres'" },
    };

    [Theory]
    [MemberData(nameof(Documents))]
    public void AStringAndAFile_AreRejectedAlike(string json, string reason)
    {
        var fromString = Assert.Throws<ArtifactFormatException>(() => ArtifactSerializer.Deserialize<PolicyArtifact>(json, "policy.jsonc"));
        var fromFile = Record.Exception(() => ArtifactSerializer.Deserialize<PolicyArtifact>(Jsonc.Parse(json, "policy.jsonc")!, "policy.jsonc"));

        switch (fromFile)
        {
            case JsoncException read:
                Assert.StartsWith(read.Reason, fromString.Reason);
                Assert.Contains($"(line {read.Line}, column {read.Column})", fromString.Reason);
                break;
            case ArtifactFormatException bound:
                Assert.Equal((bound.Pointer, bound.Reason), (fromString.Pointer, fromString.Reason));
                break;
            default:
                Assert.Fail($"The file was read as {fromFile?.GetType().Name ?? "valid"}; from a string: {fromString.Message}");
                break;
        }

        Assert.Contains(reason, fromString.Reason);
    }
}
