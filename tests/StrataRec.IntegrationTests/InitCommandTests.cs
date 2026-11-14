using System.Diagnostics;
using System.Text.Json;
using StrataRec.Cli.Commands;

namespace StrataRec.IntegrationTests;

/// <summary>
/// <c>stratarec init</c>: the workspace it writes validates without warnings, runs (in the arm the request's assignment
/// names) and explains; it refuses a directory that is not empty unless <c>--force</c>, which overwrites only its own
/// files; and the next commands it prints work when typed as printed.
/// </summary>
public sealed class InitCommandTests
{
    private const string Experiment = InitTemplates.ExperimentId;
    private const string Boost = Experiment + "/" + InitTemplates.AssignedArm;
    private const string Control = Experiment + "/" + InitTemplates.ControlArm;

    /// <summary>The weights of the final score: the pipeline's, and the one the assigned arm sets.</summary>
    private const double BaselineWeight = 0.05;
    private const double BoostWeight = 0.2;

    [Theory]
    [InlineData(InitTemplates.DefaultPipeline)]
    [InlineData("home_feed")]
    public async Task Init_WritesAWorkspaceThatValidatesWithoutWarnings_RunsInTheAssignedArm_AndExplains(string pipeline)
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "ws");

        Assert.Equal(0, Init(root, pipeline, force: false, out var printed, out var errors));
        Assert.Empty(errors);
        foreach (var file in InitTemplates.Files(pipeline))
        {
            Assert.Contains(file.Path.Replace('/', Path.DirectorySeparatorChar), printed);
            Assert.StartsWith("// ", File.ReadAllText(Path.Combine(root, file.Path)));
        }

        Assert.True(File.Exists(Path.Combine(root, "pipelines", pipeline + ".jsonc")));
        Assert.True(File.Exists(Path.Combine(root, "releases", pipeline + ".local.jsonc")));

        var validation = new StringWriter();
        Assert.Equal(0, ValidateCommand.Run(root, "local", null, "json", warningsAsErrors: true, validation));
        using (var report = JsonDocument.Parse(validation.ToString()))
        {
            Assert.Equal(0, report.RootElement.GetProperty("errors").GetInt32());
            Assert.Equal(0, report.RootElement.GetProperty("warnings").GetInt32());
        }

        Untimed(root);
        var request = Path.Combine(root, InitTemplates.RequestPath);
        var assigned = await Run(root, request, Array.Empty<string>());
        var control = await Run(root, request, new[] { Control });

        Assert.Equal(new[] { Boost }, assigned.Arms);
        Assert.Equal(new[] { Control }, control.Arms);
        Assert.Equal(10, assigned.Items.Count); // top_k of the 12 recalled
        Assert.All(assigned.Items, item => AssertScore(item, BoostWeight));
        Assert.All(control.Items, item => AssertScore(item, BaselineWeight));
        Assert.NotEqual(control.Items.Select(i => i.Id), assigned.Items.Select(i => i.Id)); // the weight reorders the feed

        var plan = new StringWriter();
        var planErrors = new StringWriter();
        Assert.True(0 == await ExplainPlanCommand.RunAsync(root, "local", null, "text", Array.Empty<string>(), plan, CancellationToken.None, errors: planErrors),
            planErrors.ToString());
        Assert.StartsWith($"pipeline {pipeline} ", plan.ToString());
        Assert.Contains($"{Boost} → {BoostWeight}", plan.ToString());
    }

    [Fact]
    public void Init_RefusesADirectoryThatIsNotEmpty_NamingWhatIsInTheWay_AndWritesNothing()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "notes.txt"), "mine");

        Assert.Equal(2, Init(temp.Path, InitTemplates.DefaultPipeline, force: false, out var printed, out var errors));
        Assert.Empty(printed);
        Assert.Contains("INIT_DIRECTORY_NOT_EMPTY", errors);
        Assert.Contains("'notes.txt' is in the way", errors);
        Assert.Contains("--force", errors);
        Assert.Equal(new[] { "notes.txt" }, Directory.GetFileSystemEntries(temp.Path).Select(Path.GetFileName));

        // A workspace already there: the file in the way is one init would overwrite.
        var workspace = Path.Combine(temp.Path, "ws");
        Assert.Equal(0, Init(workspace, InitTemplates.DefaultPipeline, force: false, out _, out _));
        Assert.Equal(2, Init(workspace, InitTemplates.DefaultPipeline, force: false, out _, out errors));
        Assert.Contains("'policy.jsonc' is in the way", errors);
        Assert.Contains($"overwrite the {InitTemplates.Files(InitTemplates.DefaultPipeline).Length} files", errors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1feed")]
    [InlineData("feed/x")]
    [InlineData("../feed")]
    [InlineData("feed x")]
    public void Init_RejectsAPipelineIdThatIsNotAnId(string pipeline)
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "ws");

        Assert.Equal(2, Init(root, pipeline, force: false, out _, out var errors));
        Assert.Contains("INIT_PIPELINE_ID", errors);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Init_IntoAFile_IsAUsageError()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "ws");
        File.WriteAllText(file, "mine");

        Assert.Equal(2, Init(file, InitTemplates.DefaultPipeline, force: false, out _, out var errors));
        Assert.Contains("INIT_NOT_A_DIRECTORY", errors);
        Assert.Equal("mine", File.ReadAllText(file));
    }

    [Fact]
    public void InitWithForce_OverwritesOnlyItsOwnFiles_DeletesNothing_AndNamesItsPipelineWhenThereAreOthers()
    {
        using var temp = new TempDirectory();
        var root = temp.Path;
        Assert.Equal(0, Init(root, "other", force: false, out _, out _));
        File.WriteAllText(Path.Combine(root, "notes.txt"), "mine");
        File.WriteAllText(Path.Combine(root, "fixtures", "extra.json"), "{}");
        File.WriteAllText(Path.Combine(root, "policy.jsonc"), "edited");

        Assert.Equal(0, Init(root, InitTemplates.DefaultPipeline, force: true, out var printed, out var errors));

        Assert.Empty(errors);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(root, "notes.txt")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(root, "fixtures", "extra.json")));
        Assert.True(File.Exists(Path.Combine(root, "pipelines", "other.jsonc")));
        Assert.True(File.Exists(Path.Combine(root, "releases", "other.local.jsonc")));
        foreach (var file in InitTemplates.Files(InitTemplates.DefaultPipeline))
        {
            Assert.Equal(file.Content, File.ReadAllText(Path.Combine(root, file.Path)));
        }

        var lines = printed.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Contains(lines, l => l.Contains("policy.jsonc", StringComparison.Ordinal) && l.EndsWith("(overwritten)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains($"pipelines{Path.DirectorySeparatorChar}feed.jsonc", StringComparison.Ordinal) && !l.Contains("overwritten", StringComparison.Ordinal));

        // Two pipelines now: validate and explain-plan are told which one.
        Assert.Contains(lines, l => l.Trim() == $"stratarec validate -c {root} -p feed");
        Assert.Contains(lines, l => l.Trim() == $"stratarec explain-plan -c {root} -p feed");
        Assert.Equal(0, ValidateCommand.Run(root, "local", "feed", "text", warningsAsErrors: true, TextWriter.Null));
    }

    /// <summary>
    /// The real command line, in its own processes, as a user types it: init, then every command it printed, from the
    /// same directory. The workspace is a relative directory, or the current one.
    /// </summary>
    [Theory]
    [InlineData("my-feed")]
    [InlineData(null)]
    public async Task PrintedNextCommands_WorkAsPrinted(string? directory)
    {
        using var temp = new TempDirectory();
        var init = await Cli(temp.Path, directory is null ? new[] { "init" } : new[] { "init", directory });
        Assert.True(init.Code == 0, init.Stderr);

        var commands = new List<string>();
        foreach (var line in init.Stdout.Split('\n'))
        {
            var command = line.Trim();
            if (command.StartsWith("stratarec ", StringComparison.Ordinal))
            {
                commands.Add(command);
            }
        }

        Assert.Equal(4, commands.Count);
        Assert.Contains(commands, c => c.Contains("--trace", StringComparison.Ordinal));
        Assert.Contains(commands, c => c.Contains($"--arms {Control}", StringComparison.Ordinal));
        foreach (var command in commands)
        {
            Assert.DoesNotContain("\"", command); // no quoting needed: split on spaces as a shell would
            var result = await Cli(temp.Path, command["stratarec ".Length..].Split(' '));
            Assert.True(result.Code == 0, $"{command}: exit {result.Code}\n{result.Stderr}\n{result.Stdout}");
            if (command.StartsWith("stratarec run ", StringComparison.Ordinal))
            {
                using var output = JsonDocument.Parse(result.Stdout);
                Assert.True(output.RootElement.GetProperty("succeeded").GetBoolean());
                Assert.NotEmpty(output.RootElement.GetProperty("items").EnumerateArray());
                var expected = command.Contains("--arms", StringComparison.Ordinal) ? Control : Boost;
                Assert.Equal(expected, Assert.Single(output.RootElement.GetProperty("experiments").GetProperty("assigned").EnumerateArray()).GetString());
            }
        }
    }

    private static int Init(string directory, string pipeline, bool force, out string printed, out string errors)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = InitCommand.Run(directory, pipeline, force, output, error);
        printed = output.ToString();
        errors = error.ToString();
        return code;
    }

    /// <summary>
    /// The final score is the model's p_click plus the weight times the freshness term, an item of unknown age counting
    /// as a week old: the score of every item says which weight the request ran with.
    /// </summary>
    private static void AssertScore(Item item, double weight)
    {
        var freshness = 1.0 / (1.0 + ((item.AgeHours ?? 168.0) / 24.0));
        Assert.Equal(item.PClick + (weight * freshness), item.Score, 4);
    }

    /// <summary>
    /// Raises the generated fixture timeout, so that a busy machine running the tests in parallel cannot turn a fixture
    /// read into a timeout (as the sample tests do with <see cref="Testing.SampleBudgets"/>); runs use an untimed deadline.
    /// </summary>
    private static void Untimed(string root)
    {
        var bindings = Path.Combine(root, "bindings", "local.jsonc");
        var text = File.ReadAllText(bindings);
        Assert.Contains("\"timeout_ms\": 1000", text);
        File.WriteAllText(bindings, text.Replace("\"timeout_ms\": 1000", $"\"timeout_ms\": {HomeFeedTestHost.UntimedMs}", StringComparison.Ordinal));
    }

    private static async Task<(string[] Arms, List<Item> Items)> Run(string root, string request, string[] arms)
    {
        var output = new StringWriter();
        Assert.Equal(0, await RunCommand.RunAsync(root, "local", request, arms, trace: true, output, CancellationToken.None, deadlineMs: HomeFeedTestHost.UntimedMs));
        using var result = JsonDocument.Parse(output.ToString());
        var json = result.RootElement;
        Assert.All(json.GetProperty("trace").EnumerateArray(), node => Assert.Equal("succeeded", node.GetProperty("outcome").GetString()));
        var items = json.GetProperty("items").EnumerateArray()
            .Select(i => new Item(
                i.GetProperty("id").GetString()!,
                i.GetProperty("score").GetDouble(),
                i.GetProperty("p_click").GetDouble(),
                i.GetProperty("age_hours").ValueKind == JsonValueKind.Null ? null : i.GetProperty("age_hours").GetDouble()))
            .ToList();
        var assigned = json.GetProperty("experiments").GetProperty("assigned").EnumerateArray().Select(a => a.GetString()!).ToArray();
        return (assigned, items);
    }

    private static async Task<(int Code, string Stdout, string Stderr)> Cli(string workingDirectory, string[] arguments)
    {
        var start = new ProcessStartInfo(System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = workingDirectory,
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
        return (process.ExitCode, await stdout, await stderr);
    }

    private sealed record Item(string Id, double Score, double PClick, double? AgeHours);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory() => Path = Directory.CreateTempSubdirectory("stratarec-init-").FullName;

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
