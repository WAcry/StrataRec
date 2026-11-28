using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.CommandLine.Commands;

namespace StrataRec.IntegrationTests;

public sealed class InitCommandTests
{
    private const string Experiment = InitTemplates.ExperimentId;
    private const string Boost = Experiment + "/" + InitTemplates.AssignedArm;
    private const string Control = Experiment + "/" + InitTemplates.ControlArm;

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

        Assert.Contains(printed.Split('\n'), line => line.TrimStart().StartsWith(Path.Combine(root, "schemas") + Path.DirectorySeparatorChar + " ", StringComparison.Ordinal)
                                                     && line.Contains("the JSON Schema of each kind of file above", StringComparison.Ordinal));
        foreach (var schema in SchemaFiles.Generate(Tool.Environment))
        {
            Assert.Equal(schema.Text, File.ReadAllText(Path.Combine(root, "schemas", schema.Name)));
        }

        foreach (var file in InitTemplates.Files(pipeline))
        {
            var document = Assert.IsType<JsonObject>(Jsonc.Parse(File.ReadAllText(Path.Combine(root, file.Path))));
            if (file.Path.StartsWith("fixtures/", StringComparison.Ordinal))
            {
                Assert.False(document.ContainsKey("$schema"), file.Path); // a connector's data: "$schema" would be an item id
                continue;
            }

            Assert.Equal("$schema", document.First().Key);
            Assert.Equal(ArtifactSchemas.Reference(file.Path, EditorSchemaTests.KindOf(file.Path)), document["$schema"]!.GetValue<string>());
        }

        Assert.True(File.Exists(Path.Combine(root, "pipelines", pipeline + ".jsonc")));
        Assert.True(File.Exists(Path.Combine(root, "releases", pipeline + ".local.jsonc")));

        var validation = new StringWriter();
        Assert.Equal(0, ValidateCommand.Run(Tool.Environment, root, "local", null, "json", warningsAsErrors: true, validation));
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
        Assert.Equal(10, assigned.Items.Count);
        Assert.All(assigned.Items, item => AssertScore(item, BoostWeight));
        Assert.All(control.Items, item => AssertScore(item, BaselineWeight));
        Assert.NotEqual(control.Items.Select(i => i.Id), assigned.Items.Select(i => i.Id));

        var plan = new StringWriter();
        var planErrors = new StringWriter();
        Assert.True(0 == await ExplainPlanCommand.RunAsync(Tool.Environment, root, "local", null, "text", Array.Empty<string>(), plan, CancellationToken.None, errors: planErrors),
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

        var workspace = Path.Combine(temp.Path, "ws");
        Assert.Equal(0, Init(workspace, InitTemplates.DefaultPipeline, force: false, out _, out _));
        Assert.Equal(2, Init(workspace, InitTemplates.DefaultPipeline, force: false, out _, out errors));
        Assert.Contains($"'{Path.Combine("catalog", "feed.jsonc")}' is in the way", errors);
        Assert.Contains($"overwrite the {InitCommand.Files(Tool.Environment, InitTemplates.DefaultPipeline).Length} files", errors);
    }

    [Fact]
    public void Init_IntoARepositoryWithOnlyHiddenEntries_WritesTheWorkspace_AndLeavesThemAlone()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, ".git"));
        File.WriteAllText(Path.Combine(temp.Path, ".gitignore"), "bin/\n");

        Assert.Equal(0, Init(temp.Path, InitTemplates.DefaultPipeline, force: false, out _, out var errors));
        Assert.Empty(errors);
        Assert.True(File.Exists(Path.Combine(temp.Path, "catalog", "feed.jsonc")));
        Assert.Equal("bin/\n", File.ReadAllText(Path.Combine(temp.Path, ".gitignore")));
        Assert.True(Directory.Exists(Path.Combine(temp.Path, ".git")));

        using var other = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(other.Path, ".git"));
        File.WriteAllText(Path.Combine(other.Path, "notes.txt"), "mine");
        Assert.Equal(2, Init(other.Path, InitTemplates.DefaultPipeline, force: false, out _, out errors));
        Assert.Contains("'notes.txt' is in the way", errors);
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
        File.WriteAllText(Path.Combine(root, "bindings", "local.jsonc"), "edited");

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
        Assert.Contains(lines, l => l.Contains($"bindings{Path.DirectorySeparatorChar}local.jsonc", StringComparison.Ordinal) && l.EndsWith("(overwritten)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains($"pipelines{Path.DirectorySeparatorChar}feed.jsonc", StringComparison.Ordinal) && !l.Contains("overwritten", StringComparison.Ordinal));

        Assert.Contains(lines, l => l.Trim() == $"stratarec validate -c {root} -p feed");
        Assert.Contains(lines, l => l.Trim() == $"stratarec explain-plan -c {root} -p feed");
        Assert.Equal(0, ValidateCommand.Run(Tool.Environment, root, "local", "feed", "text", warningsAsErrors: true, TextWriter.Null));
    }

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

        Assert.Equal(5, commands.Count);
        Assert.Contains(commands, c => c.Contains("--trace", StringComparison.Ordinal));
        Assert.Contains(commands, c => c.Contains($"--arms {Control}", StringComparison.Ordinal));
        foreach (var command in commands)
        {
            Assert.DoesNotContain("\"", command);
            var result = await Cli(temp.Path, command["stratarec ".Length..].Split(' '));
            Assert.True(result.Code == 0, $"{command}: exit {result.Code}\n{result.Stderr}\n{result.Stdout}");
            if (command.StartsWith("stratarec run ", StringComparison.Ordinal))
            {
                var lines = result.Stdout.Split('\n');
                Assert.StartsWith("request example: succeeded, served by feed, ", lines[0]);
                Assert.Contains(lines, l => l.StartsWith("  items      1. ", StringComparison.Ordinal));
                var expected = command.Contains("--arms", StringComparison.Ordinal) ? Control : Boost;
                Assert.Contains(lines, l => l.StartsWith($"  arms      {expected}", StringComparison.Ordinal));
                if (command.Contains("--explain", StringComparison.Ordinal))
                {
                    Assert.Contains($"  explain   {InitTemplates.CutItem}: dropped by rank", lines);
                }
            }
        }
    }

    [Theory]
    [InlineData("plain/path-1.2")]
    [InlineData("my feed")]
    [InlineData("feed$USER")]
    [InlineData("back\\slash")]
    [InlineData("quote\"name")]
    [InlineData("it's")]
    [InlineData("`date`")]
    [InlineData("a*b?[c]")]
    [InlineData("")]
    public async Task Quote_IsReadBackAsWritten_ByAPosixShell(string argument)
    {
        var (code, stdout, stderr) = await Bash(Path.GetTempPath(), $"printf '%s' {InitCommand.Quote(argument)}");

        Assert.True(code == 0, stderr);
        Assert.Equal(argument, stdout);
    }

    [Fact]
    public async Task PrintedNextCommands_WorkInBash_ForADirectoryNameAShellWouldExpand()
    {
        using var temp = new TempDirectory();
        const string directory = "my feed $USER `x` 'q' \"d\" b\\s";
        var init = await Cli(temp.Path, new[] { "init", directory });
        Assert.True(init.Code == 0, init.Stderr);

        var commands = new List<string>();
        foreach (var line in init.Stdout.Split('\n'))
        {
            if (line.Trim() is var command && command.StartsWith("stratarec ", StringComparison.Ordinal))
            {
                commands.Add(command);
            }
        }

        Assert.Equal(5, commands.Count);
        foreach (var command in commands)
        {
            var result = await Bash(temp.Path, command);
            Assert.True(result.Code == 0, $"{command}: exit {result.Code}\n{result.Stderr}\n{result.Stdout}");
        }

        Assert.Equal(new[] { directory }, Directory.GetDirectories(temp.Path).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("bindings/local.jsonc", "file")]
    [InlineData("fixtures", "directory")]
    [InlineData("catalog", "nowhere")]
    public void Force_NeverWritesThroughASymbolicLink_AndWritesNothing(string link, string target)
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "ws");
        Directory.CreateDirectory(root);
        var outsideFile = Path.Combine(temp.Path, "outside.txt");
        var outsideDirectory = Path.Combine(temp.Path, "outside-dir");
        File.WriteAllText(outsideFile, "do not change");
        Directory.CreateDirectory(outsideDirectory);
        var pointsAt = target switch { "file" => outsideFile, "directory" => outsideDirectory, _ => Path.Combine(temp.Path, "nowhere") };
        var top = link.Split('/')[0];
        if (top != link)
        {
            Directory.CreateDirectory(Path.Combine(root, top));
        }

        if (target == "directory")
        {
            Directory.CreateSymbolicLink(Path.Combine(root, link), pointsAt);
        }
        else
        {
            File.CreateSymbolicLink(Path.Combine(root, link), pointsAt);
        }

        Assert.Equal(2, Init(root, InitTemplates.DefaultPipeline, force: true, out var printed, out var errors));

        Assert.Empty(printed);
        Assert.Contains("INIT_PATH_IS_LINK", errors);
        Assert.Contains($"'{link.Replace('/', Path.DirectorySeparatorChar)}' is a symbolic link", errors);
        Assert.Equal("do not change", File.ReadAllText(outsideFile));
        Assert.Empty(Directory.GetFileSystemEntries(outsideDirectory));
        Assert.False(File.Exists(Path.Combine(temp.Path, "nowhere")));
        Assert.Equal(new[] { top }, Directory.GetFileSystemEntries(root).Select(Path.GetFileName));
        if (top != link)
        {
            Assert.Single(Directory.GetFileSystemEntries(Path.Combine(root, top)));
        }
    }

    [Fact]
    public void AWorkspaceRootThatIsALink_IsWhereTheUserAskedToInit()
    {
        using var temp = new TempDirectory();
        var real = Directory.CreateDirectory(Path.Combine(temp.Path, "real")).FullName;
        var link = Path.Combine(temp.Path, "link");
        Directory.CreateSymbolicLink(link, real);

        Assert.Equal(0, Init(link, InitTemplates.DefaultPipeline, force: false, out _, out var errors));
        Assert.Empty(errors);
        Assert.True(File.Exists(Path.Combine(real, "catalog", "feed.jsonc")));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void ADirectoryItCannotRead_IsADiagnostic_NotAnUnhandledException()
    {
        using var temp = new TempDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "locked")).FullName;
        File.SetUnixFileMode(root, UnixFileMode.None);
        try
        {
            try
            {
                Directory.GetFileSystemEntries(root);
                return; // a user the permissions do not stop (root): nothing to test
            }
            catch (UnauthorizedAccessException)
            {
            }

            Assert.Equal(2, Init(root, InitTemplates.DefaultPipeline, force: false, out var printed, out var errors));
            Assert.Empty(printed);
            Assert.Contains("INIT_DIRECTORY_UNREADABLE", errors);
            Assert.Contains("hint:", errors);
        }
        finally
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static int Init(string directory, string pipeline, bool force, out string printed, out string errors)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = InitCommand.Run(Tool.Environment, directory, pipeline, force, output, error);
        printed = output.ToString();
        errors = error.ToString();
        return code;
    }

    private static void AssertScore(Item item, double weight)
    {
        var freshness = 1.0 / (1.0 + ((item.AgeHours ?? 168.0) / 24.0));
        Assert.Equal(item.PClick + (weight * freshness), item.Score, 4);
    }

    /// <summary>
    /// Raises the generated fixture timeout, so that a busy machine running the tests in parallel cannot turn a fixture
    /// read into a timeout.
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
        Assert.Equal(0, await RunCommand.RunAsync(Tool.Environment, root, "local", request, arms, trace: true, output, CancellationToken.None, deadlineMs: HomeFeedTestHost.UntimedMs));
        using var result = JsonDocument.Parse(output.ToString());
        var json = result.RootElement;
        Assert.All(json.GetProperty("trace").EnumerateArray(), node => Assert.Equal("succeeded", node.GetProperty("outcome").GetString()));
        var items = json.GetProperty("items").EnumerateArray()
            .Select(i => (Id: i.GetProperty("id"), Columns: i.GetProperty("columns")))
            .Select(i => new Item(
                i.Id.GetRawText(), // a number for an int64 entity id, a string otherwise
                i.Columns.GetProperty("score").GetDouble(),
                i.Columns.GetProperty("p_click").GetDouble(),
                i.Columns.GetProperty("age_hours").ValueKind == JsonValueKind.Null ? null : i.Columns.GetProperty("age_hours").GetDouble()))
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

    private static async Task<(int Code, string Stdout, string Stderr)> Bash(string workingDirectory, string commandLine)
    {
        var start = new ProcessStartInfo("bash")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["STRATAREC_DOTNET"] = System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        start.Environment["STRATAREC_DLL"] = Path.Combine(AppContext.BaseDirectory, "stratarec.dll");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("stratarec() { \"$STRATAREC_DOTNET\" \"$STRATAREC_DLL\" \"$@\"; }\n" + commandLine);
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
