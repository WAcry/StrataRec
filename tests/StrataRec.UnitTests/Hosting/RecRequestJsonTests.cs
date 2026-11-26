using System.Text.Json;
using StrataRec.Core.Types;
using StrataRec.Hosting;
using StrataRec.Pipeline.Planning;

namespace StrataRec.UnitTests.Hosting;

/// <summary>
/// The request document every host reads the same way: the sample host hand-wrote these rules, so another
/// host would write its own; the transport tests of the sample host cover the same rules end to end.
/// </summary>
public sealed class RecRequestJsonTests
{
    private static readonly RequestSchema Schema = new("feed", new Dictionary<string, ColumnType>
    {
        ["user_id"] = ColumnType.Parse("int64"), ["region"] = ColumnType.Parse("string?"),
    });

    [Fact]
    public void ADocument_GivesTheInputsRequestIdAndAssignment()
    {
        Assert.True(Read("""{ "inputs": { "user_id": 1001 }, "request_id": "r-1", "assignment": ["exp_a/b"] }""", out var request, out var errors), Describe(errors));

        Assert.Equal(1001, request.Inputs["user_id"].AsInt64());
        Assert.False(request.Inputs.ContainsKey("region"));
        Assert.Equal("r-1", request.RequestId);
        Assert.Equal(new[] { "exp_a/b" }, request.Assignment);
    }

    [Fact]
    public void AnEmptyBody_IsADocumentWithoutMembers_AndStillNeedsTheRequiredInputs()
    {
        Assert.False(RecRequestJson.TryRead(default, Schema, out _, out var errors));
        Assert.Equal("/inputs/user_id", Assert.Single(errors).Pointer);

        var optional = new RequestSchema("feed", new Dictionary<string, ColumnType> { ["region"] = ColumnType.Parse("string?") });
        Assert.True(RecRequestJson.TryRead(default, optional, out var request, out _));
        Assert.Equal((0, null, null), (request.Inputs.Count, request.RequestId, request.Assignment));
    }

    [Theory]
    [InlineData("""{ "input": {} }""", "/input", "Unknown member 'input' in the request.", "did you mean 'inputs'?")]
    [InlineData("""{ "inputs": { "user_id": 1 }, "assignment": [7] }""", "/assignment/0", "An arm id is a string 'experiment/arm', got the number 7.", null)]
    [InlineData("""{ "inputs": { "user_id": 1 }, "request_id": "" }""", "/request_id", "request_id must be a non-empty string, got the string \"\".", null)]
    [InlineData("""{ "inputs": { "user_id": 1, "user_id": 2 } }""", "/inputs/user_id", "Member 'user_id' is given more than once.", null)]
    [InlineData("""[]""", "", "The request must be a JSON object", null)]
    public void AProblem_HasItsPointerAndAFix(string json, string pointer, string message, string? hint)
    {
        Assert.False(Read(json, out _, out var errors));

        var error = Assert.Single(errors);
        Assert.Equal(pointer, error.Pointer);
        Assert.StartsWith(message, error.Message, StringComparison.Ordinal);
        if (hint is not null)
        {
            Assert.Equal(hint, error.Hint);
        }
    }

    private static bool Read(string json, out RecRequestDocument request, out IReadOnlyList<StrataRec.Artifacts.Json.JsonBindingError> errors)
    {
        using var document = JsonDocument.Parse(json);
        return RecRequestJson.TryRead(document.RootElement, Schema, out request, out errors);
    }

    private static string Describe(IReadOnlyList<StrataRec.Artifacts.Json.JsonBindingError> errors) => string.Join("; ", errors.Select(e => $"{e.Pointer}: {e.Message}"));
}
