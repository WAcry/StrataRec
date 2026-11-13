using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using StrataRec.Core.Columns;
using StrataRec.Core.Memory;
using StrataRec.Core.Values;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Operators.Standard;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Replay;

namespace StrataRec.IntegrationTests;

/// <summary>
/// Nothing is read that no successful attempt wrote (design 4.6, <c>CandidateTable</c>): a row has no value in a column
/// until something writes it, even when the column's slot is created after the row was appended (an earlier row source
/// without the <c>recall.*</c> columns, then <c>MultiSourceRecall@1</c>); and a failed attempt leaves no slot behind, so a
/// pinned <c>col.x@node</c> of a node that failed reads as missing, live and on replay alike. Configurations that could
/// only fail at runtime this way are rejected when the plan is compiled.
/// </summary>
/// <remarks>Requests run on a pool whose arrays are full of bytes that look like data, as after an earlier request.</remarks>
public sealed class UnwrittenAndUncommittedDataTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private const string RecallSources = """
        "r": { "client": "memory_list@1", "items": [ { "id": 7, "score": 0.7 }, { "id": 8, "score": 0.8 } ] },
        "s": { "client": "memory_list@1", "items": [ { "id": 5, "score": 0.5 }, { "id": 6, "score": 0.6 } ] },
        "t": { "client": "memory_list@1", "items": [ { "id": 6, "score": 0.9 }, { "id": 9, "score": 0.3 } ] }
        """;

    private const string NullableRecallColumns = """
        "recall.sources_mask": { "type": "int64?" }, "recall.best_rank": { "type": "int64?" },
        "recall.best_score": { "type": "float64?" }, "recall.primary_source": { "type": "string?" }
        """;

    [Fact]
    public void ARowSourceBeforeRecall_ThatDoesNotWriteTheRecallColumns_IsRejected_AtIt()
    {
        // recall.* are non-nullable: the seed's rows would have no value in them.
        var compilation = TryCompile(columns: "", nodes: """
            "seed": { "order": 50, "op": "Rows@1", "config": { "ids": [1, 2] } },
            "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "quotas_r" } }
            """, output: """ "best": "col.recall.best_score" """);

        Assert.False(compilation.Succeeded);
        var errors = compilation.Diagnostics.Where(d => d.IsError).ToList();
        Assert.Equal(4, errors.Count);
        Assert.All(errors, d => Assert.Equal(("ROW_SOURCE_BEFORE_COLUMN", "/nodes/seed"), (d.Code, d.Pointer)));
        var score = errors.Single(d => d.Message.Contains("'recall.best_score'", StringComparison.Ordinal));
        Assert.Contains("which the later row source 'recall' writes", score.Message, StringComparison.Ordinal);
        Assert.Contains("\"recall.best_score\": { \"type\": \"float64?\" }", score.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNullableRecallColumns_TheEarlierRowSourcesRowsHaveNoRecallValues_AndAReplayAgrees()
    {
        // The seed appends 1 and 2 without recall.*; recall then creates those slots for its own rows 7 and 8. The seed's
        // rows used to read the slot's pooled bytes (or '' and 0) as valid values.
        var plan = Compile(NullableRecallColumns, """
            "seed": { "order": 50, "op": "Rows@1", "config": { "ids": [1, 2] } },
            "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "quotas_r" } }
            """, RecallOutput);

        var recorded = await Execute(plan, record: true);

        Assert.True(recorded.Succeeded, recorded.Error);
        Assert.Equal(new[]
        {
            (1L, false, -1.0, "none", -1L), (2L, false, -1.0, "none", -1L), (7L, true, 0.7, "r", 1L), (8L, true, 0.8, "r", 2L),
        }, RecallItems(recorded));
        await AssertReplayAgrees(plan, recorded);
    }

    [Fact]
    public async Task TwoRecallsWritingTheSameColumns_EachRowKeepsTheValuesOfTheRecallThatAppendedIt()
    {
        // Both write the one shared version of recall.*; the second appends only 9 (6 is already in the table), and the
        // seed's rows keep the seed's values after the second commits.
        var plan = Compile(columns: "", """
            "seed": { "order": 50, "op": "MultiSourceRecall@1", "config": { "quotas_param": "quotas_s" } },
            "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "quotas_t" } }
            """, RecallOutput);

        var result = await Execute(plan);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(new[] { (5L, true, 0.5, "s", 1L), (6L, true, 0.6, "s", 2L), (9L, true, 0.3, "t", 2L) }, RecallItems(result));
    }

    [Fact]
    public async Task ARecallThatFailsAfterCreatingTheRecallColumns_LeavesNothing_AndItsFallbackServesAsInAReplay()
    {
        // The failing primary is the first to write recall.*: it creates the slots, writes 7 and 8, and fails. The rollback
        // drops those slots; the fallback creates them again for its own rows. The seed's rows have no values either way.
        var plan = Compile(NullableRecallColumns, """
            "seed": { "order": 50, "op": "Rows@1", "config": { "ids": [1, 2] } },
            "recall": { "order": 100, "op": "FailingRecall@1", "on_failure": "fallback:recall_backup", "config": { "quotas_param": "quotas_r" } },
            "recall_backup": { "role": "fallback", "op": "MultiSourceRecall@1", "config": { "quotas_param": "quotas_t" } }
            """, RecallOutput);

        var recorded = await Execute(plan, record: true);

        Assert.True(recorded.Succeeded, recorded.Error);
        Assert.Equal(new[] { (1L, false, -1.0, "none", -1L), (2L, false, -1.0, "none", -1L), (6L, true, 0.9, "t", 1L), (9L, true, 0.3, "t", 2L) },
            RecallItems(recorded));
        Assert.Equal("recall_backup", recorded.Outcomes.Single(o => o.NodeId == "recall").ProducedBy);
        await AssertReplayAgrees(plan, recorded);
    }

    [Fact]
    public async Task ARowSourceThatLeavesItsOwnRowsUnwritten_FailsAtCommit_AndTheDefaultDoesNotFillThem()
    {
        // score.x is non-nullable with a default; the row source declares it but writes only its first row. The default
        // replaces values that cannot be stored, never a value nobody wrote.
        var plan = Compile(""" "score.x": { "type": "float64", "default": 0.0 } """, """
            "seed": { "order": 50, "op": "Rows@1", "config": { "ids": [1, 2, 3], "output_col": "score.x", "write_first": 1 } }
            """, """ "x": "col.score.x" """);

        var result = await Execute(plan);

        Assert.False(result.Succeeded);
        Assert.Equal("node 'seed' failed: left non-nullable column 'score.x' without a value on 2 of 3 live rows", result.Error);
    }

    [Fact]
    public void ARowSourceWritingAColumnAnotherNodeRewrote_IsRejected_AndOneAfterANodesColumnIsToldToMoveBeforeIt()
    {
        var compilation = TryCompile("""
            "score.x": { "type": "float64?", "flow": true }, "score.y": { "type": "float64", "default": 0.0 }
            """, """
            "seed": { "order": 50, "op": "Rows@1", "config": { "ids": [1, 2], "output_col": "score.x" } },
            "rescore": { "order": 60, "op": "ExpressionScorer@1", "config": { "output_col": "score.x", "expr": "1.0" } },
            "score_y": { "order": 70, "op": "ExpressionScorer@1", "config": { "output_col": "score.y", "expr": "2.0" } },
            "more": { "order": 80, "op": "Rows@1", "config": { "ids": [3], "output_col": "score.x" } }
            """, """ "x": "col.score.x ?? -1.0", "y": "col.score.y" """);

        Assert.False(compilation.Succeeded);
        var errors = compilation.Diagnostics.Where(d => d.IsError).OrderBy(d => d.Code, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { ("ROW_SOURCE_AFTER_COLUMN", "/nodes/more"), ("ROW_SOURCE_SHADOWS_VERSION", "/nodes/more") },
            errors.Select(d => (d.Code, d.Pointer)));
        Assert.Contains("after 'rescore' wrote its own version", errors[1].Message, StringComparison.Ordinal);
        Assert.StartsWith("move the row source before 'score_y'", errors[0].Hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("row source fallback after a column", "ROW_SOURCE_AFTER_COLUMN /nodes/backup", "before 'base'")]
    [InlineData("row source fallback over a version", "ROW_SOURCE_SHADOWS_VERSION /nodes/backup", "after 'base' wrote its own version")]
    [InlineData("row source fallback first to write", "ROW_SOURCE_BEFORE_COLUMN /nodes/seed", "later row source 'backup'")]
    [InlineData("version of the node, later row source", "ROW_SOURCE_SHADOWS_VERSION /nodes/more", "after 'primary' wrote its own version")]
    [InlineData("rows of the fallback, later row source", "ROW_SOURCE_BEFORE_COLUMN /nodes/backup|ROW_SOURCE_BEFORE_COLUMN /nodes/seed", "later row source 'more'")]
    [InlineData("column fallback of a row source", "COLUMN_MULTIPLE_PRODUCERS /nodes/backup", "both 'seed' and 'backup'")]
    [InlineData("row source fallback of a row source", "ROW_SOURCE_BEFORE_COLUMN /nodes/seed|ROW_SOURCE_BEFORE_COLUMN /nodes/seed|ROW_SOURCE_BEFORE_COLUMN /nodes/seed|ROW_SOURCE_BEFORE_COLUMN /nodes/seed", "later row source 'recall'")]
    public void AFallbackIsChecked_AgainstWhatCameBeforeItsNode_AndWhatFollowsSeesWhatEitherMayLeave(string scenario, string expected, string mention)
    {
        // A fallback runs where its node failed and was rolled back, so it must pass the checks the node's own position
        // would apply to it, whatever the node's effects (review 13). It used to be checked only for its outputs and
        // inputs: a row source standing in for a column writer appended rows whose non-nullable columns read pooled data.
        // Each effect is checked once: a row source fallback of a row source reports nothing twice.
        var (columns, nodes) = scenario switch
        {
            "row source fallback after a column" => ("""
                "score.x": { "type": "float64", "default": 0.0 }, "score.y": { "type": "float64?" }, "score.seen": { "type": "float64", "default": 0.0 }
                """, """
                "seed": { "order": 50, "op": "Rows@1", "config": { "ids": [7] } },
                "base": { "order": 60, "op": "ExpressionScorer@1", "config": { "output_col": "score.x", "expr": "10.0" } },
                "primary": { "order": 70, "op": "Late@1", "on_failure": "fallback:backup", "config": { "output_col": "score.y", "how": "fails" } },
                "backup": { "role": "fallback", "op": "Rows@1", "config": { "ids": [8], "output_col": "score.y" } },
                "seen": { "order": 80, "op": "ExpressionScorer@1", "config": { "output_col": "score.seen", "expr": "col.score.x + 1.0" } }
                """),
            "row source fallback over a version" => ("""
                "score.x": { "type": "float64?", "flow": true }
                """, """
                "seed": { "order": 50, "op": "Rows@1", "config": { "ids": [7] } },
                "base": { "order": 60, "op": "ExpressionScorer@1", "config": { "output_col": "score.x", "expr": "10.0" } },
                "primary": { "order": 70, "op": "Late@1", "on_failure": "fallback:backup", "config": { "output_col": "score.x", "how": "fails" } },
                "backup": { "role": "fallback", "op": "Rows@1", "config": { "ids": [8], "output_col": "score.x" } }
                """),
            "row source fallback first to write" => ("""
                "score.y": { "type": "float64", "default": 0.0 }
                """, """
                "seed": { "order": 50, "op": "Rows@1", "config": { "ids": [7] } },
                "primary": { "order": 70, "op": "Late@1", "on_failure": "fallback:backup", "config": { "output_col": "score.y", "how": "fails" } },
                "backup": { "role": "fallback", "op": "Rows@1", "config": { "ids": [8], "output_col": "score.y" } }
                """),
            "version of the node, later row source" => ("""
                "score.y": { "type": "float64?" }
                """, """
                "seed": { "order": 50, "op": "Rows@1", "config": { "ids": [7] } },
                "primary": { "order": 70, "op": "Late@1", "on_failure": "fallback:backup", "config": { "output_col": "score.y", "how": "fails" } },
                "backup": { "role": "fallback", "op": "Rows@1", "config": { "ids": [8], "output_col": "score.y" } },
                "more": { "order": 80, "op": "Rows@1", "config": { "ids": [9], "output_col": "score.y" } }
                """),
            "rows of the fallback, later row source" => ("""
                "score.y": { "type": "float64?" }, "score.z": { "type": "float64", "default": 0.0 }
                """, """
                "seed": { "order": 50, "op": "Rows@1", "config": { "ids": [7] } },
                "primary": { "order": 70, "op": "Late@1", "on_failure": "fallback:backup", "config": { "output_col": "score.y", "how": "fails" } },
                "backup": { "role": "fallback", "op": "Rows@1", "config": { "ids": [8], "output_col": "score.y" } },
                "more": { "order": 80, "op": "Rows@1", "config": { "ids": [9], "output_col": "score.z" } }
                """),
            "column fallback of a row source" => ("""
                "score.x": { "type": "float64?" }
                """, """
                "seed": { "order": 50, "op": "Rows@1", "config": { "ids": [7], "output_col": "score.x" } },
                "primary": { "order": 70, "op": "Rows@1", "on_failure": "fallback:backup", "config": { "ids": [8], "output_col": "score.x" } },
                "backup": { "role": "fallback", "op": "ExpressionScorer@1", "config": { "output_col": "score.x", "expr": "1.0" } }
                """),
            _ => ("", """
                "seed": { "order": 50, "op": "Rows@1", "config": { "ids": [1, 2] } },
                "recall": { "order": 100, "op": "FailingRecall@1", "on_failure": "fallback:backup", "config": { "quotas_param": "quotas_r" } },
                "backup": { "role": "fallback", "op": "MultiSourceRecall@1", "config": { "quotas_param": "quotas_t" } }
                """),
        };

        var compilation = TryCompile(columns, nodes, """ "one": "1.0" """);

        Assert.False(compilation.Succeeded);
        var errors = compilation.Diagnostics.Where(d => d.IsError).ToList();
        Assert.Equal(expected.Split('|'), errors.Select(d => $"{d.Code} {d.Pointer}").Order(StringComparer.Ordinal));
        Assert.All(errors, d => Assert.Contains(mention, d.Message + " " + d.Hint, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("row source shares an order", "ORDER_DEPENDENCY /nodes/score_x/order")]
    [InlineData("row source fallback shares an order", "ORDER_DEPENDENCY /nodes/score_x/order")]
    [InlineData("fallback reads what the other writes", "ORDER_DEPENDENCY /nodes/score_x/order")]
    [InlineData("independent fallback", null)]
    public void NodesSharingAnOrder_AreIndependentInEveryBranch_FallbacksIncluded(string scenario, string? expected)
    {
        // Nodes of one order run in an unspecified relative order (design 5.3), so neither may have row effects or read
        // what the other writes. A position runs its node or its fallback: a fallback that appends rows, or reads the other
        // node's column, used to pass because only the node was checked, and the result then depended on node names
        // (review 15).
        var primary = scenario switch
        {
            "row source shares an order" => """
                "primary": { "order": 70, "op": "Rows@1", "config": { "ids": [8], "output_col": "score.y" } }
                """,
            "row source fallback shares an order" => """
                "primary": { "order": 70, "op": "Late@1", "on_failure": "fallback:backup", "config": { "output_col": "score.y", "how": "fails" } },
                "backup": { "role": "fallback", "op": "Rows@1", "config": { "ids": [8], "output_col": "score.y" } }
                """,
            "fallback reads what the other writes" => """
                "primary": { "order": 70, "op": "Late@1", "on_failure": "fallback:backup", "config": { "output_col": "score.y", "how": "fails" } },
                "backup": { "role": "fallback", "op": "ExpressionScorer@1", "config": { "output_col": "score.y", "expr": "(col.score.x ?? 0.0) + 1.0" } }
                """,
            _ => """
                "primary": { "order": 70, "op": "Late@1", "on_failure": "fallback:backup", "config": { "output_col": "score.y", "how": "fails" } },
                "backup": { "role": "fallback", "op": "ExpressionScorer@1", "config": { "output_col": "score.y", "expr": "1.0" } }
                """,
        };

        var compilation = TryCompile("""
            "score.x": { "type": "float64?" }, "score.y": { "type": "float64?" }
            """, """
            "seed": { "order": 50, "op": "Rows@1", "config": { "ids": [7] } },
            """ + primary + """
            ,
            "score_x": { "order": 70, "op": "ExpressionScorer@1", "config": { "output_col": "score.x", "expr": "10.0" } }
            """, """ "x": "col.score.x ?? -1.0" """);

        var errors = compilation.Diagnostics.Where(d => d.IsError).Select(d => $"{d.Code} {d.Pointer}").ToList();
        Assert.Equal(expected is null ? Array.Empty<string>() : new[] { expected }, errors);
    }

    [Fact]
    public async Task ARowSourceFallbackOfAColumnWriter_ThatPassesTheChecks_ServesItsRows_AsInAReplay()
    {
        // The rules hold (no non-nullable column before it, nobody's version under it), so effects may differ: the node
        // writes 99 into its own version of score.y and fails; the fallback appends 8 and writes the shared version.
        var plan = Compile(""" "score.y": { "type": "float64?" } """, """
            "seed": { "order": 50, "op": "Rows@1", "config": { "ids": [7] } },
            "primary": { "order": 70, "op": "Late@1", "on_failure": "fallback:backup", "config": { "output_col": "score.y", "how": "fails" } },
            "backup": { "role": "fallback", "op": "Rows@1", "config": { "ids": [8], "output_col": "score.y" } }
            """, """ "y": "col.score.y ?? -1.0" """);

        var recorded = await Execute(plan, record: true);

        Assert.True(recorded.Succeeded, recorded.Error);
        Assert.Equal(new[] { (7L, -1.0), (8L, 0.8) }, recorded.Items.Select(i => (i.Id.Int64Value, i.Columns["y"].AsFloat64())));
        Assert.Equal("backup", recorded.Outcomes.Single(o => o.NodeId == "primary").ProducedBy);
        await AssertReplayAgrees(plan, recorded);
    }

    [Theory]
    [InlineData("fails")]
    [InlineData("throws")]
    [InlineData("returns late")]
    [InlineData("fails to commit")]
    public async Task APinnedReadOfANodeThatFailed_IsMissing_HoweverItFailed_LiveAndOnReplay(string how)
    {
        // base commits score.x = 1. late writes 99 into its own version and fails; on_failure skips it. The output and a
        // later node read its version pinned. They used to read 99 live, while a replay, which skips a recorded timeout
        // instead of running it, read it as missing (review 11).
        var timeout = how == "returns late" ? ", \"timeout_ms\": 50" : string.Empty;
        var plan = Compile(""" "score.x": { "type": "float64", "flow": true, "default": 0.0 }, "score.seen": { "type": "float64", "default": 0.0 } """, $$"""
            "seed": { "order": 50, "op": "Rows@1", "config": { "ids": [7, 8] } },
            "base": { "order": 60, "op": "ExpressionScorer@1", "config": { "output_col": "score.x", "expr": "1.0" } },
            "late": { "order": 70, "op": "Late@1", "on_failure": "skip"{{timeout}}, "config": { "output_col": "score.x", "how": "{{how}}" } },
            "seen": { "order": 80, "op": "ExpressionScorer@1", "config": { "output_col": "score.seen", "expr": "col.score.x@late ?? -1.0" } }
            """, """ "current": "col.score.x", "pinned": "col.score.x@late ?? -1.0", "seen": "col.score.seen" """);

        var recorded = await Execute(plan, record: true);

        Assert.True(recorded.Succeeded, recorded.Error);
        Assert.Equal(new[] { (7L, 1.0, -1.0, -1.0), (8L, 1.0, -1.0, -1.0) }, PinnedItems(recorded));
        var late = recorded.Outcomes.Single(o => o.NodeId == "late");
        Assert.Equal((NodeOutcomeKind.Failed, LateFailure(how) + " (skipped)"), (late.Kind, late.Detail));
        await AssertReplayAgrees(plan, recorded);
    }

    private const string RecallOutput = """
        "known": "has(col.recall.sources_mask)", "best": "col.recall.best_score ?? -1.0",
        "primary": "col.recall.primary_source ?? 'none'", "rank": "col.recall.best_rank ?? -1"
        """;

    private static string LateFailure(string how) => how switch
    {
        "fails" => "gave up after writing",
        "throws" => "InvalidOperationException: broke after writing",
        "returns late" => "timed out after 50 ms",
        _ => "left non-nullable column 'score.x' without a value on 1 of 2 live rows",
    };

    private static List<(long, bool, double, string, long)> RecallItems(PipelineResult result) => result.Items
        .Select(i => (i.Id.Int64Value, i.Columns["known"].AsBool(), i.Columns["best"].AsFloat64(), i.Columns["primary"].AsString(), i.Columns["rank"].AsInt64()))
        .ToList();

    private static List<(long, double, double, double)> PinnedItems(PipelineResult result) => result.Items
        .Select(i => (i.Id.Int64Value, i.Columns["current"].AsFloat64(), i.Columns["pinned"].AsFloat64(), i.Columns["seen"].AsFloat64()))
        .ToList();

    private static async Task AssertReplayAgrees(ExecutionPlan plan, PipelineResult recorded)
    {
        var replay = RunReplay.Create(recorded.Recording!, "/runs/0");
        var replayed = await Execute(plan, replay: replay);

        Assert.True(replayed.Succeeded, replayed.Error);
        Assert.Empty(replay.Gaps);
        Assert.Equal(Served(recorded), Served(replayed));
        Assert.Equal(Outcomes(recorded), Outcomes(replayed));
    }

    private static List<string> Served(PipelineResult result) => result.Items
        .Select(i => $"{i.Id.Int64Value}: " + string.Join(", ", i.Columns.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key}={c.Value}")))
        .ToList();

    private static List<(string, NodeOutcomeKind, string?, string?, int, int)> Outcomes(PipelineResult result) =>
        result.Outcomes.Select(o => (o.NodeId, o.Kind, o.Detail, o.ProducedBy, o.RowsIn, o.RowsOut)).ToList();

    private static ExecutionPlan Compile(string columns, string nodes, string output)
    {
        var compilation = TryCompile(columns, nodes, output);
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        return compilation.Plan!;
    }

    private static Experiments.Compilation.PlanCompilation TryCompile(string columns, string nodes, string output) => MiniWorkspace.TryCompile(
        RecallSources,
        $$"""
        { "pipeline_id": "p", "entity_key_type": "int64",
          "columns": { {{columns}} },
          "param_schema": { "quotas_r": { "type": "map<string,int64>" }, "quotas_s": { "type": "map<string,int64>" },
                            "quotas_t": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
          "params": { "quotas_r": { "r": 2 }, "quotas_s": { "s": 2 }, "quotas_t": { "t": 2 }, "k": 10 },
          "nodes": { {{nodes}} },
          "output": { "limit_param": "k", "columns": { {{output}} } } }
        """,
        operators: OperatorRegistry.CreateDefault().Add(new RowsFactory()).Add(new LateFactory()).Add(new FailingRecallFactory()));

    private static async Task<PipelineResult> Execute(ExecutionPlan plan, bool record = false, RunReplay? replay = null) =>
        await new PipelineExecutor(new DirtyPool()).ExecuteAsync(plan, new PipelineRequest
        {
            RequestId = "r",
            Inputs = new RequestInputs(),
            TimeMs = 1_767_225_600_000,
            Deadline = replay is null ? TimeSpan.FromSeconds(600) : RunReplay.Deadline,
            Record = record,
            Replay = replay,
        }).AsTask().WaitAsync(Patience);

    public sealed class RowsConfig
    {
        public required long[] Ids { get; init; }

        public string? OutputCol { get; init; }

        public int? WriteFirst { get; init; }
    }

    /// <summary><c>Rows@1</c>: appends its ids; with <c>output_col</c>, writes id / 10 into it for its first <c>write_first</c> rows (all by default).</summary>
    private sealed class RowsFactory : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } = new("Rows", 1, Effects.RowSource, typeof(RowsConfig), "Appends fixed ids.");

        public Operator? Compile(OperatorCompileContext context)
        {
            var config = context.BindConfig<RowsConfig>()!;
            OutputColumn? output = null;
            if (config.OutputCol is { } name)
            {
                if (!context.TryWriteColumn(name, "/output_col", null, out var column))
                {
                    return null;
                }

                output = column;
            }

            return new Rows(config.Ids, output, config.WriteFirst ?? config.Ids.Length);
        }

        private sealed class Rows(long[] ids, OutputColumn? output, int writeFirst) : Operator
        {
            public override int GetRowLimit(ReadOnlySpan<ScalarValue> parameters) => ids.Length;

            public override ValueTask ExecuteAsync(OperatorContext context)
            {
                var first = context.AppendRows(ids);
                if (output is { } column)
                {
                    var buffer = context.Output(column);
                    for (var i = 0; i < writeFirst; i++)
                    {
                        ColumnValues.Set(buffer, first + i, ScalarValue.FromFloat64(ids[i] / 10.0));
                    }
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    public sealed class LateConfig
    {
        public required string OutputCol { get; init; }

        public required string How { get; init; }
    }

    /// <summary>
    /// <c>Late@1</c>: writes 99 into its output on every live row, then fails: it reports a failure, throws, returns only
    /// after its budget ran out, or (to fail its commit) leaves the last row unwritten.
    /// </summary>
    private sealed class LateFactory : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } = new("Late", 1, Effects.ColumnWriter, typeof(LateConfig), "Writes 99, then fails.");

        public Operator? Compile(OperatorCompileContext context)
        {
            var config = context.BindConfig<LateConfig>()!;
            return context.TryWriteColumn(config.OutputCol, "/output_col", null, out var output) ? new Late(output, config.How) : null;
        }

        private sealed class Late(OutputColumn output, string how) : Operator
        {
            public override ValueTask ExecuteAsync(OperatorContext context)
            {
                var buffer = (ScalarColumn<double>)context.Output(output);
                var rows = context.LiveRows;
                var written = how == "fails to commit" ? rows.Length - 1 : rows.Length;
                for (var i = 0; i < written; i++)
                {
                    buffer.Set(rows[i], 99);
                }

                switch (how)
                {
                    case "fails":
                        context.Fail("gave up after writing");
                        break;
                    case "throws":
                        throw new InvalidOperationException("broke after writing");
                    case "returns late":
                        Assert.True(context.CancellationToken.WaitHandle.WaitOne(Patience)); // then returns, as a success
                        break;
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary><c>FailingRecall@1</c>: <c>MultiSourceRecall@1</c> that reports a failure once it has appended and written its rows.</summary>
    private sealed class FailingRecallFactory : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } =
            new("FailingRecall", 1, Effects.RowSource, typeof(MultiSourceRecallConfig), "MultiSourceRecall@1 that fails after writing.");

        public Operator? Compile(OperatorCompileContext context) =>
            new MultiSourceRecallFactory().Compile(context) is { } recall ? new FailingRecall(recall) : null;

        private sealed class FailingRecall(Operator recall) : Operator
        {
            public override int GetRowLimit(ReadOnlySpan<ScalarValue> parameters) => recall.GetRowLimit(parameters);

            public override async ValueTask ExecuteAsync(OperatorContext context)
            {
                var before = context.Table.PhysicalRowCount;
                await recall.ExecuteAsync(context);
                Assert.True(context.Table.PhysicalRowCount > before, "the primary appended nothing");
                context.Fail("gave up after appending");
            }
        }
    }

    /// <summary>Hands out new arrays filled with 0xA5 bytes (references stay null), like a pool holding an earlier request's data.</summary>
    private sealed class DirtyPool : IBufferPool
    {
        public T[] Rent<T>(int minimumLength)
        {
            var array = new T[Math.Max(1, minimumLength)];
            if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                MemoryMarshal.CreateSpan(ref Unsafe.As<T, byte>(ref MemoryMarshal.GetArrayDataReference(array)), array.Length * Unsafe.SizeOf<T>()).Fill(0xA5);
            }

            return array;
        }

        public void Return<T>(T[] array, bool clearArray)
        {
        }
    }
}
