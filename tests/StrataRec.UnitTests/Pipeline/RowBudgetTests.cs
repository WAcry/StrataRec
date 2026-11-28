using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Core.Values;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;
using StrataRec.Core.Types;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.UnitTests.Pipeline;

public sealed class RowBudgetTests
{
    [Fact]
    public void ReleasedBudget_SizesLaterSlots_AndLeavesRoomForLaterAppends()
    {
        var schema = new TableSchema(EntityKeyType.Int64, new[] { ColumnType.Int64, ColumnType.Float64, ColumnType.Float64 });
        var table = new CandidateTable(schema, 2000 + 100, new RequestMemory());
        table.AppendRows(new long[] { 1, 2, 3 });

        table.ReleaseRowBudget(2000 - 3);

        Assert.Equal((100, 103), (table.FreeRows, table.StorageCapacity));
        Assert.Equal(103, table.GetOrCreateSlot(1).Capacity);
        table.AppendRows(Enumerable.Range(10, 100).Select(i => (long)i).ToArray());
        Assert.Equal(0, table.FreeRows);

        table.ReleaseRowBudget(5);
        Assert.Equal((0, 103), (table.FreeRows, table.PhysicalRowCount));
    }

    [Fact]
    public async Task ARecallBelowItsQuota_DoesNotSizeLaterColumnsForTheQuota()
    {
        var plan = Compile();
        var pool = new RecordingPool();

        var result = await new PipelineExecutor(pool).ExecuteAsync(plan, new PipelineRequest
        {
            RequestId = "r", Inputs = new RequestInputs(), Deadline = TimeSpan.FromSeconds(60),
        }, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(3, result.Items.Count);
        // Two buffers are the recall's own, sized for its quota before it knows what it finds: its candidate buffer and
        // its score column. The rest (out.s, the result) are created after it finished, for the 3 rows there are.
        Assert.Equal(2, pool.DoubleRents.Count(length => length == 2000));
        Assert.All(pool.DoubleRents, length => Assert.True(length is 2000 or < 100, $"a float64 buffer of {length} rows"));
    }

    private static ExecutionPlan Compile()
    {
        var root = Directory.CreateTempSubdirectory("stratarec-budget-").FullName;
        Write(root, "catalog/c.jsonc", """{ "catalog_id": "c" }""");
        Write(root, "bindings/local.jsonc", """
            { "environment": "local", "services": { "main": { "client": "memory_list@1", "items": [1, 2, 3] } } }
            """);
        Write(root, "pipelines/p.jsonc", """
            { "pipeline_id": "p",
              "param_schema": { "quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "quotas": { "main": 2000 }, "k": 10 },
              "columns": { "out.s": { "type": "float64", "default": 0.0 } },
              "nodes": {
                "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "quotas" } },
                "score":  { "order": 200, "op": "ExpressionScorer@1", "config": { "output_col": "out.s", "expr": "col.entity_id * 0.5" } } },
              "output": { "limit_param": "k", "columns": { "s": "col.out.s" } } }
            """);

        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("p", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        var compilation = PlanCompiler.Compile(request!, new PlanEnvironment { BaseDirectory = root });
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        return compilation.Plan!;
    }

    private static void Write(string root, string path, string text)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private sealed class RecordingPool : IBufferPool
    {
        public List<int> DoubleRents { get; } = new();

        public T[] Rent<T>(int minimumLength)
        {
            if (typeof(T) == typeof(double))
            {
                lock (DoubleRents)
                {
                    DoubleRents.Add(minimumLength);
                }
            }

            return new T[Math.Max(1, minimumLength)];
        }

        public void Return<T>(T[] array, bool clearArray)
        {
        }
    }
}
