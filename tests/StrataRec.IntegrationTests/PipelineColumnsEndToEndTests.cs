using StrataRec.Artifacts.Model;
using StrataRec.Core.Values;
using StrataRec.Experiments.Assignment;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting;
using StrataRec.Hosting.Replay;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// <c>PipelineColumns@1</c>: pipeline <c>page</c> has three rows (the popular list's first: 5, 8, 11) and calls a
/// pipeline for each, whose rows become the row's lists. <c>promoted</c> (of <see cref="PipelineCallEndToEndTests"/>) takes
/// the row's key as its <c>slots</c>, so each row's call returns bids of its own; <c>tagged</c> returns columns
/// <c>promoted</c> does not have.
/// </summary>
public sealed class PipelineColumnsEndToEndTests
{
    private const string Tagged = """
        {
          "pipeline_id": "tagged",
          "entity_key_type": "int64",
          "request_schema": { "user_id": { "type": "int64?" }, "country": { "type": "string?" } },
          "param_schema": { "recall_quotas": { "type": "map<string,int64>", "min": 0, "max": 1000 }, "top_k": { "type": "int64", "min": 1, "max": 100 } },
          "params": { "recall_quotas": { "popular": 20 }, "top_k": 4 },
          "nodes": {
            "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "recall_quotas" } }
          },
          "output": { "limit_param": "top_k",
                      "columns": { "weight": "col.recall.best_score", "tag": "col.item.category", "country": "ctx.request.country" } }
        }
        """;

    private const string Page = """
        {
          "pipeline_id": "page",
          "entity_key_type": "int64",
          "request_schema": { "user_id": { "type": "int64?" }, "country": { "type": "string?" } },
          "columns": { "games": { "type": "list<int64>" }, "bids": { "type": "list<float64>?" }, "count": { "type": "int64?" },
                       "tags": { "type": "list<string>?" }, "wrong": { "type": "float64", "default": 0.0 },
                       "pair": { "type": "list<float64,2>", "default": [0.0, 0.0] } },
          "param_schema": { "recall_quotas": { "type": "map<string,int64>", "min": 0, "max": 1000 }, "top_k": { "type": "int64", "min": 1, "max": 100 } },
          "params": { "recall_quotas": { "popular": 3 }, "top_k": 50 },
          "nodes": {
            "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "recall_quotas" } },
            "content": { "order": 200, "op": "PipelineColumns@1", "on_failure": "ON_FAILURE", "config": { CONFIG } }
          },
          "log_taps": { "shown": { "group": "all", "attach": { "output": true }, "columns": { "games": "col.games" } } },
          "funnel_groups": { "all": { "key": ["ctx.request.id"], "rate": 1.0 } },
          "output": { "limit_param": "top_k", "columns": { "games": "col.games", "bids": "col.bids", "count": "col.count", "tags": "col.tags" } }
        }
        """;

    private const string OnePipeline = """
        "pipeline": "promoted", "inputs": { "slots": "col.entity_id" }, "ids": "games", "columns": { "bids": "bid" }, "values": { "count": "count" }, "max_calls": 10
        """;

    private const string TwoPipelines = """
        "pipeline": "promoted", "inputs": { "slots": "col.entity_id" }, "ids": "games", "columns": { "bids": "bid" }, "max_calls": 10,
        "pipelines": { "8": { "pipeline": "tagged", "columns": { "bids": "weight", "tags": "tag" } } }
        """;

    private static HomeFeedTestHost Host(string config = OnePipeline, string onFailure = "fail_request", Action<string>? more = null) =>
        HomeFeedTestHost.LoadModified(root =>
        {
            File.WriteAllText(Path.Combine(root, "pipelines", "promoted.jsonc"), PipelineCallEndToEndTests.Promoted);
            File.WriteAllText(Path.Combine(root, "pipelines", "tagged.jsonc"), Tagged);
            File.WriteAllText(Path.Combine(root, "pipelines", "page.jsonc"),
                Page.Replace("CONFIG", config, StringComparison.Ordinal).Replace("ON_FAILURE", onFailure, StringComparison.Ordinal));
            foreach (var id in new[] { "page", "promoted", "tagged" })
            {
                File.WriteAllText(Path.Combine(root, "releases", id + ".local.jsonc"), PipelineCallEndToEndTests.Releases.Replace("PIPELINE", id, StringComparison.Ordinal));
                File.WriteAllText(Path.Combine(root, "experiments", "exp_" + id + ".jsonc"), PipelineCallEndToEndTests.Experiments.Replace("PIPELINE", id, StringComparison.Ordinal));
            }

            more?.Invoke(root);
        });

    private static RecEngine Engine(HomeFeedTestHost host, params string[] pipelines) => Engine(host, null, pipelines);

    private static RecEngine Engine(HomeFeedTestHost host, IAssignmentProvider? provider, params string[] pipelines)
    {
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = host.Root }, provider);
        foreach (var id in pipelines.Length == 0 ? ["promoted", "tagged", "page"] : pipelines)
        {
            var diagnostics = new DiagnosticBag();
            var request = host.Workspace.CreateCompileRequest(id, "local", diagnostics);
            Assert.True(request is not null, string.Join("\n", diagnostics.Items));
            var load = engine.Load(request!);
            Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        }

        return engine;
    }

    private static RecRequest Request(string pipelineId, RequestInputs? inputs = null, DebugOptions? debug = null) => new()
    {
        PipelineId = pipelineId,
        RequestId = "req-page",
        Inputs = inputs ?? new RequestInputs().Set("user_id", 1001),
        Assignment = [],
        Deadline = HomeFeedTestHost.Deadline,
        TimeMs = 1_767_225_600_000,
        Debug = debug ?? new DebugOptions { Trace = true },
    };

    private static long[] Games(RankedItem row) => row.Columns["games"].AsInt64List().ToArray();

    private static double[] Bids(RankedItem row) => row.Columns["bids"].AsFloat64List().ToArray();

    [Fact]
    public async Task EachRowsCall_FillsItsLists_AndAnItemMayBeInSeveralRows()
    {
        var engine = Engine(Host());
        var ct = TestContext.Current.CancellationToken;

        var page = await engine.ExecuteAsync(Request("page"), ct);

        Assert.True(page.Succeeded, page.Error);
        Assert.Equal(new long[] { 5, 8, 11 }, page.Items.Select(i => i.Id.Int64Value));
        foreach (var row in page.Items)
        {
            var alone = await engine.ExecuteAsync(Request("promoted", new RequestInputs().Set("user_id", 1001).Set("slots", row.Id.Int64Value)), ct);
            Assert.True(alone.Succeeded, alone.Error);
            Assert.Equal(alone.Items.Select(i => i.Id.Int64Value), Games(row));
            Assert.Equal(alone.Items.Select(i => i.Columns["bid"].AsFloat64()), Bids(row));
            Assert.Equal(alone.Values["count"].AsInt64(), row.Columns["count"].AsInt64());
        }

        Assert.Equal(Games(page.Items[0]), Games(page.Items[1]));
        Assert.NotEqual(Bids(page.Items[0]), Bids(page.Items[1]));
        Assert.Equal(new[] { "page/content#0", "page/content#1", "page/content#2" }, page.Calls!.Select(c => c.CalledBy));
    }

    [Fact]
    public async Task RowsOfDifferentKinds_CallDifferentPipelines_EachWithColumnsOfItsOwn()
    {
        var engine = Engine(Host(TwoPipelines));
        var ct = TestContext.Current.CancellationToken;

        var page = await engine.ExecuteAsync(Request("page"), ct);

        Assert.True(page.Succeeded, page.Error);
        var tagged = await engine.ExecuteAsync(Request("tagged"), ct);
        var row = page.Items.Single(i => i.Id.Int64Value == 8);
        Assert.Equal(tagged.Items.Select(i => i.Id.Int64Value), Games(row));
        Assert.Equal(tagged.Items.Select(i => i.Columns["weight"].AsFloat64()), Bids(row));
        Assert.Equal(tagged.Items.Select(i => i.Columns["tag"].AsString()), row.Columns["tags"].AsStringList().ToArray());
        foreach (var other in page.Items.Where(i => i.Id.Int64Value != 8))
        {
            Assert.True(other.Columns["tags"].IsMissing);
            Assert.Equal(5, Games(other).Length);
        }

        Assert.Equal(new[] { "promoted", "tagged", "promoted" }, page.Calls!.Select(c => c.PipelineId));
    }

    [Fact]
    public async Task AColumnSaysARowsKind_AndAnEntryHasInputsAndValuesOfItsOwn()
    {
        const string ByCategory = """
            "pipeline": "tagged", "ids": "games", "columns": { "bids": "weight" }, "max_calls": 10,
            "by": "col.item.category",
            "pipelines": { "food": { "pipeline": "promoted", "inputs": { "slots": "col.entity_id" }, "columns": { "bids": "bid" }, "values": { "count": "count" } } }
            """;
        var engine = Engine(Host(ByCategory));
        var ct = TestContext.Current.CancellationToken;

        var page = await engine.ExecuteAsync(Request("page"), ct);

        Assert.True(page.Succeeded, page.Error);
        Assert.Equal(new[] { "tagged", "promoted", "tagged" }, page.Calls!.Select(c => c.PipelineId));
        var promoted = await engine.ExecuteAsync(Request("promoted", new RequestInputs().Set("user_id", 1001).Set("slots", 8)), ct);
        var tagged = await engine.ExecuteAsync(Request("tagged"), ct);
        foreach (var row in page.Items)
        {
            var (alone, score, food) = row.Id.Int64Value == 8 ? (promoted, "bid", true) : (tagged, "weight", false);
            Assert.Equal(alone.Items.Select(i => i.Id.Int64Value), Games(row));
            Assert.Equal(alone.Items.Select(i => i.Columns[score].AsFloat64()), Bids(row));
            Assert.Equal(!food, row.Columns["count"].IsMissing);
        }

        Assert.Equal(promoted.Values["count"].AsInt64(), page.Items[1].Columns["count"].AsInt64());
    }

    [Theory]
    [InlineData("fail_request")]
    [InlineData("skip")]
    public async Task ARowWhoseCallFails_HasNoValues_AndTheNodeFailsWhenEveryCallDoes(string onFailure)
    {
        // A node that may be skipped leaves a nullable column: the row without values is then missing, not empty.
        var nullable = onFailure == "skip";
        var host = Host(TwoPipelines, onFailure, root => ReplayTests.Edit(root, "pipelines/page.jsonc", "\"games\": { \"type\": \"list<int64>\" }",
            nullable ? "\"games\": { \"type\": \"list<int64>?\" }" : "\"games\": { \"type\": \"list<int64>\"  }"));
        var ct = TestContext.Current.CancellationToken;

        var some = await Engine(host, "promoted", "page").ExecuteAsync(Request("page"), ct);
        Assert.True(some.Succeeded, some.Error);
        var row = some.Items.Single(i => i.Id.Int64Value == 8);
        Assert.Equal(nullable, row.Columns["games"].IsMissing);
        if (!nullable)
        {
            Assert.Empty(Games(row));
        }

        Assert.True(row.Columns["bids"].IsMissing);
        Assert.All(some.Items.Where(i => i.Id.Int64Value != 8), i => Assert.Equal(5, Games(i).Length));
        var node = some.Trace!.Single(n => n.NodeId == "content");
        Assert.Contains("1 of 3 calls failed", node.Detail);
        Assert.Contains("tagged", node.Detail);

        var none = await Engine(host, "page").ExecuteAsync(Request("page"), ct);
        Assert.Equal(onFailure == "skip", none.Succeeded);
        if (onFailure == "skip")
        {
            Assert.All(none.Items, i => Assert.True(i.Columns["games"].IsMissing));
        }
        else
        {
            Assert.Contains("each of the 3 calls failed", none.Error);
        }
    }

    [Fact]
    public async Task AnInputThatCannotBeRead_FailsTheNode_BeforeAnyCallIsMade()
    {
        const string OneUnreadable = """
            "pipeline": "promoted", "inputs": { "slots": "col.entity_id" }, "ids": "games", "max_calls": 10,
            "pipelines": { "8": { "pipeline": "promoted", "inputs": { "slots": "param.slots_by.food" } } }
            """;
        var host = Host(OneUnreadable, more: root =>
        {
            ReplayTests.Edit(root, "pipelines/page.jsonc", "\"top_k\": { \"type\": \"int64\", \"min\": 1, \"max\": 100 } },",
                "\"top_k\": { \"type\": \"int64\", \"min\": 1, \"max\": 100 }, \"slots_by\": { \"type\": \"map<string,int64>\" } },");
            ReplayTests.Edit(root, "pipelines/page.jsonc", "\"top_k\": 50 },", "\"top_k\": 50, \"slots_by\": { \"food\": null } },");
        });
        var asked = 0;
        var provider = new Provider(context =>
        {
            if (context.PipelineId == "promoted")
            {
                Interlocked.Increment(ref asked); // a called run asks for its assignment as it starts
            }
        });
        var request = Request("page");

        var page = await Engine(host, provider).ExecuteAsync(
            new RecRequest { PipelineId = request.PipelineId, RequestId = request.RequestId, Inputs = request.Inputs, Deadline = request.Deadline, TimeMs = request.TimeMs, Debug = request.Debug },
            TestContext.Current.CancellationToken);

        Assert.False(page.Succeeded);
        Assert.Contains("node 'content' failed", page.Error);
        Assert.Contains("slots_by", page.Error);
        Assert.Equal(0, Volatile.Read(ref asked));
    }

    private sealed class Provider(Action<AssignmentContext> asked) : IAssignmentProvider
    {
        public ValueTask<IReadOnlyList<string>> AssignAsync(AssignmentContext context, CancellationToken cancellationToken)
        {
            asked(context);
            return ValueTask.FromResult<IReadOnlyList<string>>([]);
        }
    }

    [Fact]
    public async Task TheRowsAfterTheMostCalls_AreNotCalled()
    {
        var engine = Engine(Host(OnePipeline.Replace("\"max_calls\": 10", "\"max_calls\": 2", StringComparison.Ordinal)));

        var page = await engine.ExecuteAsync(Request("page"), TestContext.Current.CancellationToken);

        Assert.True(page.Succeeded, page.Error);
        Assert.Equal(new[] { 5, 5, 0 }, page.Items.Select(i => Games(i).Length));
        Assert.True(page.Items[2].Columns["bids"].IsMissing && page.Items[2].Columns["count"].IsMissing);
        Assert.Equal(2, page.Calls!.Count);
        Assert.Contains("1 of 3 rows were not called", page.Trace!.Single(n => n.NodeId == "content").Detail);
    }

    [Fact]
    public async Task ARowOfNoKindTheNodeHasAPipelineFor_CallsNothing()
    {
        const string OnlyEight = """
            "ids": "games", "max_calls": 10, "pipelines": { "8": { "pipeline": "tagged", "columns": { "tags": "tag" } } }
            """;
        var engine = Engine(Host(OnlyEight));

        var page = await engine.ExecuteAsync(Request("page"), TestContext.Current.CancellationToken);

        Assert.True(page.Succeeded, page.Error);
        Assert.Equal(new[] { 0, 4, 0 }, page.Items.Select(i => Games(i).Length));
        Assert.Single(page.Calls!);
        Assert.Equal(NodeOutcomeKind.Succeeded, page.Trace!.Single(n => n.NodeId == "content").Kind);
    }

    [Fact]
    public async Task ARecordedPage_ReplaysFromItsBundle()
    {
        var host = Host(TwoPipelines);
        var ct = TestContext.Current.CancellationToken;
        var recorded = await Engine(host).ExecuteAsync(Request("page", debug: new DebugOptions { Record = true, Trace = true }), ct);
        Assert.True(recorded.Succeeded, recorded.Error);
        Assert.Equal(new[] { ("content", 0, "promoted"), ("content", 1, "tagged"), ("content", 2, "promoted") },
            recorded.Recording!.Runs[0].PipelineCalls.Select(c => (c.Node, c.Seq, c.Pipeline)));

        var outcome = await Engine(host, "page").ReplayAsync(ReplayBundleFile.Parse(ReplayBundleFile.Serialize(recorded.Recording)), cancellationToken: ct);

        Assert.Empty(outcome.Report.Gaps);
        Assert.True(outcome.Report.Diff.Identical, ReplayTests.Describe(outcome.Report));
        Assert.Equal(recorded.Items.Select(Games), outcome.Result.Items.Select(Games));
    }

    [Fact]
    public async Task ATapLogsARowsList()
    {
        var engine = Engine(Host());

        var page = await engine.ExecuteAsync(Request("page", debug: new DebugOptions { CaptureLogs = true }), TestContext.Current.CancellationToken);

        Assert.True(page.Succeeded, page.Error);
        var shown = page.LogBatches!.Single(b => b.PipelineId == "page" && b.TapId == "shown");
        var column = shown.Columns.Single(c => c.Name == "games");
        Assert.Equal(page.Items.Select(Games), Enumerable.Range(0, shown.RowCount).Select(r => column.Get(r).AsInt64List().ToArray()));
    }

    [Theory]
    [InlineData("\"ids\": \"games\"", "\"ids\": \"wrong\"", "COLUMN_TYPE_MISMATCH", "/nodes/content/config/ids")]
    [InlineData("\"ids\": \"games\"", "\"ids\": \"tags\"", "PIPELINE_CALL_KEY_TYPE", "/nodes/content/config/ids")]
    [InlineData("\"bids\": \"bid\"", "\"wrong\": \"bid\"", "COLUMN_TYPE_MISMATCH", "/nodes/content/config/columns/wrong")]
    [InlineData("\"bids\": \"bid\"", "\"tags\": \"bid\"", "PIPELINE_CALL_OUTPUT", "/nodes/content/config/columns/tags")]
    [InlineData("\"bids\": \"bid\"", "\"bids\": \"bud\"", "PIPELINE_CALL_OUTPUT", "/nodes/content/config/columns/bids")]
    [InlineData("\"count\": \"count\"", "\"wrong\": \"count\"", "COLUMN_TYPE_MISMATCH", "/nodes/content/config/values/wrong")]
    // A list of a fixed length has no empty value for a row without a result.
    [InlineData("\"count\": \"count\"", "\"pair\": \"count\"", "COLUMN_TYPE_MISMATCH", "/nodes/content/config/values/pair")]
    [InlineData("\"bids\": \"bid\"", "\"games\": \"bid\"", "COLUMN_WRITTEN_TWICE", "/nodes/content/config/columns/games")]
    [InlineData("\"count\": \"count\"", "\"bids\": \"count\"", "COLUMN_WRITTEN_TWICE", "/nodes/content/config/values/bids")]
    [InlineData("\"max_calls\": 10", "\"max_calls\": 0", "PIPELINE_COLUMNS_CONFIG", "/nodes/content/config/max_calls")]
    [InlineData("\"max_calls\": 10", "\"max_calls\": 10, \"timeout_ms\": 0", "PIPELINE_COLUMNS_CONFIG", "/nodes/content/config/timeout_ms")]
    [InlineData("\"max_calls\": 10", "\"max_calls\": 10, \"by\": \"col.entity_id\"", "PIPELINE_COLUMNS_CONFIG", "/nodes/content/config/by")]
    [InlineData("\"slots\": \"col.entity_id\"", "\"slots\": \"col.item.clicks_7d\"", "PIPELINE_CALL_INPUT", "/nodes/content/config/inputs/slots")]
    public void AListThatDoesNotFit_IsRefusedWhereItIsWritten(string from, string to, string code, string pointer)
    {
        Assert.Contains(from, OnePipeline, StringComparison.Ordinal);
        var error = TheError(Host(OnePipeline.Replace(from, to, StringComparison.Ordinal)));

        Assert.Equal((code, pointer), (error.Code, error.Pointer));
    }

    [Theory]
    [InlineData("\"tags\": \"tag\"", "\"tags\": \"country\"", "PIPELINE_CALL_OUTPUT", "/nodes/content/config/pipelines/8/columns/tags")]
    [InlineData("\"bids\": \"weight\", ", "", "PIPELINE_CALL_OUTPUT", "/nodes/content/config/columns/bids")]
    [InlineData("\"pipeline\": \"tagged\"", "\"pipeline\": \"tagged\", \"inputs\": { \"slots\": \"1\" }", "PIPELINE_CALL_INPUT", "/nodes/content/config/pipelines/8/inputs/slots")]
    [InlineData("\"pipeline\": \"tagged\"", "\"pipeline\": \"tagging\"", "PIPELINE_CALL_UNKNOWN", "/nodes/content/config/pipelines/8/pipeline")]
    [InlineData("\"pipeline\": \"promoted\", \"inputs\": { \"slots\": \"col.entity_id\" }, ", "\"inputs\": { \"slots\": \"col.entity_id\" }, ", "PIPELINE_COLUMNS_CONFIG", "/nodes/content/config/inputs")]
    [InlineData("\"max_calls\": 10,", "\"max_calls\": 10, \"by\": \"col.recall.best_score\",", "PIPELINE_COLUMNS_CONFIG", "/nodes/content/config/by")]
    public void AnEntryThatDoesNotFit_IsRefusedWhereItIsWritten(string from, string to, string code, string pointer)
    {
        Assert.Contains(from, TwoPipelines, StringComparison.Ordinal);
        var error = TheError(Host(TwoPipelines.Replace(from, to, StringComparison.Ordinal)));

        Assert.Equal((code, pointer), (error.Code, error.Pointer));
    }

    private static ConfigDiagnostic TheError(HomeFeedTestHost host)
    {
        var diagnostics = new DiagnosticBag();
        var request = host.Workspace.CreateCompileRequest("page", "local", diagnostics)!;
        var compilation = PlanCompiler.Compile(request, host.Environment);
        Assert.False(compilation.Succeeded);
        var errors = compilation.Diagnostics.Where(d => d.IsError).ToList();
        Assert.True(errors.Count == 1, string.Join("\n", errors));
        return errors[0];
    }
}
