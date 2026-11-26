using System.Text.Json.Nodes;
using StrataRec.Core.Keys;
using StrataRec.Hosting;
using StrataRec.Logging;

namespace StrataRec.IntegrationTests;

/// <summary>
/// Which requests reach the persistent logs (<see cref="DebugOptions"/>), on the home_feed
/// sample with a log sink: traffic does, also with the options that only look at a request (trace, explain, record); a
/// request whose arms or configuration a debugger forced, or that captures its logs, writes nothing to the sink. Forced
/// arms used to write their <c>$experiments</c> record and taps as if the platform had assigned them.
/// </summary>
public sealed class DebugLoggingTests
{
    private static readonly string[] Arms = { "exp_freshness_calibration/calibrated", "exp_mmr_diversity/mmr", "exp_quality_weight/strong" };

    [Fact]
    public async Task ANaturallyAssignedRequest_Logs_AlsoWithTraceExplainAndRecord()
    {
        var sink = new InMemoryLogSink();
        await using (var logs = new LogPipeline(sink))
        await using (var engine = Load(logs))
        {
            var assigned = await Run(engine, "assigned", null);
            var observed = await Run(engine, "observed", new DebugOptions { Trace = true, Record = true, ExplainIds = new[] { EntityKey.FromInt64(1) } });
            Assert.Null(assigned.LogBatches);
            Assert.Null(observed.LogBatches);
            await logs.FlushAsync(TestContext.Current.CancellationToken);
        }

        foreach (var request in new[] { "assigned", "observed" })
        {
            var record = Assert.Single(sink.Snapshot(), b => b.RequestId == request && b.TapId == "$experiments");
            Assert.Equal(Arms.Order(), record.AssignedArms.Order());
            Assert.Contains(sink.Snapshot(), b => b.RequestId == request && b.TapId == "served");
        }
    }

    /// <summary>
    /// Forced arms (to an arm, or to the baseline with an empty list) and a configuration override write nothing durable;
    /// asked to capture, the request gets its batches, the forced arms in them, and the sink still nothing.
    /// </summary>
    [Theory]
    [InlineData("forced", false)]
    [InlineData("forced", true)]
    [InlineData("baseline", false)]
    [InlineData("baseline", true)]
    [InlineData("override", false)]
    [InlineData("override", true)]
    public async Task ADebugRequestThatChangesWhatIsServed_WritesNoPersistentLogs(string debugging, bool captureLogs)
    {
        var sink = new InMemoryLogSink();
        RecResult result;
        await using (var logs = new LogPipeline(sink))
        await using (var engine = Load(logs))
        {
            result = await Run(engine, "debug", new DebugOptions
            {
                ForceArms = debugging switch
                {
                    "forced" => Arms,
                    "baseline" => Array.Empty<string>(),
                    _ => null,
                },
                ConfigOverride = debugging == "override" ? JsonNode.Parse("""{ "params": { "final_top_k": 3 } }""")!.AsObject() : null,
                CaptureLogs = captureLogs,
            });
            await Run(engine, "traffic", null); // after it, through the same pipeline: the flush covers both
            await logs.FlushAsync(TestContext.Current.CancellationToken);
        }

        Assert.True(result.Succeeded, result.Error);
        Assert.DoesNotContain(sink.Snapshot(), b => b.RequestId == "debug");
        Assert.Contains(sink.Snapshot(), b => b.RequestId == "traffic" && b.TapId == "$experiments");
        if (!captureLogs)
        {
            Assert.Null(result.LogBatches);
            return;
        }

        Assert.Contains(result.LogBatches!, b => b.TapId == "served");
        if (debugging != "override") // an override's plan has no experiments, so no $experiments record
        {
            var record = Assert.Single(result.LogBatches!, b => b.TapId == "$experiments");
            Assert.Equal((debugging == "forced" ? Arms : Array.Empty<string>()).Order(), record.AssignedArms.Order());
        }
    }

    private static RecEngine Load(LogPipeline logs)
    {
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment, logs: logs);
        var load = engine.Load(host.Request);
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        return engine;
    }

    /// <summary>A request the platform assigned <see cref="Arms"/>; forced arms, when the debug options have them, win.</summary>
    private static async Task<RecResult> Run(RecEngine engine, string requestId, DebugOptions? debug)
    {
        var result = await engine.ExecuteAsync(new RecRequest
        {
            PipelineId = "home_feed",
            RequestId = requestId,
            Inputs = HomeFeedTestHost.Inputs(1001, "home"),
            Assignment = Arms,
            Deadline = HomeFeedTestHost.Deadline,
            TimeMs = 1_767_225_600_000,
            Debug = debug,
        }, TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.Error);
        return result;
    }
}
