using System.Text.RegularExpressions;
using StrataRec.Features.Runtime;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;
using StrataRec.Testing;

namespace StrataRec.IntegrationTests;

/// <summary>
/// The timing policy of the sample tests (<see cref="HomeFeedTestHost"/>): the untimed copy raises every budget and changes
/// nothing else, the relaxation refuses to guess at a budget it does not know, and a comparison of two live runs that
/// decided differently names the node.
/// </summary>
public sealed class SampleTimingTests
{
    [Fact]
    public void TheUntimedSample_RaisesEveryBudget_AndChangesNothingElse()
    {
        var asWritten = HomeFeedTestHost.SampleRoot(SampleTiming.AsWritten);
        var untimed = HomeFeedTestHost.SampleRoot();
        var budget = new Regex("(\"(?:timeout|deadline)_ms\"\\s*:\\s*)[0-9]+");
        foreach (var path in Directory.GetFiles(asWritten, "*", SearchOption.AllDirectories))
        {
            var file = Path.GetRelativePath(asWritten, path);
            var expected = file.StartsWith("fixtures", StringComparison.Ordinal)
                ? File.ReadAllText(path)
                : budget.Replace(File.ReadAllText(path), "${1}" + HomeFeedTestHost.UntimedMs);
            Assert.True(expected == File.ReadAllText(Path.Combine(untimed, file)), $"{file} differs from the sample by more than its budgets");
        }

        // The sample demonstrates a real budget and its fallback; the untimed copy keeps the fallback.
        Assert.Equal((50, "ctr_backup"), Ranker(HomeFeedTestHost.Load(SampleTiming.AsWritten).Plan()));
        var plan = HomeFeedTestHost.Load().Plan();
        Assert.Equal((HomeFeedTestHost.UntimedMs, "ctr_backup"), Ranker(plan));
        Assert.All(plan.Features.Groups.Where(g => g.Supply.Mode == SupplyMode.Fetch), g => Assert.Equal(HomeFeedTestHost.UntimedMs, g.Supply.TimeoutMs));
    }

    [Fact]
    public void ABudgetTheRelaxationDoesNotKnow_FailsIt_NamingTheFileAndTheSetting()
    {
        var host = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"order\": 520,", "\"order\": 520, \"min_remaining_ms\": 20,"),
            SampleTiming.AsWritten);

        var error = Assert.Throws<InvalidOperationException>(() => SampleBudgets.Relax(host.Root));

        Assert.Matches("^pipelines/home_feed.jsonc:[0-9]+ \"min_remaining_ms\" is a timing setting", error.Message);
    }

    [Fact]
    public void AFetchedSourceLeftWithTheFrameworksDefaultTimeout_FailsTheRelaxation()
    {
        // Without timeout_ms a fetched source gets 50 ms, and there is no number in the text to raise.
        var host = HomeFeedTestHost.LoadModified(root =>
            ReplayTests.Edit(root, "bindings/local.jsonc", "\"fixtures/users.json\", \"timeout_ms\": 1000", "\"fixtures/users.json\""), SampleTiming.AsWritten);

        var error = Assert.Throws<InvalidOperationException>(() => SampleBudgets.Relax(host.Root));

        Assert.Contains("feature source 'user_store': timeout 50 ms", error.Message);
    }

    [Fact]
    public async Task ComparingRunsThatDecidedDifferently_FailsNamingTheNodeAndWhy()
    {
        var executor = new PipelineExecutor();
        var served = await executor.ExecuteAsync(HomeFeedTestHost.Load().Plan(), HomeFeedTestHost.NewRequest());
        var fellBack = await executor.ExecuteAsync(HomeFeedTestHost.LoadModified(MetricsTests.FailModel).Plan(), HomeFeedTestHost.NewRequest());

        var error = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => LiveRunAssert.SameNodeOutcomes(served, fellBack));

        Assert.Contains("\n  ctr_ranker: Succeeded → Degraded by ctr_backup (", error.Message);
        Assert.Single(error.Message.Split('\n'), line => line.StartsWith("  ", StringComparison.Ordinal)); // only that node differs
        LiveRunAssert.SameNodeOutcomes(served, fellBack, except: "ctr_ranker");
    }

    private static (int? TimeoutMs, string? Fallback) Ranker(ExecutionPlan plan)
    {
        var ranker = plan.Timeline.Single(s => s.NodeId == "ctr_ranker").Select(RequestArms.None)!;
        return (ranker.TimeoutMs, ranker.FallbackNodeId);
    }
}
