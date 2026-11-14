using System.Text.Json;
using StrataRec.Artifacts.Json;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Pipeline.Planning;
using StrataRec.UnitTests.Artifacts;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// <see cref="RequestSchema"/>: one set of rules for a request's inputs, read from JSON (<see cref="RequestSchema.TryRead"/>)
/// or checked as typed values (<see cref="RequestSchema.Check"/>), every problem at its pointer, in the words of the JSON.
/// </summary>
public sealed class RequestSchemaTests
{
    private static readonly RequestSchema Schema = new("home_feed", new Dictionary<string, ColumnType>
    {
        ["user_id"] = ColumnType.Parse("int64"),
        ["region"] = ColumnType.Parse("string?"),
        ["n32"] = ColumnType.Parse("int32?"),
        ["tags"] = ColumnType.Parse("list<int32>?"),
        ["emb"] = ColumnType.Parse("vec<float32,3>?"),
        ["flag"] = ColumnType.Parse("bool?"),
        ["a/b"] = ColumnType.Parse("string?"),
    });

    [Fact]
    public void TheSchema_ListsItsInputsSorted_WithTheirDeclaredTypes()
    {
        Assert.Equal("home_feed", Schema.PipelineId);
        Assert.Equal(7, Schema.Count);
        Assert.Equal(new[] { "a/b", "emb", "flag", "n32", "region", "tags", "user_id" }, Schema.Names);
        Assert.True(Schema.TryGetType("n32", out var n32));
        Assert.Equal("int32?", n32.ToString()); // as declared; values are held promoted
        Assert.False(Schema.TryGetType("nope", out _));
        Assert.True(Schema.Contains("region"));
        Assert.False(Schema.Contains("Region"));
        Assert.Equal("/inputs/a~1b", RequestSchema.Pointer("a/b"));
    }

    [Fact]
    public void TryRead_ReadsEveryDeclaredType_AndNullOrAbsentAsMissing()
    {
        var inputs = Read("""{ "user_id": 1001, "region": "us", "n32": 7, "tags": [1, 2], "emb": [0.5, 1, -2], "flag": true, "a/b": null }""");

        Assert.Equal(1001L, inputs["user_id"].AsInt64());
        Assert.Equal("us", inputs["region"].AsString());
        Assert.Equal(7L, inputs["n32"].AsInt64()); // promoted
        Assert.Equal(new long[] { 1, 2 }, inputs["tags"].AsInt64List());
        Assert.Equal(new[] { 0.5f, 1f, -2f }, inputs["emb"].AsVector());
        Assert.True(inputs["flag"].AsBool());
        Assert.False(inputs.TryGetValue("a/b", out _)); // null: missing, never a default

        var minimal = Read("""{ "user_id": 1 }""");
        Assert.Single(minimal);
    }

    [Fact]
    public void TryRead_AnExactInteger_IsAnInteger_AndBeyondInt64IsAnError()
    {
        Assert.Equal(9007199254740993L, Read("""{ "user_id": 9007199254740993 }""")["user_id"].AsInt64());
        Assert.Equal(9007199254740993L, Read("""{ "user_id": 9007199254740993.0 }""")["user_id"].AsInt64()); // not rounded through double

        var fraction = Errors("""{ "user_id": 9007199254740992.5 }""");
        Assert.Equal("Input 'user_id' must be an integer (int64), got the number 9007199254740992.5.", Assert.Single(fraction).Message);
        var huge = Errors("""{ "user_id": 1e400 }""");
        Assert.Equal("Input 'user_id' must be within the int64 range [-9223372036854775808, 9223372036854775807], got the number 1e400.", Assert.Single(huge).Message);
    }

    [Theory]
    [InlineData("""{ "user_id": "1001" }""", "/inputs/user_id", "Input 'user_id' must be an integer (int64), got the string \"1001\".", null)]
    [InlineData("""{ "user_id": 1, "regoin": "us" }""", "/inputs/regoin", "Input 'regoin' is not declared in the request_schema of pipeline 'home_feed'.", "did you mean 'region'?")]
    [InlineData("""{ "user_id": 1, "n32": 3000000000 }""", "/inputs/n32",
        "Input 'n32' must be within the int32 range [-2147483648, 2147483647], got the number 3000000000; declare the type as int64? for larger values.", null)]
    [InlineData("""{ "user_id": 1, "tags": [1, 3000000000] }""", "/inputs/tags",
        "Input 'tags' must be an array of integers within the int32 range [-2147483648, 2147483647]; element 1 is the number 3000000000; declare the type as list<int64>? for larger values.", null)]
    [InlineData("""{ "user_id": 1, "emb": [1, 2] }""", "/inputs/emb", "Input 'emb' must be an array of 3 numbers (vec<float32,3>), got 2 elements.", null)]
    [InlineData("""{ "user_id": 1, "emb": [1, 2, 1e39] }""", "/inputs/emb", "Input 'emb' must be an array of 3 numbers (vec<float32,3>); element 2 is the number 1e39.", null)]
    [InlineData("""{ "user_id": 1, "flag": 1 }""", "/inputs/flag", "Input 'flag' must be true or false (bool), got the number 1.", null)]
    [InlineData("""{ "user_id": null }""", "/inputs/user_id", "Input 'user_id' is required by the request_schema of pipeline 'home_feed'.",
        "give it an integer (int64); only an input declared nullable ('int64?') may be null or absent")]
    [InlineData("""{ "region": "us" }""", "/inputs/user_id", "Input 'user_id' is required by the request_schema of pipeline 'home_feed'.",
        "give it an integer (int64); only an input declared nullable ('int64?') may be null or absent")]
    [InlineData("""{ "user_id": 1, "a/b": 2 }""", "/inputs/a~1b", "Input 'a/b' must be a string, got the number 2.", null)]
    [InlineData("""[1]""", "/inputs", "'inputs' must be an object of input values by name, got an array.",
        "declared inputs: 'a/b', 'emb', 'flag', 'n32', 'region', 'tags' and 'user_id'")]
    // Review 32: what System.Text.Json parses but a request must not hold, at its pointer.
    [InlineData("""{ "user_id": 1, "user_id": 2 }""", "/inputs/user_id", "Member 'user_id' is given more than once.",
        "give each member once: which of two values would count is not defined")]
    [InlineData("""{ "user_id": 1, "region": "\uD800" }""", "/inputs/region", "The string \"\\uD800\" is not valid Unicode: an escape is an unpaired surrogate.",
        "use a complete surrogate pair such as \\ud83d\\ude00, or write the character directly")]
    [InlineData("""{ "user_id": 1, "\uDC00x": 1 }""", "/inputs", "The member name \"\\uDC00x\" is not valid Unicode: an escape is an unpaired surrogate.",
        "use a complete surrogate pair such as \\ud83d\\ude00, or write the character directly")]
    public void TryRead_AProblem_IsAtItsPointer_InTheWordsOfTheJson(string json, string pointer, string message, string? hint)
    {
        var error = Assert.Single(Errors(json));

        Assert.Equal((pointer, message, hint), (error.Pointer, error.Message, error.Hint));
        AssertNoDotNetNames(error);
    }

    [Theory]
    [InlineData("""{ "user_id": 1, "region": "#" }""", "/inputs/region", "The string is not valid UTF-8 text.")]
    [InlineData("""{ "user_id": 1, "#": 1 }""", "/inputs", "The name of member 2 of this object is not valid UTF-8 text.")]
    public void TryRead_BytesThatAreNotUtf8_AreAnError_NotAnException(string json, string pointer, string message)
    {
        using var document = JsonDocument.Parse(StrictJsonTests.WithBytes(json, [0xFF]));

        Assert.False(Schema.TryRead(document.RootElement, out var inputs, out var errors));

        Assert.Null(inputs);
        var error = Assert.Single(errors);
        Assert.Equal((pointer, message), (error.Pointer, error.Message));
    }

    [Fact]
    public void TryRead_ReportsEveryProblem_InTheOrderOfTheBody_ThenTheMissing()
    {
        var errors = Errors("""{ "zzz": 1, "n32": "x", "region": 3 }""");

        Assert.Equal(new[] { "/inputs/zzz", "/inputs/n32", "/inputs/region", "/inputs/user_id" }, errors.Select(e => e.Pointer));
        Assert.Equal("declared inputs: 'a/b', 'emb', 'flag', 'n32', 'region', 'tags' and 'user_id'", errors[0].Hint); // nothing close to 'zzz'
        Assert.All(errors, AssertNoDotNetNames);
    }

    [Fact]
    public void TryRead_NoInputs_IsAnEmptyObject()
    {
        var empty = new RequestSchema("p", new Dictionary<string, ColumnType> { ["region"] = ColumnType.Parse("string?") });

        Assert.True(empty.TryRead(default, out var absent, out _));
        Assert.Empty(absent);
        using var nul = JsonDocument.Parse("null");
        Assert.True(empty.TryRead(nul.RootElement, out var none, out _));
        Assert.Empty(none);

        var nothing = new RequestSchema("p", new Dictionary<string, ColumnType>());
        using var given = JsonDocument.Parse("""{ "x": 1 }""");
        Assert.False(nothing.TryRead(given.RootElement, out _, out var errors));
        Assert.Equal("the pipeline declares no inputs", Assert.Single(errors).Hint);
    }

    [Fact]
    public void TryRead_MatchesEscapedNames()
    {
        // "user\u005fid" is "user_id": the name is compared after unescaping, as any JSON reader would.
        var inputs = Read("""{ "user\u005fid": 5 }""");

        Assert.Equal(5L, inputs["user_id"].AsInt64());
        Assert.Equal("/inputs/user_id", Assert.Single(Errors("""{ "user_id": 1, "user\u005fid": 2 }""")).Pointer);
    }

    [Fact]
    public void Check_TypedInputs_FollowTheSameRules_InTheSameWords()
    {
        Assert.Null(Schema.Check(new RequestInputs().Set("user_id", ScalarValue.FromInt64(1)).Set("n32", ScalarValue.FromInt64(7))));

        var wrongType = Schema.Check(new RequestInputs().Set("user_id", ScalarValue.FromString("1001")));
        Assert.Equal(("/inputs/user_id", "Input 'user_id' must be an integer (int64), got the string \"1001\"."), (wrongType!.Pointer, wrongType.Message));

        var outside = Schema.Check(new RequestInputs().Set("user_id", ScalarValue.FromInt64(1)).Set("n32", ScalarValue.FromInt64(3_000_000_000)));
        Assert.Equal(
            "Input 'n32' must be within the int32 range [-2147483648, 2147483647], got the number 3000000000; declare the type as int64? for larger values.",
            outside!.Message);

        var element = Schema.Check(new RequestInputs().Set("user_id", ScalarValue.FromInt64(1)).Set("tags", ScalarValue.FromInt64List(new long[] { 1, -3_000_000_000 })));
        Assert.Contains("element 1 is the number -3000000000; declare the type as list<int64>? for larger values", element!.Message, StringComparison.Ordinal);

        var vector = Schema.Check(new RequestInputs().Set("user_id", ScalarValue.FromInt64(1)).Set("emb", ScalarValue.FromVector(new float[] { 1, 2 })));
        Assert.Equal("Input 'emb' must be an array of 3 numbers (vec<float32,3>), got 2 elements.", vector!.Message);

        var missing = Schema.Check(new RequestInputs());
        Assert.Equal(("/inputs/user_id", "Input 'user_id' is required by the request_schema of pipeline 'home_feed'."), (missing!.Pointer, missing.Message));

        var fraction = Schema.Check(new RequestInputs().Set("user_id", ScalarValue.FromFloat64(1.5)));
        Assert.Equal("Input 'user_id' must be an integer (int64), got the number 1.5.", fraction!.Message);
    }

    [Fact]
    public void Check_AnUndeclaredInput_ComesFirst_WithADidYouMean()
    {
        var error = Schema.Check(new RequestInputs().Set("user_id", ScalarValue.FromString("x")).Set("regoin", ScalarValue.FromString("us")));

        Assert.Equal(("/inputs/regoin", "did you mean 'region'?"), (error!.Pointer, error.Hint));
    }

    [Fact]
    public void Check_ValidInputs_AllocateNothing()
    {
        var inputs = new RequestInputs().Set("user_id", ScalarValue.FromInt64(1)).Set("region", ScalarValue.FromString("us")).Set("tags", ScalarValue.FromInt64List(new long[] { 1 }));
        Assert.Null(Schema.Check(inputs)); // warm

        var before = GC.GetAllocatedBytesForCurrentThread();
        var error = Schema.Check(inputs);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(error);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Describe_IsTheMessage_ThenTheHintAsASentence()
    {
        Assert.Equal("Input 'x' is wrong.", RequestSchema.Describe(new JsonBindingError("/inputs/x", "Input 'x' is wrong.", null)));
        Assert.Equal("Input 'x' is wrong. Did you mean 'y'?", RequestSchema.Describe(new JsonBindingError("/inputs/x", "Input 'x' is wrong.", "did you mean 'y'?")));
        Assert.Equal("Input 'x' is wrong. Give it a string.", RequestSchema.Describe(new JsonBindingError("/inputs/x", "Input 'x' is wrong.", "give it a string")));
    }

    private static RequestInputs Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.True(Schema.TryRead(document.RootElement, out var inputs, out var errors), string.Join("\n", errors));
        Assert.Empty(errors);
        return inputs;
    }

    private static IReadOnlyList<JsonBindingError> Errors(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.False(Schema.TryRead(document.RootElement, out var inputs, out var errors));
        Assert.Null(inputs);
        Assert.NotEmpty(errors);
        return errors;
    }

    private static void AssertNoDotNetNames(JsonBindingError error)
    {
        foreach (var name in new[] { "Int64", "Int32", "String", "Double", "ScalarValue", "JsonElement", "System.", "Exception" })
        {
            Assert.DoesNotContain(name, error.Message + " " + error.Hint, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("tenant_id", "int64", null, "FALLBACK_INPUT_UNDECLARED")] // required by the fallback, unknown to the primary
    [InlineData("tenant_id", "int64?", null, null)] // optional in the fallback: a request without it is served
    [InlineData("region", "string?", "string?", null)] // the same declaration
    [InlineData("region", "string", "string?", "FALLBACK_INPUT_OPTIONAL")] // optional in the primary, required in the fallback
    [InlineData("region", "string?", "string", null)] // the fallback is more lenient
    [InlineData("region", "int64", "string", "FALLBACK_INPUT_TYPE")] // another type
    [InlineData("region", "int64?", "string?", "FALLBACK_INPUT_TYPE")]
    public void CheckFallback_WarnsAboutWhatTheFallbackWouldFailARequestOn(string name, string fallbackType, string? primaryType, string? code)
    {
        var primaryInputs = new Dictionary<string, ColumnType> { ["user_id"] = ColumnType.Parse("int64") };
        if (primaryType is not null)
        {
            primaryInputs[name] = ColumnType.Parse(primaryType);
        }

        var primary = new RequestSchema("feed", primaryInputs);
        var fallback = new RequestSchema("backup", new Dictionary<string, ColumnType> { [name] = ColumnType.Parse(fallbackType) });
        var diagnostics = new StrataRec.Artifacts.Model.DiagnosticBag();

        RequestSchema.CheckFallback(primary, fallback, "pipelines/feed.jsonc", diagnostics);

        if (code is null)
        {
            Assert.Empty(diagnostics.Items);
            return;
        }

        var warning = Assert.Single(diagnostics.Items);
        Assert.Equal((code, "pipelines/feed.jsonc", "/fallback_pipeline", false), (warning.Code, warning.Artifact, warning.Pointer, warning.IsError));
        Assert.Contains($"'{name}'", warning.Message, StringComparison.Ordinal);
        Assert.Contains("'backup'", warning.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(warning.Hint));
    }

}
