using StrataRec.Core.Types;
using StrataRec.Models;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;
using static StrataRec.UnitTests.Llm.LlmFixture;

namespace StrataRec.UnitTests.Llm;

/// <summary>
/// <c>LlmAnnotator@1</c> (design 5.9): one call per window row, each answer parsed by the column's type and written to the
/// row or left missing, the node Degraded when some rows have no value and Failed when every call failed, the load-time
/// checks, and a replay per entity.
/// </summary>
public sealed class LlmAnnotatorTests
{
    private const string Op = "LlmAnnotator@1";

    private const string Note = "\"note\": \"col.llm.note\"";

    /// <summary>The prompt is the row's id, so a test answers per row.</summary>
    private const string ById = "{{col.entity_id}}";

    [Fact]
    public async Task OneCallPerWindowRow_BatchOfOne_WithTheRequestSeed_AndMaxOutputTokens_EachPromptShowingItsRow()
    {
        var models = new TextModels(call => ("Note on " + call.Prompts["user"], ModelStatus.Ok));
        var plan = Compile(models, Annotate("{{col.entity_id}}: {{col.item.title}} ({{param.tone}})", extra: """, "max_output_tokens": 16"""), op: Op, output: Note);

        var result = await Run(plan, seed: 99);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(NodeOutcomeKind.Succeeded, Rank(result).Kind);
        Assert.Equal(3, models.Calls.Count);
        Assert.All(models.Calls, c => Assert.Equal((null, 1, 99L, 16), (c.Version, c.BatchSize, c.Seed, c.MaxOutputTokens)));
        Assert.Equal(new[] { "10: Red \"shoes\" (friendly)", "30: Green\nscarf (friendly)", "50: Mug (friendly)" },
            models.Calls.Select(c => c.Prompts["user"]).Order(StringComparer.Ordinal));
        Assert.Equal(Incoming, IdsOf(result)); // the order is not the annotator's
        Assert.Equal(new Dictionary<long, object?>
        {
            [30] = "Note on 30: Green\nscarf (friendly)", [10] = "Note on 10: Red \"shoes\" (friendly)", [50] = "Note on 50: Mug (friendly)",
            [20] = null, [40] = null, [70] = null, [60] = null,
        }, Values(result, "note"));
    }

    [Fact]
    public async Task TheDefaults_AreAWindowOf20_AtMost64Tokens_AndATextColumn()
    {
        var models = new TextModels(call => (call.Prompts["user"], ModelStatus.Ok));
        const string Config = """{ "model": "llm", "output": "answer", "column": "llm.note", "prompts": { "user": "{{col.item.extra}}" } }""";
        var plan = Compile(models, Config, op: Op, output: Note);

        var result = await Run(plan);

        Assert.True(plan.Schema.TryGetColumn("llm.note", out var column));
        Assert.Equal(ColumnType.Text.AsNullable(), column.Type);
        Assert.Equal(7, models.Calls.Count); // all seven rows: fewer than the window
        Assert.All(models.Calls, c => Assert.Equal(64, c.MaxOutputTokens));
        Assert.All(Values(result, "note"), v => Assert.Equal("x" + v.Key, v.Value));
    }

    [Theory]
    [InlineData("text?", "  Great for winter.\n", "Great for winter.")]
    [InlineData("text?", "```\nA \"warm\" pick\n```", "A \"warm\" pick")]
    [InlineData("string?", "sports", "sports")]
    [InlineData("bool?", "TRUE", true)]
    [InlineData("bool?", " false ", false)]
    [InlineData("int64?", "-42", -42L)]
    [InlineData("int64?", "9007199254740993", 9007199254740993L)]
    [InlineData("int64?", "```json\n7\n```", 7L)]
    [InlineData("int32?", "2147483647", 2147483647L)]
    [InlineData("float64?", "0.25", 0.25)]
    [InlineData("float64?", "-1.5e300", -1.5e300)]
    [InlineData("float64?", "1e-400", 0.0)]
    [InlineData("float32?", "0.1", (double)0.1f)]
    public async Task EachColumnType_ParsesItsAnswer(string type, string answer, object expected)
    {
        var plan = Compile(new TextModels(answer), Annotate(ById), op: Op, columns: $$""" "llm.note": { "type": "{{type}}" } """, output: Note);

        var result = await Run(plan);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(NodeOutcomeKind.Succeeded, Rank(result).Kind);
        var values = Values(result, "note");
        Assert.Equal(new[] { expected, expected, expected }, new[] { values[30], values[10], values[50] });
        Assert.Null(values[20]);
    }

    /// <summary>An answer that is not a value of the column leaves the row missing: never a guess, never a default.</summary>
    [Theory]
    [InlineData("text?", " \n ", "\"\": it is empty")]
    [InlineData("text?", "```\n```", "\"```\\n```\": it is empty")]
    [InlineData("string?", "", "\"\": it is empty")]
    [InlineData("bool?", "yes", "\"yes\": it is not true or false")]
    [InlineData("bool?", "\"true\"", "\"\\\"true\\\"\": it is not true or false")]
    [InlineData("bool?", "1", "\"1\": it is not true or false")]
    [InlineData("int64?", "4.0", "\"4.0\": it is not an integer in the int64 range")]
    [InlineData("int64?", "1e3", "\"1e3\": it is not an integer in the int64 range")]
    [InlineData("int64?", "9223372036854775808", "\"9223372036854775808\": it is not an integer in the int64 range")]
    [InlineData("int64?", "+5", "\"+5\": it is not a JSON number")]
    [InlineData("int64?", "007", "\"007\": it is not a JSON number")]
    [InlineData("int64?", "42 apples", "\"42 apples\": it is not a JSON number")]
    [InlineData("int64?", "\"42\"", "\"\\\"42\\\"\": it is not a JSON number")]
    [InlineData("int32?", "2147483648", "\"2147483648\": it is not an integer in the int32 range")]
    [InlineData("float64?", "NaN", "\"NaN\": it is not a JSON number")]
    [InlineData("float64?", "Infinity", "\"Infinity\": it is not a JSON number")]
    [InlineData("float64?", "1e309", "\"1e309\": it is outside the float64 range")]
    [InlineData("float64?", "0,5", "\"0,5\": it is not a JSON number")]
    [InlineData("float64?", ".5", "\".5\": it is not a JSON number")]
    [InlineData("float64?", "٣", "\"٣\": it is not a JSON number")]
    [InlineData("float32?", "1e39", "\"1e39\": it is outside the float32 range")]
    public async Task AnUnusableAnswer_LeavesTheRowMissing_AndTheNodeDegraded(string type, string answer, string first)
    {
        var plan = Compile(new TextModels(answer), Annotate(ById), op: Op, columns: $$""" "llm.note": { "type": "{{type}}" } """, output: Note);

        var result = await Run(plan);

        Assert.True(result.Succeeded, result.Error);
        Assert.All(Values(result, "note"), v => Assert.Null(v.Value));
        Assert.Equal(NodeOutcomeKind.Degraded, Rank(result).Kind);
        Assert.Equal($"model 'llm' annotated 0 of 3 rows; 3 answer(s) not usable as {type}, the first {first}", Rank(result).Detail);
    }

    [Fact]
    public async Task Categories_AreTheOnlyAnswersAStringColumnTakes_ComparedExactly_AndMakeAnUndeclaredColumnAString()
    {
        var answers = new Dictionary<string, string> { ["30"] = "music", ["10"] = "Music", ["50"] = " sports\n" };
        var plan = Compile(new TextModels(call => (answers[call.Prompts["user"]], ModelStatus.Ok)),
            Annotate(ById, extra: """, "categories": ["sports", "music"]"""), op: Op, output: Note);

        var result = await Run(plan);

        Assert.True(plan.Schema.TryGetColumn("llm.note", out var column));
        Assert.Equal(ColumnType.String.AsNullable(), column.Type);
        var values = Values(result, "note");
        Assert.Equal(("music", null, "sports"), (values[30], values[10], values[50]));
        Assert.Equal("model 'llm' annotated 2 of 3 rows; 1 answer(s) not usable as string?, the first \"Music\": it is not one of the 2 categories",
            Rank(result).Detail);
    }

    /// <summary>Rows whose call failed, threw or returned no text, and rows whose answer is unusable, are missing and named.</summary>
    [Fact]
    public async Task SomeRowsWithoutAValue_DegradeTheNode_NamingTheCountsAndTheFirstOfEach()
    {
        var models = new TextModels(call => call.Prompts["user"] switch
        {
            "30" => ("Fine", ModelStatus.Ok),
            "10" => (null, ModelStatus.Failed("overloaded")),
            "50" => (" ", ModelStatus.Ok with { Truncated = true }),
            "20" => throw new InvalidOperationException("boom"),
            "40" => (null, ModelStatus.Ok),
            _ => ("", ModelStatus.Ok),
        });
        var plan = Compile(models, Annotate(ById, window: 5), op: Op, output: Note);

        var result = await Run(plan);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(5, models.Calls.Count);
        Assert.Equal(new Dictionary<long, object?> { [30] = "Fine", [10] = null, [50] = null, [20] = null, [40] = null, [70] = null, [60] = null },
            Values(result, "note"));
        Assert.Equal(NodeOutcomeKind.Degraded, Rank(result).Kind);
        Assert.Equal(
            "model 'llm' annotated 1 of 5 rows; 3 call(s) failed, the first: overloaded; 1 answer(s) not usable as text?, the first \"\": it is empty; "
            + "the answer was cut at max_output_tokens (64): raise max_output_tokens or ask for a shorter answer",
            Rank(result).Detail);
    }

    [Fact]
    public async Task AThrowingOrSilentCall_IsAFailedRow_NamedInTheReason()
    {
        var thrown = await Run(Compile(new TextModels(call => call.Prompts["user"] == "30" ? throw new InvalidOperationException("boom") : ("ok", ModelStatus.Ok)),
            Annotate(ById), op: Op, output: Note));
        var silent = await Run(Compile(new TextModels(call => (call.Prompts["user"] == "30" ? null : "ok", ModelStatus.Ok)), Annotate(ById), op: Op, output: Note));

        Assert.Equal("model 'llm' annotated 2 of 3 rows; 1 call(s) failed, the first: threw InvalidOperationException: boom", Rank(thrown).Detail);
        Assert.Equal("model 'llm' annotated 2 of 3 rows; 1 call(s) failed, the first: did not return text output 'answer'", Rank(silent).Detail);
    }

    [Fact]
    public async Task EveryCallFailing_FailsTheNode_WritingNothing()
    {
        var models = new TextModels(call => call.Prompts["user"] == "10" ? throw new InvalidOperationException("boom") : (null, ModelStatus.Failed("overloaded")));
        var plan = Compile(models, Annotate(ById), op: Op, onFailure: "skip", output: Note);

        var result = await Run(plan);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(NodeOutcomeKind.Failed, Rank(result).Kind);
        Assert.Equal("model 'llm' failed on every row of the window (3): overloaded (skipped)", Rank(result).Detail);
        Assert.All(Values(result, "note"), v => Assert.Null(v.Value));
    }

    [Fact]
    public async Task AnEmptyWindow_CallsNoModel()
    {
        var models = new TextModels("x");

        var result = await Run(Compile(models, Annotate(ById), op: Op, recall: []));

        Assert.True(result.Succeeded, result.Error);
        Assert.Empty(models.Calls);
        Assert.Equal(NodeOutcomeKind.Succeeded, Rank(result).Kind);
    }

    [Fact]
    public async Task VersionParam_ChoosesTheVersionPerRequest()
    {
        var models = new TextModels("x");

        await Run(Compile(models, Annotate(ById, window: 1, extra: """, "version_param": "llm_version" """), op: Op));

        Assert.Equal("v1", Assert.Single(models.Calls).Version);
    }

    [Theory]
    [InlineData("""{ "model": "llm", "output": "answer", "column": "llm.note", "window": 0, "prompts": { "user": "x" } }""", "", "/window", "LLM_WINDOW",
        "window is 0, but it must be at least 1.")]
    [InlineData("""{ "model": "llm", "output": "answer", "column": "llm.note", "max_output_tokens": 0, "prompts": { "user": "x" } }""", "", "/max_output_tokens",
        "LLM_MAX_OUTPUT_TOKENS", "max_output_tokens is 0, but it must be at least 1.")]
    [InlineData("""{ "model": "llm", "output": "answer", "column": "", "prompts": { "user": "x" } }""", "", "/column", "LLM_COLUMN",
        "LlmAnnotator@1 needs 'column': the column the answers are written to.")]
    [InlineData("""{ "model": "llm", "output": "", "column": "llm.note", "prompts": { "user": "x" } }""", "", "/output", "MODEL_OUTPUT",
        "LlmAnnotator@1 needs 'output': the name of the model's text output holding the answer.")]
    [InlineData("""{ "model": "llm", "output": "answer", "column": "llm.note", "prompts": { } }""", "", "/prompts", "LLM_PROMPTS",
        "'prompts' is empty: the model needs at least one prompt.")]
    [InlineData("""{ "model": "llm", "output": "answer", "column": "llm.note", "categories": [], "prompts": { "user": "x" } }""", "", "/categories",
        "LLM_CATEGORIES", "'categories' is empty, so no answer could be used.")]
    [InlineData("""{ "model": "llm", "output": "answer", "column": "llm.note", "categories": ["a", "b", "a"], "prompts": { "user": "x" } }""", "",
        "/categories/2", "LLM_CATEGORIES", "Category 'a' is listed twice.")]
    [InlineData("""{ "model": "llm", "output": "answer", "column": "llm.note", "categories": ["a", " "], "prompts": { "user": "x" } }""", "",
        "/categories/1", "LLM_CATEGORIES", "Category 1 is empty, so no answer could match it.")]
    [InlineData("""{ "model": "llm", "output": "answer", "column": "llm.note", "categories": ["a "], "prompts": { "user": "x" } }""", "",
        "/categories/0", "LLM_CATEGORIES", "Category 'a ' starts or ends with whitespace, which an answer never does: answers are trimmed.")]
    [InlineData("""{ "model": "llm", "output": "answer", "column": "llm.note", "categories": ["a"], "prompts": { "user": "x" } }""",
        """ "llm.note": { "type": "text?" } """, "/categories", "LLM_CATEGORIES", "'categories' are the answers a string? column allows, but column 'llm.note' is text?.")]
    [InlineData("""{ "model": "llm", "output": "answer", "column": "llm.note", "prompts": { "user": "x" } }""", """ "llm.note": { "type": "int64", "default": 0 } """,
        "/column", "COLUMN_TYPE_MISMATCH",
        "Column 'llm.note' is int64, but LlmAnnotator@1 writes only the window's rows whose answers it can use, so it must be text?, string?, bool?, int32?, int64?, float32? or float64?.")]
    [InlineData("""{ "model": "llm", "output": "answer", "column": "llm.note", "prompts": { "user": "x" } }""", """ "llm.note": { "type": "list<string>" } """,
        "/column", "COLUMN_TYPE_MISMATCH",
        "Column 'llm.note' is list<string>, but LlmAnnotator@1 writes only the window's rows whose answers it can use, so it must be text?, string?, bool?, int32?, int64?, float32? or float64?.")]
    [InlineData("""{ "model": "llm", "output": "answer", "column": "item.title", "prompts": { "user": "x" } }""", "", "/column", "COLUMN_NOT_WRITABLE", null)]
    [InlineData("""{ "model": "llm", "version": "v1", "version_param": "llm_version", "output": "answer", "column": "llm.note", "prompts": { "user": "x" } }""", "",
        "/version", "MODEL_VERSION", "Set at most one of 'version' and 'version_param'.")]
    [InlineData("""{ "model": "llm", "output": "answer", "prompts": { "user": "x" } }""", "", "", "NODE_BAD_CONFIG", null)]
    public void AnInvalidConfig_IsALoadError_AtTheKey_WithAHint(string config, string columns, string key, string code, string? message)
    {
        var error = Error(TryCompile(new TextModels("x"), config, Op, columns: columns));

        Assert.Equal(("/nodes/rank/config" + key, code), (error.Pointer, error.Code));
        if (message is not null)
        {
            Assert.Equal(message, error.Message);
        }

        Assert.False(string.IsNullOrEmpty(error.Hint), error.ToString());
    }

    [Fact]
    public void ANonNullableColumn_IsRefused_WithTheNullableTypeInTheHint()
    {
        var error = Error(TryCompile(new TextModels("x"), Annotate(ById), Op, columns: """ "llm.note": { "type": "bool", "default": false } """));

        Assert.Equal("declare 'llm.note' as bool?: the rows after the window and the rows whose answer is not usable have no value", error.Hint);
    }

    [Theory]
    [InlineData("int32?")]
    [InlineData("float32?")]
    [InlineData("string?")]
    public void EveryNullableParsableType_IsAccepted(string type)
    {
        var compilation = TryCompile(new TextModels("x"), Annotate(ById), Op, columns: $$""" "llm.note": { "type": "{{type}}" } """);

        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
    }

    /// <summary>
    /// Each row's call is keyed by its entity, so a replay answers each entity from its own recorded call: the same window,
    /// a window that grew (the new rows were never annotated: missing, reported, the node Degraded) or shrank.
    /// </summary>
    [Fact]
    public async Task AReplay_AnswersEachEntityFromItsRecordedCall_WhateverWindowItIsIn()
    {
        var models = new TextModels(call => ("Note " + call.Prompts["user"], ModelStatus.Ok));
        var recorded = await Run(Compile(models, Annotate(ById), op: Op, output: Note), record: true);
        Assert.Equal(3, recorded.Recording!.ModelCalls.Count);
        Assert.All(recorded.Recording.ModelCalls, c => Assert.Equal((1, 1), (c.Keys!.Value.GetArrayLength(), c.InputDigests.Count)));
        var calls = models.Calls.Count;

        var (same, sameReplay) = await Replay(Compile(models, Annotate(ById), op: Op, output: Note), recorded);
        var (grown, grownReplay) = await Replay(Compile(models, Annotate(ById, window: 5), op: Op, output: Note), recorded);
        var (shrunk, shrunkReplay) = await Replay(Compile(models, Annotate(ById, window: 2), op: Op, output: Note), recorded);

        Assert.Equal(calls, models.Calls.Count);
        Assert.Equal(Values(recorded, "note"), Values(same, "note"));
        Assert.Empty(sameReplay.Gaps);
        Assert.Equal(NodeOutcomeKind.Succeeded, Rank(same).Kind);

        var values = Values(grown, "note");
        Assert.Equal(("Note 30", "Note 10", "Note 50", null, null), (values[30], values[10], values[50], values[20], values[40]));
        var gap = Assert.Single(grownReplay.Gaps);
        Assert.Equal(("model", "llm", "not_recorded", 2), (gap.Kind, gap.Source, gap.Reason, gap.Count));
        Assert.Equal(new[] { "20", "40" }, gap.Examples);
        Assert.Equal(NodeOutcomeKind.Degraded, Rank(grown).Kind);
        Assert.Equal("model 'llm' annotated 3 of 5 rows; 2 call(s) failed, the first: model 'llm' has no recorded output in the replay bundle", Rank(grown).Detail);

        values = Values(shrunk, "note");
        Assert.Equal(("Note 30", "Note 10", null), (values[30], values[10], values[50]));
        Assert.Empty(shrunkReplay.Gaps);
    }

    /// <summary>A row whose prompt changed is not served the recorded answer: its inputs changed, and it has no value.</summary>
    [Fact]
    public async Task AReplayAfterThePromptChanged_ReportsEachRowsInputsAsChanged()
    {
        var models = new TextModels(call => ("Note " + call.Prompts["user"], ModelStatus.Ok));
        var recorded = await Run(Compile(models, Annotate(ById), op: Op, output: Note), record: true);

        var (replayed, replay) = await Replay(Compile(models, Annotate("#" + ById), op: Op, onFailure: "skip", output: Note), recorded);

        var gap = Assert.Single(replay.Gaps);
        Assert.Equal(("model", "inputs_changed", 3), (gap.Kind, gap.Reason, gap.Count));
        Assert.Equal(NodeOutcomeKind.Failed, Rank(replayed).Kind);
        Assert.All(Values(replayed, "note"), v => Assert.Null(v.Value));
    }

    /// <summary>
    /// Two annotators of one model and the same rows, with different prompts. When the first one's window no longer has a
    /// row, the second's call of that row takes the first one's recorded call (same entity, other inputs) and is answered
    /// from its own recorded call of that entity alone: a single-row call's text is synthesized per entity.
    /// </summary>
    [Fact]
    public async Task ARowsTextIsSynthesizedFromTheRecordedCallOfThatEntityAlone_WhenAnotherNodesCallTookItsPlace()
    {
        var models = new TextModels(call => (call.Prompts["user"].ToUpperInvariant(), ModelStatus.Ok));
        const string Second = """
            "reason": { "order": 300, "op": "LlmAnnotator@1", "config": { "model": "llm", "output": "answer", "column": "llm.reason", "window": 3,
              "prompts": { "user": "why {{col.entity_id}}" } } }
            """;
        const string Output = Note + ", \"reason\": \"col.llm.reason\"";
        var recorded = await Run(Compile(models, Annotate("note {{col.entity_id}}"), op: Op, output: Output, nodes: Second), record: true);
        Assert.Equal(6, recorded.Recording!.ModelCalls.Count);
        var calls = models.Calls.Count;

        var (replayed, replay) = await Replay(Compile(models, Annotate("note {{col.entity_id}}", window: 2), op: Op, output: Output, nodes: Second), recorded);

        Assert.Equal(calls, models.Calls.Count);
        Assert.Empty(replay.Gaps);
        Assert.Equal(Values(recorded, "reason"), Values(replayed, "reason"));
        Assert.Equal("WHY 50", Values(replayed, "reason")[50]);
        Assert.Null(Values(replayed, "note")[50]);
    }
}
