using System.Text.Json;
using StrataRec.Core.Bindings;
using StrataRec.Features.Connectors;
using StrataRec.Models;
using StrataRec.Pipeline.Sources;

namespace StrataRec.UnitTests.Core;

/// <summary>
/// The built-in clients <c>init</c> and the sample bind (<c>linear@1</c>, <c>memory@1</c>, <c>memory_list@1</c>) check their
/// settings strictly: a misspelled setting is an error at its pointer with the name it resembles, where it used to be
/// ignored and its default used (an <c>"activaton": "sigmoid"</c> scored with the identity); a member of the wrong type
/// is an error at it, where it used to be an exception without a place.
/// </summary>
public sealed class BuiltInClientSettingsTests
{
    public static TheoryData<string, string, string, string, string?> Mistakes() => new()
    {
        // client, settings, pointer, message, hint
        { "linear@1", """{ "outputs": { "p": { "bias": 1 } }, "lantency_ms": 5 }""", "/lantency_ms", "Unknown setting 'lantency_ms'.", "did you mean 'latency_ms'?" },
        { "linear@1", """{ "outputs": { "p": { "bias": 1, "activaton": "sigmoid" } } }""", "/outputs/p/activaton", "Unknown setting 'activaton'.", "did you mean 'activation'?" },
        { "linear@1", """{ "outputs": { "p": { "bias": "high" } } }""", "/outputs/p/bias", "'bias' must be a finite number; got \"high\".", null },
        { "linear@1", """{ "outputs": { "p": { "weights": { "ctr": [1] } } } }""", "/outputs/p/weights/ctr", "The weight of 'ctr' must be a finite number; got [1].", null },
        { "linear@1", """{ "outputs": { "p": { "activation": "relu" } } }""", "/outputs/p/activation", "Unknown activation 'relu'.", "use 'sigmoid' or 'identity'" },
        { "linear@1", """{ "versions": ["v1"] }""", "", "linear@1 needs 'outputs'", "add it, e.g." },
        { "linear@1", """{ "outputs": {}, "fail": "yes" }""", "/fail", "'fail' must be true or false; got \"yes\".", null },
        { "memory@1", """{ "data": {}, "faill": true }""", "/faill", "Unknown setting 'faill'.", "did you mean 'fail'?" },
        { "memory@1", """{ "data": {}, "data_file": "items.json" }""", "/data_file", "memory@1 takes 'data' or 'data_file', not both.", null },
        { "memory@1", """{ "data_file": "missing.json" }""", "/data_file", "missing.json' cannot be read", "a relative path resolves against" },
        { "memory@1", """{ "data": [1, 2] }""", "/data", "The records must be a JSON object, by entity key.", null },
        { "memory_list@1", """{ "itmes": [1, 2] }""", "/itmes", "Unknown setting 'itmes'.", "did you mean 'items'?" },
        { "memory_list@1", """{ "it/em~s": [1, 2] }""", "/it~1em~0s", "Unknown setting 'it/em~s'.", null }, // review 54
        { "memory_list@1", """{ "items": [1, { "id": 2, "scroe": 0.5 }] }""", "/items/1/scroe", "An item has 'id' and 'score', not 'scroe'.", "did you mean 'score'?" },
        { "memory_list@1", """{ "items": [1.5] }""", "/items/0", "An id must be a string or an int64; got 1.5.", null },
        { "memory_list@1", """{ "items": [{ "id": 1, "s/co~re": 1 }] }""", "/items/0/s~1co~0re", "An item has 'id' and 'score', not 's/co~re'.", null }, // review 41
        { "memory_list@1", """{ "by_input": "user_id", "lists": { "7": 7 } }""", "/lists/7", "A list must be an array", null },
        { "memory_list@1", """{ "data_file": "list.json" }""", "/data_file", "list.json' has a member 'itemz', which memory_list@1 does not take.", "did you mean 'items'?" },
        { "memory_list@1", """{ "data_file": "bad-item.json" }""", "/data_file", "bad-item.json' at /items/1: An id must be a string or an int64; got true.", null },
        { "memory_list@1", """{ "data_file": "missing.json" }""", "/data_file", "missing.json' cannot be read", "a relative path resolves against" },
        // A fixture is read as strictly as a workspace file: a key written twice was its last value.
        { "memory@1", """{ "data_file": "twice.json" }""", "/data_file", "Duplicate key '1'", "remove or merge one of the definitions" },
        { "memory_list@1", """{ "data_file": "twice.json" }""", "/data_file", "Duplicate key '1'", "remove or merge one of the definitions" },
        // Review 46: read as text first, an invalid byte became U+FFFD, and the id it was in another id.
        { "memory_list@1", """{ "data_file": "invalid.json" }""", "/data_file", "is not valid UTF-8", "save the file as UTF-8" },
        { "memory@1", """{ "data_file": "invalid.json" }""", "/data_file", "is not valid UTF-8", "save the file as UTF-8" },
        { "random@1", """{ "inputs": ["x"], "outputs": ["p"], "sed": 1 }""", "/sed", "Unknown setting 'sed'.", "did you mean 'seed'?" },
        { "random@1", """{ "inputs": ["x", "x"], "outputs": ["p"] }""", "/inputs/1", "'x' is listed twice in 'inputs'.", "list each name once" },
        { "fixed_text@1", """{ "input": ["user"], "outputs": { "answer": "x" } }""", "/input", "Unknown setting 'input'.", "did you mean 'inputs'?" },
        { "fixed_text@1", """{ "inputs": [""], "outputs": { "answer": "x" } }""", "/inputs/0", "'inputs' item 0 must be a non-empty name, got \"\".", null },
    };

    [Theory]
    [MemberData(nameof(Mistakes))]
    public void AMistake_IsAnErrorAtItsSetting_WithTheNameItResembles(string client, string settings, string pointer, string message, string? hint)
    {
        var directory = Directory.CreateTempSubdirectory("stratarec-settings-").FullName;
        File.WriteAllText(Path.Combine(directory, "items.json"), "{}");
        File.WriteAllText(Path.Combine(directory, "list.json"), """{ "itemz": [1] }""");
        File.WriteAllText(Path.Combine(directory, "bad-item.json"), """{ "items": [1, true] }""");
        File.WriteAllText(Path.Combine(directory, "twice.json"), """{ "1": {}, "1": {} }""");
        File.WriteAllBytes(Path.Combine(directory, "invalid.json"), [.. "{ \"items\": [\"bad"u8, 0xFF, .. "\"] }"u8]);
        var context = new BindingContext("b", JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(settings)!, directory, null);

        var error = Assert.Throws<BindingException>(() => Create(client, context));

        Assert.Equal(pointer, error.Pointer);
        Assert.Contains(message, error.Reason, StringComparison.Ordinal);
        if (hint is null)
        {
            return;
        }

        Assert.NotNull(error.Hint);
        Assert.Contains(hint, error.Hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("linear@1", """{ "outputs": { "p": { "bias": -1, "weights": { "x": 2 }, "activation": "sigmoid" } }, "versions": ["v1"], "latency_ms": 0, "fail": false }""")]
    [InlineData("memory@1", """{ "data_file": "items.json", "latency_ms": 1, "fail": false }""")]
    [InlineData("memory_list@1", """{ "data_file": "list.json", "by_input": "user_id", "lists": { "*": ["a", { "id": 3, "score": 0.5 }] }, "latency_ms": 0, "fail": false }""")]
    public void EverySettingTheyTake_IsTaken(string client, string settings)
    {
        var directory = Directory.CreateTempSubdirectory("stratarec-settings-").FullName;
        File.WriteAllText(Path.Combine(directory, "items.json"), """{ "1": { "x": 1 } }""");
        File.WriteAllText(Path.Combine(directory, "list.json"), """{ "items": [1, 2], "fail": true }""");

        Assert.NotNull(Create(client, new BindingContext("b", JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(settings)!, directory, null)));
    }

    private static object Create(string client, BindingContext context) => client switch
    {
        "linear@1" => new LinearModelClientFactory().Create(context),
        "memory@1" => new MemoryFeatureConnectorFactory().Create(context),
        "random@1" => new RandomModelClientFactory().Create(context),
        "fixed_text@1" => new FixedTextModelClientFactory().Create(context),
        _ => new MemoryListCandidateSourceFactory().Create(context),
    };
}
