using StrataRec.Artifacts.Model;
using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Operators.Standard;
using StrataRec.Pipeline.Planning;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// Candidate explanations (design 13.3): each target's state after every node (present, live, rank, the values of the
/// columns the node read and wrote) through recall, a filter, a skipped node, a score and a truncation, across a
/// compaction, a rolled-back attempt and a row source's fallback; the reasons of the operators that give one; the bound on
/// targets; int64 and string keys alike.
/// </summary>
/// <remarks>
/// The fixture recalls items 1–6 in that order. <c>safety</c> drops 4 (unsafe) and compacts the table, <c>promo</c> is
/// skipped (its <c>when</c> is false), <c>score</c> gives base × (1 + boost) + 0.5: 1 → 1.4, 2 → 1.5, 3 → 1.3, 5 → 0.6,
/// 6 → 0.8, and <c>top</c> keeps 3 (2, 1, 3), of which the output returns 2.
/// </remarks>
public sealed class CandidateExplainerTests
{
    [Theory]
    [InlineData("int64")]
    [InlineData("string")]
    public async Task EachTarget_IsFollowedThroughRecallFilterScoreAndTruncation_AcrossACompaction(string keyType)
    {
        var plan = Compile(keyType);
        var ids = new[] { Key(keyType, 2), Key(keyType, 1), Key(keyType, 3), Key(keyType, 4), Key(keyType, 5), Key(keyType, 99) };

        var result = await Run(plan, ids);

        Assert.True(result.Succeeded, result.Error);
        var explanation = Assert.IsType<RunExplanation>(result.Explanation);
        Assert.Equal("p", explanation.PipelineId);
        Assert.Equal(ids, explanation.Candidates.Select(c => c.Id));
        Assert.Equal(new[] { Key(keyType, 2), Key(keyType, 1) }, result.Items.Select(i => i.Id));

        var two = explanation.Candidates[0];
        Assert.Equal((ExplainVerdict.Returned, (int?)1, (string?)null), (two.Verdict, two.Rank, two.DroppedBy));
        Assert.Equal(new[] { "recall", "safety", "promo", "score", "top" }, two.Steps.Select(s => s.Node));
        Assert.Equal(
            new (ExplainStepKind, ExplainChange, int?, int?)[]
            {
                (ExplainStepKind.Ran, ExplainChange.Entered, null, 2),
                (ExplainStepKind.Ran, ExplainChange.Stayed, 2, 2),
                (ExplainStepKind.Skipped, ExplainChange.Stayed, 2, 2),
                (ExplainStepKind.Ran, ExplainChange.Stayed, 2, 2),
                (ExplainStepKind.Ran, ExplainChange.Stayed, 2, 1),
            },
            two.Steps.Select(s => (s.Kind, s.Change, s.RankBefore, s.Rank)));
        Assert.All(two.Steps, s => Assert.True(s.Present && s.Alive));
        Assert.Equal("when is false", two.Steps[2].Detail);
        Assert.Empty(two.Steps[2].Values);

        // The values of what the node read and wrote, after it: read from the rows the compaction after safety moved.
        Assert.True(Value(two.Steps[1], "item.safe").AsBool());
        Assert.Equal((0.5, 1.0, 1.5), (Value(two.Steps[3], "item.quality").AsFloat64(), Value(two.Steps[3], "item.boost").AsFloat64(),
            Value(two.Steps[3], "score.final").AsFloat64()));
        Assert.Equal(1.5, Value(two.Steps[4], "score.final").AsFloat64());
        Assert.Equal(1L, Value(two.Steps[0], "recall.sources_mask").AsInt64()); // what the row source wrote

        var one = explanation.Candidates[1];
        Assert.Equal((ExplainVerdict.Returned, (int?)2), (one.Verdict, one.Rank));
        var three = explanation.Candidates[2];
        Assert.Equal((ExplainVerdict.BeyondLimit, (int?)3), (three.Verdict, three.Rank));

        var four = explanation.Candidates[3];
        Assert.Equal((ExplainVerdict.Dropped, (int?)null, "safety"), (four.Verdict, four.Rank, four.DroppedBy));
        var dropped = four.Steps[1];
        Assert.Equal((ExplainStepKind.Ran, ExplainChange.Dropped, (int?)4, (int?)null), (dropped.Kind, dropped.Change, dropped.RankBefore, dropped.Rank));
        Assert.False(Value(dropped, "item.safe").AsBool());
        // Released by the compaction, it stays what it was: in the table once, and dropped.
        Assert.All(four.Steps.Skip(2), s => Assert.Equal((ExplainChange.Absent, true, false), (s.Change, s.Present, s.Alive)));
        Assert.All(four.Steps.Skip(2), s => Assert.Empty(s.Values));

        var five = explanation.Candidates[4];
        Assert.Equal((ExplainVerdict.Dropped, "top"), (five.Verdict, five.DroppedBy));
        Assert.Equal((ExplainChange.Dropped, (int?)4), (five.Steps[4].Change, five.Steps[4].RankBefore));
        // 5 moved from physical row 4 to 3 in the compaction (4 was released before it): its own values, not 6's.
        Assert.Equal((0.1, 0.6), (Value(five.Steps[3], "item.quality").AsFloat64(), Value(five.Steps[3], "score.final").AsFloat64()));

        var never = explanation.Candidates[5];
        Assert.Equal((ExplainVerdict.NotRecalled, (int?)null, (string?)null), (never.Verdict, never.Rank, never.DroppedBy));
        Assert.Equal(5, never.Steps.Count);
        Assert.All(never.Steps, s => Assert.Equal((ExplainChange.Absent, false, false), (s.Change, s.Present, s.Alive)));
        Assert.All(never.Steps, s => Assert.Empty(s.Values));
        Assert.All(never.Steps, s => Assert.Null(s.Reason));
    }

    [Fact]
    public async Task TheOperatorsExplainThemselves_TheFilterItsPredicate_TheScoreItsTerms_TheCutItsKeys()
    {
        var result = await Run(Compile("int64"), new[] { Key("int64", 2), Key("int64", 4), Key("int64", 5) });

        var candidates = result.Explanation!.Candidates;
        var kept = candidates[0].Steps;
        Assert.Equal(("kept: col.item.safe is true", true), (kept[1].Reason, Term(kept[1], "predicate").AsBool()));
        Assert.Equal("score.final = 0.5 × 2 + 0.5 = 1.5", kept[3].Reason);
        Assert.Equal(new[] { ("base", 0.5), ("multiplier.boost", 2.0), ("addend.bonus", 0.5) }, kept[3].Terms.Select(t => (t.Name, t.Value.AsFloat64())));
        Assert.Equal("kept: score.final 1.5 ranks 1 of the 3 kept (k = 3)", kept[4].Reason);
        Assert.Equal((3L, 1.5, 1.3), (Term(kept[4], "k").AsInt64(), Term(kept[4], "key").AsFloat64(), Term(kept[4], "last_kept_key").AsFloat64()));

        var filtered = candidates[1].Steps[1];
        Assert.Equal(("dropped: col.item.safe is false", false), (filtered.Reason, Term(filtered, "predicate").AsBool()));
        Assert.Null(candidates[1].Steps[3].Reason); // not live when scored: nothing to say

        var cut = candidates[2].Steps[4];
        Assert.Equal("cut: score.final 0.6 ranks after the last kept 1.3 (k = 3)", cut.Reason);
        Assert.Equal((3L, 0.6, 1.3), (Term(cut, "k").AsInt64(), Term(cut, "key").AsFloat64(), Term(cut, "last_kept_key").AsFloat64()));
    }

    [Fact]
    public async Task ARowCutOnATie_IsSaidToLoseTheTieByEntityId()
    {
        // Every score is 1.0 × 1 + 0.5: top keeps the three lowest ids.
        var result = await Run(Compile("int64", baseExpr: "1.0", multiplier: "1.0"), new[] { Key("int64", 3), Key("int64", 5) });

        var candidates = result.Explanation!.Candidates;
        Assert.Equal(ExplainVerdict.BeyondLimit, candidates[0].Verdict);
        Assert.Equal("cut: score.final 1.5 ties the last kept row's; ties go by entity_id ascending (k = 3)", candidates[1].Steps[4].Reason);
    }

    [Theory]
    [InlineData("fail")]
    [InlineData("commit")]
    public async Task AFailedAttempt_IsRolledBack_AndItsTargetsAreAsBeforeIt(string mode)
    {
        // reorder runs after score, reverses the ranking, and fails: by reporting it (keeping one row), or at its commit
        // (a non-nullable column it wrote on one row only), after it explained itself. on_failure skips it.
        var plan = Compile("int64", reorder: mode);

        var result = await Run(plan, new[] { Key("int64", 2), Key("int64", 6) });

        Assert.True(result.Succeeded, result.Error);
        foreach (var candidate in result.Explanation!.Candidates)
        {
            var before = candidate.Steps.Single(s => s.Node == "score");
            var failed = candidate.Steps.Single(s => s.Node == "reorder");
            Assert.Equal(ExplainStepKind.RolledBack, failed.Kind);
            Assert.Equal((ExplainChange.Stayed, before.Rank, before.Rank), (failed.Change, failed.RankBefore, failed.Rank));
            Assert.Null(failed.Reason); // what the attempt explained went with it
            Assert.Empty(failed.Terms);
            Assert.Empty(failed.Values);
            Assert.Equal(before.Rank, candidate.Steps.Single(s => s.Node == "top").RankBefore);
        }

        Assert.Equal(mode == "fail" ? "gave up after reordering" : "left non-nullable column 'flag' without a value on 4 of 5 live rows",
            result.Explanation.Candidates[0].Steps.Single(s => s.Node == "reorder").Detail);
        Assert.Equal((ExplainVerdict.Returned, (int?)1), (result.Explanation.Candidates[0].Verdict, result.Explanation.Candidates[0].Rank));
    }

    [Fact]
    public async Task ARowSourceThatAppendedAndFailed_IsRolledBack_AndItsFallbackAppendsTheTarget()
    {
        var plan = Compile("int64", recallFails: true);

        var result = await Run(plan, new[] { Key("int64", 2), Key("int64", 1) });

        Assert.True(result.Succeeded, result.Error);
        var two = result.Explanation!.Candidates[0];
        Assert.Equal(new[] { "recall", "recall_backup", "safety", "promo", "score", "top" }, two.Steps.Select(s => s.Node));
        Assert.Equal((ExplainStepKind.RolledBack, ExplainChange.Absent, false, "gave up after appending"),
            (two.Steps[0].Kind, two.Steps[0].Change, two.Steps[0].Present, two.Steps[0].Detail));
        Assert.Equal((ExplainStepKind.Ran, ExplainChange.Entered, (int?)1), (two.Steps[1].Kind, two.Steps[1].Change, two.Steps[1].Rank));
        Assert.Equal("backup", Value(two.Steps[1], "recall.primary_source").AsString());
        Assert.Equal(ExplainVerdict.Returned, two.Verdict);

        // The failed primary appended 1 too; the fallback does not return it.
        var one = result.Explanation.Candidates[1];
        Assert.Equal(ExplainVerdict.NotRecalled, one.Verdict);
        Assert.All(one.Steps, s => Assert.False(s.Present));
    }

    [Fact]
    public async Task RepeatedIds_AreExplainedOnce_AndIdsOfTheOtherKeyType_AreNeverFound()
    {
        var result = await Run(Compile("int64"), new[] { Key("int64", 2), Key("string", 2), Key("int64", 2) });

        Assert.Equal(new[] { Key("int64", 2), Key("string", 2) }, result.Explanation!.Candidates.Select(c => c.Id));
        Assert.Equal(ExplainVerdict.NotRecalled, result.Explanation.Candidates[1].Verdict);
    }

    [Fact]
    public async Task MoreTargetsThanTheBound_FailTheRun_LikeInvalidInputs()
    {
        var plan = Compile("int64");
        var ids = Enumerable.Range(1, ExplainTargets.MaxCount + 1).Select(i => Key("int64", i)).ToArray();

        var result = await Run(plan, ids);

        Assert.False(result.Succeeded);
        Assert.Equal("explain_ids has 101 ids, but a request explains at most 100 candidates; ask about fewer ids per request.", result.Error);
        Assert.Null(result.Explanation);
        Assert.Empty(result.Outcomes);

        var atTheBound = await Run(plan, ids[..ExplainTargets.MaxCount]);
        Assert.True(atTheBound.Succeeded, atTheBound.Error);
        Assert.Equal(ExplainTargets.MaxCount, atTheBound.Explanation!.Candidates.Count);
    }

    [Fact]
    public void TheRulesForIds_NameTheBoundAndTheKeyType()
    {
        Assert.Null(ExplainTargets.Check(new[] { EntityKey.FromInt64(1) }, EntityKeyType.Int64, "p"));
        Assert.Equal("explain id 'a' is a string, but pipeline 'p' has int64 entity ids; give its ids as int64s.",
            ExplainTargets.Check(new[] { EntityKey.FromInt64(1), EntityKey.FromString("a") }, EntityKeyType.Int64, "p"));
        Assert.Equal("explain id 7 is an int64, but pipeline 'p' has string entity ids; give its ids as strings.",
            ExplainTargets.Check(new[] { EntityKey.FromInt64(7) }, EntityKeyType.String, "p"));
        Assert.StartsWith("explain_ids has 101 ids",
            ExplainTargets.Check(Enumerable.Range(0, 101).Select(i => EntityKey.FromInt64(i)).ToArray(), EntityKeyType.Int64, "p"));

        Assert.True(ExplainTargets.TryParse("-42", EntityKeyType.Int64, out var number));
        Assert.Equal(EntityKey.FromInt64(-42), number);
        Assert.False(ExplainTargets.TryParse("4x", EntityKeyType.Int64, out _));
        Assert.True(ExplainTargets.TryParse("42", EntityKeyType.String, out var text));
        Assert.Equal(EntityKey.FromString("42"), text);
    }

    [Fact]
    public async Task ARunWithoutExplainIds_HasNoExplanation()
    {
        var result = await Run(Compile("int64"), null);

        Assert.True(result.Succeeded, result.Error);
        Assert.Null(result.Explanation);
    }

    [Fact]
    public async Task AFailedRun_ExplainsUpToTheFailure_WithNoResultVerdicts()
    {
        var plan = Compile("int64", reorder: "fail", reorderPolicy: "fail_request");

        var result = await Run(plan, new[] { Key("int64", 2) });

        Assert.False(result.Succeeded);
        var two = Assert.Single(result.Explanation!.Candidates);
        Assert.Equal(ExplainVerdict.NoResult, two.Verdict);
        Assert.Equal(("reorder", ExplainStepKind.RolledBack), (two.Steps[^1].Node, two.Steps[^1].Kind));
    }

    private static EntityKey Key(string keyType, int n) => keyType == "string" ? EntityKey.FromString("i" + n) : EntityKey.FromInt64(n);

    private static ScalarValue Value(ExplainStep step, string name) => step.Values.Single(v => v.Name == name).Value;

    private static ScalarValue Term(ExplainStep step, string name) => step.Terms.Single(v => v.Name == name).Value;

    private static async Task<PipelineResult> Run(ExecutionPlan plan, IReadOnlyList<EntityKey>? explain) =>
        await new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
        {
            RequestId = "r",
            Inputs = new RequestInputs().Set("surface", "home"),
            Deadline = TimeSpan.FromSeconds(60),
            ExplainIds = explain,
        });

    /// <param name="reorder">Adds <c>reorder</c> (order 350): <c>fail</c> reports a failure, <c>commit</c> fails at its commit.</param>
    /// <param name="recallFails">The recall appends 1 and 2 and fails; its fallback <c>recall_backup</c> appends 2, 3 and 7.</param>
    private static ExecutionPlan Compile(
        string keyType, string baseExpr = "col.item.quality", string multiplier = "1.0 + col.item.boost", string? reorder = null,
        string reorderPolicy = "skip", bool recallFails = false)
    {
        string Id(int n) => keyType == "string" ? $"\"i{n}\"" : n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string Ids(params int[] ns) => "[" + string.Join(", ", ns.Select(Id)) + "]";
        string Item(int n) => keyType == "string" ? $"i{n}" : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var root = Directory.CreateTempSubdirectory("stratarec-explain-").FullName;
        Write(root, "catalog/c.jsonc", $$"""
            { "catalog_id": "c",
              "entities": { "item": { "scope": "candidate", "key": "col.entity_id", "key_type": "{{keyType}}" } },
              "features": {
                "item.quality": { "type": "float64", "source": "items", "field": "q",     "default": 0.0 },
                "item.boost":   { "type": "float64", "source": "items", "field": "boost", "default": 0.0 },
                "item.safe":    { "type": "bool",    "source": "items", "field": "safe",  "default": true } } }
            """);
        Write(root, "bindings/local.jsonc", $$"""
            { "environment": "local",
              "sources": { "items": { "connector": "memory@1", "data_file": "fixtures/items.json", "timeout_ms": 30000 } },
              "candidate_sources": {
                "main":   { "client": "memory_list@1", "items": {{Ids(1, 2, 3, 4, 5, 6)}} },
                "backup": { "client": "memory_list@1", "items": {{Ids(2, 3, 7)}} } } }
            """);
        Write(root, "fixtures/items.json", $$"""
            { "{{Item(1)}}": { "q": 0.9 }, "{{Item(2)}}": { "q": 0.5, "boost": 1.0 }, "{{Item(3)}}": { "q": 0.8 },
              "{{Item(4)}}": { "q": 1.0, "safe": false }, "{{Item(5)}}": { "q": 0.1 }, "{{Item(6)}}": { "q": 0.3 }, "{{Item(7)}}": { "q": 0.2 } }
            """);
        var recall = recallFails
            ? """
              "recall": { "order": 100, "op": "AppendThenFail@1", "on_failure": "fallback:recall_backup", "config": { "quotas_param": "quotas" } },
              "recall_backup": { "role": "fallback", "op": "MultiSourceRecall@1", "config": { "quotas_param": "backup_quotas" } },
              """
            : """
              "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "quotas" } },
              """;
        var reorderNode = reorder is null
            ? string.Empty
            : $$"""
              "reorder": { "order": 350, "op": "Reorder@1", "on_failure": "{{reorderPolicy}}", "config": { "mode": "{{reorder}}" } },
              """;
        Write(root, "pipelines/p.jsonc", $$"""
            { "pipeline_id": "p", "entity_key_type": "{{keyType}}",
              "request_schema": { "surface": { "type": "string" } },
              "columns": { "score.final": { "type": "float64", "default": 0.0 }, "flag": { "type": "float64", "default": 0.0 } },
              "param_schema": { "quotas": { "type": "map<string,int64>" }, "backup_quotas": { "type": "map<string,int64>" },
                                "k": { "type": "int64" }, "top": { "type": "int64" } },
              "params": { "quotas": { "main": 10 }, "backup_quotas": { "backup": 10 }, "k": 2, "top": 3 },
              "nodes": {
                {{recall}}
                "safety": { "order": 200, "op": "ExpressionFilter@1", "compact": "always", "config": { "predicate": "col.item.safe" } },
                "promo":  { "order": 250, "op": "ExpressionFilter@1", "when": "ctx.request.surface == 'promo'",
                            "config": { "predicate": "col.item.quality > 0.85" } },
                "score":  { "order": 300, "op": "CompositeMultiplierScorer@1",
                            "config": { "output_col": "score.final", "base_expr": "{{baseExpr}}",
                                        "multipliers": { "boost": "{{multiplier}}" }, "addends": { "bonus": "0.5" } } },
                {{reorderNode}}
                "top":    { "order": 400, "op": "TopK@1", "config": { "score_col": "score.final", "k_param": "top" } } },
              "output": { "limit_param": "k", "columns": { "score": "col.score.final" } } }
            """);

        var diagnostics = new DiagnosticBag();
        var workspace = ConfigWorkspace.Load(root, diagnostics);
        var request = workspace.CreateCompileRequest("p", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        var operators = OperatorRegistry.CreateDefault().Add(new ReorderFactory()).Add(new AppendThenFailFactory());
        var compilation = PlanCompiler.Compile(request!, new PlanEnvironment { BaseDirectory = root, Operators = operators });
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        return compilation.Plan!;
    }

    private static void Write(string root, string path, string content)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public sealed class ReorderConfig
    {
        public required string Mode { get; init; }
    }

    /// <summary>
    /// <c>Reorder@1</c>: reverses the ranking, then fails: <c>fail</c> keeps one row and reports a failure; <c>commit</c>
    /// writes its non-nullable <c>flag</c> on the first row only, so its commit fails. It explains itself either way, which a
    /// rolled-back attempt must not keep.
    /// </summary>
    private sealed class ReorderFactory : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } =
            new("Reorder", 1, Effects.RowOrder | Effects.RowFilter | Effects.ColumnWriter, typeof(ReorderConfig), "Reverses the ranking and fails.");

        public Operator? Compile(OperatorCompileContext context) =>
            context.BindConfig<ReorderConfig>() is { } config && context.TryWriteColumn("flag", "/mode", null, out var flag)
                ? new Reorder(config.Mode, flag)
                : null;

        private sealed class Reorder(string mode, OutputColumn flag) : Operator, IExplainableOperator
        {
            public override ValueTask ExecuteAsync(OperatorContext context)
            {
                var order = context.LiveRows.ToArray();
                Array.Reverse(order);
                if (mode == "fail")
                {
                    context.SetOrder(order.AsSpan(0, 1));
                    context.Fail("gave up after reordering");
                }
                else
                {
                    context.SetOrder(order);
                    ((ScalarColumn<double>)context.Output(flag)).Set(order[0], 1.0);
                }

                return ValueTask.CompletedTask;
            }

            public void Explain(ExplainContext context)
            {
                for (var t = 0; t < context.Count; t++)
                {
                    context.Reason(t, "reversed");
                    context.Term(t, "mode", ScalarValue.FromString(mode));
                }
            }
        }
    }

    /// <summary><c>AppendThenFail@1</c>: <c>MultiSourceRecall@1</c> that fails once it has appended its candidates.</summary>
    private sealed class AppendThenFailFactory : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } =
            new("AppendThenFail", 1, Effects.RowSource, typeof(MultiSourceRecallConfig), "MultiSourceRecall@1 that fails after appending.");

        public Operator? Compile(OperatorCompileContext context) =>
            new MultiSourceRecallFactory().Compile(context) is { } recall ? new AppendThenFail(recall) : null;

        private sealed class AppendThenFail(Operator recall) : Operator
        {
            public override int GetRowLimit(ReadOnlySpan<ScalarValue> parameters) => recall.GetRowLimit(parameters);

            public override async ValueTask ExecuteAsync(OperatorContext context)
            {
                var before = context.Table.PhysicalRowCount;
                await recall.ExecuteAsync(context);
                Assert.True(context.Table.PhysicalRowCount > before);
                context.Fail("gave up after appending");
            }
        }
    }
}
