using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;
using StrataRec.Core.Services;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Features.Runtime;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Replay;

namespace StrataRec.IntegrationTests;

/// <summary>
/// Entities with args (IO step 5): a service computes their features once per request from the arguments, a request input
/// or a context feature of any type, through the service's own path: its cache, the source's and its own timeout, the
/// recording and the replay. A failed argument fails the entity without a call; a failed call fails its features.
/// </summary>
public sealed class ComputedEntityEndToEndTests
{
    [Fact]
    public async Task AnEntityWithArgs_IsComputedOncePerRequest_FromItsArguments_AndItsOutputsTakeTheFeaturesTypes()
    {
        var service = new IntentService();
        var plan = Compile(Workspace(), service);

        var result = await Execute(plan, Inputs(user: 7, query: "shoes", recent: [11, 12]));

        Assert.True(result.Succeeded, result.Error);
        var call = Assert.Single(service.Calls);
        Assert.Equal(0, call.BatchSize);
        Assert.Equal(0, call.Limit);
        Assert.Equal(["label", "score", "topics"], call.Outputs.Order(StringComparer.Ordinal));
        Assert.Equal(
            ["ctr:Float32[]=0.25", "query:String[]=shoes", "recent:Int64[2]=11,12"],
            call.Inputs.Order(StringComparer.Ordinal));
        Assert.All(result.Items, item =>
        {
            Assert.Equal(0.75, item.Columns["score"].AsFloat64(), 6); // a float32 output for the float32 feature
            Assert.Equal("sporty", item.Columns["label"].AsString()); // a text output
            Assert.Equal([3L, 4L], item.Columns["topics"].AsInt64List().ToArray());
        });
    }

    [Fact]
    public async Task AMissingArgument_IsSentAsMissing_AndTheServiceStillAnswers()
    {
        var service = new IntentService();
        var plan = Compile(Workspace(), service);

        var result = await Execute(plan, Inputs(user: 8, query: null, recent: null)); // user 8 has no record: ctr is absent

        Assert.True(result.Succeeded, result.Error);
        var call = Assert.Single(service.Calls);
        Assert.Equal(["ctr:Float32[]=missing", "query:String[]=missing", "recent:Int64[-1]=missing"], call.Inputs.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AFailedArgument_FailsTheEntity_WithoutACall()
    {
        var service = new IntentService();
        var plan = Compile(Workspace(users: """{ "connector": "memory@1", "data": {}, "fail": true, "timeout_ms": 1000 }"""), service);

        var result = await Execute(plan, Inputs(user: 7, query: "shoes", recent: null));

        Assert.False(result.Succeeded);
        Assert.Contains("output reads a feature that failed", result.Error);
        Assert.Empty(service.Calls);
    }

    [Fact]
    public async Task AFailedCall_FailsItsFeatures_WhichANodeThatSkipsOnFailureGoesOnWithout()
    {
        var service = new IntentService { Fail = true };
        var plan = Compile(Workspace(readInOutput: false), service);

        var result = await Execute(plan, Inputs(user: 7, query: "shoes", recent: null));

        Assert.True(result.Succeeded, result.Error);
        Assert.Single(service.Calls);
        var boost = Assert.Single(result.Outcomes, n => n.NodeId == "boost");
        Assert.Equal(NodeOutcomeKind.Failed, boost.Kind);
        Assert.Contains("source 'intent' failed: service 'intent_model': the model is down", boost.Detail);
        Assert.All(result.Items, item => Assert.True(item.Columns["s"].IsMissing)); // not written
    }

    [Fact]
    public async Task ASlowService_IsCutOffAtTheSourcesTimeout()
    {
        var service = new IntentService { DelayMs = 10_000 };
        var plan = Compile(Workspace(readInOutput: false, intentTimeoutMs: 100), service);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        var result = await Execute(plan, Inputs(user: 7, query: "shoes", recent: null));

        Assert.True(result.Succeeded, result.Error);
        Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
        var boost = Assert.Single(result.Outcomes, n => n.NodeId == "boost");
        Assert.Equal(NodeOutcomeKind.Failed, boost.Kind);
        Assert.Contains("source 'intent' timed out", boost.Detail);
    }

    /// <summary>
    /// An output of another type than the feature's, or of several values for a scalar, fails the call, saying what the
    /// feature takes: no number is widened or rounded (review of the IO redesign: a float64 output was rounded to the
    /// float32 feature, and an int32 list widened to an int64 one).
    /// </summary>
    [Theory]
    [InlineData("string", "output 'score' as string, which a float32 feature cannot take: it takes float32, the type it is declared with")]
    [InlineData("float64", "output 'score' as float64, which a float32 feature cannot take: it takes float32, the type it is declared with")]
    [InlineData("two", "output 'score' with 2 values; a float32 feature takes one")]
    public async Task AnOutputTheFeatureCannotTake_FailsTheCall(string answer, string error)
    {
        var service = new IntentService { Score = answer };
        var plan = Compile(Workspace(readInOutput: false), service);

        var result = await Execute(plan, Inputs(user: 7, query: "shoes", recent: null));

        var boost = Assert.Single(result.Outcomes, n => n.NodeId == "boost");
        Assert.Equal(NodeOutcomeKind.Failed, boost.Kind);
        Assert.Contains(error, boost.Detail);
    }

    /// <summary>A list's elements are of the feature's element type too: int32 elements are not widened to an int64 list.</summary>
    [Fact]
    public async Task AListOfAnotherElementType_FailsTheCall()
    {
        var service = new IntentService { Topics = "int32" };
        var plan = Compile(Workspace(), service);

        var result = await Execute(plan, Inputs(user: 7, query: "shoes", recent: null));

        Assert.False(result.Succeeded);
        Assert.Contains("output 'topics' as int32, which a list<int64>? feature cannot take: it takes int64, the type it is declared with", result.Error);
    }

    /// <summary>A service's cache keys the answer by the arguments: requests with the same ones share it.</summary>
    [Fact]
    public async Task TheServicesCache_AnswersRequestsWithTheSameArguments()
    {
        var service = new IntentService();
        var root = Workspace(serviceCache: """, "cache": { "ttl_ms": 600000, "max_bytes": "1MiB" }""");
        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("p", "local", diagnostics);
        await using var engine = new RecEngine(new PlanEnvironment { BaseDirectory = root, Bindings = PlanEnvironment.DefaultBindings().Add(service) });
        Assert.True(engine.Load(request!).Applied);

        async Task<int> CallsAfter(string query)
        {
            var result = await engine.ExecuteAsync(new RecRequest
            {
                PipelineId = "p", RequestId = "r-" + query, Inputs = Inputs(user: 7, query: query, recent: null), Deadline = TimeSpan.FromSeconds(20),
            }, TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded, result.Error);
            return service.Calls.Count;
        }

        Assert.Equal(1, await CallsAfter("shoes"));
        for (var attempt = 0; await CallsAfter("shoes") > 1; attempt++)
        {
            Assert.True(attempt < 2_000, "no request was answered from the cache"); // the write-back is applied in the background
            await Task.Delay(5, TestContext.Current.CancellationToken);
            service.Forget(1);
        }

        Assert.Equal(2, await CallsAfter("hats"));
        Assert.Equal(["query:String[]=shoes", "query:String[]=hats"], service.Calls.Select(c => c.Inputs.Single(i => i.StartsWith("query", StringComparison.Ordinal))));
    }

    /// <summary>
    /// Values past the TTL that the service's cache answers with, when the service fails, degrade the entity's features
    /// (and the node reading them), as a connector's partial answer does; a replay of the request is identical.
    /// </summary>
    [Fact]
    public async Task AStaleAnswerOfTheServicesCache_DegradesItsFeatures_AndReplaysAsServed()
    {
        var token = TestContext.Current.CancellationToken;
        var service = new IntentService();
        var clock = new MovableClock();
        var root = Workspace(readInOutput: false, serviceCache: """, "cache": { "ttl_ms": 1000, "max_bytes": "1MiB", "stale_if_error_ms": 60000 }""");
        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("p", "local", diagnostics);
        await using var engine = new RecEngine(new PlanEnvironment { BaseDirectory = root, Bindings = PlanEnvironment.DefaultBindings().Add(service), Clock = clock });
        Assert.True(engine.Load(request!).Applied);
        RecRequest Request(string id) => new()
        {
            PipelineId = "p", RequestId = id, Inputs = Inputs(user: 7, query: "shoes", recent: null), Deadline = TimeSpan.FromSeconds(20),
            Debug = new DebugOptions { Record = true },
        };

        for (var attempt = 0; ; attempt++)
        {
            var calls = service.Calls.Count;
            Assert.True((await engine.ExecuteAsync(Request("warm-" + attempt), token)).Succeeded);
            if (service.Calls.Count == calls)
            {
                break; // answered from the cache
            }

            Assert.True(attempt < 2_000, "no request was answered from the cache");
            await Task.Delay(5, token);
        }

        clock.Advance(2_000); // past the TTL, within stale_if_error_ms
        service.Fail = true;
        var served = await engine.ExecuteAsync(Request("stale"), token);

        Assert.True(served.Succeeded, served.Error);
        var boost = Assert.Single(served.Recording!.Runs[0].Nodes, n => n.Node == "boost");
        Assert.Equal("degraded", boost.Outcome);
        var replay = await engine.ReplayAsync(ReplayBundleFile.Parse(ReplayBundleFile.Serialize(served.Recording)), cancellationToken: token);
        Assert.True(replay.Report.Diff.Identical, ReplayTests.Describe(replay.Report));
        Assert.All(replay.Report.Nodes, n => Assert.False(n.Changed, $"{n.Node}: {n.Recorded} -> {n.Replayed}"));
    }

    /// <summary>
    /// The call is recorded as the source's (no node), in role fetch, and a replay answers it from the recording without
    /// calling the service; a replay whose arguments changed has no answer for it: a gap, and the features fail.
    /// </summary>
    [Fact]
    public async Task ARecordedCall_IsTheSourcesAndReplaysAsRecorded_AndChangedArgumentsAreAGap()
    {
        var service = new IntentService();
        var plan = Compile(Workspace(), service);
        var live = await Execute(plan, Inputs(user: 7, query: "shoes", recent: [11]), record: true);
        Assert.True(live.Succeeded, live.Error);

        var recorded = Assert.Single(live.Recording!.ServiceCalls, c => c.Service == "intent_model");
        Assert.Equal((null, "intent", IoRoles.Fetch, "ok"), (recorded.Node, recorded.Source, recorded.Role, recorded.Status));

        var replay = RunReplay.Create(live.Recording, "/runs/0");
        var again = await Execute(plan, Inputs(user: 7, query: "shoes", recent: [11]), replay: replay);
        Assert.True(again.Succeeded, again.Error);
        Assert.Empty(replay.Gaps);
        Assert.Single(service.Calls); // the live run's
        Assert.Equal(live.Items.Select(i => i.Columns["label"].AsString()), again.Items.Select(i => i.Columns["label"].AsString()));

        var changed = RunReplay.Create(live.Recording, "/runs/0");
        var other = await Execute(plan, Inputs(user: 7, query: "hats", recent: [11]), replay: changed);
        Assert.False(other.Succeeded);
        var gap = Assert.Single(changed.Gaps);
        Assert.Equal((IoRoles.Fetch, "intent_model", "inputs_changed"), (gap.Kind, gap.Source, gap.Reason));
        Assert.Single(service.Calls);
    }

    [Theory]
    [InlineData("""{ "key": "ctx.request.user_id", "args": { "q": "ctx.request.query" } }""", "ENTITY_BAD_ARGS", "/entities/intent", "both a key and args")]
    [InlineData("""{ }""", "ENTITY_BAD_KEY", "/entities/intent", "neither a key nor args")]
    [InlineData("""{ "args": { } }""", "ENTITY_BAD_ARGS", "/entities/intent/args", "has no args")]
    [InlineData("""{ "args": { "q": "ctx.request.qeury" } }""", "ENTITY_BAD_ARGS", "/entities/intent/args/q", "'qeury' is not declared")]
    [InlineData("""{ "args": { "q": "col.entity_id" } }""", "ENTITY_BAD_ARGS", "/entities/intent/args/q", "a candidate value")]
    [InlineData("""{ "args": { "q": "ctx.user.nope" } }""", "ENTITY_BAD_ARGS", "/entities/intent/args/q", "not a declared feature")]
    [InlineData("""{ "args": { "q": "ctx.item.pop" } }""", "ENTITY_BAD_ARGS", "/entities/intent/args/q", "a candidate feature")]
    public void AnEntitysArgs_AreCheckedAtTheirPointer(string entity, string code, string pointer, string message)
    {
        var diagnostics = Diagnostics(Workspace(intent: entity));

        Assert.Contains(diagnostics, d => d.Code == code && d.Pointer == pointer && d.Message.Contains(message, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "mode": "service" }""", "SOURCE_SERVICE_MISSING", "/sources/intent", "names no 'service'")]
    [InlineData("""{ "mode": "service", "service": "intent_modle" }""", "SERVICE_UNBOUND", "/sources/intent/service", "has no binding")]
    [InlineData("""{ "mode": "service", "service": "intent_model", "connector": "memory@1" }""", "SOURCE_SETTING_WRONG_MODE", "/sources/intent/connector", "mode 'fetch' and 'cache'")]
    [InlineData("""{ "mode": "service", "service": "intent_model", "max_bytes": "1MiB" }""", "SOURCE_SETTING_WRONG_MODE", "/sources/intent/max_bytes", "is bound with mode 'service'")]
    [InlineData("""{ "connector": "memory@1", "data": {} }""", "SOURCE_SERVICE_ENTITY", "/sources/intent", "a service computes its features")]
    public void ASourceInModeService_IsChecked_AndOnlyAServiceComputesAnEntityWithArgs(string binding, string code, string pointer, string message)
    {
        var diagnostics = Diagnostics(Workspace(intentSource: binding));

        Assert.Contains(diagnostics, d => d.Code == code && d.Pointer == pointer && d.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void AKeyedEntitysSource_IsNotAService()
    {
        var diagnostics = Diagnostics(Workspace(users: """{ "mode": "service", "service": "intent_model" }"""));

        Assert.Contains(diagnostics, d => d.Code == "SOURCE_SERVICE_ENTITY" && d.Pointer == "/sources/users/service" && d.Message.Contains("entity 'user' has a key"));
    }

    /// <summary>An argument that leads back to its entity is a cycle, as a key part that does.</summary>
    [Fact]
    public void AnArgumentLeadingBackToItsEntity_IsAKeyCycle()
    {
        var diagnostics = Diagnostics(Workspace(
            intent: """{ "args": { "q": "ctx.other.x" } }""",
            extraEntities: """, "other": { "key": "ctx.intent.label" }""",
            extraFeatures: """, "other.x": { "type": "string", "source": "users", "field": "x" }"""));

        Assert.Contains(diagnostics, d => d.Code == "ENTITY_KEY_CYCLE" && d.Message.Contains("leads to a key cycle", StringComparison.Ordinal));
    }

    /// <summary>
    /// An element of a list of strings the service left unwritten fails the feature, and an argument reading it sends no
    /// call (review of IO step 5: the null element reached the later call's digest and failed the recording).
    /// </summary>
    [Fact]
    public async Task AStringListWithAnUnwrittenElement_FailsTheFeature_AndAnArgumentReadingItSendsNoCall()
    {
        var service = new IntentService { Topics = "strings, one unwritten" };
        var root = Workspace(
            extraEntities: """, "later": { "args": { "topics": "ctx.intent.topics" } }""",
            extraFeatures: """, "later.score": { "type": "float32", "source": "later", "field": "score" }""",
            extraSources: """, "later": { "mode": "service", "service": "intent_model", "timeout_ms": 5000 }""",
            extraOutput: """, "late": "ctx.later.score" """);
        Replace(root, "catalog/c.jsonc", "list<int64>", "list<string>");
        var plan = Compile(root, service);

        var result = await Execute(plan, Inputs(user: 7, query: "shoes", recent: null), record: true);

        Assert.False(result.Succeeded);
        Assert.Contains("answered output 'topics' with element 1 of 2 unwritten", result.Error);
        Assert.Single(service.Calls); // intent's; later's argument failed
    }

    /// <summary>
    /// Two entities of one source are two calls, each answered by its own in a replay: a recorded call keeps the outputs it
    /// asked for, so the call of the entity that reads other outputs does not take the other's failure (review of IO
    /// step 5, where they did, and of its fixes, where one source computed one entity, which a reload changed anyway).
    /// </summary>
    [Fact]
    public async Task TwoEntitiesOfOneSource_AreTwoCalls_EachReplayedByItsOwn()
    {
        var service = new IntentService { FailWhenAsked = "score" };
        var plan = Compile(Workspace(
            readInOutput: false,
            extraEntities: """, "other": { "args": { "query": "ctx.request.query", "ctr": "ctx.user.ctr", "recent": "ctx.request.recent" } }""",
            extraFeatures: """, "other.label": { "type": "string", "source": "intent", "field": "label" }""",
            extraOutput: """, "label": "ctx.other.label" """), service);
        var inputs = Inputs(user: 7, query: "shoes", recent: null);

        var live = await Execute(plan, inputs, record: true);
        Assert.True(live.Succeeded, live.Error); // boost skipped: intent's call (score) failed; other's (label) answered
        Assert.Equal(2, service.Calls.Count);
        var replay = RunReplay.Create(live.Recording!, "/runs/0");
        var again = await Execute(plan, inputs, replay: replay);

        Assert.True(again.Succeeded, again.Error);
        Assert.Empty(replay.Gaps);
        Assert.All(again.Items, item => Assert.Equal("sporty", item.Columns["label"].AsString()));
        Assert.Equal(NodeOutcomeKind.Failed, Assert.Single(again.Outcomes, o => o.NodeId == "boost").Kind);
    }

    /// <summary>
    /// Two entities of one source whose calls are the same (arguments and outputs) are still each answered by its own in a
    /// replay, made in whatever order: a source's call is recorded with its entity, as a node's with its node (review of
    /// da5cfe6: the replay made them in another order, and the transient failure of one went to the other).
    /// </summary>
    [Fact]
    public async Task TwoEntitiesOfOneSourceMakingTheSameCall_AreEachReplayedByItsOwn_WhateverTheOrder()
    {
        var service = new IntentService { FailFirst = true };
        var plan = Compile(Workspace(
            intent: """{ "args": { "q": "ctx.user.tag" } }""",
            readInOutput: false,
            users: """{ "connector": "memory@1", "data": { "7": { "tag": "shoes" } }, "latency_ms": 300, "timeout_ms": 5000 }""",
            extraEntities: """, "other": { "args": { "q": "ctx.alt.tag" } }, "alt": { "key": "ctx.request.user_id" }""",
            extraFeatures: """, "user.tag": { "type": "string", "source": "users", "field": "tag" }, "alt.tag": { "type": "string", "source": "fast_users", "field": "tag" }, "other.score": { "type": "float32", "source": "intent", "field": "score", "default": 0.5 }""",
            extraSources: """, "fast_users": { "connector": "memory@1", "data": { "7": { "tag": "shoes" } }, "timeout_ms": 5000 }""",
            extraNodes: """, "boost_other": { "order": 300, "op": "ExpressionScorer@1", "on_failure": "skip", "config": { "output_col": "out.o", "expr": "ctx.other.score * 2.0" } }""",
            extraColumns: """, "out.o": { "type": "float32?" }"""), service);
        var inputs = Inputs(user: 7, query: "shoes", recent: null);

        var live = await Execute(plan, inputs, record: true);
        var replay = RunReplay.Create(live.Recording!, "/runs/0");
        var again = await Execute(plan, inputs, replay: replay);

        Assert.True(live.Succeeded && again.Succeeded, live.Error ?? again.Error);
        static (NodeOutcomeKind, NodeOutcomeKind) Boosts(PipelineResult r) =>
            (r.Outcomes.Single(o => o.NodeId == "boost").Kind, r.Outcomes.Single(o => o.NodeId == "boost_other").Kind);
        Assert.Equal((NodeOutcomeKind.Succeeded, NodeOutcomeKind.Failed), Boosts(live)); // other's call went first, and failed
        Assert.Equal(Boosts(live), Boosts(again));
        Assert.Empty(replay.Gaps);
        Assert.Equal(2, service.Calls.Count); // the live run's
    }

    /// <summary>
    /// A replay counts a service source's fields in its feature stats, as a fetch's: read once, missing when the service
    /// left the output unwritten or the call failed (review of the IO redesign: a comparison's missing rate left them out).
    /// </summary>
    [Theory]
    [InlineData("answered", 0)]
    [InlineData("omitted", 1)]
    [InlineData("failed", 1)]
    public async Task AServiceSourcesFields_AreCountedInTheReplaysFeatureStats(string answer, long missing)
    {
        var service = new IntentService { Omit = answer == "omitted" ? "score" : null, Fail = answer == "failed" };
        var plan = Compile(Workspace(), service);
        var inputs = Inputs(user: 7, query: "shoes", recent: null);

        var live = await Execute(plan, inputs, record: true);
        var replay = RunReplay.Create(live.Recording!, "/runs/0");
        await Execute(plan, inputs, replay: replay);

        var stats = Assert.Single(replay.FieldStats, s => s.Source == "intent" && s.Field == "score");
        Assert.Equal((1L, missing), (stats.Requested, stats.Missing));
    }

    /// <summary>
    /// Two entities of one source each ask with the names their group built once, request after request: they were kept
    /// for the last group only, and built again at every entity's call (review of the IO redesign).
    /// </summary>
    [Fact]
    public async Task TwoEntitiesOfOneSource_AskWithTheNamesTheirGroupBuiltOnce()
    {
        var service = new IntentService();
        var plan = Compile(Workspace(
            readInOutput: false,
            extraEntities: """, "other": { "args": { "query": "ctx.request.query", "ctr": "ctx.user.ctr", "recent": "ctx.request.recent" } }""",
            extraFeatures: """, "other.label": { "type": "string", "source": "intent", "field": "label" }""",
            extraOutput: """, "label": "ctx.other.label" """), service);
        var inputs = Inputs(user: 7, query: "shoes", recent: null);

        await Execute(plan, inputs);
        await Execute(plan, inputs);

        var calls = service.Calls;
        Assert.Equal(4, calls.Count);
        foreach (var output in new[] { "score", "label" })
        {
            var asked = calls.Where(c => c.Outputs.Contains(output)).ToArray();
            Assert.Equal(2, asked.Length);
            Assert.NotNull(asked[0].Names);
            Assert.Same(asked[0].Names, asked[1].Names);
        }
    }

    /// <summary>
    /// An output the service left unwritten is missing, live and in a replay of the same configuration, which neither
    /// reports it as a gap nor takes it from another call that has it (review of the step 5 fixes).
    /// </summary>
    [Fact]
    public async Task AnOutputTheServiceLeftUnwritten_IsMissing_AndReplaysSo()
    {
        var service = new IntentService { Omit = "score" };
        var plan = Compile(Workspace(), service);
        var inputs = Inputs(user: 7, query: "shoes", recent: null);

        var live = await Execute(plan, inputs, record: true);
        var replay = RunReplay.Create(live.Recording!, "/runs/0");
        var again = await Execute(plan, inputs, replay: replay);

        Assert.True(live.Succeeded && again.Succeeded, live.Error ?? again.Error);
        Assert.All(live.Items.Concat(again.Items), item => Assert.Equal(0.5, item.Columns["score"].AsFloat64(), 6)); // the default
        Assert.Empty(replay.Gaps);
    }

    /// <summary>
    /// Two features of one field ask the service for it once, and both take its answer (review of IO step 5), of the one
    /// type both are declared with: an output has one type.
    /// </summary>
    [Fact]
    public async Task TwoFeaturesOfOneField_AskForItOnce()
    {
        var service = new IntentService();
        var plan = Compile(Workspace(
            extraFeatures: """, "intent.alias": { "type": "float32", "source": "intent", "field": "score" }""",
            extraOutput: """, "alias": "ctx.intent.alias" """), service);

        var result = await Execute(plan, Inputs(user: 7, query: "shoes", recent: null));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(["label", "score", "topics"], Assert.Single(service.Calls).Outputs.Order(StringComparer.Ordinal));
        Assert.All(result.Items, item => Assert.Equal((0.75, 0.75), (item.Columns["score"].AsFloat64(), item.Columns["alias"].AsFloat64())));
    }

    /// <summary>
    /// Two features reading one output as two types is a load error at the second's type, in name order: the service
    /// answers the output once, and a feature takes only the type it is declared with (review of 43bdf97: the plan
    /// loaded, and every call failed, whichever type the service answered).
    /// </summary>
    [Fact]
    public void TwoFeaturesReadingOneOutputAsTwoTypes_IsALoadError()
    {
        var diagnostics = Diagnostics(Workspace(
            extraFeatures: """, "intent.wide": { "type": "float64", "source": "intent", "field": "score" }""",
            extraOutput: """, "wide": "ctx.intent.wide" """));

        Assert.Contains(diagnostics, d => d.Code == "SERVICE_OUTPUT_TYPE_CONFLICT" && d.Pointer == "/features/intent.wide/type"
            && d.Message.Contains("as float64, but feature 'intent.score' reads it as float32", StringComparison.Ordinal));
    }

    /// <summary>
    /// An argument may read a derived feature of its own entity: what it reads is in its expression, not in the entity
    /// (review of IO step 5: the catalog reported a key cycle).
    /// </summary>
    [Fact]
    public async Task AnArgumentReadingADerivedFeatureOfItsOwnEntity_IsNoCycle()
    {
        var service = new IntentService();
        var plan = Compile(Workspace(
            intent: """{ "args": { "query": "ctx.intent.prep" } }""",
            derived: """ "intent.prep": { "type": "string", "expr": "ctx.request.query ?? 'none'" } """), service);

        var result = await Execute(plan, Inputs(user: 7, query: null, recent: null));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(["query:String[]=none"], Assert.Single(service.Calls).Inputs);
    }

    /// <summary>The call carries the request's id and seed, as a node's does (review of IO step 5).</summary>
    [Fact]
    public async Task TheCall_CarriesTheRequestsIdAndSeed()
    {
        var service = new IntentService();
        var plan = Compile(Workspace(), service);

        await Execute(plan, Inputs(user: 7, query: "shoes", recent: null));

        var call = Assert.Single(service.Calls);
        Assert.Equal(("r", (long?)PipelineRequest.DefaultSeed("r")), (call.RequestId, call.Seed));
    }

    /// <summary>
    /// A source's call that the caller cancelled in the recording is cancelled in the replay too, whose own caller did not
    /// cancel (review of IO step 5: it replayed as a timeout).
    /// </summary>
    [Fact]
    public async Task ARecordedCancellation_ReplaysAsACancellation()
    {
        var service = new IntentService();
        var plan = Compile(Workspace(readInOutput: false), service);
        var inputs = Inputs(user: 7, query: "shoes", recent: null);
        var run = (await Execute(plan, inputs, record: true)).Recording!;
        var at = run.ServiceCalls.FindIndex(c => c.Service == "intent_model");
        var recorded = run.ServiceCalls[at];
        run.ServiceCalls[at] = new ReplayServiceCall
        {
            Seq = recorded.Seq, Source = recorded.Source, Entity = recorded.Entity, Service = recorded.Service, Role = recorded.Role, Status = "cancelled", Threw = true,
            InputDigests = recorded.InputDigests, Requested = recorded.Requested,
        };

        var replayed = await Execute(plan, inputs, replay: RunReplay.Create(run, "/runs/0"));

        var boost = Assert.Single(replayed.Outcomes, o => o.NodeId == "boost");
        Assert.Equal(NodeOutcomeKind.Failed, boost.Kind);
        Assert.Contains("source 'intent' was cancelled", boost.Detail);
    }

    /// <summary>
    /// The fetch's drain waits for a source's call it abandoned at the source's timeout, as for a connector's (review of
    /// IO step 5); the call holds the request memory until then either way.
    /// </summary>
    [Fact]
    public async Task TheFetchsDrain_WaitsForASourcesCallItAbandoned()
    {
        var service = new IntentService { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var plan = Compile(Workspace(intent: """{ "args": { "q": "ctx.request.query" } }""", intentTimeoutMs: 100), service);
        var feature = plan.Features.Features.Single(f => f.Definition.Name == "intent.score");
        var memory = new RequestMemory();
        var fetcher = new FeatureFetcher(
            plan.Features, new CandidateTable(plan.TableSchema, 1, memory), new ScalarValue[plan.Schema.ContextFields.Count],
            new OutputBindings(plan.Schema.Columns.Count, memory), [], memory, long.MaxValue);
        try
        {
            fetcher.StartContextFetch([feature.Index], TestContext.Current.CancellationToken);
            await fetcher.ContextStoredTask(feature.Index);
            Assert.Equal(FeatureState.Failed, fetcher.ContextStoredState(feature.Index).State); // timed out

            var drained = fetcher.WhenIoCompleteAsync();
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.False(drained.IsCompleted, "the drain ended while the abandoned call was still running");
            service.Gate.SetResult();
            await drained.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        finally
        {
            service.Gate.TrySetResult();
            memory.Release();
        }
    }

    private static void Replace(string root, string path, string find, string replace)
    {
        var file = Path.Combine(root, path);
        var text = File.ReadAllText(file);
        Assert.Contains(find, text);
        File.WriteAllText(file, text.Replace(find, replace, StringComparison.Ordinal));
    }

    private static RequestInputs Inputs(long user, string? query, long[]? recent)
    {
        var inputs = new RequestInputs().Set("user_id", user);
        if (query is not null)
        {
            inputs.Set("query", query);
        }

        if (recent is not null)
        {
            inputs.Set("recent", recent);
        }

        return inputs;
    }

    private static ValueTask<PipelineResult> Execute(ExecutionPlan plan, RequestInputs inputs, bool record = false, RunReplay? replay = null) =>
        new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
        {
            RequestId = "r",
            Inputs = inputs,
            Deadline = replay is null ? TimeSpan.FromSeconds(20) : RunReplay.Deadline,
            TimeMs = 1_767_225_600_000,
            Record = record,
            Replay = replay,
        }, TestContext.Current.CancellationToken);

    private static ExecutionPlan Compile(string root, IntentService service)
    {
        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("p", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        var compilation = PlanCompiler.Compile(request!, new PlanEnvironment { BaseDirectory = root, Bindings = PlanEnvironment.DefaultBindings().Add(service) });
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        return compilation.Plan!;
    }

    /// <summary>Every diagnostic of loading and compiling the workspace.</summary>
    private static List<ConfigDiagnostic> Diagnostics(string root)
    {
        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("p", "local", diagnostics);
        var all = new List<ConfigDiagnostic>(diagnostics.Items);
        if (request is not null)
        {
            all.AddRange(PlanCompiler.Compile(request, new PlanEnvironment { BaseDirectory = root, Bindings = PlanEnvironment.DefaultBindings().Add(new IntentService()) }).Diagnostics);
        }

        return all;
    }

    /// <summary>
    /// A workspace whose entity <c>intent</c> has args (a string and a list input, and the user's CTR, fetched first) and
    /// three features of three kinds, which the boost node and the output read.
    /// </summary>
    private static string Workspace(
        string intent = """{ "args": { "query": "ctx.request.query", "ctr": "ctx.user.ctr", "recent": "ctx.request.recent" } }""",
        string intentSource = """{ "mode": "service", "service": "intent_model", "timeout_ms": 5000 }""",
        string users = """{ "connector": "memory@1", "data": { "7": { "ctr": 0.25 } }, "timeout_ms": 5000 }""",
        string serviceCache = "",
        bool readInOutput = true,
        int? intentTimeoutMs = null,
        string extraEntities = "",
        string extraFeatures = "",
        string extraSources = "",
        string extraOutput = "",
        string derived = "",
        string extraNodes = "",
        string extraColumns = "")
    {
        if (intentTimeoutMs is { } timeout)
        {
            intentSource = intentSource.Replace("\"timeout_ms\": 5000", $"\"timeout_ms\": {timeout}", StringComparison.Ordinal);
        }

        var root = Directory.CreateTempSubdirectory("stratarec-computed-").FullName;
        foreach (var dir in new[] { "catalog", "bindings", "pipelines" })
        {
            Directory.CreateDirectory(Path.Combine(root, dir));
        }

        File.WriteAllText(Path.Combine(root, "catalog", "c.jsonc"), $$"""
            { "catalog_id": "c",
              "entities": { "user": { "key": "ctx.request.user_id" }, "item": { "key": "col.entity_id" }, "intent": {{intent}}{{extraEntities}} },
              "features": {
                "user.ctr":      { "type": "float32", "source": "users", "field": "ctr" },
                "item.pop":      { "type": "string", "source": "users", "field": "pop" },
                "intent.score":  { "type": "float32", "source": "intent", "field": "score", "default": 0.5 },
                "intent.label":  { "type": "string", "source": "intent", "field": "label" },
                "intent.topics": { "type": "list<int64>", "source": "intent", "field": "topics" }{{extraFeatures}}
              },
              "derived_features": { {{derived}} } }
            """);
        File.WriteAllText(Path.Combine(root, "bindings", "local.jsonc"), $$"""
            { "environment": "local",
              "sources": { "users": {{users}}, "intent": {{intentSource}}{{extraSources}} },
              "services": {
                "pool": { "client": "memory_list@1", "items": [1, 2] },
                "intent_model": { "client": "{{IntentService.TypeName}}"{{serviceCache}} } } }
            """);
        var output = readInOutput
            ? $$"""{ "s": "col.out.s", "score": "ctx.intent.score", "label": "ctx.intent.label", "topics": "ctx.intent.topics"{{extraOutput}} }"""
            : $$"""{ "s": "col.out.s"{{extraOutput}} }""";
        File.WriteAllText(Path.Combine(root, "pipelines", "p.jsonc"), $$"""
            { "pipeline_id": "p",
              "request_schema": { "user_id": { "type": "int64?" }, "query": { "type": "string?" }, "recent": { "type": "list<int64>?" } },
              "param_schema": { "recall_quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "recall_quotas": { "pool": 10 }, "k": 5 },
              "columns": { "out.s": { "type": "float32?" }{{extraColumns}} },
              "nodes": {
                "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "recall_quotas" } },
                "boost":  { "order": 200, "op": "ExpressionScorer@1", "on_failure": "skip", "config": { "output_col": "out.s", "expr": "ctx.intent.score * 2.0" } }{{extraNodes}}
              },
              "output": { "limit_param": "k", "columns": {{output}} } }
            """);
        return root;
    }

    /// <summary>
    /// <c>intent_test@1</c>: records each call (its inputs as <c>name:Type[shape]=values</c>) and answers <c>score</c>
    /// 0.75 (float64), <c>label</c> "sporty" (a text) and <c>topics</c> [3, 4] (int32); or fails, or answers late.
    /// </summary>
    private sealed class IntentService : IBindingFactory<IServiceClient>, IServiceClient
    {
        public const string TypeName = "intent_test@1";

        private readonly List<Received> _calls = [];

        public string Type => TypeName;

        public Type SettingsType => typeof(NoSettings);

        public bool Fail { get; set; }

        public int DelayMs { get; init; }

        /// <summary><c>number</c>, <c>string</c> (a string tensor) or <c>two</c> (two numbers).</summary>
        public string Score { get; init; } = "number";

        /// <summary><c>int32</c>, or two strings of which the second is left unwritten.</summary>
        public string Topics { get; init; } = "int64";

        /// <summary>Fails the first call, as a service failing for a moment.</summary>
        public bool FailFirst { get; init; }

        private int _sent;

        /// <summary>Fails a call that asks for this output.</summary>
        public string? FailWhenAsked { get; init; }

        /// <summary>An output it leaves unwritten (<c>score</c>).</summary>
        public string? Omit { get; init; }

        /// <summary>Answers only once this completes, whatever its token.</summary>
        public TaskCompletionSource? Gate { get; init; }

        public List<Received> Calls
        {
            get
            {
                lock (_calls)
                {
                    return [.. _calls];
                }
            }
        }

        public IServiceClient Create(BindingContext context) => this;

        /// <summary>Forgets the calls after the first <paramref name="kept"/>.</summary>
        public void Forget(int kept)
        {
            lock (_calls)
            {
                _calls.RemoveRange(kept, _calls.Count - kept);
            }
        }

        public async ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse response, CancellationToken cancellationToken)
        {
            var inputs = new List<string>();
            foreach (var input in call.Inputs.Span)
            {
                inputs.Add(Describe(input));
            }

            lock (_calls)
            {
                _calls.Add(new Received(
                    call.BatchSize, call.Limit, call.Outputs.ToArray(), [.. inputs], call.RequestId, call.Options.Seed,
                    System.Runtime.InteropServices.MemoryMarshal.TryGetArray(call.Outputs, out var names) ? names.Array : null));
            }

            if (DelayMs > 0)
            {
                await Task.Delay(DelayMs, cancellationToken);
            }

            if (Gate is { } gate)
            {
                await gate.Task.ConfigureAwait(false); // ignores the token
            }

            if (Fail || (FailWhenAsked is { } failing && call.Outputs.Span.Contains(failing)) || (FailFirst && Interlocked.Increment(ref _sent) == 1))
            {
                return ServiceStatus.Failed("the model is down");
            }

            switch (Omit == "score" ? "omitted" : Score)
            {
                case "omitted":
                    break;
                case "string":
                    response.GetValues<string>("score", 1).Span[0] = "high";
                    break;
                case "two":
                    response.GetValues<float>("score", 2).Span.Fill(0.75f);
                    break;
                case "float64":
                    response.GetValues<double>("score", 1).Span[0] = 0.75;
                    break;
                default:
                    response.GetValues<float>("score", 1).Span[0] = 0.75f;
                    break;
            }

            response.SetText("label", "sporty");
            if (Topics == "int64")
            {
                var topics = response.GetValues<long>("topics", 2).Span;
                topics[0] = 3;
                topics[1] = 4;
            }
            else if (Topics == "int32")
            {
                var topics = response.GetValues<int>("topics", 2).Span;
                topics[0] = 3;
                topics[1] = 4;
            }
            else
            {
                response.GetValues<string>("topics", 2).Span[0] = "shoe"; // the second left unwritten
            }

            return ServiceStatus.Ok;
        }

        private static string Describe(NamedInput input)
        {
            var head = $"{input.Name}:{input.ElementType}[{string.Join(",", input.Shape.ToArray())}]=";
            if (input.IsMissing)
            {
                return head + "missing";
            }

            return head + input.ElementType switch
            {
                TensorElementType.Float32 => string.Join(",", input.Float32.ToArray().Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                TensorElementType.Int64 => string.Join(",", input.Int64.ToArray()),
                TensorElementType.String => string.Join(",", input.String.ToArray()),
                TensorElementType.Text => input.Text,
                _ => "?",
            };
        }

        public ValueTask<ServiceSignature> DescribeAsync(string model, string? version, CancellationToken cancellationToken) =>
            ValueTask.FromException<ServiceSignature>(new NotSupportedException());
    }

    private sealed class NoSettings;

    /// <param name="Names">The array the outputs' names were sent in.</param>
    private sealed record Received(int BatchSize, int Limit, string[] Outputs, string[] Inputs, string RequestId, long? Seed, string[]? Names = null);

    private sealed class MovableClock : TimeProvider
    {
        private long _timeMs = 1_720_000_000_000L;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Volatile.Read(ref _timeMs));

        public void Advance(long milliseconds) => Interlocked.Add(ref _timeMs, milliseconds);
    }
}
