using System.Diagnostics.Metrics;
using StrataRec.Core.Diagnostics;
using StrataRec.Llm;
using StrataRec.Models;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Replay;
using StrataRec.UnitTests.Features.Cache;
using static StrataRec.UnitTests.Llm.LlmFixture;

namespace StrataRec.UnitTests.Llm;

/// <summary>
/// <c>LlmReranker@1</c> (design 5.9): the one call it sends, how it turns the answer into a new order of the window, what
/// fails the node (leaving the order as it was), and the load-time checks of its config and of every model version.
/// </summary>
public sealed class LlmRerankerTests
{
    private const string Ranks = "{{#rows}}{{rank}}. {{col.item.title}}\n{{/rows}}";

    private const string Ids = "{{#rows}}{{col.entity_id}}: {{col.item.title}}\n{{/rows}}";

    /// <summary>The incoming order, which a failed node leaves as it is.</summary>
    private static readonly long[] Unchanged = Incoming;

    [Fact]
    public async Task OneCall_BatchOfOne_WithTheRequestSeed_AndMaxOutputTokens_ShowingTheWindow()
    {
        var models = new TextModels("[1]");
        var plan = Compile(models, Rerank(Ranks, extra: """, "max_output_tokens": 64"""));

        var result = await Run(plan, seed: 99);

        Assert.True(result.Succeeded, result.Error);
        var call = Assert.Single(models.Calls);
        Assert.Equal((null, 1, 99L, 64), (call.Version, call.BatchSize, call.Seed, call.MaxOutputTokens));
        Assert.Equal("1. Green\nscarf\n2. Red \"shoes\"\n3. Mug\n", call.Prompts["user"]);
    }

    [Fact]
    public async Task TheDefaults_AreAWindowOf20_AndAtMost256Tokens_KeepingUnlistedRows()
    {
        var models = new TextModels("[7, 1]");
        const string Config = """{ "model": "llm", "output": "answer", "answer": "rank", "prompts": { "user": "{{#rows}}{{rank}}{{/rows}}" } }""";

        var result = await Run(Compile(models, Config));

        Assert.Equal("1234567", Assert.Single(models.Calls).Prompts["user"]); // all seven rows: fewer than the window
        Assert.Equal(256, models.Calls[0].MaxOutputTokens);
        Assert.Equal(new long[] { 60, 30, 10, 50, 20, 40, 70 }, IdsOf(result));
    }

    [Theory]
    [InlineData("rank", "[3, 1]")]
    [InlineData("entity_id", "[50, 30]")]
    public async Task TheListedRowsComeFirst_InTheAnswersOrder_ThenTheUnlistedWindowRows_ThenTheRest(string answer, string text)
    {
        var result = await Run(Compile(new TextModels(text), Rerank(answer == "rank" ? Ranks : Ids, answer)));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(new long[] { 50, 30, 10, 20, 40, 70, 60 }, IdsOf(result));
    }

    [Fact]
    public async Task WithUnlistedDrop_TheWindowRowsTheAnswerDoesNotList_AreDropped_TheRowsAfterItStay()
    {
        var result = await Run(Compile(new TextModels("[3]"), Rerank(Ranks, extra: """, "unlisted": "drop" """)));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(new long[] { 50, 20, 40, 70, 60 }, IdsOf(result));
        Assert.Equal((7, 5), (Rank(result).RowsIn, Rank(result).RowsOut));
    }

    [Fact]
    public async Task StringKeys_AreAnsweredAsJsonStrings()
    {
        var models = new TextModels("""["sku-e", "sku-c", 7, "sku-b"]""");

        var result = await Run(Compile(models, Rerank(Ids, "entity_id"), stringKeys: true));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("sku-c: C\nsku-a: A\nsku-e: E\n", Assert.Single(models.Calls).Prompts["user"]);
        Assert.Equal(new[] { "sku-e", "sku-c", "sku-a", "sku-b", "sku-d" }, result.Items.Select(i => i.Id.StringValue));
    }

    [Theory]
    [InlineData("```json\n[2, 3]\n```")]
    [InlineData("```\n[2, 3]\n```")]
    [InlineData("  ```JSON\r\n  [2, 3]\r\n```\n")]
    [InlineData("\n [2,3] \n")]
    public async Task AnAnswerInOneCodeFence_IsReadInside(string text)
    {
        var result = await Run(Compile(new TextModels(text), Rerank(Ranks)));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(new long[] { 10, 50, 30, 20, 40, 70, 60 }, IdsOf(result));
    }

    /// <summary>Elements that name no window row and repeats are ignored; the rest still reorders the window.</summary>
    [Fact]
    public async Task UnknownAndRepeatedElements_AreIgnored()
    {
        var text = """[2, "x", 0, 9, 2, 1.5, [1, 2], { "rank": 3 }, null, true, 3]""";

        var result = await Run(Compile(new TextModels(text), Rerank(Ranks)));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(new long[] { 10, 50, 30, 20, 40, 70, 60 }, IdsOf(result));
    }

    [Fact]
    public async Task AnEmptyWindow_CallsNoModel()
    {
        var models = new TextModels("[1]");

        var result = await Run(Compile(models, Rerank(Ranks), recall: []));

        Assert.True(result.Succeeded, result.Error);
        Assert.Empty(models.Calls);
        Assert.Equal(NodeOutcomeKind.Succeeded, Rank(result).Kind);
    }

    [Theory]
    [InlineData("I think [2, 1", "it is not valid JSON (byte 0: 'I' is an invalid start of a value); the answer starts \"I think [2, 1\"")]
    [InlineData("[2, 1", "it is not valid JSON (byte 5: '1' is an invalid end of a number. Expected a delimiter); the answer starts \"[2, 1\"")]
    [InlineData("[2, 1] Hope this helps!", "it is not valid JSON (byte 7: 'H' is invalid after a single JSON value. Expected end of data); the answer starts \"[2, 1] Hope this helps!\"")]
    [InlineData("[2,\n 1,\n", "it is not valid JSON (line 2, byte 2: Expected start of a property name or value, but instead reached end of data); the answer starts \"[2,\\n 1,\"")]
    [InlineData("{ \"ranking\": [2, 1] }", "it is a JSON object, not an array; the answer starts \"{ \\\"ranking\\\": [2, 1] }\"")]
    [InlineData("2", "it is a JSON number, not an array; the answer starts \"2\"")]
    [InlineData(" \n ", "the answer is empty; the answer starts \"\"")]
    [InlineData("```python\n[2, 1]\n```", "it is not valid JSON (byte 0: '`' is an invalid start of a value); the answer starts \"```python\\n[2, 1]\\n```\"")]
    public async Task AnAnswerThatIsNotAJsonArray_FailsTheNode_LeavingTheOrder(string text, string reason)
    {
        var result = await Run(Compile(new TextModels(text), Rerank(Ranks), onFailure: "skip"));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(Unchanged, IdsOf(result));
        Assert.Equal(NodeOutcomeKind.Failed, Rank(result).Kind);
        Assert.Equal($"model 'llm' did not answer a JSON array of window ranks (1 to 3): {reason} (skipped)", Rank(result).Detail);
    }

    [Theory]
    [InlineData("rank", "[0, 4, -1, \"1\"]", "answered 4 element(s), none of them one of the window ranks (1 to 3): \"[0, 4, -1, \\\"1\\\"]\"")]
    [InlineData("entity_id", "[\"30\", 20, 3]", "answered 3 element(s), none of them one of the entity ids of the window's rows: \"[\\\"30\\\", 20, 3]\"")]
    [InlineData("rank", "[]", "answered 0 element(s), none of them one of the window ranks (1 to 3): \"[]\"")]
    public async Task AnArrayNamingNoWindowRow_FailsTheNode_LeavingTheOrder(string answer, string text, string reason)
    {
        var result = await Run(Compile(new TextModels(text), Rerank(answer == "rank" ? Ranks : Ids, answer), onFailure: "skip"));

        Assert.Equal(Unchanged, IdsOf(result));
        Assert.Equal($"model 'llm' {reason} (skipped)", Rank(result).Detail);
    }

    [Fact]
    public async Task ATruncatedAnswer_FailsTheNode_SayingItWasCutAtTheLimit()
    {
        var models = new TextModels(_ => ("[2, 3, 1", ModelStatus.Ok with { OutputTokens = 8, Truncated = true }));

        var result = await Run(Compile(models, Rerank(Ranks, extra: """, "max_output_tokens": 8"""), onFailure: "skip"));

        Assert.Equal(Unchanged, IdsOf(result));
        Assert.Equal(
            "model 'llm' did not answer a JSON array of window ranks (1 to 3): it is not valid JSON (byte 8: '1' is an invalid end of a number. Expected a delimiter); "
            + "the answer starts \"[2, 3, 1\"; the answer was cut at max_output_tokens (8): raise max_output_tokens or shorten the window (skipped)",
            Rank(result).Detail);
    }

    [Fact]
    public async Task ATruncatedAnswerThatStillParses_IsApplied()
    {
        var models = new TextModels(_ => ("[2]", ModelStatus.Ok with { Truncated = true }));

        var result = await Run(Compile(models, Rerank(Ranks)));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(new long[] { 10, 30, 50, 20, 40, 70, 60 }, IdsOf(result));
    }

    [Fact]
    public async Task ALongAnswer_IsQuotedUpTo200Characters()
    {
        var text = "Sure! " + new string('x', 300);

        var result = await Run(Compile(new TextModels(text), Rerank(Ranks), onFailure: "skip"));

        Assert.EndsWith($"the answer starts \"Sure! {new string('x', 194)}…\" (skipped)", Rank(result).Detail);
    }

    [Fact]
    public async Task AFailedCall_OrNoTextOutput_FailsTheNode_LeavingTheOrder()
    {
        var failed = await Run(Compile(new TextModels(_ => (null, ModelStatus.Failed("overloaded"))), Rerank(Ranks), onFailure: "skip"));
        var silent = await Run(Compile(new TextModels(_ => (null, ModelStatus.Ok)), Rerank(Ranks), onFailure: "skip"));

        Assert.Equal("model 'llm' failed: overloaded (skipped)", Rank(failed).Detail);
        Assert.Equal("model 'llm' did not return text output 'answer' (skipped)", Rank(silent).Detail);
        Assert.Equal(Unchanged, IdsOf(failed));
        Assert.Equal(Unchanged, IdsOf(silent));
    }

    [Fact]
    public async Task VersionParam_ChoosesTheVersionPerRequest()
    {
        var models = new TextModels("[1]");

        await Run(Compile(models, Rerank(Ranks, extra: """, "version_param": "llm_version" """)));

        Assert.Equal("v1", Assert.Single(models.Calls).Version);
    }

    [Theory]
    [InlineData(""", "max_output_tokens": 0""", "/max_output_tokens", "LLM_MAX_OUTPUT_TOKENS", "max_output_tokens is 0, but it must be at least 1.")]
    [InlineData(""", "unlisted": "hide" """, "/unlisted", "LLM_UNLISTED", "unlisted is 'hide', but it must be \"keep\" or \"drop\".")]
    [InlineData(""", "version": "v1", "version_param": "llm_version" """, "/version", "MODEL_VERSION", "Set at most one of 'version' and 'version_param'.")]
    [InlineData(""", "version_param": "tone" """, "/version_param", "MODEL_VERSION_ENUM", "Parameter 'tone' must declare an enum of allowed model versions.")]
    [InlineData("window 0", "/window", "RANKER_WINDOW", "window is 0, but it must be at least 1.")]
    public void AnInvalidKey_IsALoadError_AtTheKey(string extra, string key, string code, string message)
    {
        var config = extra == "window 0" ? Rerank(Ranks, window: 0) : Rerank(Ranks, extra: extra);

        var error = Error(TryCompile(new TextModels("[1]"), config));

        Assert.Equal(("/nodes/rank/config" + key, code, message), (error.Pointer, error.Code, error.Message));
    }

    [Theory]
    [InlineData("""{ "model": "llm", "output": "answer", "answer": "ids", "prompts": { "user": "x" } }""", "/answer", "LLM_ANSWER")]
    [InlineData("""{ "model": "llm", "output": "answer", "prompts": { "user": "x" } }""", "", "NODE_BAD_CONFIG")]
    [InlineData("""{ "model": "llm", "output": "answer", "answer": "rank", "prompts": { } }""", "/prompts", "LLM_PROMPTS")]
    [InlineData("""{ "model": "llm", "output": "", "answer": "rank", "prompts": { "user": "x" } }""", "/output", "MODEL_OUTPUT")]
    [InlineData("""{ "model": "nope", "output": "answer", "answer": "rank", "prompts": { "user": "x" } }""", "/model", "MODEL_UNBOUND")]
    public void TheAnswerKind_ThePrompts_TheOutput_AndTheModel_AreRequired(string config, string key, string code)
    {
        var error = Error(TryCompile(new TextModels("[1]"), config));

        Assert.Equal(("/nodes/rank/config" + key, code), (error.Pointer, error.Code));
        Assert.False(string.IsNullOrEmpty(error.Hint), error.ToString());
    }

    /// <summary>Every version a request may use is checked against what the node sends and reads; v2 differs in each way.</summary>
    [Theory]
    [InlineData("float_input", "/nodes/rank/config/prompts/user", "MODEL_TENSOR_MISMATCH",
        "Model 'llm' version 'v2' declares input 'user' as float32 [-1], but LlmReranker@1 sends a rendered prompt, which is text.")]
    [InlineData("extra_input", "/nodes/rank/config/model", "MODEL_INPUT_MISSING", "Model 'llm' version 'v2' requires input 'system', which 'prompts' does not supply.")]
    [InlineData("no_input", "/nodes/rank/config/prompts/user", "MODEL_SIGNATURE", "Model 'llm' version 'v2' has no input 'user'.")]
    [InlineData("float_output", "/nodes/rank/config/output", "MODEL_TENSOR_UNSUPPORTED", "Model 'llm' version 'v2' declares output 'answer' as float32 [-1].")]
    [InlineData("no_output", "/nodes/rank/config/output", "MODEL_SIGNATURE", "Model 'llm' version 'v2' has no output 'answer'.")]
    public void EveryVersionsSignature_IsCheckedAtLoad(string v2, string pointer, string code, string message)
    {
        var text = new TensorSpec("user", TensorElementType.Text, Array.Empty<int>());
        var answer = new TensorSpec("answer", TensorElementType.Text, new[] { 1 }); // a text's shape is not checked
        var signature = v2 switch
        {
            "float_input" => new ModelSignature(new[] { new TensorSpec("user", TensorElementType.Float32, new[] { -1 }) }, new[] { answer }),
            "extra_input" => new ModelSignature(new[] { text, new TensorSpec("system", TensorElementType.Text, Array.Empty<int>()) }, new[] { answer }),
            "no_input" => new ModelSignature(Array.Empty<TensorSpec>(), new[] { answer }),
            "float_output" => new ModelSignature(new[] { text }, new[] { new TensorSpec("answer", TensorElementType.Float32, new[] { -1 }) }),
            _ => new ModelSignature(new[] { text }, new[] { new TensorSpec("reply", TensorElementType.Text, Array.Empty<int>()) }),
        };
        var models = new TextModels(_ => ("[1]", ModelStatus.Ok), version => version == "v2" ? signature : new ModelSignature(new[] { text }, new[] { answer }));

        var error = Error(TryCompile(models, Rerank(Ranks, extra: """, "version_param": "llm_version" """)));

        Assert.Equal((pointer, code, message), (error.Pointer, error.Code, error.Message));
        Assert.False(string.IsNullOrEmpty(error.Hint));
    }
}

/// <summary>
/// What a generative call counts (design 13.1): the tokens and truncations its client reports, for every model call, and
/// the fate of each element of an answer the node parses; a replay counts nothing, and answers from the recording with
/// the same status, so the node decides and reports as the request did.
/// </summary>
[Collection(MetricMeasurement.Name)]
public sealed class LlmMetricsTests
{
    private const string Template = "{{#rows}}{{rank}}. {{col.item.title}}\n{{/rows}}";

    [Fact]
    public async Task TokensTruncationsAndAnswerElements_AreCounted_TaggedWithTheModel_AndNotInAReplay()
    {
        var truncated = false;
        var models = new TextModels(_ => truncated
            ? ("[2, 3", ModelStatus.Ok with { OutputTokens = 64, Truncated = true })
            : ("""[2, "x", 2, 9, 1]""", ModelStatus.Ok with { OutputTokens = 42 }));
        var plan = Compile(models, Rerank(Template, extra: """, "max_output_tokens": 64"""), onFailure: "skip");
        using var metrics = new Measurements();

        var answered = await Run(plan, record: true);
        truncated = true;
        var cut = await Run(plan, record: true);

        Assert.Equal(new long[] { 10, 30, 50, 20, 40, 70, 60 }, IdsOf(answered));
        Assert.Equal(Incoming, IdsOf(cut));
        Assert.Equal(new[] { (42L, "llm", (string?)null), (64L, "llm", null) }, metrics.Of(StrataRecMetrics.Names.ModelOutputTokens).Select(m => (m.Value, m.Model!, m.Outcome)));
        Assert.Equal(new[] { (1L, "llm", (string?)null) }, metrics.Of(StrataRecMetrics.Names.ModelTruncated).Select(m => (m.Value, m.Model!, m.Outcome)));
        Assert.Equal(new[] { ("used", 2L), ("unknown", 2L), ("repeated", 1L) },
            metrics.Of(StrataRecMetrics.Names.ModelAnswerIds).Select(m => (m.Outcome!, m.Value)));
        Assert.All(metrics.Of(StrataRecMetrics.Names.ModelAnswerIds), m => Assert.Equal("llm", m.Model));

        // The recording has the answers and the status details; a replay serves them without calling the model.
        var recorded = Assert.Single(cut.Recording!.ModelCalls);
        Assert.Equal(("[2, 3", 64, (bool?)true), (recorded.Texts["answer"], recorded.OutputTokens, recorded.Truncated));
        Assert.Equal((42, (bool?)null), (Assert.Single(answered.Recording!.ModelCalls).OutputTokens, answered.Recording.ModelCalls[0].Truncated));
        metrics.Clear();
        var calls = models.Calls.Count;

        var replayedAnswer = await Replay(plan, answered);
        var replayedCut = await Replay(plan, cut);

        Assert.Equal(IdsOf(answered), IdsOf(replayedAnswer));
        Assert.Equal(Rank(cut).Detail, Rank(replayedCut).Detail); // "… the answer was cut at max_output_tokens (64) …", from the recorded status
        Assert.Contains("the answer was cut at max_output_tokens (64)", Rank(replayedCut).Detail);
        Assert.Equal(calls, models.Calls.Count);
        Assert.Empty(metrics.All);
    }

    /// <summary>A replay after the limit changed serves no recorded answer: the call differs, so the node follows <c>on_failure</c>.</summary>
    [Fact]
    public async Task AReplayWithAnotherMaxOutputTokens_IsNotServedTheRecordedAnswer()
    {
        var models = new TextModels("[3]");
        var recorded = await Run(Compile(models, Rerank(Template, extra: """, "max_output_tokens": 64"""), onFailure: "skip"), record: true);
        var replay = RunReplay.Create(recorded.Recording!, "/runs/0");

        var replayed = await new PipelineExecutor().ExecuteAsync(Compile(models, Rerank(Template, extra: """, "max_output_tokens": 65"""), onFailure: "skip"),
            new PipelineRequest { RequestId = "r", Inputs = Inputs(), Deadline = RunReplay.Deadline, Seed = 1234, Replay = replay });

        Assert.Equal(new long[] { 50, 30, 10, 20, 40, 70, 60 }, IdsOf(recorded));
        Assert.Equal(Incoming, IdsOf(replayed));
        Assert.Contains(replay.Gaps, g => g is { Kind: "model", Source: "llm", Reason: "not_recorded" });
        Assert.Single(models.Calls);
    }

    private static ValueTask<PipelineResult> Replay(ExecutionPlan plan, PipelineResult recorded) =>
        new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
        {
            RequestId = "r",
            Inputs = Inputs(),
            Deadline = RunReplay.Deadline,
            Seed = 1234,
            Replay = RunReplay.Create(recorded.Recording!, "/runs/0"),
        });

    /// <summary>The model metrics as (name, value, model, outcome), in the order they were recorded.</summary>
    private sealed class Measurements : IDisposable
    {
        private static readonly HashSet<string> Names = new()
        {
            StrataRecMetrics.Names.ModelOutputTokens, StrataRecMetrics.Names.ModelTruncated, StrataRecMetrics.Names.ModelAnswerIds,
        };

        private readonly MeterListener _listener = new();
        private readonly List<(string Name, long Value, string? Model, string? Outcome)> _all = new();

        public Measurements()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == StrataRecTelemetry.Name && Names.Contains(instrument.Name))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                string? model = null, outcome = null;
                foreach (var tag in tags)
                {
                    model = tag.Key == StrataRecTags.Model ? (string?)tag.Value : model;
                    outcome = tag.Key == StrataRecTags.Outcome ? (string?)tag.Value : outcome;
                }

                lock (_all)
                {
                    _all.Add((instrument.Name, value, model, outcome));
                }
            });
            _listener.Start();
        }

        public List<(string Name, long Value, string? Model, string? Outcome)> All
        {
            get
            {
                lock (_all)
                {
                    return _all.ToList();
                }
            }
        }

        public List<(string Name, long Value, string? Model, string? Outcome)> Of(string name) => All.Where(m => m.Name == name).ToList();

        public void Clear()
        {
            lock (_all)
            {
                _all.Clear();
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
