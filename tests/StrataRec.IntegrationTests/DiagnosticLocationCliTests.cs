using System.Diagnostics;
using System.Text.Json;
using StrataRec.CommandLine.Commands;

namespace StrataRec.IntegrationTests;

/// <summary>
/// The CLI prints a configuration error as <c>file:line:column: error CODE /pointer: message</c> (and the same location in
/// <c>--format json</c>), so a terminal or editor opens the place; one mistake is one error, not a cascade. Expected
/// positions are counted from the edited file here, independently of the locator.
/// </summary>
public sealed class DiagnosticLocationCliTests : IDisposable
{
    private const string Pipeline = "pipelines/home_feed.jsonc";

    private readonly List<string> _roots = new();

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Text_AnUnknownConfigKey_IsAtTheKey_WithThePointerAndADidYouMean()
    {
        var root = Workspace((Pipeline, "\"config\": { \"output_col\": \"score.coarse\", \"expr\"", "\"config\": { \"outputcol\": \"score.coarse\", \"expr\""));

        var lines = ValidateText(root, expectedErrors: 1);

        var (line, column) = Position(root, Pipeline, "\"outputcol\"");
        Assert.Contains(
            $"{root}/{Pipeline}:{line}:{column}: error NODE_BAD_CONFIG /nodes/coarse_score/config/outputcol: Unknown key 'outputcol' in the config of ExpressionScorer@1.",
            lines);
        Assert.Contains("    hint: did you mean 'output_col'?", lines);
    }

    [Fact]
    public void Text_AValueOfTheWrongKind_IsAtItsKey_InJsonWords()
    {
        var root = Workspace((Pipeline, "\"order\": 350, \"op\": \"TopK@1\"", "\"order\": \"350\", \"op\": \"TopK@1\""));

        var lines = ValidateText(root, expectedErrors: 1);

        var (line, column) = Position(root, Pipeline, "\"order\": \"350\"");
        Assert.Contains(
            $"{root}/{Pipeline}:{line}:{column}: error ARTIFACT_SCHEMA /nodes/coarse_top_k/order: 'order' must be an integer or null, got the string \"350\".",
            lines);
    }

    [Fact]
    public void Json_AnExpressionTypeError_IsAtThePartAtFault_InsideTheString()
    {
        var root = Workspace((Pipeline, "\"expr\": \"col.item.smoothed_ctr\" }", "\"expr\": \"col.item.smoothed_ctr * 'a'\" }"));
        var output = new StringWriter();

        Assert.Equal(1, ValidateCommand.Run(Tool.Environment, root, "local", null, "json", warningsAsErrors: false, output));

        using var report = JsonDocument.Parse(output.ToString());
        var error = Assert.Single(report.RootElement.GetProperty("diagnostics").EnumerateArray(), d => d.GetProperty("severity").GetString() == "error");
        var (line, column) = Position(root, Pipeline, "'a'\" }");
        Assert.Equal("EXPR_TYPE_MISMATCH", error.GetProperty("code").GetString());
        Assert.Equal("pipeline:home_feed", error.GetProperty("artifact").GetString());
        Assert.Equal("/nodes/coarse_score/config/expr", error.GetProperty("pointer").GetString());
        Assert.Equal($"{root}/{Pipeline}", error.GetProperty("file").GetString());
        Assert.Equal(line, error.GetProperty("line").GetInt32());
        Assert.Equal(column, error.GetProperty("column").GetInt32());
        Assert.Equal("'*' needs numbers, got float64 and string. (at offset 24 in 'col.item.smoothed_ctr * 'a'')", error.GetProperty("message").GetString());
    }

    [Fact]
    public void Json_EveryLocatedDiagnostic_HasFileLineAndColumn()
    {
        var root = Workspace();
        var output = new StringWriter();

        Assert.Equal(0, ValidateCommand.Run(Tool.Environment, root, "local", null, "json", warningsAsErrors: false, output));

        using var report = JsonDocument.Parse(output.ToString());
        var warning = Assert.Single(report.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("experiment:exp_mmr_diversity", warning.GetProperty("artifact").GetString());
        Assert.Equal($"{root}/experiments/exp_mmr_diversity.jsonc", warning.GetProperty("file").GetString());
        var (line, column) = Position(root, "experiments/exp_mmr_diversity.jsonc", "\"mmr_diversifier\"");
        Assert.Equal((line, column), (warning.GetProperty("line").GetInt32(), warning.GetProperty("column").GetInt32()));
    }

    [Theory]
    [InlineData(Pipeline, "\"coarse_k\":       { \"type\": \"int64\"", "\"coarse_k\":       { \"type\": \"int46\"",
        "PARAM_BAD_TYPE", "/param_schema/coarse_k/type", "\"type\": \"int46\"")]
    [InlineData(Pipeline, "\"score.coarse\": { \"type\": \"float32\"", "\"score.coarse\": { \"type\": \"flaot32\"",
        "COLUMN_BAD_TYPE", "/columns/score.coarse/type", "\"type\": \"flaot32\"")]
    [InlineData(Pipeline, "\"user_id\":    { \"type\": \"int64?\" }", "\"user_id\":    { \"type\": \"int46?\" }",
        "REQUEST_BAD_TYPE", "/request_schema/user_id/type", "\"type\": \"int46?\"")]
    [InlineData("catalog/home_feed.jsonc", "\"item.clicks_7d\":      { \"type\": \"float32\"", "\"item.clicks_7d\":      { \"type\": \"flot32\"",
        "FEATURE_BAD_TYPE", "/features/item.clicks_7d/type", "\"type\": \"flot32\"")]
    [InlineData("catalog/home_feed.jsonc", "\"item.author_id\":      { \"type\": \"int64\"", "\"item.author_id\":      { \"type\": \"int46\"",
        "FEATURE_BAD_TYPE", "/features/item.author_id/type", "\"type\": \"int46\"")] // the key of an entity
    [InlineData(Pipeline, "\"op\": \"ModelScorer@1\"", "\"op\": \"ModelScorr@1\"",
        "OPERATOR_UNKNOWN", "/nodes/ctr_ranker/op", "\"op\": \"ModelScorr@1\"")] // a tap takes its columns from this node
    [InlineData(Pipeline, "\"key\": [\"ctx.request.user_id\", \"ctx.request.session_id\"]", "\"key\": [\"ctx.request.usr_id\", \"ctx.request.session_id\"]",
        "EXPR_UNKNOWN_NAME", "/funnel_groups/deep_trace/key/0", "ctx.request.usr_id")] // a tap samples by this group
    [InlineData("experiments/exp_mmr_diversity.jsonc", "\"diversity_cap\": null,", "\"diversty_cap\": null,",
        "PATCH_DELETE_MISSING", "/arms/mmr/nodes/diversty_cap", "\"diversty_cap\"")] // the arm's parameters are not reported again
    public void Text_ADeclarationWithAnError_IsTheOnlyError(string file, string old, string replacement, string code, string pointer, string at)
    {
        var root = Workspace((file, old, replacement));

        var lines = ValidateText(root, expectedErrors: 1);

        var (line, column) = Position(root, file, at);
        Assert.Contains(lines, l => l.StartsWith($"{root}/{file}:{line}:{column}: error {code} {pointer}: ", StringComparison.Ordinal));
    }

    [Fact]
    public void Text_ASyntaxError_IsAtTheReadersPosition_WithoutAPointer()
    {
        var root = Workspace((Pipeline, "\"order\": 350,", "\"order\": 350"));

        var lines = ValidateText(root, expectedErrors: 1);

        var (line, column) = Position(root, Pipeline, "\"op\": \"TopK@1\",\n      \"config\": { \"score_col\": \"score.coarse\"");
        Assert.Contains(lines, l => l.StartsWith($"{root}/{Pipeline}:{line}:{column}: error JSONC_SYNTAX: ", StringComparison.Ordinal));
    }

    [Fact]
    public void Text_AnArmsNodeConfig_IsInTheExperimentFile()
    {
        var root = Workspace(("experiments/exp_mmr_diversity.jsonc", "\"window\": 20", "\"window\": \"20\""));

        var lines = ValidateText(root, expectedErrors: 1);

        var (line, column) = Position(root, "experiments/exp_mmr_diversity.jsonc", "\"window\": \"20\"");
        Assert.Contains(
            $"{root}/experiments/exp_mmr_diversity.jsonc:{line}:{column}: error NODE_BAD_CONFIG /arms/mmr/nodes/mmr_diversifier/config/window: 'window' must be an integer, got the string \"20\".",
            lines);
    }

    [Theory]
    [InlineData("\"user_id\": 1001", "\"user_id\": \"1001\"", "\"user_id\"",
        "error REQUEST_INVALID /inputs/user_id: Input 'user_id' must be an integer (int64), got the string \"1001\".")]
    [InlineData("\"inputs\"", "\"inptus\"", "\"inptus\"", "error REQUEST_INVALID /inptus: Unknown key 'inptus' in the request.")]
    [InlineData("\"surface\": \"home\"", "\"surfce\": \"home\"", "\"surfce\"",
        "error REQUEST_INVALID /inputs/surfce: Input 'surfce' is not declared in the request_schema of pipeline 'home_feed'.")]
    [InlineData("\"deadline_ms\": 200", "\"deadline_ms\": \"200\"", "\"deadline_ms\"",
        "error REQUEST_INVALID /deadline_ms: 'deadline_ms' must be a number or null, got the string \"200\".")]
    // Review 32: a null pipeline_id was an unhandled exception; duplicate members are rejected as in a service's request body.
    [InlineData("\"pipeline_id\": \"home_feed\"", "\"pipeline_id\": null", "\"pipeline_id\"",
        "error REQUEST_INVALID /pipeline_id: 'pipeline_id' must name a pipeline, got null.\n    hint: give the id of the pipeline to run, e.g. \"pipeline_id\": \"home_feed\"")]
    [InlineData("\"pipeline_id\": \"home_feed\"", "\"pipeline_id\": \"\"", "\"pipeline_id\"",
        "error REQUEST_INVALID /pipeline_id: 'pipeline_id' must name a pipeline, got an empty string.")]
    [InlineData("\"request_id\": \"req-1001\"", "\"request_id\": \"req-1001\", \"request_id\": \"req-1002\"", "\"request_id\": \"req-1002\"",
        "error JSONC_DUPLICATE_KEY: Duplicate key 'request_id'")]
    [InlineData("\"user_id\": 1001", "\"user_id\": 1001, \"user_id\": 1002", "\"user_id\": 1002",
        "error JSONC_DUPLICATE_KEY: Duplicate key 'user_id'")]
    [InlineData("\"surface\": \"home\"", "\"surface\": \"home\", \"tenant\": 1, \"surfce\": null", "\"tenant\"",
        "error REQUEST_INVALID /inputs/tenant: Input 'tenant' is not declared in the request_schema of pipeline 'home_feed'.")]
    public async Task Run_AMistakeInTheRequestFile_IsAtItsLineAndColumn(string old, string replacement, string at, string error)
    {
        const string requestFile = "requests/user_1001.json";
        var root = Workspace((requestFile, old, replacement));
        var request = Path.Combine(root, requestFile);

        var (code, stderr) = await Cli("run", "-c", root, "-r", request);

        Assert.True(code == 2, $"exit {code}; stderr: {stderr}");
        var (line, column) = Position(root, requestFile, at);
        Assert.Contains($"{request}:{line}:{column}: {error}", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_ATablesErrors_AreAtTheirKeys()
    {
        const string table = "tables/boost.jsonc";
        var root = Workspace((table, string.Empty, """
            { "table_id": "boost", "key_type": "int64", "value_type": "float64",
              "entries": { "1": 0.5, "x": 1.0, "3": "a" } }
            """));

        var lines = ValidateText(root, expectedErrors: 2);

        var key = Position(root, table, "\"x\"");
        var value = Position(root, table, "\"3\"");
        Assert.Contains($"{root}/{table}:{key.Line}:{key.Column}: error TABLE_INVALID /entries/x: Key 'x' is not an integer, and the table's key_type is int64.", lines);
        Assert.Contains($"{root}/{table}:{value.Line}:{value.Column}: error TABLE_INVALID /entries/3: The value of '3' must be a number (float64), got the string \"a\".", lines);
    }

    [Fact]
    public async Task Diff_ASidesError_IsInThatSidesWorkspace_AndSaysWhichSide()
    {
        var from = Workspace();
        var to = Workspace((Pipeline, "\"config\": { \"output_col\": \"score.coarse\", \"expr\"", "\"config\": { \"outputcol\": \"score.coarse\", \"expr\""));
        var errors = new StringWriter();

        Assert.Equal(2, await DiffCommand.RunAsync(Tool.Environment, from, to, null, "local", null, Array.Empty<string>(), "text", TextWriter.Null, CancellationToken.None, errors));

        var (line, column) = Position(to, Pipeline, "\"outputcol\"");
        Assert.Contains(
            $"{to}/{Pipeline}:{line}:{column}: error NODE_BAD_CONFIG --to pipeline:home_feed/nodes/coarse_score/config/outputcol: Unknown key 'outputcol' in the config of ExpressionScorer@1.",
            errors.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A copy of the sample with each (file, old, new) replacement applied once; an empty old writes a new file.</summary>
    private string Workspace(params (string File, string Old, string New)[] edits)
    {
        var root = Directory.CreateTempSubdirectory("stratarec-diagnostics-").FullName;
        _roots.Add(root);
        var sample = HomeFeedTestHost.SampleRoot(SampleTiming.AsWritten);
        foreach (var file in Directory.GetFiles(sample, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(root, Path.GetRelativePath(sample, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        foreach (var (file, old, replacement) in edits)
        {
            var path = Path.Combine(root, file);
            if (old.Length == 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, replacement);
                continue;
            }

            var text = File.ReadAllText(path);
            var at = text.IndexOf(old, StringComparison.Ordinal);
            Assert.True(at >= 0, $"'{old}' is not in {file}");
            File.WriteAllText(path, text[..at] + replacement + text[(at + old.Length)..]);
        }

        return root;
    }

    /// <summary>Runs <c>stratarec validate</c> in text: its lines, after checking the error count.</summary>
    private static List<string> ValidateText(string root, int expectedErrors)
    {
        var output = new StringWriter();
        ValidateCommand.Run(Tool.Environment, root, "local", null, "text", warningsAsErrors: false, output);
        var lines = output.ToString().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.True(lines.Count(l => l.Contains(": error ", StringComparison.Ordinal)) == expectedErrors, output.ToString());
        Assert.Contains($"{expectedErrors} error(s), ", output.ToString(), StringComparison.Ordinal);
        return lines;
    }

    /// <summary>The 1-based line and UTF-16 column of the first occurrence of <paramref name="marker"/> in the file.</summary>
    private static (int Line, int Column) Position(string root, string file, string marker)
    {
        var text = File.ReadAllText(Path.Combine(root, file));
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{marker}' is not in {file}");
        var lineStart = text.LastIndexOf('\n', Math.Max(index - 1, 0)) + 1;
        return (1 + text[..index].Count(c => c == '\n'), index - lineStart + 1);
    }

    /// <summary>The real command line, in its own process (it writes to the console).</summary>
    private static async Task<(int Code, string Stderr)> Cli(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "stratarec.dll"));
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        await stdout;
        return (process.ExitCode, await stderr);
    }
}
