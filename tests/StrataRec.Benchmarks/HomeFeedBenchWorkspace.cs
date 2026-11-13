using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.LocalTables;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Planning;
using StrataRec.Testing;

namespace StrataRec.Benchmarks;

/// <summary>How a scaled home_feed workspace binds its candidate feature sources, <c>item_store</c> and <c>author_store</c>.</summary>
public enum FeatureSupply
{
    /// <summary>As the sample binds them: items fetched from <c>memory@1</c> in chunks of 25, authors a local table.</summary>
    Sample,

    /// <summary>Both fetched from <c>memory@1</c>, each in one call per request (<c>max_batch</c> above the candidate count).</summary>
    Fetch,

    /// <summary>Both local tables loaded from <c>snapshot_files@1</c> directories holding the same records.</summary>
    Local,
}

/// <summary>
/// The home_feed sample copied to a temp directory with its fixtures regenerated for a candidate count, compiled with
/// its local tables loaded as the engine loads them before serving a plan.
/// </summary>
/// <remarks>
/// The copy is untimed (<see cref="SampleBudgets.Relax"/>): a benchmark measures the pipeline's work, and a budget hit by
/// a cold first request on a busy runner would measure a fallback instead (or fail the setup check).
/// </remarks>
internal static class HomeFeedBenchWorkspace
{
    public const int Authors = 300;

    /// <summary>The coarse stage keeps a tenth of the candidates for the model, as a production funnel would.</summary>
    public static string Create(int candidates, FeatureSupply supply)
    {
        var root = Directory.CreateTempSubdirectory("stratarec-bench-").FullName;
        CopyDirectory(FindSample(), root);
        SampleBudgets.Relax(root);
        Scale(root, candidates, supply);
        return root;
    }

    /// <summary>Compiles home_feed with <paramref name="tables"/> holding its local tables, and waits for their loads.</summary>
    public static ExecutionPlan Compile(string root, LocalTableHost tables)
    {
        var diagnostics = new DiagnosticBag();
        var workspace = ConfigWorkspace.Load(root, diagnostics);
        var request = workspace.CreateCompileRequest("home_feed", "local", diagnostics);
        diagnostics.ThrowIfErrors("load benchmark workspace");
        var lease = tables.OpenLease();
        var plan = PlanCompiler.Compile(request!, new PlanEnvironment { BaseDirectory = root }, lease).GetPlanOrThrow();
        var failures = lease.WaitLoadedAsync().GetAwaiter().GetResult();
        if (failures.Count > 0)
        {
            throw new InvalidOperationException("Benchmark tables failed to load: " + failures[0].Message);
        }

        return plan;
    }

    public static void Delete(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static void Scale(string root, int candidates, FeatureSupply supply)
    {
        var random = new Random(20261113);
        var categories = new[] { "music", "food", "sports", "travel", "tech", "news" };

        var items = new string[candidates];
        for (var i = 0; i < candidates; i++)
        {
            items[i] = Invariant($"""
                "i_age_h": {random.NextDouble() * 500:F1}, "i_author_id": {100 + random.Next(Authors)}, "i_category": "{categories[random.Next(categories.Length)]}", "i_clk_7d": {random.Next(1000)}.0, "i_imp_7d": {1000 + random.Next(20000)}.0, "i_report_rate": {random.NextDouble() * 0.06:F4}, "i_safe": {(random.Next(20) != 0 ? "true" : "false")}, "i_emb": [{random.NextDouble() * 2 - 1:F3}, {random.NextDouble() * 2 - 1:F3}, {random.NextDouble() * 2 - 1:F3}, {random.NextDouble() * 2 - 1:F3}]
                """);
        }

        var authors = new string[Authors];
        for (var i = 0; i < Authors; i++)
        {
            authors[i] = Invariant($"\"a_followers\": {random.Next(1_000_000)}.0");
        }

        WriteMemory(Path.Combine(root, "fixtures", "items.json"), firstKey: 1, items);
        WriteMemory(Path.Combine(root, "fixtures", "authors.json"), firstKey: 100, authors);
        WriteSnapshot(Path.Combine(root, "fixtures", "item_snapshot"), firstKey: 1, items);
        WriteSnapshot(Path.Combine(root, "fixtures", "author_snapshot"), firstKey: 100, authors);
        switch (supply)
        {
            case FeatureSupply.Fetch:
                Rebind(root, "item_store", $$"""{ "connector": "memory@1", "data_file": "fixtures/items.json", "max_batch": 100000, "timeout_ms": {{SampleBudgets.UntimedMs}} }""");
                Rebind(root, "author_store", $$"""{ "connector": "memory@1", "data_file": "fixtures/authors.json", "max_batch": 100000, "timeout_ms": {{SampleBudgets.UntimedMs}} }""");
                break;
            case FeatureSupply.Local:
                Rebind(root, "item_store", """{ "mode": "local", "max_bytes": "256MiB", "bootstrap": { "source": "snapshot_files@1", "uri": "fixtures/item_snapshot" } }""");
                break;
        }

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

    /// <summary>A <c>memory@1</c> data file: one object per key.</summary>
    private static void WriteMemory(string path, int firstKey, string[] records)
    {
        var text = new StringBuilder("{");
        for (var i = 0; i < records.Length; i++)
        {
            text.Append(i == 0 ? "\n" : ",\n").Append(Invariant($"\"{firstKey + i}\": {{ {records[i]} }}"));
        }

        File.WriteAllText(path, text.Append("\n}").ToString());
    }

    /// <summary>A <c>snapshot_files@1</c> directory: a manifest and one JSONL part.</summary>
    private static void WriteSnapshot(string directory, int firstKey, string[] records)
    {
        Directory.CreateDirectory(directory);
        var text = new StringBuilder();
        for (var i = 0; i < records.Length; i++)
        {
            text.Append(Invariant($"{{\"key\": {firstKey + i}, \"version\": 1, \"fields\": {{ {records[i]} }}}}\n"));
        }

        File.WriteAllText(Path.Combine(directory, "part-0000.jsonl"), text.ToString());
        File.WriteAllText(Path.Combine(directory, "snapshot.json"), """{ "format": "jsonl", "files": ["part-0000.jsonl"] }""");
    }

    private static void Rebind(string root, string source, string binding)
    {
        var path = Path.Combine(root, "bindings", "local.jsonc");
        var document = (JsonObject)Jsonc.Parse(File.ReadAllBytes(path), path)!;
        ((JsonObject)document["sources"]!)[source] = JsonNode.Parse(binding);
        File.WriteAllText(path, document.ToJsonString());
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
