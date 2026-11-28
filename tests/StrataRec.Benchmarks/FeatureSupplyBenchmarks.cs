using BenchmarkDotNet.Attributes;
using StrataRec.Core.Values;
using StrataRec.Hosting.Bindings;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Benchmarks;

[MemoryDiagnoser]
public class FeatureSupplyBenchmarks
{
    private ExecutionPlan _plan = null!;
    private PipelineExecutor _executor = null!;
    private RequestInputs _inputs = null!;
    private string _root = null!;
    private BindingHost _tables = null!;

    [Params(500, 5_000)]
    public int Candidates { get; set; }

    [Params(FeatureSupply.Fetch, FeatureSupply.Local)]
    public FeatureSupply Supply { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _root = HomeFeedBenchWorkspace.Create(Candidates, Supply);
        _tables = new BindingHost();
        _plan = HomeFeedBenchWorkspace.Compile(_root, _tables);
        _executor = new PipelineExecutor();
        _inputs = new RequestInputs().Set("user_id", 1001L).Set("session_id", "s-1001").Set("surface", "home");

        var check = Run();
        if (!check.Succeeded || check.Items.Count == 0 || check.Degraded)
        {
            throw new InvalidOperationException("Benchmark request failed or degraded: " + check.Error);
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _tables.Dispose();
        HomeFeedBenchWorkspace.Delete(_root);
    }

    [Benchmark]
    public int Request() => Run().Items.Count;

    private PipelineResult Run() => _executor.ExecuteAsync(_plan, new PipelineRequest
    {
        RequestId = "bench",
        Inputs = _inputs,
        Deadline = TimeSpan.FromSeconds(10),
        TimeMs = 1_767_225_600_000,
    }).GetAwaiter().GetResult();
}
