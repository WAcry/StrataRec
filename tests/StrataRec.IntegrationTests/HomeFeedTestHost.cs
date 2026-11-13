using StrataRec.Artifacts.Model;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>Compiles and runs the home_feed sample workspace (or a modified copy of it).</summary>
internal sealed class HomeFeedTestHost
{
    private HomeFeedTestHost(string root, ConfigWorkspace workspace, PlanCompileRequest request)
    {
        Root = root;
        Workspace = workspace;
        Request = request;
        Environment = new PlanEnvironment { BaseDirectory = root };
    }

    public string Root { get; }

    public ConfigWorkspace Workspace { get; }

    public PlanCompileRequest Request { get; }

    public PlanEnvironment Environment { get; }

    public static HomeFeedTestHost Load(string? root = null)
    {
        root ??= Samples.HomeFeed;
        var diagnostics = new DiagnosticBag();
        var workspace = ConfigWorkspace.Load(root, diagnostics);
        var request = workspace.CreateCompileRequest("home_feed", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        return new HomeFeedTestHost(root, workspace, request!);
    }

    /// <summary>Copies the sample to a temp directory, lets the test edit files, and loads the copy.</summary>
    public static HomeFeedTestHost LoadModified(Action<string> edit)
    {
        var root = Directory.CreateTempSubdirectory("stratarec-home-feed-").FullName;
        CopyDirectory(Samples.HomeFeed, root);
        edit(root);
        return Load(root);
    }

    public PlanCompilation Compile() => PlanCompiler.Compile(Request, Environment);

    public ExecutionPlan Plan()
    {
        var compilation = Compile();
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        return compilation.Plan!;
    }

    public static PipelineRequest NewRequest(long? userId = 1001, string surface = "home", RequestArms arms = default, bool captureLogs = false) => new()
    {
        RequestId = "req-" + (userId?.ToString() ?? "anon"),
        Inputs = Inputs(userId, surface),
        Arms = arms,
        Deadline = TimeSpan.FromSeconds(5),
        TimeMs = 1_767_225_600_000,
        CaptureLogs = captureLogs,
    };

    public static RequestInputs Inputs(long? userId, string surface)
    {
        var inputs = new RequestInputs().Set("surface", surface).Set("session_id", "s-" + (userId?.ToString() ?? "anon"));
        return userId is { } id ? inputs.Set("user_id", id) : inputs;
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
