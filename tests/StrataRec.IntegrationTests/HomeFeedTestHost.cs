using StrataRec.Artifacts.Model;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.LocalTables;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;
using StrataRec.Testing;

namespace StrataRec.IntegrationTests;

/// <summary>Which budgets a test of the home_feed sample runs under (see <see cref="HomeFeedTestHost"/>).</summary>
internal enum SampleTiming
{
    /// <summary>
    /// The default: a copy of the sample whose every budget is <see cref="HomeFeedTestHost.UntimedMs"/>
    /// (<see cref="SampleBudgets.Relax"/>), so that no machine is slow enough to hit one and two runs of a request take
    /// the same decisions. The fallback wiring is as written.
    /// </summary>
    Untimed,

    /// <summary>
    /// The sample's budgets as written (ctr_ranker's 50 ms, the request files' 200 ms): for tests of those budgets, which
    /// decide when a client answers instead of racing the clock, and for tests of the sample's own files.
    /// </summary>
    AsWritten,
}

/// <summary>Compiles and runs the home_feed sample workspace (or a modified copy of it).</summary>
/// <remarks>
/// <para>The timing policy of every test of the sample lives here. The sample keeps a realistic budget: ctr_ranker gives
/// its model 50 ms and falls back to ctr_backup. A cold call takes about 20 ms on a dev box and can take more than 50 ms
/// on a busy CI runner, and an answer after the budget is a timeout however it arrived (design 4.8), so a test comparing
/// two live runs would sometimes compare the backup's scores with the model's. Tests therefore run on an untimed copy
/// (<see cref="SampleTiming.Untimed"/>) and ask for <see cref="SampleTiming.AsWritten"/> when the budgets are what they
/// test. A budget a test writes into its own edits is the test's.</para>
/// <para>Requests built here, and by the other request helpers of the sample tests, use <see cref="Deadline"/>.</para>
/// </remarks>
internal sealed class HomeFeedTestHost
{
    /// <inheritdoc cref="SampleBudgets.UntimedMs"/>
    public const int UntimedMs = SampleBudgets.UntimedMs;

    private static readonly Lazy<string> UntimedSample = new(CreateUntimedSample);

    private HomeFeedTestHost(string root, ConfigWorkspace workspace, PlanCompileRequest request)
    {
        Root = root;
        Workspace = workspace;
        Request = request;
        Environment = new PlanEnvironment { BaseDirectory = root };
    }

    /// <summary>The deadline of an untimed request (<see cref="UntimedMs"/>).</summary>
    public static TimeSpan Deadline { get; } = TimeSpan.FromMilliseconds(UntimedMs);

    public string Root { get; }

    public ConfigWorkspace Workspace { get; }

    public PlanCompileRequest Request { get; }

    public PlanEnvironment Environment { get; }

    /// <summary>
    /// The local tables of the plans this host compiles (the sample binds <c>author_store</c> locally). Plans compiled by
    /// <see cref="Compile"/> keep their tables loaded for the life of the test.
    /// </summary>
    public LocalTableHost Tables { get; } = new();

    /// <summary>
    /// The sample's directory: the untimed copy (made once per test run and shared, so never edit it) or the sample as
    /// written. Edit a copy with <see cref="LoadModified"/>.
    /// </summary>
    public static string SampleRoot(SampleTiming timing = SampleTiming.Untimed) => timing switch
    {
        SampleTiming.Untimed => UntimedSample.Value,
        SampleTiming.AsWritten => Path.Combine(AppContext.BaseDirectory, "samples", "home_feed"),
    };

    public static HomeFeedTestHost Load(SampleTiming timing = SampleTiming.Untimed) => Open(SampleRoot(timing));

    /// <summary>Copies the sample to a temp directory, lets the test edit files, and loads the copy.</summary>
    public static HomeFeedTestHost LoadModified(Action<string> edit, SampleTiming timing = SampleTiming.Untimed)
    {
        var root = Directory.CreateTempSubdirectory("stratarec-home-feed-").FullName;
        CopyDirectory(SampleRoot(timing), root);
        edit(root);
        return Open(root);
    }

    /// <summary>Compiles the plan and loads its local tables, as the engine does before serving it.</summary>
    public PlanCompilation Compile()
    {
        var lease = Tables.OpenLease();
        var compilation = PlanCompiler.Compile(Request, Environment, lease);
        var failures = lease.WaitLoadedAsync().GetAwaiter().GetResult();
        Assert.True(failures.Count == 0, string.Join("\n", failures.Select(f => f.Message)));
        return compilation;
    }

    /// <summary><see cref="PlanCompiler.CompileMerged"/> with the local tables loaded, like <see cref="Compile"/>.</summary>
    public PlanCompilation CompileMerged(IReadOnlyList<string> arms)
    {
        var lease = Tables.OpenLease();
        var compilation = PlanCompiler.CompileMerged(Request, arms, Environment, lease);
        var failures = lease.WaitLoadedAsync().GetAwaiter().GetResult();
        Assert.True(failures.Count == 0, string.Join("\n", failures.Select(f => f.Message)));
        return compilation;
    }

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
        Deadline = Deadline,
        TimeMs = 1_767_225_600_000,
        CaptureLogs = captureLogs,
    };

    public static RequestInputs Inputs(long? userId, string surface)
    {
        var inputs = new RequestInputs().Set("surface", surface).Set("session_id", "s-" + (userId?.ToString() ?? "anon"));
        return userId is { } id ? inputs.Set("user_id", id) : inputs;
    }

    private static string CreateUntimedSample()
    {
        var root = Directory.CreateTempSubdirectory("stratarec-home-feed-untimed-").FullName;
        CopyDirectory(SampleRoot(SampleTiming.AsWritten), root);
        SampleBudgets.Relax(root);
        return root;
    }

    private static HomeFeedTestHost Open(string root)
    {
        var diagnostics = new DiagnosticBag();
        var workspace = ConfigWorkspace.Load(root, diagnostics);
        var request = workspace.CreateCompileRequest("home_feed", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        return new HomeFeedTestHost(root, workspace, request!);
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
