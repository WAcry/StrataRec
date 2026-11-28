using System.Diagnostics;
using System.Diagnostics.Metrics;
using BenchmarkDotNet.Attributes;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Values;
using StrataRec.Hosting.Bindings;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Benchmarks;

[MemoryDiagnoser]
public class PipelineBenchmarks
{
    private ExecutionPlan _plan = null!;
    private PipelineExecutor _executor = null!;
    private RequestInputs _inputs = null!;
    private RequestArms _allArms;
    private string _root = null!;
    private BindingHost _tables = null!;
    private MeterListener? _meterListener;
    private ActivityListener? _activityListener;

    [Params(500, 5_000)]
    public int Candidates { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _root = HomeFeedBenchWorkspace.Create(Candidates, FeatureSupply.Sample);
        _tables = new BindingHost();
        _plan = HomeFeedBenchWorkspace.Compile(_root, _tables);
        _allArms = _plan.ArmsFromIds(new[] { "exp_mmr_diversity/mmr", "exp_freshness_calibration/calibrated", "exp_quality_weight/strong" });
        _executor = new PipelineExecutor();
        _inputs = new RequestInputs().Set("user_id", 1001L).Set("session_id", "s-1001").Set("surface", "home");

        var check = Run(_allArms, captureLogs: true);
        if (!check.Succeeded || check.Items.Count == 0)
        {
            throw new InvalidOperationException("Benchmark request failed: " + check.Error);
        }
    }

    [GlobalSetup(Target = nameof(BaselineMetered))]
    public void SetupMetered()
    {
        Setup();
        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == StrataRecTelemetry.Name)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _meterListener.SetMeasurementEventCallback<long>(static (_, _, _, _) => { });
        _meterListener.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
        _meterListener.Start();
    }

    [GlobalSetup(Target = nameof(BaselineTraced))]
    public void SetupTraced()
    {
        Setup();
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == StrataRecTelemetry.Name,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(_activityListener);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _meterListener?.Dispose();
        _activityListener?.Dispose();
        _tables.Dispose();
        HomeFeedBenchWorkspace.Delete(_root);
    }

    [Benchmark(Baseline = true)]
    public int Baseline() => Run(RequestArms.None, captureLogs: false).Items.Count;

    [Benchmark]
    public int AllArms() => Run(_allArms, captureLogs: false).Items.Count;

    [Benchmark]
    public int AllArmsWithLogs() => Run(_allArms, captureLogs: true).LogBatches.Count;

    [Benchmark]
    public int BaselineMetered() => Run(RequestArms.None, captureLogs: false).Items.Count;

    [Benchmark]
    public int BaselineTraced()
    {
        using var request = StrataRec.Hosting.RequestTelemetry.StartRequest("home_feed", "bench");
        return Run(RequestArms.None, captureLogs: false).Items.Count;
    }

    private PipelineResult Run(RequestArms arms, bool captureLogs) => _executor.ExecuteAsync(_plan, new PipelineRequest
    {
        RequestId = "bench",
        Inputs = _inputs,
        Arms = arms,
        Deadline = TimeSpan.FromSeconds(10),
        TimeMs = 1_767_225_600_000,
        CaptureLogs = captureLogs,
    }).GetAwaiter().GetResult();
}
