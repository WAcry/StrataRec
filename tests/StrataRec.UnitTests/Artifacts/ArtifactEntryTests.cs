using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;

namespace StrataRec.UnitTests.Artifacts;

/// <summary>
/// One document, one verdict, whichever entry reads it: a workspace file is parsed strictly (<see cref="Jsonc.Parse(string, string?)"/>)
/// and then bound; a manifest, a replay bundle or an artifact of the store is bound from a string. A key written twice
/// was kept as its last value from a string (the serializer's default) and rejected in a file.
/// </summary>
public sealed class ArtifactEntryTests
{
    public static TheoryData<string, string> Documents => new()
    {
        { """{ "experiment_patchable": ["/params"], "experiment_patchable": ["/nodes"] }""", "Duplicate key 'experiment_patchable'" },
        { """{ "experiment_patchable": ["/par\uD800ams"] }""", "unpaired UTF-16 surrogate" },
        { """{ "experiment_patchable": ["/params"] """, "" }, // a syntax error
        { """{ "log_bytes_per_request_budget": "1" }""", "'log_bytes_per_request_budget' must be an integer or null, got the string \"1\"." },
        { """{ "owners": null }""", "'owners' must be an object, got null." },
        { """{ "experiment_patchable": ["/params", null] }""", "Item 1 of 'experiment_patchable' must be a string, got null." },
        { """{ "experiment_patchabel": [] }""", "Unknown key 'experiment_patchabel'" },
    };

    [Theory]
    [MemberData(nameof(Documents))]
    public void AStringAndAFile_AreRejectedAlike(string json, string reason)
    {
        var fromString = Assert.Throws<ArtifactFormatException>(() => ArtifactSerializer.Deserialize<PolicyArtifact>(json, "policy.jsonc"));
        var fromFile = Record.Exception(() => ArtifactSerializer.Deserialize<PolicyArtifact>(Jsonc.Parse(json, "policy.jsonc")!, "policy.jsonc"));

        switch (fromFile)
        {
            case JsoncException read: // the text: the same reason, at its line and column
                Assert.StartsWith(read.Reason, fromString.Reason);
                Assert.Contains($"(line {read.Line}, column {read.Column})", fromString.Reason);
                break;
            case ArtifactFormatException bound: // the binding: the same pointer and reason
                Assert.Equal((bound.Pointer, bound.Reason), (fromString.Pointer, fromString.Reason));
                break;
            default:
                Assert.Fail($"The file was read as {fromFile?.GetType().Name ?? "valid"}; from a string: {fromString.Message}");
                break;
        }

        Assert.Contains(reason, fromString.Reason);
    }
}
