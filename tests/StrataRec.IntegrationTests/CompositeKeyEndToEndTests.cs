using System.Text.Json;
using StrataRec.Core.Values;
using StrataRec.Hosting;
using StrataRec.Hosting.Bindings;
using StrataRec.Hosting.Replay;

namespace StrataRec.IntegrationTests;

/// <summary>
/// The sample's <c>item_country</c> entity, keyed by <c>["col.entity_id", "ctx.request.country"]</c>: a local table
/// loaded from a snapshot whose keys are arrays, read by the fusion multiplier.
/// </summary>
public sealed class CompositeKeyEndToEndTests
{
    private static RecRequest Request(string requestId, string? country) => new()
    {
        PipelineId = "home_feed",
        RequestId = requestId,
        Inputs = country is null ? HomeFeedTestHost.Inputs(1001, "home") : HomeFeedTestHost.Inputs(1001, "home").Set("country", country),
        Deadline = HomeFeedTestHost.Deadline,
        TimeMs = 1_767_225_600_000,
        Debug = new DebugOptions { Record = true, ForceArms = ReplayTests.ControlArms },
    };

    private static List<(long Id, double Score)> Scores(RecResult result) =>
        result.Items.Select(i => (i.Id.Int64Value, i.Columns["score"].AsFloat64())).ToList();

    [Fact]
    public async Task ACountrysBoosts_ChangeTheScores_AndACountryWithoutRecords_OrNone_ChangesNothing()
    {
        var (engine, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());

        var none = await engine.ExecuteAsync(Request("req-none", null), TestContext.Current.CancellationToken);
        var unknown = await engine.ExecuteAsync(Request("req-zz", "ZZ"), TestContext.Current.CancellationToken);
        var us = await engine.ExecuteAsync(Request("req-us", "US"), TestContext.Current.CancellationToken);

        Assert.True(none.Succeeded && unknown.Succeeded && us.Succeeded, none.Error ?? unknown.Error ?? us.Error);
        Assert.Equal(Scores(none), Scores(unknown)); // every boost is the default, 1.0
        Assert.NotEqual(Scores(none), Scores(us)); // items 1-3 have US boosts (0.5, 1.5, 2.0)

        var noneCall = none.Recording!.Runs[0].FeatureCalls.SingleOrDefault(c => c.Source == "item_country_store");
        Assert.True(noneCall is null || noneCall.Keys.GetArrayLength() == 0);
        var usCall = Assert.Single(us.Recording!.Runs[0].FeatureCalls, c => c.Source == "item_country_store");
        Assert.Equal("ok", usCall.Status);
        Assert.All(usCall.Keys.EnumerateArray(), key =>
        {
            Assert.Equal(JsonValueKind.Array, key.ValueKind);
            Assert.Equal(2, key.GetArrayLength());
            Assert.Equal(JsonValueKind.Number, key[0].ValueKind);
            Assert.Equal("US", key[1].GetString());
        });
        Assert.Contains(usCall.Keys.EnumerateArray(), key => key[0].GetInt64() == 3);
    }

    [Fact]
    public async Task ARecordedRequest_ReplaysByTuple_OnAnEngineThatLoadsNoTables()
    {
        var (recording, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        var recorded = await recording.ExecuteAsync(Request("req-us-replay", "US"), TestContext.Current.CancellationToken);
        Assert.True(recorded.Succeeded, recorded.Error);
        var text = ReplayBundleFile.Serialize(recorded.Recording!);
        var bundle = ReplayBundleFile.Parse(text);
        var call = Assert.Single(bundle.Runs[0].FeatureCalls, c => c.Source == "item_country_store");
        Assert.Contains(call.Keys.EnumerateArray(), key => key[0].GetInt64() == 3 && key[1].GetString() == "US");

        var sample = HomeFeedTestHost.Load();
        await using var replayOnly = new RecEngine(sample.Environment, bindings: new BindingHost(new BindingHostOptions { Load = false }));
        Assert.True(replayOnly.Load(sample.Request).Applied);
        var replayed = await replayOnly.ReplayAsync(bundle, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(replayed.Report.Diff.Identical, ReplayTests.Describe(replayed.Report));
        Assert.Empty(replayed.Report.Gaps);

        var at = text.IndexOf("\"country\": {", StringComparison.Ordinal); // the request input, before the runs
        var value = text.IndexOf("\"US\"", at, StringComparison.Ordinal);
        var edited = ReplayBundleFile.Parse(string.Concat(text.AsSpan(0, value), "\"DE\"", text.AsSpan(value + 4)));
        Assert.Equal("DE", edited.Request.Inputs["country"].Value!.Value.GetString());
        var mismatched = await replayOnly.ReplayAsync(edited, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEmpty(mismatched.Report.Gaps);
    }

    [Fact]
    public void TheTable_IsKeyedByBothParts_AsTheSnapshotWroteThem()
    {
        var (engine, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        Assert.True(engine.TryGetPipeline("home_feed", out var pipeline));
        var table = Assert.Single(pipeline.Bindings.Tables, t => t.Schema.Source == "item_country_store");
        var version = table.Pin();

        Assert.Equal("(int64,string)", table.Schema.Key.TypeText);
        Assert.Equal(6, version.RecordCount);
        var boost = table.Schema.FieldIndex("boost");
        Assert.Equal(2.0, version.Read(boost, version.Find([Core.Keys.EntityKey.FromInt64(3), Core.Keys.EntityKey.FromString("US")])).AsFloat64(), 6);
        Assert.Equal(1.2, version.Read(boost, version.Find([Core.Keys.EntityKey.FromInt64(1), Core.Keys.EntityKey.FromString("DE")])).AsFloat64(), 6);
        Assert.False(version.Find([Core.Keys.EntityKey.FromInt64(3), Core.Keys.EntityKey.FromString("DE")]).Found);
    }
}
