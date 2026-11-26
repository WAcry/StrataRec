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
        ["emb"] = ColumnType.Parse("list<float32,3>?"),
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
        Assert.Equal(new[] { 0.5, 1, -2 }, inputs["emb"].AsFloat64List().ToArray());
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
    [InlineData("""{ "user_id": 1, "emb": [1, 2] }""", "/inputs/emb", "Input 'emb' must be an array of 3 numbers (list<float32,3>), got 2 elements.", null)]
    [InlineData("""{ "user_id": 1, "emb": [1, 2, 1e39] }""", "/inputs/emb",
        "Input 'emb' must be an array of numbers within the float32 range [-3.4028235E+38, 3.4028235E+38]; element 2 is the number 1e39; declare the type as list<float64,3>? for larger values.", null)]
    [InlineData("""{ "user_id": 1, "flag": 1 }""", "/inputs/flag", "Input 'flag' must be true or false (bool), got the number 1.", null)]
    [InlineData("""{ "user_id": null }""", "/inputs/user_id", "Input 'user_id' is required by the request_schema of pipeline 'home_feed'.",
        "give it an integer (int64); only an input declared nullable ('int64?') may be null or absent")]
    [InlineData("""{ "region": "us" }""", "/inputs/user_id", "Input 'user_id' is required by the request_schema of pipeline 'home_feed'.",
        "give it an integer (int64); only an input declared nullable ('int64?') may be null or absent")]
    [InlineData("""{ "user_id": 1, "a/b": 2 }""", "/inputs/a~1b", "Input 'a/b' must be a string, got the number 2.", null)]
    [InlineData("""[1]""", "/inputs", "'inputs' must be an object of input values by name, got an array.",
        "declared inputs: 'a/b', 'emb', 'flag', 'n32', 'region', 'tags' and 'user_id'")]
    // What System.Text.Json parses but a request must not hold, at its pointer.
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

        var vector = Schema.Check(new RequestInputs().Set("user_id", ScalarValue.FromInt64(1)).Set("emb", ScalarValue.FromFloat32List(new float[] { 1, 2 })));
        Assert.Equal("Input 'emb' must be an array of 3 numbers (list<float32,3>), got 2 elements.", vector!.Message);

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
    // Judged by what the fallback's binding converts (review: int32 → int64 was flagged, though the fallback served it).
    [InlineData("n", "int64", "int32", null)] // every int32 is an int64
    [InlineData("n", "int64?", "int32", null)]
    [InlineData("n", "int32", "int64", "FALLBACK_INPUT_TYPE")] // beyond the int32 range fails
    [InlineData("x", "float64", "float32", null)]
    [InlineData("x", "float32", "float64", "FALLBACK_INPUT_TYPE")] // beyond the float32 range fails
    [InlineData("x", "float64", "int64", null)] // an integer converts to the nearest double, as for any float input
    [InlineData("x", "float32", "int32", null)]
    [InlineData("n", "int64", "float64", "FALLBACK_INPUT_TYPE")] // a fraction fails
    [InlineData("n", "int32", "string", "FALLBACK_INPUT_TYPE")]
    [InlineData("b", "bool", "bool", null)]
    [InlineData("b", "bool", "int32", "FALLBACK_INPUT_TYPE")]
    [InlineData("l", "list<int64>", "list<int32>", null)] // elements as scalars
    [InlineData("l", "list<int32>", "list<int64>", "FALLBACK_INPUT_TYPE")]
    [InlineData("l", "list<float64>", "list<float32,4>", null)] // a fixed length into any length, a wider element
    [InlineData("l", "list<float32,4>", "list<float32,4>", null)]
    [InlineData("l", "list<float32,3>", "list<float32,4>", "FALLBACK_INPUT_TYPE")] // no list converts
    [InlineData("l", "list<float32,4>", "list<float32>", "FALLBACK_INPUT_TYPE")] // other lengths fail
    [InlineData("l", "list<float32,4>", "list<float64,4>", "FALLBACK_INPUT_TYPE")]
    [InlineData("l", "list<int64>", "int64", "FALLBACK_INPUT_TYPE")] // a scalar is no list
    [InlineData("l", "int64", "list<int64>", "FALLBACK_INPUT_TYPE")]
    [InlineData("n", "int64", "int32?", "FALLBACK_INPUT_OPTIONAL")] // nullability apart: the type widens
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

    [Theory]
    [InlineData("int64", "int32",
        "Input 'n' is int64 in 'feed' but int32 in its fallback pipeline 'backup': a value outside the int32 range does not convert to it, and a request that falls back with it fails.",
        "declare 'n' as int64 in 'backup', which takes every value 'feed' accepts")]
    [InlineData("float64", "int64?",
        "Input 'n' is float64 in 'feed' but int64? in its fallback pipeline 'backup': a value with a fraction, or beyond the int64 range, does not convert to it, and a request that falls back with it fails.",
        "declare 'n' as float64? in 'backup', which takes every value 'feed' accepts")]
    [InlineData("list<int64>", "list<int64,3>",
        "Input 'n' is list<int64> in 'feed' but list<int64,3> in its fallback pipeline 'backup': a list of other than 3 elements does not convert to it, and a request that falls back with it fails.",
        "declare 'n' as list<int64> in 'backup', which takes every value 'feed' accepts")]
    [InlineData("list<int64,2>", "list<int64,3>",
        "Input 'n' is list<int64,2> in 'feed' but list<int64,3> in its fallback pipeline 'backup': a list of 2 elements does not convert to one of 3, and a request that falls back with it fails.",
        "declare 'n' as list<int64,2> in 'backup', which takes every value 'feed' accepts")]
    [InlineData("list<float64>", "list<float32>",
        "Input 'n' is list<float64> in 'feed' but list<float32> in its fallback pipeline 'backup': an element beyond the float32 range does not convert to it, and a request that falls back with it fails.",
        "declare 'n' as list<float64> in 'backup', which takes every value 'feed' accepts")]
    [InlineData("string", "int64",
        "Input 'n' is string in 'feed' but int64 in its fallback pipeline 'backup': no value converts to it, and a request that falls back with it fails.",
        "declare 'n' as string in 'backup', which takes every value 'feed' accepts")]
    public void CheckFallback_SaysWhatFailsToConvert_AndATypeThatTakesEveryValue(string primaryType, string fallbackType, string message, string hint)
    {
        var diagnostics = new StrataRec.Artifacts.Model.DiagnosticBag();

        RequestSchema.CheckFallback(Single("feed", primaryType), Single("backup", fallbackType), "pipelines/feed.jsonc", diagnostics);

        var warning = Assert.Single(diagnostics.Items);
        Assert.Equal(("FALLBACK_INPUT_TYPE", message, hint), (warning.Code, warning.Message, warning.Hint));
    }

    /// <summary>A narrower type and a required input are two failures, each reported: the type no longer hides the other.</summary>
    [Fact]
    public void CheckFallback_ReportsTheTypeAndTheNullabilityApart()
    {
        var diagnostics = new StrataRec.Artifacts.Model.DiagnosticBag();

        RequestSchema.CheckFallback(Single("feed", "int64?"), Single("backup", "int32"), "pipelines/feed.jsonc", diagnostics);

        Assert.Equal(new[] { "FALLBACK_INPUT_TYPE", "FALLBACK_INPUT_OPTIONAL" }, diagnostics.Items.Select(d => d.Code));
    }

    /// <summary>
    /// <see cref="RequestSchema.ConversionFailure"/> says exactly what the fallback's binding fails at run time
    /// (<see cref="ScalarValue.TryConvert"/>): for every pair of types, every value an input of the first accepted (typed
    /// values of every kind a host may pass, at the edges of each range) converts to the second when it says none fails,
    /// and one does not when it says some do. So the check can neither flag a fallback that serves every request nor miss
    /// one that fails some.
    /// </summary>
    [Fact]
    public void ConversionFailure_AgreesWithTheConversionTheFallbackRuns()
    {
        var types = new[]
        {
            "bool", "int32", "int64", "float32", "float64", "string",
            "list<bool>", "list<int32>", "list<int64>", "list<float32>", "list<float64>", "list<string>",
            "list<int32,2>", "list<int64,2>", "list<int64,3>", "list<float32,2>", "list<float64,2>", "list<float64,3>",
        };
        var probes = Probes();
        foreach (var givenText in types)
        {
            foreach (var requiredText in types)
            {
                var (given, required) = (ColumnType.Parse(givenText), ColumnType.Parse(requiredText));
                var accepted = probes.Where(p => p.TryConvert(given, out _)).ToList();
                var failing = accepted.Where(p => !p.TryConvert(required, out _)).ToList();
                var failure = RequestSchema.ConversionFailure(given, required);
                Assert.True(failure is null == (failing.Count == 0),
                    $"{givenText} → {requiredText}: the check says '{failure ?? "every value converts"}', the conversion fails {failing.Count} of {accepted.Count} values");
            }
        }
    }

    private static RequestSchema Single(string pipeline, string type) =>
        new(pipeline, new Dictionary<string, ColumnType> { ["n"] = ColumnType.Parse(type) });

    /// <summary>Typed values of every kind, at the edges of every range, and lists of them of several lengths.</summary>
    private static List<ScalarValue> Probes()
    {
        long[] integers = [0, 1, -1, int.MaxValue, int.MinValue, int.MaxValue + 1L, int.MinValue - 1L, long.MaxValue, long.MinValue, 9_007_199_254_740_993];
        double[] floats = [0.0, 0.5, -2.0, 3.0, float.MaxValue, -(double)float.MaxValue, float.MaxValue * 2.0, 1e300, -1e300, int.MaxValue + 1.0, 9.3e18, 4096.25];
        var probes = new List<ScalarValue> { ScalarValue.FromBool(true), ScalarValue.FromString("a"), ScalarValue.FromFloat32(0.5f), ScalarValue.FromFloat32(float.MaxValue) };
        probes.AddRange(integers.Select(ScalarValue.FromInt64));
        probes.AddRange(floats.Select(ScalarValue.FromFloat64));
        foreach (var length in new[] { 0, 1, 2, 3 })
        {
            probes.Add(ScalarValue.FromBoolList(Enumerable.Repeat(true, length).ToArray()));
            probes.Add(ScalarValue.FromStringList(Enumerable.Repeat("a", length).ToArray()));
            foreach (var integer in integers)
            {
                probes.Add(ScalarValue.FromInt64List(Enumerable.Repeat(integer, length).ToArray()));
            }

            foreach (var number in floats)
            {
                probes.Add(ScalarValue.FromFloat64List(Enumerable.Repeat(number, length).ToArray()));
            }

            probes.Add(ScalarValue.FromFloat32List(Enumerable.Repeat(1.5f, length).ToArray()));
        }

        return probes;
    }

}
