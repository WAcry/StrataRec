using StrataRec.Experiments.Assignment;
using StrataRec.Hosting;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// Where a request's arms come from (design 11.1): forced arms, then the platform's answer the caller passed, then the
/// assignment provider, otherwise none. A pending provider and the deadline are covered by <see cref="Review4RegressionTests"/>.
/// </summary>
public sealed class AssignmentProviderTests
{
    private static readonly string[] ProviderArms = { "exp_quality_weight/strong", "exp_mmr_diversity/mmr" };

    private sealed class RecordingProvider(IReadOnlyList<string> arms) : IAssignmentProvider
    {
        public List<AssignmentContext> Calls { get; } = new();

        public ValueTask<IReadOnlyList<string>> AssignAsync(AssignmentContext context, CancellationToken cancellationToken)
        {
            Calls.Add(context);
            return ValueTask.FromResult(arms);
        }
    }

    [Fact]
    public async Task TheProviderIsToldTheRequestAndTheServedPlansExperimentsReadOnly()
    {
        var host = HomeFeedTestHost.Load();
        var provider = new RecordingProvider(ProviderArms);
        var engine = new RecEngine(host.Environment, provider);
        Assert.True(engine.Load(host.Request).Applied);
        engine.TryGetPipeline("home_feed", out var served);

        var result = await engine.ExecuteAsync(Request("req-ctx"));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(ProviderArms.Order(), result.Experiments.Assigned.Order());
        var context = Assert.Single(provider.Calls);
        Assert.Equal("home_feed", context.PipelineId);
        Assert.Equal("req-ctx", context.RequestId);
        Assert.Equal(1001L, context.Inputs["user_id"].AsInt64());
        Assert.Equal(served.Plan.Layers.Select(l => l.Name), context.Layers.Select(l => l.Name));
        Assert.Equal(served.Plan.Experiments.Select(e => e.Id), context.Experiments.Select(e => e.Id));
        Assert.Equal(served.Plan.Arms.Select(a => a.Id), context.Arms.Select(a => a.Id));

        // Views of the plan's arrays, not the arrays: a provider cannot change what the plan serves.
        Assert.False(context.Layers is LayerInfo[]);
        Assert.True(((ICollection<LayerInfo>)context.Layers).IsReadOnly);
        Assert.Throws<NotSupportedException>(() => ((IList<ArmInfo>)context.Arms)[0] = context.Arms[1]);

        // Built once per plan: every request of the plan passes the same views.
        await engine.ExecuteAsync(Request("req-ctx-2"));
        Assert.Same(provider.Calls[0].Arms, provider.Calls[1].Arms);
    }

    [Fact]
    public async Task ForcedArmsWinOverTheCallersAssignmentWhichWinsOverTheProvider()
    {
        var host = HomeFeedTestHost.Load();
        var provider = new RecordingProvider(ProviderArms);
        var engine = new RecEngine(host.Environment, provider);
        Assert.True(engine.Load(host.Request).Applied);
        string[] platform = { "exp_freshness_calibration/calibrated" };
        string[] forced = { "exp_quality_weight/control" };

        var fromProvider = await engine.ExecuteAsync(Request("req-provider"));
        var fromCaller = await engine.ExecuteAsync(Request("req-caller", assignment: platform));
        var fromNothing = await engine.ExecuteAsync(Request("req-empty", assignment: Array.Empty<string>()));
        var fromForce = await engine.ExecuteAsync(Request("req-forced", assignment: platform, force: forced));

        Assert.Equal(ProviderArms.Order(), fromProvider.Experiments.Assigned.Order());
        Assert.Equal("provider", fromProvider.Recording!.Request.Assignment.Source);
        Assert.Equal(platform, fromCaller.Experiments.Assigned);
        Assert.Equal("external", fromCaller.Recording!.Request.Assignment.Source);
        Assert.Empty(fromNothing.Experiments.Assigned); // the platform answered "no arms": not a reason to ask again
        Assert.Equal("external", fromNothing.Recording!.Request.Assignment.Source);
        Assert.Equal(forced, fromForce.Experiments.Assigned);
        Assert.Equal("forced", fromForce.Recording!.Request.Assignment.Source);
        Assert.Single(provider.Calls); // asked only when nothing else decided
    }

    [Fact]
    public async Task WithoutAnAnswerOrAProviderTheRequestRunsTheBaseline()
    {
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment);
        Assert.True(engine.Load(host.Request).Applied);

        var result = await engine.ExecuteAsync(Request("req-none"));

        Assert.True(result.Succeeded, result.Error);
        Assert.Empty(result.Experiments.Assigned);
        Assert.Empty(result.Experiments.Triggered);
        Assert.Equal("none", result.Recording!.Request.Assignment.Source);
    }

    /// <summary>Does its work before returning, then answers synchronously: the part of a provider the engine cannot bound.</summary>
    private sealed class BlockingProvider(Action<CancellationToken> work) : IAssignmentProvider
    {
        public ValueTask<IReadOnlyList<string>> AssignAsync(AssignmentContext context, CancellationToken cancellationToken)
        {
            work(cancellationToken);
            return ValueTask.FromResult<IReadOnlyList<string>>(ProviderArms);
        }
    }

    [Fact]
    public async Task AProviderAnsweringSynchronouslyAfterTheCallerCancelled_CancelsTheRequest()
    {
        // Review 18 #2: an answer the engine observes complete is judged when it is observed, as BoundedCall judges a
        // client's: the caller's cancellation comes first, so the request ends there and no pipeline runs on the answer
        // (a run would deliver its aborted bundle to OnRecording).
        using var caller = new CancellationTokenSource();
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment, new BlockingProvider(_ => caller.Cancel()));
        Assert.True(engine.Load(host.Request).Applied);
        var bundles = 0;
        var request = new RecRequest
        {
            PipelineId = "home_feed",
            RequestId = "req-cancelled",
            Inputs = HomeFeedTestHost.Inputs(1001, "home"),
            Deadline = HomeFeedTestHost.Deadline,
            Debug = new DebugOptions { OnRecording = _ => Interlocked.Increment(ref bundles) },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await engine.ExecuteAsync(request, caller.Token));
        Assert.Equal(0, bundles);
    }

    [Fact]
    public async Task AProviderAnsweringSynchronouslyPastTheDeadline_IsRejectedAsPastIt()
    {
        // Review 18 #2: the provider blocks the request's thread until the deadline has cancelled its token, then answers.
        // The engine cannot cut that short, but it does not take the late answer.
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment, new BlockingProvider(token => Assert.True(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30)))));
        Assert.True(engine.Load(host.Request).Applied);

        var result = await engine.ExecuteAsync(new RecRequest
        {
            PipelineId = "home_feed",
            RequestId = "req-late",
            Inputs = HomeFeedTestHost.Inputs(1001, "home"),
            Deadline = TimeSpan.FromMilliseconds(50),
        });

        Assert.False(result.Succeeded);
        Assert.Equal("deadline exceeded during assignment", result.Error);
        Assert.Empty(result.Experiments.Assigned); // rejected before the arms were decided
        Assert.Empty(result.Experiments.Ignored);
    }

    private static RecRequest Request(string requestId, IReadOnlyList<string>? assignment = null, IReadOnlyList<string>? force = null) => new()
    {
        PipelineId = "home_feed",
        RequestId = requestId,
        Inputs = HomeFeedTestHost.Inputs(1001, "home"),
        Assignment = assignment,
        Deadline = HomeFeedTestHost.Deadline,
        Debug = new DebugOptions { Record = true, ForceArms = force },
    };
}
