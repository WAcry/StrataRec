using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;
using StrataRec.Core.Values;
using StrataRec.Hosting.Bindings;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Benchmarks;

/// <summary>
/// One home_feed request with a wide candidate tap after coarse_score (3,284 of 5,000 rows survive the safety filter):
/// six scalar columns (five float32, the int64 entity id) and the item embedding (<c>list&lt;float32,D&gt;</c>), the
/// request with and without log capture. What a sampled request pays for its taps: the rows' columns copied at their
/// declared width.
/// </summary>
/// <remarks>Run: <c>dotnet run -c Release --project tests/StrataRec.Benchmarks -- --filter '*TapCaptureBenchmarks*'</c>.</remarks>
[MemoryDiagnoser]
public class TapCaptureBenchmarks
{
    private ExecutionPlan _plan = null!;
    private PipelineExecutor _executor = null!;
    private RequestInputs _inputs = null!;
    private string _root = null!;
    private BindingHost _tables = null!;

    [Params(5_000)]
    public int Candidates { get; set; }

    [Params(4, 64)]
    public int Dimensions { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _root = HomeFeedBenchWorkspace.Create(Candidates, FeatureSupply.Sample);
        Rewrite(Path.Combine(_root, "catalog", "home_feed.jsonc"),
            "\"type\": \"list<float32,4>\", \"source\": \"item_store\"", $"\"type\": \"list<float32,{Dimensions}>\", \"source\": \"item_store\"");
        var itemsPath = Path.Combine(_root, "fixtures", "items.json");
        var items = JsonNode.Parse(File.ReadAllText(itemsPath))!.AsObject();
        var random = new Random(7);
        foreach (var (_, item) in items)
        {
            var embedding = new JsonArray();
            for (var d = 0; d < Dimensions; d++)
            {
                embedding.Add(Math.Round(random.NextDouble() * 2 - 1, 3));
            }

            item!["i_emb"] = embedding;
        }

        File.WriteAllText(itemsPath, items.ToJsonString());
        var pipeline = Path.Combine(_root, "pipelines", "home_feed.jsonc");
        Rewrite(pipeline, "\"predicate\": \"(col.item.is_policy_safe ?? true) && col.item.report_rate < 0.05\"",
            "\"predicate\": \"(col.item.is_policy_safe ?? true) && col.item.report_rate < 0.05 && (has(col.item.embedding) || col.item.report_rate >= 0.0)\"");
        Rewrite(pipeline, "\"log_taps\": {", """
            "log_taps": {
              "wide": { "group": "all", "attach": { "after": "coarse_score" },
                        "columns": { "coarse": "col.score.coarse", "report_rate": "col.item.report_rate", "clicks": "col.item.clicks_7d",
                                     "imps": "col.item.impressions_7d", "ctr": "col.item.smoothed_ctr", "id": "col.entity_id",
                                     "emb": "col.item.embedding" } },
            """);
        _tables = new BindingHost();
        _plan = HomeFeedBenchWorkspace.Compile(_root, _tables);
        _executor = new PipelineExecutor();
        _inputs = new RequestInputs().Set("user_id", 1001L).Set("session_id", "s-1001").Set("surface", "home");

        var check = Run(captureLogs: true);
        var wide = check.LogBatches.Single(b => b.TapId == "wide");
        Console.WriteLine($"// wide tap: {wide.RowCount} rows, {wide.ApproximateBytes} bytes, emb {wide.Columns.Single(c => c.Name == "emb").Values.GetType().Name}");
        if (!check.Succeeded || wide.RowCount < 1000)
        {
            throw new InvalidOperationException("Benchmark request failed: " + check.Error);
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _tables.Dispose();
        HomeFeedBenchWorkspace.Delete(_root);
    }

    [Benchmark(Baseline = true)]
    public int WithoutLogs() => Run(captureLogs: false).Items.Count;

    [Benchmark]
    public int WithLogs() => Run(captureLogs: true).LogBatches.Count;

    private static void Rewrite(string path, string from, string to)
    {
        var text = File.ReadAllText(path);
        if (!text.Contains(from, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"'{from}' is not in {path}.");
        }

        File.WriteAllText(path, text.Replace(from, to, StringComparison.Ordinal));
    }

    private PipelineResult Run(bool captureLogs) => _executor.ExecuteAsync(_plan, new PipelineRequest
    {
        RequestId = "bench",
        Inputs = _inputs,
        Arms = RequestArms.None,
        Deadline = TimeSpan.FromSeconds(10),
        TimeMs = 1_767_225_600_000,
        CaptureLogs = captureLogs,
    }).GetAwaiter().GetResult();
}
