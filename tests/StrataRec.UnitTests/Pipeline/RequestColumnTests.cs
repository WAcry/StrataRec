using StrataRec.Artifacts.Model;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// Request columns (<c>request_columns</c>, read as <c>req.*</c>): written by a node, staged while it runs and published
/// when it commits with the rules of a column; read by later nodes and their <c>when</c>, a tap's context and the output's
/// values; checked on every path as columns are.
/// </summary>
public sealed class RequestColumnTests
{
    /// <summary>id → tag, score (null: none). Scores 0.9, 0.5, 0.3, 0.2, 0.1; item 6 has none.</summary>
    private static readonly (long Id, string Tag, double? Score)[] Items =
    [
        (1, "a", 0.9),
        (2, "a", 0.5),
        (3, "b", 0.3),
        (4, "b", 0.2),
        (5, "c", 0.1),
        (6, "c", null),
    ];

    private const string Stats = """
        "stats": { "order": 200, "op": "RequestAggregate@1", "config": { "values": {
            "candidates.count": { "fn": "count" },
            "candidates.a": { "fn": "count", "where": "col.item.tag == 'a'" },
            "score.count": { "fn": "count", "of": "col.item.score" },
            "score.sum": { "fn": "sum", "of": "col.item.score" },
            "score.mean": { "fn": "mean", "of": "col.item.score" },
            "score.min": { "fn": "min", "of": "col.item.score" },
            "score.max": { "fn": "max", "of": "col.item.score" },
            "score.max_b": { "fn": "max", "of": "col.item.score", "where": "col.item.tag == 'b'" },
            "weight.sum": { "fn": "sum", "of": "col.item.weight" } } } }
        """;

    private const string StatsColumns = """
        "candidates.count": { "type": "int64" }, "candidates.a": { "type": "int32", "default": 0 }, "score.count": { "type": "int64" },
        "score.sum": { "type": "float64?" }, "score.mean": { "type": "float64?" }, "score.min": { "type": "float64?" },
        "score.max": { "type": "float64?" }, "score.max_b": { "type": "float32?" }, "weight.sum": { "type": "int64?" }
        """;

    [Fact]
    public async Task RequestAggregate_WritesStatisticsOfTheLiveCandidates_LeavingOutMissingValues()
    {
        var plan = Compile(StatsColumns, Stats, values: """
            "n": "req.candidates.count", "a": "req.candidates.a", "scored": "req.score.count", "sum": "req.score.sum",
            "mean": "req.score.mean", "min": "req.score.min", "max": "req.score.max", "max_b": "req.score.max_b", "weights": "req.weight.sum"
            """);

        var result = await Run(plan);

        Assert.True(result.Succeeded, result.Error);
        var values = Values(result);
        Assert.Equal(6, values["n"].AsInt64());
        Assert.Equal(2, values["a"].AsInt64());
        Assert.Equal(5, values["scored"].AsInt64()); // count of an expression: the rows where it has a value
        Assert.Equal(2.0, values["sum"].AsFloat64(), 12);
        Assert.Equal(0.4, values["mean"].AsFloat64(), 12);
        Assert.Equal(0.1, values["min"].AsFloat64());
        Assert.Equal(0.9, values["max"].AsFloat64());
        Assert.Equal(0.3f, values["max_b"].AsFloat32()); // stored as its float32 declaration holds it
        Assert.Equal(21, values["weights"].AsInt64()); // an integer expression sums as an integer
        Assert.Equal(ColumnType.Parse("int32"), Type(result, "a")); // a plain reference: the declared type
        Assert.Equal(ColumnType.Parse("float32?"), Type(result, "max_b"));
    }

    [Fact]
    public async Task WithNoCandidate_CountIsZero_AndEveryOtherFunctionMissing()
    {
        var plan = Compile(StatsColumns, """
            "none": { "order": 150, "op": "ExpressionFilter@1", "config": { "predicate": "false" } },
            """ + Stats, values: """ "n": "req.candidates.count", "max": "req.score.max", "mean": "req.score.mean" """);

        var values = Values(await Run(plan));

        Assert.Equal(0, values["n"].AsInt64());
        Assert.True(values["max"].IsMissing);
        Assert.True(values["mean"].IsMissing);
    }

    /// <summary>A later node reads a request column per row, as it reads a request input: a threshold relative to the best score.</summary>
    [Fact]
    public async Task ALaterNode_ReadsARequestColumn_InItsExpressions()
    {
        var plan = Compile(StatsColumns, Stats + """
            , "relative": { "order": 300, "op": "ExpressionFilter@1", "config": { "predicate": "(col.item.score ?? 0.0) >= (req.score.max ?? 0.0) * 0.5" } }
            """);

        var result = await Run(plan);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal([1L, 2], Ids(result)); // 0.9 and 0.5 are at least half the best score
    }

    /// <summary>
    /// A <c>when</c> reading a request column is evaluated at its node, not ahead with its segment's: the recall's fetch
    /// barrier prefetched for the segment, where the count was not written yet, and the cached decision skipped the node.
    /// </summary>
    [Fact]
    public async Task AWhenReadingARequestColumn_IsEvaluatedAtItsNode_AfterTheWriteBeforeIt()
    {
        var plan = Compile(StatsColumns, Stats + """
            , "only_many": { "order": 300, "op": "TopK@1", "when": "req.candidates.count > 3", "config": { "score_col": "item.weight", "k": 2 } }
            , "only_few": { "order": 400, "op": "TopK@1", "when": "req.candidates.count <= 3", "config": { "score_col": "item.weight", "k": 1 } }
            """);

        var result = await Run(plan);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal([6L, 5], Ids(result)); // six candidates: only_many ran, only_few did not
        Assert.Equal(NodeOutcomeKind.SkippedWhen, Outcome(result, "only_few"));
    }

    /// <summary>A failed node's staged values are dropped with its writes: on_failure skip, then a fallback writing its own.</summary>
    [Fact]
    public async Task AFailedNode_WritesNothing_ItsFallbackWritesItsOwn()
    {
        var plan = Compile("""
            "decision": { "type": "string?", "flow": true }
            """, """
            "first": { "order": 200, "op": "SetRequest@1", "config": { "column": "decision", "text": "first" } },
            "failing": { "order": 300, "op": "SetRequest@1", "on_failure": "skip", "config": { "column": "decision", "text": "dropped", "fail": true } },
            "with_fallback": { "order": 400, "op": "SetRequest@1", "on_failure": "fallback", "fallback": "backup",
                "config": { "column": "decision", "text": "dropped too", "fail": true } },
            "backup": { "role": "fallback", "op": "SetRequest@1", "config": { "column": "decision", "text": "backup" } },
            "reader": { "order": 500, "op": "SetRequest@1", "when": "(req.decision ?? '') == 'backup'", "config": { "column": "seen", "text": "yes" } }
            """, values: """ "decision": "req.decision", "seen": "req.seen" """, extraColumns: """ "seen": { "type": "string?" } """);

        var result = await Run(plan);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("backup", Values(result)["decision"].AsString());
        Assert.Equal("yes", Values(result)["seen"].AsString());
        Assert.Equal(NodeOutcomeKind.Failed, Outcome(result, "failing"));
    }

    /// <summary>
    /// A node's write is the new value: a nullable flow column it wrote nothing to is missing after it, as a column whose
    /// slot a node never asked for is.
    /// </summary>
    [Fact]
    public async Task ANodeThatWritesNothing_LeavesItsNullableColumnMissing()
    {
        var plan = Compile("""
            "decision": { "type": "string?", "flow": true }
            """, """
            "first": { "order": 200, "op": "SetRequest@1", "config": { "column": "decision", "text": "first" } },
            "second": { "order": 300, "op": "SetRequest@1", "config": { "column": "decision", "unset": true } }
            """, values: """ "decision": "req.decision" """);

        Assert.True(Values(await Run(plan))["decision"].IsMissing);
    }

    [Fact]
    public async Task ANonNullableColumnLeftMissing_FailsItsNode()
    {
        var plan = Compile("""
            "decision": { "type": "string" }
            """, """
            "writer": { "order": 200, "op": "SetRequest@1", "config": { "column": "decision", "unset": true } }
            """);

        var result = await Run(plan);

        Assert.False(result.Succeeded);
        Assert.Contains("left non-nullable request column 'decision' without a value", result.Error);
    }

    /// <summary>
    /// An output column may read a request column, per item (here the count beside each item); a non-nullable one needs a
    /// producer, as the output's values do (review of 1102725: a plan was accepted and threw building the result).
    /// </summary>
    [Fact]
    public async Task AnOutputColumn_ReadsARequestColumn_AndANonNullableOneNeedsAProducer()
    {
        var plan = Compile(""" "n": { "type": "int64" } """, """
            "count": { "order": 200, "op": "RequestAggregate@1", "config": { "values": { "n": { "fn": "count" } } } }
            """, outputColumns: """ "candidates": "req.n" """);
        var result = await Run(plan);
        Assert.True(result.Succeeded, result.Error);
        Assert.All(result.Columns["candidates"].ToArray(), v => Assert.Equal(Items.Length, v.AsInt64()));

        var refused = TryCompile(""" "n": { "type": "int64" } """, """
            "other": { "order": 200, "op": "SetRequest@1", "when": "(ctx.request.user ?? '') == 'x'", "config": { "column": "n", "integer": 1 } }
            """, outputColumns: """ "candidates": "req.n + 1" """);
        var error = Assert.Single(refused.Diagnostics, d => d.IsError);
        Assert.Equal(("COLUMN_NO_PRODUCER", "/output/columns/candidates"), (error.Code, error.Pointer));
        Assert.Contains("its producer 'other' may be skipped", error.Message);
    }

    /// <summary>
    /// Integer sums and means are exact until the end (review of 1102725): a sum whose running total leaves int64 on the way
    /// is the sum, the mean of the extremes is -0.5, and only a sum outside int64 fails the node. Rows 1, 2, 3 in order.
    /// </summary>
    [Theory]
    [InlineData("sum", "col.entity_id == 1 ? 9223372036854775807 : (col.entity_id == 2 ? 1 : -1)", "9223372036854775807")]
    [InlineData("sum", "col.entity_id == 1 ? -9223372036854775807 - 1 : (col.entity_id == 2 ? -1 : 1)", "-9223372036854775808")]
    [InlineData("mean", "col.entity_id == 1 ? 9223372036854775807 : (col.entity_id == 2 ? -9223372036854775807 - 1 : 0)", "-0.3333333333333333")]
    [InlineData("sum", "col.entity_id == 3 ? 0 : 9223372036854775807", null)]
    public async Task IntegerSumsAndMeans_AreExact_UntilTheEnd(string fn, string of, string? expected)
    {
        var type = fn == "mean" ? "float64?" : "int64?";
        var plan = Compile($$""" "x": { "type": "{{type}}" } """, $$"""
            "agg": { "order": 200, "op": "RequestAggregate@1", "on_failure": "skip",
                     "config": { "values": { "x": { "fn": "{{fn}}", "of": "{{of}}", "where": "col.entity_id <= 3" } } } }
            """, values: """ "x": "req.x" """);

        var result = await Run(plan);

        Assert.True(result.Succeeded, result.Error);
        if (expected is null)
        {
            Assert.Equal(NodeOutcomeKind.Failed, Outcome(result, "agg"));
            Assert.True(Values(result)["x"].IsMissing);
        }
        else
        {
            Assert.Equal(expected, fn == "mean"
                ? Values(result)["x"].AsFloat64().ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                : Values(result)["x"].AsInt64().ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// A node's <c>when</c> reads no parameter (design 5.5, review of 1102725): an experiment overriding it would decide
    /// whether the node runs before its trigger is recorded, which only a running node's read records.
    /// </summary>
    [Fact]
    public void AWhen_ReadingAParameter_IsRefused()
    {
        var compilation = TryCompile(""" "decision": { "type": "string?" } """, """
            "w": { "order": 200, "op": "SetRequest@1", "when": "param.k > 5", "config": { "column": "decision", "text": "a" } }
            """);

        var error = Assert.Single(compilation.Diagnostics, d => d.IsError);
        Assert.Equal(("WHEN_READS_PARAM", "/nodes/w/when"), (error.Code, error.Pointer));
        Assert.Contains("'param.k'", error.Message);
    }

    /// <summary>
    /// A defective operator's value fails its own node, which its <c>on_failure</c> handles (review of 1102725): one set
    /// through a handle it changed is checked against what the node resolved, and a list with a null element is refused.
    /// Committed, either broke a later reader instead: the output threw, the replay refused the recording.
    /// </summary>
    [Theory]
    [InlineData("\"forge\": true", "\"n\": { \"type\": \"int64?\" }", "is int64?, and node 'w' set it to a string value")]
    [InlineData("\"null_element\": true", "\"n\": { \"type\": \"list<string>?\" }", "a list whose element 1 is null")]
    public async Task ADefectiveOperatorsValue_FailsItsNode(string option, string declaration, string message)
    {
        var plan = Compile(declaration, $$"""
            "w": { "order": 200, "op": "SetRequest@1", "on_failure": "skip", "config": { "column": "n", {{option}} } }
            """, values: """ "n": "req.n" """);

        var result = await Run(plan);

        Assert.True(result.Succeeded, result.Error);
        Assert.True(Values(result)["n"].IsMissing);
        var outcome = result.Outcomes.Single(o => o.NodeId == "w");
        Assert.Equal(NodeOutcomeKind.Failed, outcome.Kind);
        Assert.Contains(message, outcome.Detail);
    }

    /// <summary>
    /// The aggregate's values share its buffers (review of 1102725: each value rented its own, held to the request's
    /// end): twenty values with a <c>where</c> and an <c>of</c> rent no more of the request's memory than one.
    /// </summary>
    [Fact]
    public async Task TheAggregatesValues_ShareItsBuffers()
    {
        static async Task<long> Rented(int values)
        {
            var columns = string.Join(", ", Enumerable.Range(0, values).Select(i => $"\"v{i}\": {{ \"type\": \"float64?\" }}"));
            var specs = string.Join(", ", Enumerable.Range(0, values).Select(i => $"\"v{i}\": {{ \"fn\": \"max\", \"of\": \"col.item.score\", \"where\": \"col.entity_id > {i % 3}\" }}"));
            var plan = Compile(columns, $$"""
                "agg": { "order": 200, "op": "RequestAggregate@1", "config": { "values": { {{specs}} } } },
                "probe": { "order": 300, "op": "MemoryProbe@1", "config": {} }
                """);
            var result = await Run(plan);
            Assert.True(result.Succeeded, result.Error);
            return MemoryProbeFactory.Last;
        }

        Assert.Equal(await Rented(1), await Rented(20));
    }

    /// <summary>The commit stores a value as the declared type holds it, replacing what it cannot hold with the default or missing.</summary>
    [Fact]
    public async Task TheCommit_ReplacesWhatTheTypeCannotHold_AndRoundsFloat32()
    {
        var plan = Compile("""
            "nan": { "type": "float64", "default": -1.0 }, "nan_nullable": { "type": "float64?" },
            "big": { "type": "int32", "default": 0 }, "single": { "type": "float32?" }, "single_default": { "type": "float32", "default": 0.1 }
            """, """
            "a": { "order": 200, "op": "SetRequest@1", "config": { "column": "nan", "nan": true } },
            "b": { "order": 210, "op": "SetRequest@1", "config": { "column": "nan_nullable", "nan": true } },
            "c": { "order": 220, "op": "SetRequest@1", "config": { "column": "big", "integer": 3000000000 } },
            "d": { "order": 230, "op": "SetRequest@1", "config": { "column": "single", "number": 0.1 } },
            "e": { "order": 240, "op": "SetRequest@1", "config": { "column": "single_default", "nan": true } }
            """, values: """
            "nan": "req.nan", "nan_nullable": "req.nan_nullable", "big": "req.big", "single": "req.single",
            "single_widened": "req.single ?? 0.0", "default_widened": "req.single_default + 0.0"
            """);

        var result = await Run(plan);

        var values = Values(result);
        Assert.Equal(-1.0, values["nan"].AsFloat64());
        Assert.True(values["nan_nullable"].IsMissing);
        Assert.Equal(0, values["big"].AsInt64());
        Assert.Equal(0.1f, values["single"].AsFloat32());
        Assert.True(values["single"].IsFloat32);
        // The default replacing a float32 value is a float32 too: a later reader sees what a written 0.1 would be.
        Assert.Equal((double)0.1f, values["single_widened"].AsFloat64());
        Assert.Equal((double)0.1f, values["default_widened"].AsFloat64());
        Assert.Equal(3, result.NonFiniteValues);
        Assert.Equal(1, result.OutOfRangeValues);
    }

    [Fact]
    public async Task AValueOfAnotherKind_FailsTheNode_NamingTheColumn()
    {
        var plan = Compile("""
            "n": { "type": "int64?" }
            """, """
            "writer": { "order": 200, "op": "SetRequest@1", "config": { "column": "n", "text": "three" } }
            """);

        var result = await Run(plan);

        Assert.False(result.Succeeded);
        Assert.Contains("Request column 'n' is int64?, and node 'writer' set it to a string value", result.Error);
    }

    /// <summary>A tap's context reads a request column as of its point; one whose producer was skipped is logged missing.</summary>
    [Fact]
    public async Task ATap_LogsARequestColumn_AsOfItsPoint()
    {
        var plan = Compile(StatsColumns + """
            , "decision": { "type": "string" }
            """, Stats + """
            , "maybe": { "order": 300, "op": "SetRequest@1", "when": "req.candidates.count > 100", "config": { "column": "decision", "text": "many" } }
            , "after": { "order": 400, "op": "TopK@1", "config": { "score_col": "item.weight", "k": 1 } }
            """, taps: """
            "after_stats": { "group": "all", "attach": { "after": "stats" }, "context": { "n": "req.candidates.count" } },
            "after_maybe": { "group": "all", "attach": { "after": "after" }, "context": { "decision": "req.decision", "n": "req.candidates.count" } }
            """);

        var result = await Run(plan, writeLogs: true);

        Assert.True(result.Succeeded, result.Error);
        var stats = Assert.Single(result.LogBatches, b => b.TapId == "after_stats");
        Assert.Equal(6, Context(stats, "n").AsInt64());
        var later = Assert.Single(result.LogBatches, b => b.TapId == "after_maybe");
        Assert.True(Context(later, "decision").IsMissing); // non-nullable, but its producer was skipped: no value to log
        Assert.Equal(6, Context(later, "n").AsInt64());
    }

    [Theory]
    // A non-nullable request column read with no producer that cannot be skipped before it.
    [InlineData("""
        "w": { "order": 200, "op": "SetRequest@1", "when": "ctx.request.id == 'x'", "config": { "column": "decision", "text": "a" } },
        "r": { "order": 300, "op": "ExpressionFilter@1", "config": { "predicate": "req.decision == 'a'" } }
        """, "COLUMN_NO_PRODUCER", "/nodes/r", "its producer 'w' may be skipped")]
    [InlineData("""
        "w": { "order": 300, "op": "SetRequest@1", "config": { "column": "decision", "text": "a" } },
        "r": { "order": 200, "op": "ExpressionFilter@1", "when": "req.decision == 'a'", "config": { "predicate": "true" } }
        """, "COLUMN_NO_PRODUCER", "/nodes/r", "its producer 'w' runs later")]
    [InlineData("""
        "r": { "order": 200, "op": "ExpressionFilter@1", "config": { "predicate": "req.decision == 'a'" } }
        """, "COLUMN_NO_PRODUCER", "/nodes/r", "no node writes it")]
    // One producer per path, unless it is a flow column.
    [InlineData("""
        "w1": { "order": 200, "op": "SetRequest@1", "config": { "column": "decision", "text": "a" } },
        "w2": { "order": 300, "op": "SetRequest@1", "config": { "column": "decision", "text": "b" } }
        """, "COLUMN_MULTIPLE_PRODUCERS", "/nodes/w2", "written by both 'w1' and 'w2'")]
    // Nodes of one order do not depend on each other.
    [InlineData("""
        "w": { "order": 200, "op": "SetRequest@1", "config": { "column": "other", "text": "a" } },
        "r": { "order": 200, "op": "SetRequest@1", "when": "(req.other ?? '') == 'a'", "config": { "column": "decision", "text": "b" } }
        """, "ORDER_DEPENDENCY", "/nodes/w/order", null)]
    // A fallback writes the request columns of its node.
    [InlineData("""
        "w": { "order": 200, "op": "SetRequest@1", "on_failure": "fallback", "fallback": "f", "config": { "column": "decision", "text": "a" } },
        "f": { "role": "fallback", "op": "SetRequest@1", "config": { "column": "other", "text": "b" } }
        """, "FALLBACK_OUTPUTS", "/nodes/f", "columns and request columns of 'w'")]
    // A write needs a declaration.
    [InlineData("""
        "w": { "order": 200, "op": "SetRequest@1", "config": { "column": "decison", "text": "a" } }
        """, "REQUEST_COLUMN_UNDECLARED", "/nodes/w/config/column", null)]
    public void TheDataFlowRulesOfColumns_HoldForRequestColumns(string nodes, string code, string pointer, string? message)
    {
        var compilation = TryCompile("""
            "decision": { "type": "string" }, "other": { "type": "string?" }
            """, nodes);

        var error = Assert.Single(compilation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal((code, pointer), (error.Code, error.Pointer));
        if (message is not null)
        {
            Assert.Contains(message, error.Message);
        }
    }

    [Fact]
    public void AnUndeclaredWrite_SuggestsTheDeclaredName()
    {
        var compilation = TryCompile(""" "decision": { "type": "string" } """, """
            "w": { "order": 200, "op": "SetRequest@1", "config": { "column": "decison", "text": "a" } }
            """);

        var error = Assert.Single(compilation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("did you mean 'decision'?", error.Hint);
    }

    [Fact]
    public void AFlowColumn_MayBeWrittenAgain_AndANullableOneReadWithoutAProducer()
    {
        var compilation = TryCompile("""
            "decision": { "type": "string", "flow": true }, "other": { "type": "string?" }
            """, """
            "w1": { "order": 200, "op": "SetRequest@1", "config": { "column": "decision", "text": "a" } },
            "w2": { "order": 300, "op": "SetRequest@1", "when": "(req.other ?? '') == ''", "config": { "column": "decision", "text": "b" } }
            """);

        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
    }

    [Theory]
    [InlineData("""{ "funnel": ["req.decision"] }""", "FUNNEL_KEY_REQUEST_COLUMN", "/funnel_groups/all/key/0")]
    [InlineData("""{ "value": "req.decision" }""", "COLUMN_NO_PRODUCER", "/output/values/value")]
    [InlineData("""{ "tap": "req.decision" }""", "TAP_COLUMN_NOT_PRODUCED", "/log_taps/early")]
    public void RequestLevelSites_ReadARequestColumnOnlyWhereItIsWritten(string site, string code, string pointer)
    {
        var read = System.Text.Json.Nodes.JsonNode.Parse(site)!.AsObject();
        var compilation = TryCompile(""" "decision": { "type": "string" } """, """
            "w": { "order": 300, "op": "SetRequest@1", "when": "ctx.request.id == 'x'", "config": { "column": "decision", "text": "a" } }
            """,
            values: read["value"] is { } value ? $"\"value\": \"{value}\"" : null,
            taps: read["tap"] is { } tap ? $$"""
                "early": { "group": "all", "attach": { "after": "recall" }, "context": { "d": "{{tap}}" } }
                """ : null,
            funnelKey: read["funnel"]?[0]?.GetValue<string>());

        var error = Assert.Single(compilation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal((code, pointer), (error.Code, error.Pointer));
    }

    [Theory]
    [InlineData("""{ "fn": "count" }""", "float64\", \"default\": 0.0, \"description\": \"x", "COLUMN_TYPE_MISMATCH", "/nodes/agg/config/values/x", "declare it as int64")]
    [InlineData("""{ "fn": "max", "of": "col.item.score" }""", "float64\", \"default\": 0.0, \"description\": \"x", "COLUMN_TYPE_MISMATCH", "/nodes/agg/config/values/x", "declare it as float64?")]
    [InlineData("""{ "fn": "mean", "of": "col.item.weight" }""", "int64?", "COLUMN_TYPE_MISMATCH", "/nodes/agg/config/values/x", "declare it as float64?")]
    [InlineData("""{ "fn": "sum" }""", "float64?", "REQUEST_AGGREGATE_VALUE", "/nodes/agg/config/values/x", null)]
    [InlineData("""{ "fn": "sum", "of": "col.item.tag" }""", "float64?", "COLUMN_TYPE_MISMATCH", "/nodes/agg/config/values/x/of", null)]
    [InlineData("""{ "fn": "avg", "of": "col.item.score" }""", "float64?", "REQUEST_AGGREGATE_FN", "/nodes/agg/config/values/x/fn", null)]
    [InlineData("""{ "fn": "count", "where": "col.item.score > 0.5" }""", "int64", "EXPR_NULLABLE_RESULT", "/nodes/agg/config/values/x/where", null)]
    public void RequestAggregate_RefusesWhatItCannotWrite(string value, string type, string code, string pointer, string? hint)
    {
        var compilation = TryCompile($$""" "x": { "type": "{{type}}" } """, $$"""
            "agg": { "order": 200, "op": "RequestAggregate@1", "config": { "values": { "x": {{value}} } } }
            """);

        var error = Assert.Single(compilation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal((code, pointer), (error.Code, error.Pointer));
        if (hint is not null)
        {
            Assert.Contains(hint, error.Hint);
        }
    }

    /// <summary>An undeclared value of the aggregate is refused with the declaration to add: the type the function writes.</summary>
    [Theory]
    [InlineData("""{ "fn": "count" }""", "\"n\": { \"type\": \"int64\" }")]
    [InlineData("""{ "fn": "max", "of": "col.item.score" }""", "\"n\": { \"type\": \"float64?\" }")]
    [InlineData("""{ "fn": "sum", "of": "col.item.weight" }""", "\"n\": { \"type\": \"int64?\" }")]
    [InlineData("""{ "fn": "mean", "of": "col.item.weight" }""", "\"n\": { \"type\": \"float64?\" }")]
    public void RequestAggregate_AnUndeclaredValue_IsGivenTheDeclarationToAdd(string value, string declaration)
    {
        var compilation = TryCompile(""" "decision": { "type": "string" } """, $$"""
            "agg": { "order": 200, "op": "RequestAggregate@1", "config": { "values": { "n": {{value}} } } }
            """);

        var error = Assert.Single(compilation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(("REQUEST_COLUMN_UNDECLARED", "/nodes/agg/config/values/n"), (error.Code, error.Pointer));
        Assert.Equal("declare it under /request_columns: " + declaration, error.Hint);
    }

    [Theory]
    [InlineData("req.user", "read it as 'ctx.request.user'")]
    [InlineData("ctx.decision", "read it as 'req.decision'")]
    [InlineData("ctx.request.decision", "read it as 'req.decision'")]
    public void AName_WrittenInTheOtherRequestLevelNamespace_IsPointedThere(string name, string hint)
    {
        var compilation = TryCompile(""" "decision": { "type": "string?" } """, $$"""
            "r": { "order": 200, "op": "ExpressionFilter@1", "config": { "predicate": "{{name}} == 'a'" } }
            """);

        var error = Assert.Single(compilation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains(hint, error.Hint);
    }

    [Fact]
    public void ADeclarationWithAnError_IsReportedOnce()
    {
        var compilation = TryCompile(""" "decision": { "type": "strnig" } """, """
            "w": { "order": 200, "op": "SetRequest@1", "config": { "column": "decision", "text": "a" } },
            "r": { "order": 300, "op": "ExpressionFilter@1", "config": { "predicate": "req.decision == 'a'" } }
            """);

        var error = Assert.Single(compilation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(("COLUMN_BAD_TYPE", "/request_columns/decision/type"), (error.Code, error.Pointer));
    }

    private static IReadOnlyDictionary<string, ScalarValue> Values(PipelineResult result)
    {
        Assert.True(result.Succeeded, result.Error);
        var values = new Dictionary<string, ScalarValue>(StringComparer.Ordinal);
        foreach (var value in result.Values.All)
        {
            values[value.Name] = value.Value;
        }

        return values;
    }

    private static ColumnType Type(PipelineResult result, string name)
    {
        foreach (var value in result.Values.All)
        {
            if (value.Name == name)
            {
                return value.Type;
            }
        }

        throw new KeyNotFoundException(name);
    }

    private static long[] Ids(PipelineResult result) => result.Items.Select(item => item.Id.Int64Value).ToArray();

    private static NodeOutcomeKind Outcome(PipelineResult result, string node) => result.Outcomes.Single(o => o.NodeId == node).Kind;

    private static ScalarValue Context(StrataRec.Logging.LogBatch batch, string name) => batch.Context.Single(c => c.Item1 == name).Item2;

    private static ValueTask<PipelineResult> Run(ExecutionPlan plan, bool writeLogs = false) => new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
    {
        RequestId = "r",
        Inputs = new RequestInputs(),
        Deadline = TimeSpan.FromSeconds(30),
        CaptureLogs = writeLogs,
    });

    private static ExecutionPlan Compile(
        string requestColumns, string nodes, string? values = null, string? taps = null, string? extraColumns = null, string? outputColumns = null)
    {
        var compilation = TryCompile(extraColumns is null ? requestColumns : requestColumns + ", " + extraColumns, nodes, values, taps, outputColumns: outputColumns);
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        return compilation.Plan!;
    }

    /// <summary>
    /// A workspace of one pipeline: recall of the six items, then <paramref name="nodes"/>, with
    /// <paramref name="requestColumns"/> declared and <paramref name="values"/> returned; <c>SetRequest@1</c> is registered.
    /// </summary>
    private static PlanCompilation TryCompile(
        string requestColumns, string nodes, string? values = null, string? taps = null, string? funnelKey = null, string? outputColumns = null)
    {
        var root = Directory.CreateTempSubdirectory("stratarec-request-columns-").FullName;
        Write(root, "catalog/c.jsonc", """
            { "catalog_id": "c",
              "entities": { "item": { "key": "col.entity_id" } },
              "features": {
                "item.tag":    { "type": "string",  "source": "items", "field": "tag", "default": "" },
                "item.score":  { "type": "float64", "source": "items", "field": "score" },
                "item.weight": { "type": "int64",   "source": "items", "field": "weight", "default": 0 } } }
            """);
        Write(root, "bindings/local.jsonc", """
            { "environment": "local",
              "sources": { "items": { "connector": "memory@1", "data_file": "fixtures/items.json", "timeout_ms": 30000 } },
              "services": { "s": { "client": "memory_list@1", "data_file": "fixtures/ids.json" } } }
            """);
        Write(root, "pipelines/p.jsonc", $$"""
            { "pipeline_id": "p", "entity_key_type": "int64",
              "request_schema": { "user": { "type": "string?" } },
              "request_columns": { {{requestColumns}} },
              "param_schema": { "quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "quotas": { "s": 100 }, "k": 100 },
              "nodes": {
                "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "quotas" } },
                {{nodes}} },
              "funnel_groups": { "all": { "key": [{{(funnelKey is null ? "\"ctx.request.id\"" : $"\"{funnelKey}\"")}}], "rate": 1.0 } },
              "log_taps": { {{taps}} },
              "output": { "limit_param": "k", "columns": { "score": "col.item.score"{{(outputColumns is null ? "" : ", " + outputColumns)}} }, "values": { {{values}} } } }
            """);
        Write(root, "fixtures/ids.json", "{ \"items\": [" + string.Join(", ", Items.Select(i => i.Id)) + "] }");
        Write(root, "fixtures/items.json", "{ " + string.Join(", ", Items.Select(i =>
            $"\"{i.Id}\": {{ \"tag\": \"{i.Tag}\", \"score\": {(i.Score is { } s ? s.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null")}, \"weight\": {i.Id} }}")) + " }");

        var diagnostics = new DiagnosticBag();
        var workspace = ConfigWorkspace.Load(root, diagnostics);
        var request = workspace.CreateCompileRequest("p", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        var operators = OperatorRegistry.CreateDefault().Add(new SetRequestFactory()).Add(new MemoryProbeFactory());
        return PlanCompiler.Compile(request!, new PlanEnvironment { BaseDirectory = root, Operators = operators });
    }

    private static void Write(string root, string path, string content)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>Config of <c>SetRequest@1</c>: the column, one value to stage (or none), and whether to fail afterwards.</summary>
    private sealed class SetRequestConfig
    {
        public required string Column { get; init; }

        public string? Text { get; init; }

        public double? Number { get; init; }

        public long? Integer { get; init; }

        public bool Nan { get; init; }

        public bool Unset { get; init; }

        public bool Fail { get; init; }

        /// <summary>Sets <see cref="Text"/> through a handle whose type the operator changed to string: a defective operator.</summary>
        public bool Forge { get; init; }

        /// <summary>Sets a list of strings with a null element: a defective operator.</summary>
        public bool NullElement { get; init; }
    }

    /// <summary><c>MemoryProbe@1</c>: records how much of the request's memory is rented when it runs.</summary>
    private sealed class MemoryProbeFactory : IOperatorFactory
    {
        public static long Last;

        public OperatorDescriptor Descriptor { get; } = new("MemoryProbe", 1, Effects.None, typeof(NoConfig), "Records the rented bytes.");

        private sealed class NoConfig;

        public Operator? Compile(OperatorCompileContext context) => new Probe();

        private sealed class Probe : Operator
        {
            public override ValueTask ExecuteAsync(OperatorContext context)
            {
                Last = context.Memory.RentedBytes;
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary><c>SetRequest@1</c>: a custom operator staging one request column's value, as a host's would.</summary>
    private sealed class SetRequestFactory : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } = new("SetRequest", 1, Effects.RequestWriter, typeof(SetRequestConfig), "Sets a request column.");

        public Operator? Compile(OperatorCompileContext context) =>
            context.BindConfig<SetRequestConfig>() is { } config && context.TryWriteRequestColumn(config.Column, "/column", out var output)
                ? new SetRequest(config, output)
                : null;

        private sealed class SetRequest(SetRequestConfig config, RequestOutput output) : Operator
        {
            public override ValueTask ExecuteAsync(OperatorContext context)
            {
                if (config.Forge)
                {
                    context.SetRequestValue(output with { Type = ColumnType.String }, ScalarValue.FromString(config.Text ?? "forged"));
                }
                else if (config.NullElement)
                {
                    context.SetRequestValue(output, ScalarValue.FromStringList(["a", null!]));
                }
                else if (!config.Unset)
                {
                    context.SetRequestValue(output, config switch
                    {
                        { Nan: true } => ScalarValue.FromFloat64(double.NaN),
                        { Text: { } text } => ScalarValue.FromString(text),
                        { Number: { } number } => ScalarValue.FromFloat64(number),
                        { Integer: { } integer } => ScalarValue.FromInt64(integer),
                        _ => ScalarValue.Missing,
                    });
                }

                if (config.Fail)
                {
                    context.Fail("asked to fail");
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
