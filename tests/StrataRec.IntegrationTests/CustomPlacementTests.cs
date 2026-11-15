using System.Runtime.InteropServices;
using StrataRec.Core.Columns;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Values;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Replay;
using StrataRec.Pipeline.Sources;

namespace StrataRec.IntegrationTests;

/// <summary>
/// Interleaving and fixed-position injection are business logic, written as custom operators against the public operator
/// API; the framework has no slot operator. These two are reference implementations, and the tests pin down what the
/// framework guarantees them: an injector after ranking calls a candidate source through the context (bounded, recorded,
/// replayed), appends what it got, gives those rows their score in the column the ranking wrote (design 4.6: a row source
/// writes its rows into the current version), and puts them at fixed positions; an interleaver only reorders.
/// </summary>
public sealed class CustomPlacementTests
{
    private const string Sources = """
        "organic": { "client": "memory_list@1", "items": [ { "id": 1, "score": 0.9 }, { "id": 2, "score": 0.8 }, { "id": 3, "score": 0.7 },
                                                            { "id": 4, "score": 0.6 }, { "id": 5, "score": 0.5 } ] },
        "fresh": { "client": "memory_list@1", "items": [ { "id": 11, "score": 0.4 }, { "id": 12, "score": 0.3 }, { "id": 13, "score": 0.2 } ] },
        "ads": { "client": "memory_list@1", "items": [ { "id": 3, "score": 0.95 }, { "id": 21, "score": 0.25 }, { "id": 22, "score": 0.15 },
                                                       { "id": 23, "score": 0.05 } ] }
        """;

    [Fact]
    public async Task AnInjectorAfterRanking_PutsASourcesNewCandidatesAtFixedPositions_WithTheirScore_AsInAReplay()
    {
        // TopK keeps 1–4. The injector asks 'ads' for candidates, skips 3 (shown already), appends 21 and 22 and
        // places them 2nd and 4th, with their source score in score.final, which TopK ranked by.
        var plan = Compile("""
            "recall": { "order": 10, "op": "MultiSourceRecall@1", "config": { "quotas_param": "quotas" } },
            "score": { "order": 20, "op": "ExpressionScorer@1", "config": { "output_col": "score.final", "expr": "col.recall.best_score ?? 0.0" } },
            "top": { "order": 30, "op": "TopK@1", "config": { "score_col": "score.final", "k": 4 } },
            "ads": { "order": 40, "op": "SlotInjector@1", "config": { "source": "ads", "slots": [4, 2], "score_col": "score.final" } }
            """, """ "score": "col.score.final", "source": "col.recall.primary_source ?? 'injected'" """);

        var recorded = await Execute(plan, record: true);

        Assert.True(recorded.Succeeded, recorded.Error);
        Assert.Equal(
            new[] { (1L, 0.9, "organic"), (21L, 0.25, "injected"), (2L, 0.8, "organic"), (22L, 0.15, "injected"), (3L, 0.7, "organic"), (4L, 0.6, "organic") },
            Items(recorded));
        await AssertReplayAgrees(plan, recorded);
    }

    [Fact]
    public async Task AnInjector_PlacesWhatItGot_AndAFailedSourceInjectsNothing()
    {
        // TopK keeps 1 and 2, so 3, which it dropped, is a candidate like 21. A slot past the end puts the candidate last;
        // with the source failing, the ranking is served as it was, degraded.
        var plan = Compile("""
            "recall": { "order": 10, "op": "MultiSourceRecall@1", "config": { "quotas_param": "quotas" } },
            "score": { "order": 20, "op": "ExpressionScorer@1", "config": { "output_col": "score.final", "expr": "col.recall.best_score ?? 0.0" } },
            "top": { "order": 30, "op": "TopK@1", "config": { "score_col": "score.final", "k": 2 } },
            "ads": { "order": 40, "op": "SlotInjector@1", "config": { "source": "ads", "slots": [1, 9], "score_col": "score.final" } }
            """, """ "score": "col.score.final" """);

        var served = await Execute(plan);
        Assert.True(served.Succeeded, served.Error);
        Assert.Equal(new long[] { 3, 1, 2, 21 }, served.Items.Select(i => i.Id.Int64Value));

        var failing = Compile("""
            "recall": { "order": 10, "op": "MultiSourceRecall@1", "config": { "quotas_param": "quotas" } },
            "score": { "order": 20, "op": "ExpressionScorer@1", "config": { "output_col": "score.final", "expr": "col.recall.best_score ?? 0.0" } },
            "top": { "order": 30, "op": "TopK@1", "config": { "score_col": "score.final", "k": 2 } },
            "ads": { "order": 40, "op": "SlotInjector@1", "config": { "source": "broken", "slots": [1], "score_col": "score.final" } }
            """, """ "score": "col.score.final" """, extraSources: """, "broken": { "client": "failing@1" }""");
        var degraded = await Execute(failing);
        Assert.True(degraded.Succeeded, degraded.Error);
        Assert.Equal(new long[] { 1, 2 }, degraded.Items.Select(i => i.Id.Int64Value));
        Assert.True(degraded.Degraded);
    }

    [Fact]
    public async Task AnInterleaver_MixesTheSourcesByAPattern_KeepingEachSourcesOrder()
    {
        // Ranked by score, 'organic' fills the top. The pattern takes two organic rows, then one fresh, and again; when a
        // source runs out the others continue in their order.
        var plan = Compile("""
            "recall": { "order": 10, "op": "MultiSourceRecall@1", "config": { "quotas_param": "quotas" } },
            "score": { "order": 20, "op": "ExpressionScorer@1", "config": { "output_col": "score.final", "expr": "col.recall.best_score ?? 0.0" } },
            "top": { "order": 30, "op": "TopK@1", "config": { "score_col": "score.final", "k": 8 } },
            "mix": { "order": 40, "op": "Interleave@1", "config": { "source_col": "recall.primary_source", "pattern": ["organic", "organic", "fresh"] } }
            """, """ "source": "col.recall.primary_source ?? 'none'" """, quotas: """ { "organic": 5, "fresh": 3 } """);

        var recorded = await Execute(plan, record: true);

        Assert.True(recorded.Succeeded, recorded.Error);
        Assert.Equal(new long[] { 1, 2, 11, 3, 4, 12, 5, 13 }, recorded.Items.Select(i => i.Id.Int64Value));
        await AssertReplayAgrees(plan, recorded);
    }

    private static List<(long, double, string)> Items(PipelineResult result) =>
        result.Items.Select(i => (i.Id.Int64Value, Math.Round(i.Columns["score"].AsFloat64(), 9), i.Columns["source"].AsString())).ToList();

    private static ExecutionPlan Compile(string nodes, string output, string quotas = """ { "organic": 5 } """, string extraSources = "")
    {
        var compilation = MiniWorkspace.TryCompile(
            Sources + extraSources,
            $$"""
            { "pipeline_id": "p", "entity_key_type": "int64",
              "columns": {
                "recall.sources_mask": { "type": "int64?" }, "recall.best_rank": { "type": "int64?" },
                "recall.best_score": { "type": "float64?" }, "recall.primary_source": { "type": "string?" },
                "score.final": { "type": "float64", "default": 0.0 } },
              "param_schema": { "quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "quotas": {{quotas}}, "k": 10 },
              "nodes": { {{nodes}} },
              "output": { "limit_param": "k", "columns": { {{output}} } } }
            """,
            sources: CandidateSourceRegistry.CreateDefault().Add(new FailingSourceFactory()),
            operators: OperatorRegistry.CreateDefault().Add(new SlotInjectorFactory()).Add(new InterleaveFactory()));
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        return compilation.Plan!;
    }

    private static async Task<PipelineResult> Execute(ExecutionPlan plan, bool record = false, RunReplay? replay = null) =>
        await new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
        {
            RequestId = "r",
            Inputs = new RequestInputs(),
            TimeMs = 1_767_225_600_000,
            Deadline = replay is null ? TimeSpan.FromSeconds(600) : RunReplay.Deadline,
            Record = record,
            Replay = replay,
        }).AsTask().WaitAsync(TimeSpan.FromSeconds(30));

    private static async Task AssertReplayAgrees(ExecutionPlan plan, PipelineResult recorded)
    {
        var replay = RunReplay.Create(recorded.Recording!, "/runs/0");
        var replayed = await Execute(plan, replay: replay);

        Assert.True(replayed.Succeeded, replayed.Error);
        Assert.Empty(replay.Gaps);
        Assert.Equal(Served(recorded), Served(replayed));
    }

    private static List<string> Served(PipelineResult result) => result.Items
        .Select(i => $"{i.Id.Int64Value}: " + string.Join(", ", i.Columns.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key}={c.Value}")))
        .ToList();

    public sealed class SlotInjectorConfig
    {
        /// <summary>The candidate source to inject from (a binding's <c>candidate_sources</c> entry).</summary>
        public required string Source { get; init; }

        /// <summary>1-based positions in the ranking, one per injected candidate, in any order.</summary>
        public required int[] Slots { get; init; }

        /// <summary>The ranking's score column: injected rows get their source score in it.</summary>
        public required string ScoreCol { get; init; }
    }

    /// <summary>
    /// <c>SlotInjector@1</c> (RowSource | RowOrder): calls a candidate source, appends the candidates that are not in the
    /// table yet, at most one per slot, writes their score, and moves them to the slots; the other rows keep their order.
    /// </summary>
    private sealed class SlotInjectorFactory : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } =
            new("SlotInjector", 1, Effects.RowSource | Effects.RowOrder, typeof(SlotInjectorConfig), "Puts a source's candidates at fixed positions.");

        public Operator? Compile(OperatorCompileContext context)
        {
            var config = context.BindConfig<SlotInjectorConfig>()!;
            var bound = context.CandidateSource(config.Source, "/source");
            if (bound is null || !context.TryWriteColumn(config.ScoreCol, "/score_col", null, out var score))
            {
                return null;
            }

            var slots = (int[])config.Slots.Clone();
            Array.Sort(slots);
            return new SlotInjector(config.Source, bound.Value.Source, bound.Value.TimeoutMs, slots, score);
        }

        private sealed class SlotInjector(string name, ICandidateSource source, int? timeoutMs, int[] slots, OutputColumn score) : Operator
        {
            private readonly IoTarget _target = new(IoKind.CandidateSource, name);

            public override int GetRowLimit(ReadOnlySpan<ScalarValue> parameters) => slots.Length;

            public override async ValueTask ExecuteAsync(OperatorContext context)
            {
                // Ask for more than the slots: some candidates may be in the table already.
                var buffer = new CandidateBuffer(2 * slots.Length, context.Memory);
                var query = new CandidateQuery { SourceName = name, Limit = buffer.Limit, Inputs = context.Inputs, RequestId = context.RequestId, Seed = context.Seed };
                var status = await context.RetrieveAsync(source, _target, timeoutMs, query, buffer);
                if (!status.Succeeded)
                {
                    context.ReportDegraded($"source '{name}' failed: {status.Error}; nothing injected");
                    return;
                }

                var ids = new List<long>(slots.Length);
                var scores = new List<double>(slots.Length);
                for (var i = 0; i < buffer.Count && ids.Count < slots.Length; i++)
                {
                    var key = buffer.Keys[i];
                    // Skip what the ranking shows already; a candidate a filter dropped may be injected.
                    var row = context.Table.FindRow(key);
                    if (!key.IsString && (row < 0 || !context.Table.IsAlive(row)) && !ids.Contains(key.Int64Value))
                    {
                        ids.Add(key.Int64Value);
                        scores.Add(buffer.Scores[i]);
                    }
                }

                if (ids.Count == 0)
                {
                    return;
                }

                // Appended at the end of the ranking. The column is the version TopK ranked by: the rows join it.
                var first = context.AppendRows(CollectionsMarshal.AsSpan(ids));
                var column = (ScalarColumn<double>)context.Output(score);
                for (var i = 0; i < ids.Count; i++)
                {
                    column.Set(first + i, scores[i]);
                }

                var live = context.LiveRows;
                var organic = live.Length - ids.Count;
                var order = new int[live.Length];
                int nextOrganic = 0, nextInjected = 0, slot = 0;
                for (var position = 0; position < order.Length; position++)
                {
                    var injectHere = nextInjected < ids.Count && (nextOrganic == organic || slots[slot] == position + 1);
                    order[position] = injectHere ? live[organic + nextInjected++] : live[nextOrganic++];
                    if (injectHere)
                    {
                        slot++;
                    }
                }

                context.SetOrder(order);
            }
        }
    }

    public sealed class InterleaveConfig
    {
        /// <summary>The string column naming each row's source.</summary>
        public required string SourceCol { get; init; }

        /// <summary>The sources to take a row from in turn, repeated.</summary>
        public required string[] Pattern { get; init; }
    }

    /// <summary>
    /// <c>Interleave@1</c> (RowOrder): takes the next row of each source the pattern names in turn, keeping each source's
    /// order; rows of sources the pattern does not name, and those left when the pattern's sources run out, follow in order.
    /// </summary>
    private sealed class InterleaveFactory : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } =
            new("Interleave", 1, Effects.RowOrder, typeof(InterleaveConfig), "Mixes sources by a pattern.");

        public Operator? Compile(OperatorCompileContext context)
        {
            var config = context.BindConfig<InterleaveConfig>()!;
            return context.TryReadColumn(config.SourceCol, "/source_col", out var column) ? new Interleave(column, config.Pattern) : null;
        }

        private sealed class Interleave(ColumnRef sourceColumn, string[] pattern) : Operator
        {
            public override ValueTask ExecuteAsync(OperatorContext context)
            {
                var live = context.LiveRows;
                var sources = context.Read(sourceColumn);
                var queues = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
                foreach (var name in pattern)
                {
                    queues.TryAdd(name, new Queue<int>());
                }

                var others = new List<int>();
                foreach (var row in live)
                {
                    var value = sources is null ? ScalarValue.Missing : ColumnValues.Get(sources, row);
                    if (!value.IsMissing && queues.TryGetValue(value.AsString(), out var queue))
                    {
                        queue.Enqueue(row);
                    }
                    else
                    {
                        others.Add(row);
                    }
                }

                var order = new List<int>(live.Length);
                for (var taken = true; taken;)
                {
                    taken = false;
                    foreach (var name in pattern)
                    {
                        if (queues[name].TryDequeue(out var row))
                        {
                            order.Add(row);
                            taken = true;
                        }
                    }
                }

                order.AddRange(others);
                context.SetOrder(CollectionsMarshal.AsSpan(order));
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary><c>failing@1</c>: a candidate source that always fails.</summary>
    private sealed class FailingSourceFactory : ICandidateSourceFactory
    {
        public string ClientType => "failing@1";

        public ICandidateSource Create(Core.Bindings.BindingContext context) => new Failing();

        private sealed class Failing : ICandidateSource
        {
            public string ClientType => "failing@1";

            public ValueTask<CandidateSourceStatus> RetrieveAsync(CandidateQuery query, ICandidateSink sink, CancellationToken cancellationToken) =>
                ValueTask.FromResult(CandidateSourceStatus.Failed("unavailable"));
        }
    }
}
