using StrataRec.Models;
using StrataRec.Pipeline.Execution;
using static StrataRec.UnitTests.Llm.LlmFixture;

namespace StrataRec.UnitTests.Llm;

/// <summary>
/// <c>GenerativeRetriever@1</c> (design 5.9): the one call it sends, how it turns the answer into appended rows (new ids
/// only, up to <c>max_items</c>, with their features fetched like any appended row), what fails the node, the load-time
/// checks, and a replay by the whole call.
/// </summary>
public sealed class GenerativeRetrieverTests
{
    private const string Op = "GenerativeRetriever@1";

    /// <summary>The recall leaves 30, 10 and 50; the fixture also knows 20, 40, 60 and 70.</summary>
    private static readonly long[] Recalled = { 30, 10, 50 };

    private const string Prompt = "Items for {{ctx.request.query}} ({{param.tone}}{{#ctx.user.name}}, {{ctx.user.name}}{{/ctx.user.name}}, tier {{ctx.user.tier}})";

    private const string WithRank = DefaultOutput + ", \"rank\": \"col.llm.rank\"";

    [Fact]
    public async Task OneCall_BatchOfOne_WithTheRequestSeed_AndMaxOutputTokens_ShowingTheRequest_AppendsTheNewIds_WithTheirFeatures()
    {
        var models = new TextModels("[20, 40]");
        var plan = Compile(models, Retrieve(Prompt, extra: """, "max_output_tokens": 32"""), op: Op, recall: Recalled, columns: NullableRecallColumns);

        var result = await Run(plan, seed: 99);

        Assert.True(result.Succeeded, result.Error);
        var call = Assert.Single(models.Calls);
        Assert.Equal((null, 1, 99L, 32), (call.Version, call.BatchSize, call.Seed, call.MaxOutputTokens));
        Assert.Equal("Items for gifts for \"dad\" (friendly, Ada, tier 2)", call.Prompts["user"]);
        Assert.Equal(new long[] { 30, 10, 50, 20, 40 }, IdsOf(result));
        Assert.Equal(new Dictionary<long, object?> { [30] = "Green\nscarf", [10] = "Red \"shoes\"", [50] = "Mug", [20] = "Blue hat", [40] = "Tea" },
            Values(result, "title")); // the appended rows' features are fetched like any appended row's
        Assert.Equal((3, 5), (Rank(result).RowsIn, Rank(result).RowsOut));
    }

    [Fact]
    public async Task TheDefaults_AreAtMost256Tokens_AndMaxItems_IsTheRowLimit()
    {
        var models = new TextModels("[]");
        var plan = Compile(models, Retrieve("x", maxItems: 7), op: Op, recall: Recalled, columns: NullableRecallColumns);

        await Run(plan);

        Assert.Equal(256, Assert.Single(models.Calls).MaxOutputTokens);
        Assert.Equal(7, plan.Nodes.Single(n => n.NodeId == "rank").Operator.GetRowLimit(plan.Params.Baseline));
    }

    /// <summary>
    /// Elements of the wrong type, repeats and ids the table has are skipped; after max_items new ids the rest is not read.
    /// The rank column holds each appended row's position in the answer; the other rows have none.
    /// </summary>
    [Fact]
    public async Task OnlyNewIdsOfTheKeyType_AreAppended_InTheAnswersOrder_UpToMaxItems_WithTheirPositionsInTheAnswer()
    {
        const string Answer = """[60, 10, "20", 60, 1.5, [40], { "id": 40 }, null, true, 40, 20, 70, "x"]""";
        var plan = Compile(new TextModels(Answer), Retrieve("x", extra: """, "rank_col": "llm.rank" """), op: Op, recall: Recalled,
            columns: NullableRecallColumns, output: WithRank);

        var result = await Run(plan);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(NodeOutcomeKind.Succeeded, Rank(result).Kind);
        Assert.Equal(new long[] { 30, 10, 50, 60, 40, 20 }, IdsOf(result));
        Assert.Equal(new Dictionary<long, object?> { [30] = null, [10] = null, [50] = null, [60] = 1L, [40] = 10L, [20] = 11L }, Values(result, "rank"));
    }

    [Fact]
    public async Task MaxItems_BoundsTheAppendedRows()
    {
        var result = await Run(Compile(new TextModels("[20, 40, 60, 70]"), Retrieve("x", maxItems: 2), op: Op, recall: Recalled, columns: NullableRecallColumns));

        Assert.Equal(new long[] { 30, 10, 50, 20, 40 }, IdsOf(result));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("```json\n[]\n```")]
    [InlineData("[30, 10, 30]")]
    [InlineData("[\"20\", 4.5]")]
    public async Task AnAnswerWithNothingNew_AppendsNothing_AndTheNodeSucceeds(string answer)
    {
        var result = await Run(Compile(new TextModels(answer), Retrieve("x"), op: Op, recall: Recalled, columns: NullableRecallColumns));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(NodeOutcomeKind.Succeeded, Rank(result).Kind);
        Assert.Equal(Recalled, IdsOf(result));
    }

    [Fact]
    public async Task StringKeys_AreAnsweredAsJsonStrings()
    {
        var models = new TextModels("""["sku-f", "sku-a", 7, "sku-f", "sku-g", "\uD800"]""");

        var result = await Run(Compile(models, Retrieve("x", maxItems: 5, extra: """, "rank_col": "llm.rank" """), op: Op, stringKeys: true,
            columns: NullableRecallColumns, output: WithRank));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(new[] { "sku-c", "sku-a", "sku-e", "sku-b", "sku-d", "sku-f", "sku-g" }, result.Items.Select(i => i.Id.StringValue));
        Assert.Equal(new object?[] { null, null, null, null, null, 1L, 5L }, result.Items.Select(i => Value(i.Columns["rank"])));
        Assert.Equal(new object?[] { "C", "A", "E", "B", "D", "F", "" }, result.Items.Select(i => Value(i.Columns["title"])));
    }

    [Theory]
    [InlineData("I suggest [20]", "it is not valid JSON (byte 0: 'I' is an invalid start of a value); the answer starts \"I suggest [20]\"")]
    [InlineData("{ \"ids\": [20] }", "it is a JSON object, not an array; the answer starts \"{ \\\"ids\\\": [20] }\"")]
    [InlineData("[20] [40]", "it is not valid JSON (byte 5: '[' is invalid after a single JSON value. Expected end of data); the answer starts \"[20] [40]\"")]
    [InlineData(" ", "the answer is empty; the answer starts \"\"")]
    public async Task AnAnswerThatIsNotAJsonArray_FailsTheNode_AppendingNothing(string answer, string reason)
    {
        var result = await Run(Compile(new TextModels(answer), Retrieve("x"), op: Op, onFailure: "skip", recall: Recalled, columns: NullableRecallColumns));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(Recalled, IdsOf(result));
        Assert.Equal(NodeOutcomeKind.Failed, Rank(result).Kind);
        Assert.Equal($"model 'llm' did not answer a JSON array of entity ids (integers): {reason} (skipped)", Rank(result).Detail);
    }

    /// <summary>The ids before the cut are not appended: the answer is not valid JSON, and a strict answer is all or nothing.</summary>
    [Fact]
    public async Task ATruncatedAnswer_FailsTheNode_SayingItWasCutAtTheLimit()
    {
        var models = new TextModels(_ => ("[20, 40, 6", ModelStatus.Ok with { Truncated = true }));

        var result = await Run(Compile(models, Retrieve("x", maxItems: 1, extra: """, "max_output_tokens": 8"""), op: Op, onFailure: "skip", recall: Recalled,
            columns: NullableRecallColumns));

        Assert.Equal(Recalled, IdsOf(result));
        Assert.Equal(
            "model 'llm' did not answer a JSON array of entity ids (integers): it is not valid JSON (byte 10: '6' is an invalid end of a number. Expected a delimiter); "
            + "the answer starts \"[20, 40, 6\"; the answer was cut at max_output_tokens (8): raise max_output_tokens or ask for fewer ids (skipped)",
            Rank(result).Detail);
    }

    [Fact]
    public async Task AFailedCall_OrNoTextOutput_FailsTheNode_AppendingNothing()
    {
        var failed = await Run(Compile(new TextModels(_ => (null, ModelStatus.Failed("overloaded"))), Retrieve("x"), op: Op, onFailure: "skip", recall: Recalled,
            columns: NullableRecallColumns));
        var silent = await Run(Compile(new TextModels(_ => (null, ModelStatus.Ok)), Retrieve("x"), op: Op, onFailure: "skip", recall: Recalled,
            columns: NullableRecallColumns));

        Assert.Equal("model 'llm' failed: overloaded (skipped)", Rank(failed).Detail);
        Assert.Equal("model 'llm' did not return text output 'answer' (skipped)", Rank(silent).Detail);
        Assert.Equal(Recalled, IdsOf(failed));
        Assert.Equal(Recalled, IdsOf(silent));
    }

    [Fact]
    public async Task VersionParam_ChoosesTheVersionPerRequest()
    {
        var models = new TextModels("[]");

        await Run(Compile(models, Retrieve("x", extra: """, "version_param": "llm_version" """), op: Op, columns: NullableRecallColumns));

        Assert.Equal("v1", Assert.Single(models.Calls).Version);
    }

    [Theory]
    [InlineData("""{ "model": "llm", "output": "answer", "max_items": 0, "prompts": { "user": "x" } }""", "", "/max_items", "LLM_MAX_ITEMS",
        "max_items is 0, but it must be at least 1.")]
    [InlineData("""{ "model": "llm", "output": "answer", "prompts": { "user": "x" } }""", "", "", "NODE_BAD_CONFIG", null)]
    [InlineData("""{ "model": "llm", "output": "answer", "max_items": 3, "max_output_tokens": 0, "prompts": { "user": "x" } }""", "", "/max_output_tokens",
        "LLM_MAX_OUTPUT_TOKENS", "max_output_tokens is 0, but it must be at least 1.")]
    [InlineData("""{ "model": "llm", "output": "", "max_items": 3, "prompts": { "user": "x" } }""", "", "/output", "MODEL_OUTPUT",
        "GenerativeRetriever@1 needs 'output': the name of the model's text output holding the answer.")]
    [InlineData("""{ "model": "llm", "output": "answer", "max_items": 3, "prompts": { } }""", "", "/prompts", "LLM_PROMPTS",
        "'prompts' is empty: the model needs at least one prompt.")]
    [InlineData("""{ "model": "nope", "output": "answer", "max_items": 3, "prompts": { "user": "x" } }""", "", "/model", "MODEL_UNBOUND", null)]
    [InlineData("""{ "model": "llm", "output": "answer", "max_items": 3, "rank_col": "llm.rank", "prompts": { "user": "x" } }""",
        """ "llm.rank": { "type": "int64", "default": 0 } """, "/rank_col", "COLUMN_TYPE_MISMATCH",
        "Rank column 'llm.rank' is int64, but GenerativeRetriever@1 writes it only for the rows it appends: it must be int64? or int32?.")]
    [InlineData("""{ "model": "llm", "output": "answer", "max_items": 3, "rank_col": "llm.rank", "prompts": { "user": "x" } }""",
        """ "llm.rank": { "type": "float64?" } """, "/rank_col", "COLUMN_TYPE_MISMATCH",
        "Rank column 'llm.rank' is float64?, but GenerativeRetriever@1 writes it only for the rows it appends: it must be int64? or int32?.")]
    public void AnInvalidConfig_IsALoadError_AtTheKey_WithAHint(string config, string columns, string key, string code, string? message)
    {
        var error = Error(TryCompile(new TextModels("[]"), config, Op, columns: NullableRecallColumns + (columns.Length == 0 ? "" : ", " + columns)));

        Assert.Equal(("/nodes/rank/config" + key, code), (error.Pointer, error.Code));
        if (message is not null)
        {
            Assert.Equal(message, error.Message);
        }

        Assert.False(string.IsNullOrEmpty(error.Hint), error.ToString());
    }

    [Fact]
    public async Task AnInt32RankColumn_IsWritten()
    {
        var plan = Compile(new TextModels("[20]"), Retrieve("x", extra: """, "rank_col": "llm.rank" """), op: Op, recall: Recalled,
            columns: NullableRecallColumns + """, "llm.rank": { "type": "int32?" } """, output: WithRank);

        var result = await Run(plan);

        Assert.Equal(1L, Values(result, "rank")[20]);
    }

    /// <summary>
    /// The recall's attribution columns are non-nullable, and a row source after it appends rows without them, which would
    /// fail every request at commit: the plan is refused, with the fix (declare them nullable) in the hint.
    /// </summary>
    [Fact]
    public void AfterARecall_ItsNonNullableAttributionColumns_MustBeDeclaredNullable()
    {
        var compilation = TryCompile(new TextModels("[]"), Retrieve("x"), Op);

        Assert.False(compilation.Succeeded);
        var errors = compilation.Diagnostics.Where(d => d.IsError).ToList();
        Assert.Equal(4, errors.Count);
        Assert.All(errors, e => Assert.Equal(("ROW_SOURCE_AFTER_COLUMN", "/nodes/rank"), (e.Code, e.Pointer)));
        Assert.Contains("declare the column nullable", errors[0].Hint);
    }

    /// <summary>The call is recorded and replayed whole, by its digest: the same prompt replays it; another is not recorded.</summary>
    [Fact]
    public async Task AReplay_AnswersTheCallFromItsRecording_AndAnotherPromptIsNotRecorded()
    {
        var models = new TextModels("[20, 40]");
        var recorded = await Run(Compile(models, Retrieve(Prompt), op: Op, recall: Recalled, columns: NullableRecallColumns), record: true);
        var call = Assert.Single(recorded.Recording!.ModelCalls);
        Assert.Equal((false, 1, "[20, 40]"), (call.Keys.HasValue, call.InputDigests.Count, call.Texts["answer"]));

        var (same, sameReplay) = await Replay(Compile(new TextModels("[60]"), Retrieve(Prompt), op: Op, recall: Recalled, columns: NullableRecallColumns), recorded);
        var (changed, changedReplay) = await Replay(
            Compile(new TextModels("[60]"), Retrieve(Prompt + "!"), op: Op, onFailure: "skip", recall: Recalled, columns: NullableRecallColumns), recorded);

        Assert.Equal(IdsOf(recorded), IdsOf(same));
        Assert.Empty(sameReplay.Gaps);
        Assert.Equal(Recalled, IdsOf(changed));
        Assert.Contains(changedReplay.Gaps, g => g is { Kind: "model", Source: "llm", Reason: "not_recorded" });
        Assert.Equal(NodeOutcomeKind.Failed, Rank(changed).Kind);
        Assert.Single(models.Calls);
    }
}
