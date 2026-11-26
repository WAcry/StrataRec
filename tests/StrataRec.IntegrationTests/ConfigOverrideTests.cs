using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting;
using StrataRec.Hosting.Bindings;
using StrataRec.Hosting.Replay;
using StrataRec.Logging;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Operators;

namespace StrataRec.IntegrationTests;

/// <summary>
/// A request's configuration override (<see cref="DebugOptions.ConfigOverride"/>): a merge patch of the pipeline
/// applied after the request's arms, compiled for that request alone, on the home_feed sample.
/// </summary>
public sealed class ConfigOverrideTests
{
    private static readonly string[] TreatmentArms = { "exp_freshness_calibration/calibrated", "exp_mmr_diversity/mmr", "exp_quality_weight/strong" };

    private const string NotAJsonNumber =
        "CONFIG_OVERRIDE_NOT_CANONICAL request/config_override/params/final_top_k: NaN and Infinity are not JSON numbers (RFC 8785 section 3.2.2.3). " +
        "(hint: use a finite number or an explicit string such as \"none\")";

    [Fact]
    public async Task AnOverride_WinsOverTheArms_KeepsWhatItDoesNotTouch_AndLeavesTheServedPlanAsItWas()
    {
        var (engine, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());

        // exp_quality_weight/strong sets final_top_k 8 and quality_weight 0.8; the override sets final_top_k 3.
        var overridden = await engine.ExecuteAsync(Request("ov", TreatmentArms, Override("""{ "params": { "final_top_k": 3 } }""")),
            TestContext.Current.CancellationToken);
        var served = await engine.ExecuteAsync(Request("plain", TreatmentArms, null), TestContext.Current.CancellationToken);

        Assert.True(overridden.Succeeded, overridden.Error);
        Assert.Equal(3, overridden.Items.Count);
        var parameters = overridden.Recording!.Runs[0].Params;
        Assert.Equal("3", parameters["final_top_k"].Value!.Value.GetRawText());
        Assert.Equal("0.8", parameters["quality_weight"].Value!.Value.GetRawText()); // the arm's, which the override does not touch
        Assert.Equal(TreatmentArms.Order(), overridden.Experiments.Assigned.Order());
        Assert.Empty(overridden.Experiments.Triggered); // the plan it ran has no experiments
        Assert.Equal(served.ManifestId, overridden.ManifestId);

        Assert.True(served.Succeeded, served.Error);
        Assert.Equal(8, served.Items.Count);
        Assert.Equal(overridden.Items.Take(3).Select(i => i.Id), served.Items.Take(3).Select(i => i.Id)); // the same ranking, cut shorter
    }

    [Fact]
    public async Task AnOverride_CanDeleteANode()
    {
        var (engine, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());

        var overridden = await engine.ExecuteAsync(Request("ov", null, Override("""{ "nodes": { "diversity_cap": null } }""")), TestContext.Current.CancellationToken);
        var served = await engine.ExecuteAsync(Request("plain", null, null), TestContext.Current.CancellationToken);

        Assert.True(overridden.Succeeded, overridden.Error);
        Assert.DoesNotContain(overridden.Trace!, n => n.NodeId == "diversity_cap");
        Assert.Contains(served.Trace!, n => n.NodeId == "diversity_cap");
    }

    [Theory]
    // What the compile of the merged configuration finds at a value the override sets is the override's, at its pointer
    //: it was at the pipeline's pointer, and the request had no ErrorPointer.
    [InlineData("""{ "params": { "final_top_k": "three" } }""", "PARAM_BAD_VALUE request/config_override/params/final_top_k: Parameter 'final_top_k'",
        "/config_override/params/final_top_k")]
    [InlineData("""{ "nodez": {} }""", "CONFIG_OVERRIDE_UNKNOWN_SECTION request/config_override/nodez", "/config_override/nodez")]
    [InlineData("""{ "nodes": { "rank_sort": { "config": { "score_col": "no.such_column" } } } }""",
        "request/config_override/nodes/rank_sort/config/score_col", "/config_override/nodes/rank_sort/config/score_col")]
    // An integer the canonical form cannot tell from a float passes the override's own check and is refused by
    // the parameter's binding: at the override, with the binding's code and words, as the same integer written as digits is.
    [InlineData("""{ "params": { "final_top_k": -90071992547409930E-1 } }""",
        "PARAM_BAD_VALUE request/config_override/params/final_top_k: Parameter 'final_top_k' must be an integer (int64) that RFC 8785 represents exactly, " +
        "got the number -90071992547409930E-1, whose canonical form would be -9007199254740992, a different integer; " +
        "write integers beyond ±2^53 (9007199254740992) as strings", "/config_override/params/final_top_k")]
    [InlineData("""{ "params": { "recall_quotas": { "popular": -90071992547409930E-1 } } }""",
        "PARAM_BAD_VALUE request/config_override/params/recall_quotas/popular: Parameter 'recall_quotas.popular' must be an integer (int64) that RFC 8785",
        "/config_override/params/recall_quotas/popular")]
    [InlineData("""{ "features": { "item.clicks_7d": { "default": "many" } } }""", "request/config_override/features/item.clicks_7d/default",
        "/config_override/features/item.clicks_7d/default")]
    // An integer beyond 2^53 RFC 8785 cannot hold exactly: the compiler's hash of the node threw out of the request.
    [InlineData("""{ "nodes": { "coarse_top_k": { "order": 1152921504606846976 } } }""",
        "CONFIG_OVERRIDE_NOT_CANONICAL request/config_override/nodes/coarse_top_k/order", "/config_override/nodes/coarse_top_k/order")]
    // A host's JsonNode.Parse keeps a repeated member, and the object threw ArgumentException out of the request
    // when the compiler first enumerated it (the patch's sections, or a nested object when it was canonicalized).
    [InlineData("""{ "params": { "final_top_k": 3 }, "params": { "final_top_k": 4 } }""",
        "CONFIG_OVERRIDE_INVALID request/config_override/params: Member 'params' is given more than once. (hint: give each member once", "/config_override/params")]
    [InlineData("""{ "params": { "final_top_k": 3, "final_top_k": 4 } }""",
        "CONFIG_OVERRIDE_INVALID request/config_override/params/final_top_k: Member 'final_top_k' is given more than once. (hint: give each member once",
        "/config_override/params/final_top_k")]
    public async Task AnOverrideThatDoesNotCompile_RejectsTheRequestAsInvalid_NamingTheError(string patch, string error, string? pointer)
    {
        var (engine, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());

        var result = await engine.ExecuteAsync(Request("bad", TreatmentArms, Override(patch)), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(RecErrorKind.InvalidRequest, result.ErrorKind);
        Assert.StartsWith("the configuration override does not compile", result.Error, StringComparison.Ordinal);
        Assert.Contains(error, result.Error, StringComparison.Ordinal);
        Assert.Equal(pointer, result.ErrorPointer);
        Assert.Equal(TreatmentArms.Order(), result.Experiments.Assigned.Order());
        Assert.Null(result.Recording); // nothing ran
    }

    /// <summary>
    /// What a host builds in code, beyond what its text can say. Each was an exception out of the request (or,
    /// for the integer, an error with no pointer): names that differ only in case under a case-insensitive parse, which the
    /// check passed and the object threw on when first enumerated; an object repeating a member beside a NaN, which made
    /// the check give up and the canonical form throw; a tree nested deeper than a reader's default depth, which threw in
    /// the check. Each is an invalid request at the override's pointer. A number JSON cannot write is
    /// CONFIG_OVERRIDE_NOT_CANONICAL whatever its CLR type, as a NaN double is: a Half NaN or infinity threw
    /// JsonReaderException out of the request where the canonical form wrote it, and a boxed NaN was
    /// CONFIG_OVERRIDE_INVALID, written by the check.
    /// </summary>
    [Theory]
    [InlineData("ignore_case", "CONFIG_OVERRIDE_INVALID request/config_override/params/FINAL_TOP_K: Member 'FINAL_TOP_K' is given more than once",
        "/config_override/params/FINAL_TOP_K")]
    [InlineData("repeated_beside_nan", "CONFIG_OVERRIDE_INVALID request/config_override/nodes/coarse_top_k: Member 'coarse_top_k' is given more than once",
        "/config_override/nodes/coarse_top_k")]
    [InlineData("nan", "CONFIG_OVERRIDE_NOT_CANONICAL request/config_override/params/final_top_k", "/config_override/params/final_top_k")]
    [InlineData("half_nan", NotAJsonNumber, "/config_override/params/final_top_k")]
    [InlineData("half_infinity", NotAJsonNumber, "/config_override/params/final_top_k")]
    [InlineData("boxed_nan", NotAJsonNumber, "/config_override/params/final_top_k")]
    [InlineData("boxed_half_nan", NotAJsonNumber, "/config_override/params/final_top_k")]
    public async Task AHostBuiltOverride_ThatCannotBeRead_RejectsTheRequestAtItsPointer(string shape, string error, string pointer)
    {
        var (engine, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        var configOverride = shape switch
        {
            "ignore_case" => JsonNode.Parse("""{ "params": { "final_top_k": 3, "FINAL_TOP_K": 4 } }""",
                new JsonNodeOptions { PropertyNameCaseInsensitive = true })!.AsObject(),
            "repeated_beside_nan" => new JsonObject
            {
                ["params"] = new JsonObject { ["final_top_k"] = double.NaN },
                ["nodes"] = JsonNode.Parse("""{ "coarse_top_k": { "order": 1 }, "coarse_top_k": { "order": 2 } }"""),
            },
            "half_nan" => new JsonObject { ["params"] = new JsonObject { ["final_top_k"] = JsonValue.Create(Half.NaN) } },
            "half_infinity" => new JsonObject { ["params"] = new JsonObject { ["final_top_k"] = JsonValue.Create(Half.PositiveInfinity) } },
            "boxed_nan" => new JsonObject { ["params"] = new JsonObject { ["final_top_k"] = JsonValue.Create<object>(double.NaN) } },
            "boxed_half_nan" => new JsonObject { ["params"] = new JsonObject { ["final_top_k"] = JsonValue.Create<object>(Half.NaN) } },
            _ => new JsonObject { ["params"] = new JsonObject { ["final_top_k"] = double.NaN } },
        };

        var result = await engine.ExecuteAsync(Request("host", TreatmentArms, configOverride), TestContext.Current.CancellationToken);

        Assert.Equal(RecErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Contains(error, result.Error, StringComparison.Ordinal);
        Assert.Equal(pointer, result.ErrorPointer);
    }

    /// <summary>
    /// An override nested deeper than a reader's default depth (64) threw out of the request where it was
    /// checked. It may nest <see cref="PlanCompiler.OverrideMaxDepth"/> levels (62), so that every override that runs can be
    /// recorded and replayed from its file: at the limit it compiles as before (an array is no value of final_top_k), one
    /// level more is an invalid request at the first level too deep.
    /// </summary>
    [Theory]
    [InlineData(62)]
    [InlineData(63)]
    [InlineData(70)]
    public async Task AnOverrideNestedPastTheLimit_IsAnInvalidRequest_AtTheFirstLevelTooDeep(int levels)
    {
        var (engine, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        JsonNode value = 3;
        for (var level = 3; level <= levels; level++) // the override is level 1, params level 2
        {
            value = new JsonArray(value);
        }

        var result = await engine.ExecuteAsync(Request("deep", TreatmentArms, new JsonObject { ["params"] = new JsonObject { ["final_top_k"] = value } }),
            TestContext.Current.CancellationToken);

        Assert.Equal(RecErrorKind.InvalidRequest, result.ErrorKind);
        if (levels <= PlanCompiler.OverrideMaxDepth)
        {
            Assert.Contains("PARAM_BAD_VALUE request/config_override/params/final_top_k: ", result.Error, StringComparison.Ordinal);
            Assert.Equal("/config_override/params/final_top_k", result.ErrorPointer);
            return;
        }

        var deepest = "/config_override/params/final_top_k" + string.Concat(Enumerable.Repeat("/0", PlanCompiler.OverrideMaxDepth - 2)); // level 63
        Assert.Contains($"CONFIG_OVERRIDE_INVALID request{deepest}: Objects and arrays nest deeper than 62 levels here: this one is level 63. " +
            "(hint: nest objects and arrays at most 62 levels deep, counting the outermost)", result.Error, StringComparison.Ordinal);
        Assert.Equal(deepest, result.ErrorPointer);
    }

    /// <summary>
    /// A CLR value the host wraps (JsonValue.Create) is JSON only once it is written, and the check passed it as a
    /// leaf: an object holding one name twice (a property and extension data) threw ArgumentException out of the request
    /// where the canonical form wrote it (it used to be an invalid request at its member), and a cycle threw
    /// JsonException there. Each is an invalid request at its pointer.
    /// </summary>
    [Theory]
    [InlineData("repeated", "/config_override/nodes/probe/config/payload/A", "Member 'A' is given more than once.")]
    [InlineData("cycle", "/config_override/nodes/probe/config/payload", "This value cannot be written as JSON: A possible object cycle was detected.")]
    public async Task AWrappedClrValue_ThatJsonCannotHold_IsAnInvalidRequest_AtItsPointer(string shape, string pointer, string error)
    {
        await using var engine = OpaqueEngine();
        var cycle = new Cycle();
        cycle.Next = cycle;
        var payload = shape == "repeated" ? JsonValue.Create(new RepeatedName()) : JsonValue.Create(cycle);

        var result = await engine.ExecuteAsync(Request("wrapped", null, Probe(payload)), TestContext.Current.CancellationToken);

        Assert.Equal(RecErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Contains($"CONFIG_OVERRIDE_INVALID request{pointer}: {error}", result.Error, StringComparison.Ordinal);
        Assert.Equal(pointer, result.ErrorPointer);
    }

    /// <summary>
    /// A wrapped CLR <c>object[]</c> nested past the limit ran, and writing its recording threw JsonException at
    /// <c>$.Request.ConfigOverride</c>; the same arrays built of JsonArray were an invalid request. Wrapped or not, an
    /// override of 62 levels runs, and its recording is written, read and replayed; one of 63 is an invalid request at level 63.
    /// </summary>
    [Theory]
    [InlineData(62, false)]
    [InlineData(62, true)]
    [InlineData(63, false)]
    [InlineData(63, true)]
    public async Task AWrappedClrArray_NestsNoDeeperThanAJsonArray(int levels, bool wrapped)
    {
        await using var engine = OpaqueEngine();
        const int PayloadLevel = 5; // the override, nodes, probe, config, then the payload
        object clr = 1;
        JsonNode explicitNodes = 1;
        for (var level = PayloadLevel; level <= levels; level++)
        {
            clr = new object[] { clr };
            explicitNodes = new JsonArray(explicitNodes);
        }

        var result = await engine.ExecuteAsync(Request("deep", null, Probe(wrapped ? JsonValue.Create(clr) : explicitNodes)), TestContext.Current.CancellationToken);

        if (levels > PlanCompiler.OverrideMaxDepth)
        {
            var deepest = "/config_override/nodes/probe/config/payload" + string.Concat(Enumerable.Repeat("/0", PlanCompiler.OverrideMaxDepth + 1 - PayloadLevel));
            Assert.Equal(RecErrorKind.InvalidRequest, result.ErrorKind);
            Assert.Contains($"CONFIG_OVERRIDE_INVALID request{deepest}: Objects and arrays nest deeper than 62 levels here: this one is level 63.", result.Error,
                StringComparison.Ordinal);
            Assert.Equal(deepest, result.ErrorPointer);
            return;
        }

        Assert.True(result.Succeeded, result.Error);
        var bundle = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(result.Recording!));
        var outcome = await engine.ReplayAsync(bundle, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(outcome.Report.Diff.Identical, ReplayTests.Describe(outcome.Report));
    }

    /// <summary>
    /// Every reader of a wrapped CLR value writes it anew, and a value may write something else the second time:
    /// the canonical form, the merge and the recording each wrote this one, which writes another popular quota each time,
    /// so the recording held a quota that did not run. It is written by the check, and what it wrote then runs and is
    /// recorded: the replay is identical, and nothing writes it after the request.
    /// </summary>
    [Fact]
    public async Task AWrappedClrValue_IsWrittenOnce_AndRunsAndIsRecordedAsItWasWritten()
    {
        var (engine, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        var quotas = new CountingQuota();

        var recorded = await engine.ExecuteAsync(Request("counting", null, Quotas(JsonValue.Create(quotas))), TestContext.Current.CancellationToken);

        Assert.True(recorded.Succeeded, recorded.Error);
        var writes = quotas.Writes;
        Assert.Equal(1, writes); // Asking the value its kind wrote it, then writing it wrote it again
        var ran = recorded.Recording!.Runs[0].Params["recall_quotas.popular"].Value!.Value.GetRawText();
        Assert.Equal("21", ran);
        var bundle = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording!));
        Assert.Equal(ran, bundle.Request.ConfigOverride!["params"]!["recall_quotas"]!["popular"]!.ToJsonString());
        var outcome = await engine.ReplayAsync(bundle, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(outcome.Report.Diff.Identical, ReplayTests.Describe(outcome.Report));
        Assert.Empty(outcome.Report.Params);
        Assert.Equal(writes, quotas.Writes);
    }

    /// <summary>
    /// A wrapped CLR value is checked as the JSON it writes, by its own converter: a double NaN one writes as
    /// the string "NaN" is that string, which runs and is recorded, not the number JSON cannot write.
    /// </summary>
    [Fact]
    public async Task AClrNaNItsConverterWritesAsAString_IsThatString()
    {
        await using var engine = OpaqueEngine();
        var options = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        options.Converters.Add(new NaNAsString());
        var payload = JsonValue.Create(double.NaN, (JsonTypeInfo<double>)options.GetTypeInfo(typeof(double)));

        var result = await engine.ExecuteAsync(Request("wrapped", null, Probe(payload)), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("\"NaN\"", result.Recording!.Request.ConfigOverride!["nodes"]!["probe"]!["config"]!["payload"]!.ToJsonString());
    }

    private sealed class NaNAsString : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) =>
            writer.WriteStringValue(double.IsNaN(value) ? "NaN" : value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A wrapped CLR value that writes an object is merged as RFC 7396 merges an object, as the same object
    /// built of JsonObject is: the merge set it whole, a leaf to it, and the baseline's two_tower quota was dropped
    /// (RECALL_UNKNOWN_SOURCE). The JSON of a patch, not how the host built it, decides how it merges.
    /// </summary>
    [Fact]
    public async Task AWrappedClrObject_MergesAsTheObjectItWrites()
    {
        var (engine, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());

        var wrapped = await engine.ExecuteAsync(Request("quotas", null, Quotas(JsonValue.Create(new Dictionary<string, int> { ["popular"] = 5 }))),
            TestContext.Current.CancellationToken);
        var built = await engine.ExecuteAsync(Request("quotas", null, Quotas(new JsonObject { ["popular"] = 5 })), TestContext.Current.CancellationToken);

        Assert.True(wrapped.Succeeded, wrapped.Error);
        Assert.True(built.Succeeded, built.Error);
        foreach (var result in new[] { wrapped, built })
        {
            var parameters = result.Recording!.Runs[0].Params;
            Assert.Equal(("5", "40"), (parameters["recall_quotas.popular"].Value!.Value.GetRawText(), parameters["recall_quotas.two_tower"].Value!.Value.GetRawText()));
        }

        Assert.Equal(built.Items.Select(i => i.Id), wrapped.Items.Select(i => i.Id));
    }

    private static JsonObject Quotas(JsonNode? quotas) => new() { ["params"] = new JsonObject { ["recall_quotas"] = quotas } };

    /// <summary>
    /// What the merged compile finds at the baseline's <c>final_top_k</c> (its value, below the minimum the
    /// override sets) stays at the baseline when the override sets only <c>FINAL_TOP_K</c>, another parameter to the
    /// merge, which compares names by ordinal. Traced back through a host's object parsed ignoring case, it moved to
    /// <c>/config_override/params/final_top_k</c>, a member the override does not have. Only the host's options differ.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADiagnosticAtTheBaseline_StaysThere_HoweverTheHostsObjectComparesNames(bool ignoreCase)
    {
        var (engine, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        var configOverride = JsonNode.Parse("""{ "param_schema": { "final_top_k": { "min": 1000 } }, "params": { "FINAL_TOP_K": 1000 } }""",
            new JsonNodeOptions { PropertyNameCaseInsensitive = ignoreCase })!.AsObject();

        var result = await engine.ExecuteAsync(Request("case", null, configOverride), TestContext.Current.CancellationToken);

        Assert.Equal(RecErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Contains("PARAM_OUT_OF_RANGE pipeline:home_feed/params/final_top_k: Parameter 'final_top_k': 10 is below the minimum 1000.", result.Error,
            StringComparison.Ordinal);
        Assert.Null(result.ErrorPointer);
    }

    /// <summary>An engine on home_feed with <c>Opaque@1</c>, a node that takes any JSON as its <c>payload</c> and does nothing.</summary>
    private static RecEngine OpaqueEngine()
    {
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment with { Operators = OperatorRegistry.CreateDefault().Add(new OpaqueFactory()) });
        var load = engine.Load(host.Request);
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        return engine;
    }

    /// <summary>An override adding an <c>Opaque@1</c> node <c>probe</c> with <paramref name="payload"/>, at level 5.</summary>
    private static JsonObject Probe(JsonNode? payload) => new()
    {
        ["nodes"] = new JsonObject
        {
            ["probe"] = new JsonObject { ["op"] = "Opaque@1", ["order"] = 1200, ["config"] = new JsonObject { ["payload"] = payload } },
        },
    };

    /// <summary>Writes <c>{"A":1,"A":2}</c>: a property, and extension data holding its name.</summary>
    public sealed class RepeatedName
    {
        public int A { get; } = 1;

        [JsonExtensionData]
        public Dictionary<string, object> More { get; } = new() { ["A"] = 2 };
    }

    public sealed class Cycle
    {
        public Cycle? Next { get; set; }
    }

    /// <summary>
    /// Writes the quota map <c>{"two_tower": 40, "popular": n}</c>, n one more on each write: the second write is not the
    /// first. Both quotas, so that it runs however it is merged.
    /// </summary>
    [JsonConverter(typeof(CountingQuotaConverter))]
    public sealed class CountingQuota
    {
        public int Writes { get; set; }
    }

    public sealed class CountingQuotaConverter : JsonConverter<CountingQuota>
    {
        public override CountingQuota Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, CountingQuota value, JsonSerializerOptions options)
        {
            value.Writes++;
            writer.WriteStartObject();
            writer.WriteNumber("two_tower", 40);
            writer.WriteNumber("popular", 20 + value.Writes);
            writer.WriteEndObject();
        }
    }

    public sealed class OpaqueConfig
    {
        public required JsonElement Payload { get; init; }
    }

    private sealed class OpaqueFactory : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } = new("Opaque", 1, Effects.None, typeof(OpaqueConfig), "Takes any JSON as its payload, and does nothing.");

        public Operator? Compile(OperatorCompileContext context) => context.BindConfig<OpaqueConfig>() is null ? null : new Opaque();

        private sealed class Opaque : Operator
        {
            public override ValueTask ExecuteAsync(OperatorContext context) => ValueTask.CompletedTask;
        }
    }

    /// <summary>What a configuration nobody released produced never reaches the log sinks; asked for, it is captured.</summary>
    [Fact]
    public async Task AnOverride_WritesNoLogs_AndCapturesThemWhenAsked()
    {
        var host = HomeFeedTestHost.Load();
        var sink = new InMemoryLogSink();
        await using var logs = new LogPipeline(sink);
        await using var engine = new RecEngine(host.Environment, logs: logs);
        var load = engine.Load(host.Request);
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        var patch = """{ "params": { "final_top_k": 3 } }""";

        var silent = await engine.ExecuteAsync(Request("ov", null, Override(patch)), TestContext.Current.CancellationToken);
        var captured = await engine.ExecuteAsync(Request("ov-logs", null, Override(patch), captureLogs: true), TestContext.Current.CancellationToken);
        await logs.FlushAsync(TestContext.Current.CancellationToken);
        Assert.True(silent.Succeeded && captured.Succeeded, silent.Error ?? captured.Error);
        Assert.Empty(sink.Snapshot());
        Assert.Null(silent.LogBatches);
        Assert.NotEmpty(captured.LogBatches!);

        var plain = await engine.ExecuteAsync(Request("plain", null, null), TestContext.Current.CancellationToken);
        await logs.FlushAsync(TestContext.Current.CancellationToken);
        Assert.True(plain.Succeeded, plain.Error);
        Assert.NotEmpty(sink.Snapshot()); // the served configuration logs as before
    }

    [Fact]
    public async Task ARecordedOverride_ReplaysExactly_OnAFreshEngine()
    {
        var (engine, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        var recorded = await engine.ExecuteAsync(
            Request("ov", TreatmentArms, Override("""{ "params": { "final_top_k": 4 }, "nodes": { "diversity_cap": null } }""")), TestContext.Current.CancellationToken);
        Assert.True(recorded.Succeeded, recorded.Error);

        var bundle = ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording!));
        Assert.Equal(4, (int)bundle.Request.ConfigOverride!["params"]!["final_top_k"]!);
        var (fresh, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        var outcome = await fresh.ReplayAsync(bundle, cancellationToken: TestContext.Current.CancellationToken);

        var report = outcome.Report;
        Assert.True(report.Diff.Identical, ReplayTests.Describe(report));
        Assert.Equal(recorded.Items.Select(i => i.Id), outcome.Result.Items.Select(i => i.Id));
        Assert.Equal(4, outcome.Result.Items.Count);
        Assert.Equal(report.RecordedArms.Order(), report.ReplayedArms.Order());
        Assert.Equal(TreatmentArms.Order(), report.ReplayedArms.Order());
        Assert.Empty(report.Params);
        Assert.Empty(report.Gaps);
        Assert.All(report.Nodes, n => Assert.False(n.Changed, $"{n.Node}: {n.Recorded} -> {n.Replayed}"));
    }

    /// <summary>
    /// An override builds on the configuration as it was loaded, not on the host's objects: the host edits them
    /// after the load (a parameter, an arm removed), and an empty override still runs what is served, takes the arm the
    /// served plan has, and replays identically on a fresh engine loaded from the files.
    /// </summary>
    [Fact]
    public async Task AnOverride_BuildsOnTheConfigurationAsLoaded_NotOnTheHostsObjectsEditedSince()
    {
        var host = HomeFeedTestHost.Load();
        var (engine, _) = MetricsTests.LoadEngine(host);
        host.Request.Pipeline.Params["final_top_k"] = 2;
        Assert.True(host.Request.Experiments.Single(e => e.Experiment.ExperimentId == "exp_quality_weight").Experiment.Arms.Remove("strong"));

        var served = await engine.ExecuteAsync(Request("plain", null, null), TestContext.Current.CancellationToken);
        var empty = await engine.ExecuteAsync(Request("empty", null, Override("{}")), TestContext.Current.CancellationToken);
        var strong = await engine.ExecuteAsync(Request("strong", TreatmentArms, Override("{}")), TestContext.Current.CancellationToken);

        Assert.True(served.Succeeded && empty.Succeeded, served.Error ?? empty.Error);
        Assert.Equal(served.Items.Select(i => i.Id), empty.Items.Select(i => i.Id));
        Assert.True(strong.Succeeded, strong.Error);
        Assert.Equal(8, strong.Items.Count); // the served strong arm's final_top_k

        var (fresh, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        var outcome = await fresh.ReplayAsync(ReplayBundleFile.Parse(ReplayBundleFile.Serialize(empty.Recording!)), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(outcome.Report.Diff.Identical, ReplayTests.Describe(outcome.Report));
    }

    /// <summary>
    /// The override's plan takes over what the served plan has loaded and holds nothing after its request: once the engine
    /// is disposed, nothing of the binding host is left (a plan the request kept would keep its tables and instances).
    /// </summary>
    [Fact]
    public async Task AnOverride_SharesTheServedBindings_AndReleasesThemWithItsRequest()
    {
        var host = HomeFeedTestHost.Load();
        await using var bindings = new BindingHost();
        var engine = new RecEngine(host.Environment, bindings: bindings);
        Assert.True(engine.Load(host.Request).Applied);
        var (tables, instances) = (bindings.TableCount, bindings.InstanceCount);
        Assert.True(tables > 0 && instances > 0);

        var result = await engine.ExecuteAsync(Request("ov", null, Override("""{ "params": { "final_top_k": 3 } }""")), TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal((tables, instances), (bindings.TableCount, bindings.InstanceCount)); // nothing loaded twice

        await engine.DisposeAsync();
        Assert.True(SpinWait.SpinUntil(() => bindings.TableCount == 0 && bindings.InstanceCount == 0, TimeSpan.FromSeconds(30)),
            $"{bindings.TableCount} tables and {bindings.InstanceCount} instances are still held");
    }

    private static JsonObject Override(string json) => JsonNode.Parse(json)!.AsObject();

    private static RecRequest Request(string requestId, IReadOnlyList<string>? arms, JsonObject? configOverride, bool captureLogs = false) => new()
    {
        PipelineId = "home_feed",
        RequestId = requestId,
        Inputs = HomeFeedTestHost.Inputs(1001, "home"),
        Deadline = TimeSpan.FromMilliseconds(HomeFeedTestHost.UntimedMs),
        TimeMs = 1_767_225_600_000,
        Debug = new DebugOptions { Record = true, Trace = true, ForceArms = arms, ConfigOverride = configOverride, CaptureLogs = captureLogs },
    };
}
