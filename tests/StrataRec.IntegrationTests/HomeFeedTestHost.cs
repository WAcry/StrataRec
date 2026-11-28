using StrataRec.Artifacts.Model;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Bindings;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;
using StrataRec.Testing;

namespace StrataRec.IntegrationTests;

internal enum SampleTiming
{
    Untimed,

    AsWritten,
}

/// <remarks>
/// <para>Tests run on an untimed copy (<see cref="SampleTiming.Untimed"/>) and ask for
/// <see cref="SampleTiming.AsWritten"/> when the budgets are what they test. A budget a test writes into its own edits
/// is the test's.</para>
/// </remarks>
internal sealed class HomeFeedTestHost
{
    public const int UntimedMs = SampleBudgets.UntimedMs;

    private static readonly Lazy<string> UntimedSample = new(CreateUntimedSample);

    private HomeFeedTestHost(string root, ConfigWorkspace workspace, PlanCompileRequest request)
    {
        Root = root;
        Workspace = workspace;
        Request = request;
        Environment = new PlanEnvironment { BaseDirectory = root };
    }

    public static TimeSpan Deadline { get; } = TimeSpan.FromMilliseconds(UntimedMs);

    public string Root { get; }

    public ConfigWorkspace Workspace { get; }

    public PlanCompileRequest Request { get; }

    public PlanEnvironment Environment { get; }

    /// <summary>
    /// Plans compiled by <see cref="Compile()"/> keep their tables and clients loaded for the life of the test.
    /// </summary>
    public BindingHost Bindings { get; } = new();

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

    public static HomeFeedTestHost LoadModified(Action<string> edit, SampleTiming timing = SampleTiming.Untimed)
    {
        var root = Directory.CreateTempSubdirectory("stratarec-home-feed-").FullName;
        CopyDirectory(SampleRoot(timing), root);
        edit(root);
        return Open(root);
    }

    public PlanCompilation Compile() => Compile(Request);

    public PlanCompilation Compile(PlanCompileRequest request)
    {
        var lease = Bindings.OpenLease();
        var compilation = lease.CompileAsync(bindings => PlanCompiler.Compile(request, Environment, bindings)).GetAwaiter().GetResult();
        var failures = lease.WaitLoadedAsync().GetAwaiter().GetResult();
        Assert.True(failures.Count == 0, string.Join("\n", failures.Select(f => f.Message)));
        return compilation;
    }

    public PlanCompilation CompileMerged(IReadOnlyList<string> arms)
    {
        var lease = Bindings.OpenLease();
        var compilation = lease.CompileAsync(bindings => PlanCompiler.CompileMerged(Request, arms, Environment, bindings)).GetAwaiter().GetResult();
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

    public static PipelineRequest NewRequest(
        long? userId = 1001, string surface = "home", RequestArms arms = default, bool captureLogs = false, bool? writeLogs = null) => new()
    {
        RequestId = "req-" + (userId?.ToString() ?? "anon"),
        Inputs = Inputs(userId, surface),
        Arms = arms,
        Deadline = Deadline,
        TimeMs = 1_767_225_600_000,
        CaptureLogs = captureLogs,
        WriteLogs = writeLogs ?? !captureLogs, // as the engine runs traffic: an executor with a log pipeline writes to it
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
