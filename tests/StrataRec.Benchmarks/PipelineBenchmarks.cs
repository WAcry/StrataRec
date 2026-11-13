using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Benchmarks;

/// <summary>
/// One home_feed request end to end (recall → filter → coarse score → top-k → model → fusion → sort → diversity) over
/// a synthetic catalog of <see cref="Candidates"/> recalled items. Measures engine overhead per request: feature
/// fetches from in-memory connectors, expression scoring, selection and the experiment variant switch.
/// </summary>
/// <remarks>
/// <para>The sample workspace is copied to a temp directory and its fixtures are regenerated at the requested size;
/// the coarse stage keeps a tenth of the candidates for the model, as a production funnel would.</para>
/// <para><c>Baseline</c> runs without experiments; <c>AllArms</c> forces every treatment arm (MMR re-rank, an extra
/// calibration node, parameter overrides); <c>AllArmsWithLogs</c> also captures the funnel log taps.
/// <c>BaselineMetered</c> attaches a <see cref="MeterListener"/> to every StrataRec instrument (design 13.1);
/// <c>BaselineTraced</c> records the request span and its node and I/O spans (design 13.2).</para>
/// <para>Run: <c>dotnet run -c Release --project tests/StrataRec.Benchmarks -- --filter '*PipelineBenchmarks*'</c>.</para>
/// </remarks>
[MemoryDiagnoser]
public class PipelineBenchmarks
{
    private ExecutionPlan _plan = null!;
    private PipelineExecutor _executor = null!;
    private RequestInputs _inputs = null!;
    private RequestArms _allArms;
    private string _root = null!;
    private MeterListener? _meterListener;
    private ActivityListener? _activityListener;

    [Params(500, 5_000)]
    public int Candidates { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _root = Directory.CreateTempSubdirectory("stratarec-bench-").FullName;
        CopyDirectory(FindSample(), _root);
        Scale(_root, Candidates);

        var diagnostics = new DiagnosticBag();
        var workspace = ConfigWorkspace.Load(_root, diagnostics);
        var request = workspace.CreateCompileRequest("home_feed", "local", diagnostics);
        diagnostics.ThrowIfErrors("load benchmark workspace");
        _plan = PlanCompiler.Compile(request!, new PlanEnvironment { BaseDirectory = _root }).GetPlanOrThrow();
        _allArms = _plan.ArmsFromIds(new[] { "exp_mmr_diversity/mmr", "exp_freshness_calibration/calibrated", "exp_quality_weight/strong" });
        _executor = new PipelineExecutor();
        _inputs = new RequestInputs().Set("user_id", 1001L).Set("session_id", "s-1001").Set("surface", "home");

        // Fail fast on a broken setup instead of measuring an error path.
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
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
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
        using var request = StrataRecTracing.StartRequest("home_feed", "bench");
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

    /// <summary>Rewrites fixtures and parameters for <paramref name="candidates"/> recalled items.</summary>
    private static void Scale(string root, int candidates)
    {
        var random = new Random(20261113);
        var categories = new[] { "music", "food", "sports", "travel", "tech", "news" };
        const int Authors = 300;

        var items = new StringBuilder("{");
        for (var id = 1; id <= candidates; id++)
        {
            items.Append(id == 1 ? "\n" : ",\n").Append(Invariant($"""
                "{id}": {"{"} "i_age_h": {random.NextDouble() * 500:F1}, "i_author_id": {100 + random.Next(Authors)}, "i_category": "{categories[random.Next(categories.Length)]}",
                  "i_clk_7d": {random.Next(1000)}.0, "i_imp_7d": {1000 + random.Next(20000)}.0, "i_report_rate": {random.NextDouble() * 0.06:F4}, "i_safe": {(random.Next(20) != 0 ? "true" : "false")},
                  "i_emb": [{random.NextDouble() * 2 - 1:F3}, {random.NextDouble() * 2 - 1:F3}, {random.NextDouble() * 2 - 1:F3}, {random.NextDouble() * 2 - 1:F3}] {"}"}
                """));
        }

        File.WriteAllText(Path.Combine(root, "fixtures", "items.json"), items.Append("\n}").ToString());

        var authors = new StringBuilder("{");
        for (var id = 100; id < 100 + Authors; id++)
        {
            authors.Append(id == 100 ? "\n" : ",\n").Append(Invariant($"\"{id}\": {{ \"a_followers\": {random.Next(1_000_000)}.0 }}"));
        }

        File.WriteAllText(Path.Combine(root, "fixtures", "authors.json"), authors.Append("\n}").ToString());

        // Two thirds from the personalized source, the rest from the popular list; the lists overlap by design.
        var twoTower = candidates * 2 / 3;
        var popular = candidates - twoTower;
        File.WriteAllText(Path.Combine(root, "fixtures", "recall_two_tower.json"), RecallList(1, twoTower));
        File.WriteAllText(Path.Combine(root, "fixtures", "recall_popular.json"), RecallList(candidates - popular - (popular / 2) + 1, popular));

        var pipeline = Path.Combine(root, "pipelines", "home_feed.jsonc");
        var text = File.ReadAllText(pipeline)
            .Replace("\"min\": 0, \"max\": 1000 }", "\"min\": 0, \"max\": 1000000 }", StringComparison.Ordinal)
            .Replace("\"min\": 1, \"max\": 1000 }", "\"min\": 1, \"max\": 1000000 }", StringComparison.Ordinal)
            .Replace("\"two_tower\": 40, \"popular\": 20", Invariant($"\"two_tower\": {twoTower}, \"popular\": {popular}"), StringComparison.Ordinal)
            .Replace("\"coarse_k\": 30,", Invariant($"\"coarse_k\": {Math.Max(30, candidates / 10)},"), StringComparison.Ordinal);
        File.WriteAllText(pipeline, text);
    }

    private static string RecallList(int firstId, int count)
    {
        var list = new StringBuilder("{ \"items\": [");
        for (var i = 0; i < count; i++)
        {
            list.Append(i == 0 ? string.Empty : ", ").Append(Invariant($"{{ \"id\": {firstId + i}, \"score\": {1.0 - (i / (double)count):F5} }}"));
        }

        return list.Append("] }").ToString();
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    private static string FindSample()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "samples", "home_feed");
                if (Directory.Exists(Path.Combine(candidate, "pipelines")))
                {
                    return candidate;
                }
            }
        }

        throw new DirectoryNotFoundException($"samples/home_feed not found above {AppContext.BaseDirectory} or {Environment.CurrentDirectory}");
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var directory in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(from, to, StringComparison.Ordinal));
        }

        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(from, to, StringComparison.Ordinal), overwrite: true);
        }
    }
}
