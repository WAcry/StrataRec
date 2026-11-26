using System.Text.Json;
using StrataRec.Core.Bindings;
using StrataRec.Features.Connectors;
using StrataRec.Models;
using StrataRec.Pipeline.Sources;
using StrataRec.Core.Services;

namespace StrataRec.UnitTests.Core;

/// <summary>
/// The built-in clients <c>init</c> and the sample bind (<c>linear@1</c>, <c>memory@1</c>, <c>memory_list@1</c>) check their
/// settings strictly: a misspelled setting is an error at its pointer with the name it resembles, where it used to be
/// ignored and its default used (an <c>"activaton": "sigmoid"</c> scored with the identity); a member of the wrong type
/// is an error at it, where it used to be an exception without a place. The settings are bound to each factory's settings
/// type (<see cref="StrataRec.Artifacts.Model.BindingSettings"/>), so the words are those of every other config.
/// </summary>
public sealed class BuiltInClientSettingsTests
{
    private const string SchemaHint = "'stratarec schema bindings' prints the JSON Schema of bindings";

    public static TheoryData<string, string, string, string, string?> Mistakes() => new()
    {
        // client, settings, pointer, message, hint
        { "linear@1", """{ "outputs": { "p": { "bias": 1 } }, "lantency_ms": 5 }""", "/lantency_ms", "Unknown key 'lantency_ms' in the settings of linear@1.", "did you mean 'latency_ms'?" },
        { "linear@1", """{ "outputs": { "p": { "bias": 1, "activaton": "sigmoid" } } }""", "/outputs/p/activaton", "Unknown key 'activaton' in entry 'p' of 'outputs'.", "did you mean 'activation'?" },
        { "linear@1", """{ "outputs": { "p": { "bias": "high" } } }""", "/outputs/p/bias", "'bias' must be a number, got the string \"high\".", SchemaHint },
        { "linear@1", """{ "outputs": { "p": { "bias": 1e400 } } }""", "/outputs/p/bias", "The number 1e400 is outside the double-precision range.", "use a smaller magnitude" },
        { "memory_list@1", """{ "items": [{ "id": 1, "score": -1e999 }] }""", "/items/0/score", "The number -1e999 is outside", null }, // one check for every settings type
        { "linear@1", """{ "outputs": { "p": { "weights": { "ctr": [1] } } } }""", "/outputs/p/weights/ctr", "Entry 'ctr' of 'weights' must be a number, got an array", null },
        { "linear@1", """{ "outputs": { "p": { "activation": "relu" } } }""", "/outputs/p/activation", "Unknown activation 'relu'.", "use 'sigmoid' or 'identity'" },
        { "linear@1", """{ "versions": ["v1"] }""", "", "'outputs' is required in the settings of linear@1.", "'outputs': " },
        { "linear@1", """{ "outputs": {}, "fail": "yes" }""", "/fail", "'fail' must be true or false, got the string \"yes\".", null },
        { "linear@1", """{ "outputs": {}, "latency_ms": 2.5 }""", "/latency_ms", "'latency_ms' must be an integer, got the number 2.5.", null },
        { "linear@1", """{ "outputs": {}, "latency_ms": -1 }""", "/latency_ms", "'latency_ms' must be 0 or more, got -1.", null },
        { "linear@1", """{ "outputs": {}, "versions": ["v1", ""] }""", "/versions/1", "must be a non-empty", null },
        { "memory@1", """{ "data": {}, "faill": true }""", "/faill", "Unknown key 'faill' in the settings of memory@1.", "did you mean 'fail'?" },
        { "memory@1", """{ "data_file": "s3://bucket/items.json" }""", "/data_file", "is not a local path", "local directory" }, // a URI was read as a path
        { "memory@1", """{ "data": {}, "data_file": "items.json" }""", "/data_file", "memory@1 takes 'data' or 'data_file', not both.", null },
        { "memory@1", """{ "data_file": "missing.json" }""", "/data_file", "missing.json' cannot be read", "a relative path resolves against" },
        { "memory@1", """{ "data": [1, 2] }""", "/data", "The records must be a JSON object, by entity key.", null },
        { "memory_list@1", """{ "itmes": [1, 2] }""", "/itmes", "Unknown key 'itmes' in the settings of memory_list@1.", "did you mean 'items'?" },
        { "memory_list@1", """{ "it/em~s": [1, 2] }""", "/it~1em~0s", "Unknown key 'it/em~s' in the settings of memory_list@1.", "did you mean 'items'?" },
        { "memory_list@1", """{ "items": [1, { "id": 2, "scroe": 0.5 }] }""", "/items/1/scroe", "Unknown key 'scroe' in an item of memory_list@1.", "did you mean 'score'?" },
        { "memory_list@1", """{ "items": [1.5] }""", "/items/0", "An id must be a string or an int64; got 1.5.", null },
        { "memory_list@1", """{ "items": [{ "id": 1, "s/co~re": 1 }] }""", "/items/0/s~1co~0re", "Unknown key 's/co~re' in an item of memory_list@1.", null },
        { "memory_list@1", """{ "items": [{ "score": 1 }] }""", "/items/0", "'id' is required in an item of memory_list@1.", "'id': " },
        { "memory_list@1", """{ "by_input": "user_id", "lists": { "7": 7 } }""", "/lists/7", "Entry '7' of 'lists' must be an array, got the number 7.", null },
        { "memory_list@1", """{ "by_input": "", "lists": {} }""", "/by_input", "'by_input' must name an input of the call.", "e.g. \"by_input\": \"user_id\"" },
        { "memory_list@1", """{ "data_file": "list.json" }""", "/data_file", "list.json' at /itemz: Unknown key 'itemz' in the file.", "did you mean 'items'?" },
        { "memory_list@1", """{ "data_file": "bad-item.json" }""", "/data_file", "bad-item.json' at /items/1: An id must be a string or an int64; got true.", null },
        { "memory_list@1", """{ "data_file": "missing.json" }""", "/data_file", "missing.json' cannot be read", "a relative path resolves against" },
        // A fixture is read as strictly as a workspace file: a key written twice was its last value.
        { "memory@1", """{ "data_file": "twice.json" }""", "/data_file", "Duplicate key '1'", "remove or merge one of the definitions" },
        { "memory_list@1", """{ "data_file": "twice.json" }""", "/data_file", "Duplicate key '1'", "remove or merge one of the definitions" },
        // Read as text first, an invalid byte became U+FFFD, and the id it was in another id.
        { "memory_list@1", """{ "data_file": "invalid.json" }""", "/data_file", "is not valid UTF-8", "save the file as UTF-8" },
        { "memory@1", """{ "data_file": "invalid.json" }""", "/data_file", "is not valid UTF-8", "save the file as UTF-8" },
        { "random@1", """{ "inputs": ["x"], "outputs": ["p"], "sed": 1 }""", "/sed", "Unknown key 'sed' in the settings of random@1.", "did you mean 'seed'?" },
        { "random@1", """{ "inputs": ["x", "x"], "outputs": ["p"] }""", "/inputs/1", "'x' is listed twice in 'inputs'.", "list each name once" },
        { "fixed_text@1", """{ "input": ["user"], "outputs": { "answer": "x" } }""", "/input", "Unknown key 'input' in the settings of fixed_text@1.", "did you mean 'inputs'?" },
        { "fixed_text@1", """{ "inputs": [""], "outputs": { "answer": "x" } }""", "/inputs/0", "'inputs' item 0 must be a non-empty name.", null },
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
    [InlineData("random@1", """{ "inputs": { "x": { "type": "float32", "shape": [-1, 4] } }, "outputs": { "p": { "shape": [-1] } }, "seed": 7 }""")]
    [InlineData("fixed_text@1", """{ "inputs": ["user"], "outputs": { "answer": "x" } }""")]
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
        _ => new MemoryListServiceFactory().Create(context),
    };
}
