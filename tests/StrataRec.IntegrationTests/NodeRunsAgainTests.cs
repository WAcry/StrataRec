using StrataRec.Core.Columns;
using StrataRec.Core.Values;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Replay;

namespace StrataRec.IntegrationTests;

/// <summary>
/// A node may run more than once in a request: a fallback node serves every node that names it.
/// </summary>
public sealed class NodeRunsAgainTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData("fails")]
    [InlineData("throws")]
    [InlineData("returns late")]
    [InlineData("fails to commit")]
    public async Task ALaterRunThatFails_LeavesWhatTheEarlierRunCommitted_AndAReplayAgrees(string how)
    {
        var marks = new MarksFactory((context, column, _, run) =>
        {
            foreach (var row in context.LiveRows)
            {
                column.Set(row, run);
            }

            if (run == 1)
            {
                return;
            }

            switch (how)
            {
                case "fails":
                    context.Fail("gave up after writing");
                    break;
                case "throws":
                    throw new InvalidOperationException("broke after writing");
                case "returns late":
                    Assert.True(context.CancellationToken.WaitHandle.WaitOne(Patience));
                    break;
                default:
                    column.SetMissing(context.LiveRows[0]);
                    break;
            }
        });
        var plan = Compile(marks, "int64", how == "returns late" ? ", \"timeout_ms\": 200" : string.Empty);

        var recorded = await Execute(plan, marks, record: true);

        Assert.True(recorded.Succeeded, recorded.Error);
        Assert.Equal(2, marks.Runs);
        Assert.Equal(new long?[] { 1, 1, 1 }, Marks(recorded));
        Assert.Equal("backup", recorded.Outcomes.Single(o => o.NodeId == "a").ProducedBy);
        var second = recorded.Outcomes.Single(o => o.NodeId == "b");
        var failure = how switch
        {
            "fails" => "gave up after writing",
            "throws" => "InvalidOperationException: broke after writing",
            "returns late" => "timed out after 200 ms",
            _ => "left non-nullable column 'mark' without a value on 1 of 3 live rows",
        };
        Assert.Equal((NodeOutcomeKind.Failed, $"primary is down; fallback 'backup': {failure} (skipped)"), (second.Kind, second.Detail));

        // A replay skips an attempt recorded as timed out instead of running it: it must end where the live run did.
        var replay = RunReplay.Create(recorded.Recording!, "/runs/0");
        var replayed = await Execute(plan, marks, replay: replay);

        Assert.Empty(replay.Gaps);
        Assert.Equal(Marks(recorded), Marks(replayed));
        Assert.Equal(recorded.Outcomes.Select(o => (o.NodeId, o.Kind, o.Detail, o.ProducedBy)), replayed.Outcomes.Select(o => (o.NodeId, o.Kind, o.Detail, o.ProducedBy)));
    }

    [Fact]
    public async Task ALaterRunThatSucceeds_StartsWithNoValues_AndReadsTheEarlierRunsWhileItWrites()
    {
        var blank = false;
        long?[]? read = null;
        var marks = new MarksFactory((context, column, before, run) =>
        {
            var rows = context.LiveRows;
            if (run == 1)
            {
                foreach (var row in rows)
                {
                    column.Set(row, 1);
                }

                return;
            }

            blank = rows.ToArray().All(row => !column.IsValid(row));
            column.Set(rows[0], 2);
            read = rows.ToArray().Select(row => before.IsValid(row) ? before.Values[row] : (long?)null).ToArray(); // after the write: another buffer
        });
        var plan = Compile(marks, "int64?", string.Empty);

        var result = await Execute(plan, marks);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(2, marks.Runs);
        Assert.True(blank, "the second run's buffer held the first run's values");
        Assert.Equal(new long?[] { 1, 1, 1 }, read);
        Assert.Equal(new long?[] { 2, null, null }, Marks(result));
    }

    private static long?[] Marks(PipelineResult result) =>
        result.Items.Select(i => i.Columns["mark"] is { IsMissing: false } mark ? mark.AsInt64() : (long?)null).ToArray();

    private static ExecutionPlan Compile(MarksFactory marks, string type, string backupTimeout)
    {
        var pipeline = $$"""
            { "pipeline_id": "p", "entity_key_type": "int64",
              "columns": { "mark": { "type": "{{type}}", "flow": true } },
              "param_schema": { "quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "quotas": { "seed": 3 }, "k": 10 },
              "nodes": {
                "seed": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "quotas" } },
                "first": { "order": 150, "op": "Marks@1", "config": { "col": "mark", "as": "first" } },
                "a": { "order": 200, "op": "Marks@1", "on_failure": "fallback", "fallback": "backup", "config": { "col": "mark", "as": "primary" } },
                "b": { "order": 300, "op": "Marks@1", "on_failure": "fallback", "fallback": "backup", "config": { "col": "mark", "as": "primary" } },
                "backup": { "role": "fallback", "op": "Marks@1", "on_failure": "skip"{{backupTimeout}}, "config": { "col": "mark", "as": "backup" } } },
              "output": { "limit_param": "k", "columns": { "mark": "col.mark" } } }
            """;
        return MiniWorkspace.Compile("""  "seed": { "client": "memory_list@1", "items": [5, 6, 7] }  """, pipeline, operators: OperatorRegistry.CreateDefault().Add(marks));
    }

    private static async Task<PipelineResult> Execute(ExecutionPlan plan, MarksFactory marks, bool record = false, RunReplay? replay = null)
    {
        marks.Runs = 0;
        return await new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
        {
            RequestId = "r",
            Inputs = new RequestInputs(),
            TimeMs = 1_767_225_600_000,
            Deadline = replay is null ? TimeSpan.FromSeconds(600) : RunReplay.Deadline,
            Record = record,
            Replay = replay,
        }).AsTask().WaitAsync(Patience);
    }

    private sealed class MarksConfig
    {
        public required string Col { get; init; }

        public required string As { get; init; }
    }

    private sealed class MarksFactory(Action<OperatorContext, ScalarColumn<long>, ScalarColumn<long>, int> backup) : IOperatorFactory
    {
        public int Runs { get; set; }

        private Action<OperatorContext, ScalarColumn<long>, ScalarColumn<long>, int> Backup { get; } = backup;

        public OperatorDescriptor Descriptor { get; } = new("Marks", 1, Effects.ColumnWriter, typeof(MarksConfig), "Writes a column as a test says.");

        public Operator? Compile(OperatorCompileContext context)
        {
            var config = context.BindConfig<MarksConfig>();
            if (config is null)
            {
                return null;
            }

            ColumnRef read = default;
            if (config.As == "backup" && !context.TryReadColumn(config.Col, "/col", out read))
            {
                return null;
            }

            return context.TryWriteColumn(config.Col, "/col", null, out var column) ? new Marks(this, config.As, read, column) : null;
        }

        private sealed class Marks(MarksFactory owner, string role, ColumnRef read, OutputColumn column) : Operator
        {
            public override ValueTask ExecuteAsync(OperatorContext context)
            {
                switch (role)
                {
                    case "first":
                        var buffer = (ScalarColumn<long>)context.Output(column);
                        foreach (var row in context.LiveRows)
                        {
                            buffer.Set(row, 0);
                        }

                        break;
                    case "primary":
                        context.Fail("primary is down");
                        break;
                    default:
                        owner.Backup(context, (ScalarColumn<long>)context.Output(column), (ScalarColumn<long>)context.Read(read)!, ++owner.Runs);
                        break;
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
