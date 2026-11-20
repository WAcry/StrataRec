using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using StrataRec.Core.Bindings;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;
using StrataRec.Hosting;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Replay;
using StrataRec.Pipeline.Sources;

namespace StrataRec.IntegrationTests;

/// <summary>
/// A recall source's query (design 5.4): the sample's two_tower searches with the user's embedding from the catalog
/// (<c>"queries": { "two_tower": { "user_embedding": "ctx.user.embedding" } }</c>). Each source waits for the context
/// features its own query reads and for nothing else, a feature that failed fails only the source that reads it, a
/// missing value is sent as missing, and a recording keeps the values a replay compares.
/// </summary>
/// <remarks>
/// user_store's fetch is held by a gate the test opens, so whether a source was sent before the user's features were
/// stored is not a question of timing: the test waits for the other source's call while the gate is closed.
/// </remarks>
public sealed class RecallQueryTests
{
    internal static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ASourceWithoutAQuery_IsSentAtOnce_AndOneWithAQuery_OnceItsFeaturesAreStored()
    {
        var host = HomeFeedTestHost.Load();
        var baseline = await Engine(host, new ProbeSources()).ExecuteAsync(Request("req-baseline"), TestContext.Current.CancellationToken);
        using var users = new GatedConnectors("user_store");
        var sources = new ProbeSources();
        var engine = Engine(host, sources, users);

        var running = engine.ExecuteAsync(Request("req-gated"), TestContext.Current.CancellationToken).AsTask();
        await sources.Called("popular").WaitAsync(Patience, TestContext.Current.CancellationToken);
        await users.Started.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        // popular was sent while the user's features were still being fetched; two_tower waits for its embedding.
        Assert.False(sources.Called("two_tower").IsCompleted);
        Assert.False(running.IsCompleted);
        users.Open();
        var result = await running.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        var query = sources.Query("two_tower");
        Assert.Equal(1, query.Count);
        Assert.Equal(("user_embedding", "list<float64,3>"), (query.Name(0), query.Type(0).ToString()));
        Assert.Equal([1.0, 0.0, 0.0], query["user_embedding"].AsFloat64List().ToArray()); // users.json: 1001
        Assert.Equal(0, sources.Query("popular").Count);
        Assert.Equal(Items(baseline), Items(result));
    }

    [Fact]
    public async Task EachSource_WaitsForTheFeaturesOfItsOwnQuery()
    {
        // A third source whose query reads the request's region boost, a chain of two other context sources.
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"two_tower\": 40, \"popular\": 20", "\"two_tower\": 40, \"popular\": 20, \"regional\": 5");
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"source_order\": [\"two_tower\", \"popular\"]", "\"source_order\": [\"two_tower\", \"popular\", \"regional\"]");
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"queries\": { \"two_tower\": {", "\"queries\": { \"regional\": { \"boost\": \"ctx.region.boost\", \"surface\": \"ctx.request.surface\" }, \"two_tower\": {");
            ReplayTests.Edit(root, "bindings/local.jsonc", "\"popular\":   { \"client\": \"memory_list@1\", \"data_file\": \"fixtures/recall_popular.json\" }",
                "\"popular\":   { \"client\": \"memory_list@1\", \"data_file\": \"fixtures/recall_popular.json\" }, \"regional\": { \"client\": \"memory_list@1\", \"items\": [7, 8, 9] }");
        });
        using var users = new GatedConnectors("user_store");
        var sources = new ProbeSources();
        var engine = Engine(host, sources, users);

        var running = engine.ExecuteAsync(Request("req-regional"), TestContext.Current.CancellationToken).AsTask();
        await sources.Called("regional").WaitAsync(Patience, TestContext.Current.CancellationToken);

        // regional's features were stored while user_store's fetch was held: it did not wait for two_tower's.
        Assert.False(sources.Called("two_tower").IsCompleted);
        var regional = sources.Query("regional");
        Assert.Equal(("boost", "surface"), (regional.Name(0), regional.Name(1))); // in name order, whatever the JSON's
        Assert.False(regional["boost"].IsMissing);
        Assert.Equal("home", regional["surface"].AsString());
        users.Open();
        var result = await running.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(NodeOutcomeKind.Succeeded, result.Trace!.Single(n => n.NodeId == "recall").Kind);
        Assert.Equal([1.0, 0.0, 0.0], sources.Query("two_tower")["user_embedding"].AsFloat64List().ToArray());
    }

    [Fact]
    public async Task AFeatureThatFailed_FailsTheSourceWhoseQueryReadsIt_WhichIsNotSent()
    {
        var host = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "bindings/local.jsonc",
            "\"user_store\":   { \"connector\": \"memory@1\",", "\"user_store\":   { \"connector\": \"memory@1\", \"fail\": true,"));
        var sources = new ProbeSources();

        var result = await Engine(host, sources).ExecuteAsync(Request("req-users-failed"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.False(sources.Called("two_tower").IsCompleted);
        var recall = result.Trace!.Single(n => n.NodeId == "recall");
        Assert.Equal(NodeOutcomeKind.Degraded, recall.Kind);
        Assert.StartsWith("recall sources failed: two_tower: its query reads a context feature that failed (", recall.Detail, StringComparison.Ordinal);
        Assert.All(result.Items, item => Assert.Equal("popular", item.Columns["primary_source"].AsString()));
        Assert.Equal(new[] { "popular" }, result.Recording!.Runs[0].CandidateCalls.Select(c => c.Source)); // nothing sent, nothing recorded

        // With popular failing too, every source failed: the node fails, naming both.
        var none = HomeFeedTestHost.LoadModified(root =>
        {
            ReplayTests.Edit(root, "bindings/local.jsonc", "\"user_store\":   { \"connector\": \"memory@1\",", "\"user_store\":   { \"connector\": \"memory@1\", \"fail\": true,");
            ReplayTests.Edit(root, "bindings/local.jsonc", "\"client\": \"memory_list@1\",", "\"client\": \"memory_list@1\", \"fail\": true,");
        });
        var failed = await Engine(none, new ProbeSources()).ExecuteAsync(Request("req-all-failed"), TestContext.Current.CancellationToken);
        var node = failed.Trace!.Single(n => n.NodeId == "recall");
        Assert.Contains("every recall source failed (", node.Detail, StringComparison.Ordinal);
        Assert.Contains("two_tower: its query reads a context feature that failed", node.Detail, StringComparison.Ordinal);
        Assert.Contains("popular: memory_list@1 source is configured to fail.", node.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADegradedFeature_IsSent_AndDegradesTheNode()
    {
        var sources = new ProbeSources();
        using var users = new GatedConnectors("user_store", partial: true);
        users.Open();

        var result = await Engine(HomeFeedTestHost.Load(), sources, users).ExecuteAsync(Request("req-users-partial"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal([1.0, 0.0, 0.0], sources.Query("two_tower")["user_embedding"].AsFloat64List().ToArray());
        var recall = result.Trace!.Single(n => n.NodeId == "recall");
        Assert.Equal(NodeOutcomeKind.Degraded, recall.Kind);
        Assert.StartsWith("the query of recall source 'two_tower' reads a degraded context feature", recall.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheWaitForAQuerysFeatures_IsWithinTheNodesBudget_AndAFeatureSourceTimeoutBelowItDegradesInstead()
    {
        // user_store answers after 2 s; the recall node has 300 ms. Waiting for the embedding times the node out, as a call
        // still in flight at the deadline does: popular's answer is not merged.
        var slow = HomeFeedTestHost.LoadModified(root =>
        {
            ReplayTests.Edit(root, "bindings/local.jsonc", "\"user_store\":   { \"connector\": \"memory@1\",", "\"user_store\":   { \"connector\": \"memory@1\", \"latency_ms\": 2000,");
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"order\": 100, \"op\": \"MultiSourceRecall@1\",", "\"order\": 100, \"op\": \"MultiSourceRecall@1\", \"timeout_ms\": 300,");
        });
        var sources = new ProbeSources();

        var timedOut = await Engine(slow, sources).ExecuteAsync(Request("req-wait-timed-out"), TestContext.Current.CancellationToken);

        var recall = timedOut.Trace!.Single(n => n.NodeId == "recall");
        Assert.Equal("timed out after 300 ms", recall.Detail);
        Assert.False(sources.Called("two_tower").IsCompleted);
        Assert.Equal(new[] { "popular" }, timedOut.Recording!.Runs[0].CandidateCalls.Select(c => c.Source));

        // With user_store's own timeout below the node's budget, the feature fails in time: only two_tower fails.
        var bounded = HomeFeedTestHost.LoadModified(root =>
        {
            SampleBindings.Rebind(root, "user_store", """{ "connector": "memory@1", "data_file": "fixtures/users.json", "latency_ms": 2000, "timeout_ms": 50 }""");
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"order\": 100, \"op\": \"MultiSourceRecall@1\",", "\"order\": 100, \"op\": \"MultiSourceRecall@1\", \"timeout_ms\": 1000,");
        });

        var degraded = await Engine(bounded, new ProbeSources()).ExecuteAsync(Request("req-wait-bounded"), TestContext.Current.CancellationToken);

        Assert.True(degraded.Succeeded, degraded.Error);
        var node = degraded.Trace!.Single(n => n.NodeId == "recall");
        Assert.Equal(NodeOutcomeKind.Degraded, node.Kind);
        Assert.StartsWith("recall sources failed: two_tower: its query reads a context feature that failed (", node.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingValue_IsSentAsMissing_AndTheVectorIndexReturnsNothingForIt()
    {
        // An anonymous request, and an embedding without a default: nothing to search with.
        var host = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "catalog/home_feed.jsonc",
            "{ \"type\": \"list<float32,3>\", \"source\": \"user_store\", \"field\": \"u_emb\", \"default\": [0.0, 0.0, 1.0] }",
            "{ \"type\": \"list<float32,3>\", \"source\": \"user_store\", \"field\": \"u_emb\" }"));
        var sources = new ProbeSources();

        var result = await Engine(host, sources).ExecuteAsync(Request("req-anonymous", userId: null), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        var query = sources.Query("two_tower");
        Assert.True(query.TryGet("user_embedding", out var type, out var value));
        Assert.Equal(("list<float64,3>?", true), (type.ToString(), value.IsMissing));
        Assert.True(query["no_such_value"].IsMissing);
        Assert.False(query.TryGet("no_such_value", out _, out _));
        Assert.Equal(NodeOutcomeKind.Succeeded, result.Trace!.Single(n => n.NodeId == "recall").Kind);
        Assert.All(result.Items, item => Assert.Equal("popular", item.Columns["primary_source"].AsString()));
        var call = result.Recording!.Runs[0].CandidateCalls.Single(c => c.Source == "two_tower");
        Assert.Equal(("list<float64,3>?", null), (call.Query!["user_embedding"].Type, call.Query["user_embedding"].Value));
    }

    [Fact]
    public async Task AReplayWhoseQueryDiffers_ReportsIt_AndAnswersWithTheRecordedCandidates()
    {
        var recorded = await Engine(HomeFeedTestHost.Load(), new ProbeSources()).ExecuteAsync(Request("req-recorded"), TestContext.Current.CancellationToken);
        Assert.Equal("[1,0,0]", recorded.Recording!.Runs[0].CandidateCalls.Single(c => c.Source == "two_tower").Query!["user_embedding"].Value!.Value.GetRawText());

        // The same configuration replays the same query: no gap.
        var (same, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        var replayed = await same.ReplayAsync(recorded.Recording, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(replayed.Report.Gaps);
        Assert.True(replayed.Report.Diff.Identical, ReplayTests.Describe(replayed.Report));

        // The query now reads another user feature: two_tower is answered from the recording, and the replay says it
        // answered another query.
        var changed = HomeFeedTestHost.LoadModified(root =>
        {
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "{ \"user_embedding\": \"ctx.user.embedding\" }",
                "{ \"tenure\": \"ctx.user.tenure_days\", \"user_embedding\": \"ctx.user.embedding\" }");
        });
        var (candidate, _) = MetricsTests.LoadEngine(changed);
        var outcome = await candidate.ReplayAsync(recorded.Recording, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error);
        var gap = Assert.Single(outcome.Report.Gaps, g => g.Reason == "query_changed");
        Assert.Equal(("candidate_source", "two_tower", "tenure", 1, "recall"), (gap.Kind, gap.Source, gap.Field, gap.Count, gap.Examples.Single()));
        Assert.Equal("candidate source two_tower: its query value 'tenure' differs from the recorded one in 1 call(s), which were answered as recorded", gap.Description);
        Assert.Equal(Items(recorded), Items(outcome.Result));
    }

    [Fact]
    public async Task AQueryReadingADerivedFeature_IsSentOnceItIsComputed_NotAfterTheWholePrefetch()
    {
        // user.query_embedding is derived from the user's embedding; geo_store (the request's country) is still in flight.
        // The derived feature was computed only once every group was stored, so two_tower waited for geo_store (review 80).
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            ReplayTests.Edit(root, "catalog/home_feed.jsonc", "\"derived_features\": {",
                "\"derived_features\": { \"user.query_embedding\": { \"type\": \"list<float32,3>\", \"expr\": \"ctx.user.embedding\" },");
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"user_embedding\": \"ctx.user.embedding\"", "\"user_embedding\": \"ctx.user.query_embedding\"");
        });
        using var geo = new GatedConnectors("geo_store");
        var sources = new ProbeSources();
        await using var engine = Engine(host, sources, geo);
        var running = engine.ExecuteAsync(WithCountry("req-derived-query"), TestContext.Current.CancellationToken).AsTask();
        try
        {
            await geo.Started.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
            await sources.Called("two_tower").WaitAsync(Patience, TestContext.Current.CancellationToken);

            Assert.Equal([1.0, 0.0, 0.0], sources.Query("two_tower")["user_embedding"].AsFloat64List().ToArray());
        }
        finally
        {
            geo.Open();
        }

        var result = await running.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(NodeOutcomeKind.Succeeded, result.Trace!.Single(n => n.NodeId == "recall").Kind);
    }

    [Fact]
    public async Task AFeatureThatFailed_RefusesTheSourceAtOnce_WhateverItsOtherFeaturesDo()
    {
        // two_tower reads the user's embedding, whose store fails, and the country's region, whose store is held past the
        // node's budget: the source is refused when the embedding fails, not timed out with the region (review 80).
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            ReplayTests.Edit(root, "bindings/local.jsonc", "\"user_store\":   { \"connector\": \"memory@1\",", "\"user_store\":   { \"connector\": \"memory@1\", \"fail\": true,");
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"user_embedding\": \"ctx.user.embedding\"",
                "\"user_embedding\": \"ctx.user.embedding\", \"region\": \"ctx.country.region\"");
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"order\": 100, \"op\": \"MultiSourceRecall@1\",", "\"order\": 100, \"op\": \"MultiSourceRecall@1\", \"timeout_ms\": 500,");
        });
        using var geo = new GatedConnectors("geo_store");
        var sources = new ProbeSources();
        await using var engine = Engine(host, sources, geo);
        var running = engine.ExecuteAsync(WithCountry("req-failed-first"), TestContext.Current.CancellationToken).AsTask();
        await geo.Started.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        _ = Task.Delay(1500, TestContext.Current.CancellationToken).ContinueWith(_ => geo.Open(), TaskScheduler.Default);

        var result = await running.WaitAsync(Patience, TestContext.Current.CancellationToken);

        var recall = result.Trace!.Single(n => n.NodeId == "recall");
        Assert.Equal(NodeOutcomeKind.Degraded, recall.Kind);
        Assert.StartsWith("recall sources failed: two_tower: its query reads a context feature that failed (", recall.Detail, StringComparison.Ordinal);
        Assert.False(sources.Called("two_tower").IsCompleted);
    }

    [Fact]
    public async Task SourcesWhoseFeaturesAreStoredTogether_AreSentInSourceOrder()
    {
        // popular reads the user's tenure, two_tower the user's embedding: both stored by user_store at once. two_tower comes
        // first in source_order, so it is sent first, every time (each source waited on its own and raced, review 80).
        var host = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "pipelines/home_feed.jsonc",
            "\"queries\": { \"two_tower\": {", "\"queries\": { \"popular\": { \"tenure\": \"ctx.user.tenure_days\" }, \"two_tower\": {"));
        for (var i = 0; i < 16; i++)
        {
            using var users = new GatedConnectors("user_store");
            var sources = new ProbeSources();
            await using var engine = Engine(host, sources, users);
            var running = engine.ExecuteAsync(Request($"req-order-{i}"), TestContext.Current.CancellationToken).AsTask();
            await users.Started.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
            users.Open();

            var result = await running.WaitAsync(Patience, TestContext.Current.CancellationToken);

            Assert.Equal(["two_tower", "popular"], result.Recording!.Runs[0].CandidateCalls.OrderBy(c => c.Seq).Select(c => c.Source));
        }
    }

    [Fact]
    public async Task TheWaitForAQuerysFeatures_EndsAtTheDeadline_EvenWhileAClientsCancellationCallbackRuns()
    {
        // popular's client blocks in a callback on its token, registered after the node started waiting for two_tower's
        // embedding; the node's token is cancelled at the deadline on a thread that runs the newest callback first. The
        // wait was cancelled through that token, so it waited for the client's callback to return (review 80); it ends at
        // the deadline on its own now, as a call does.
        var host = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "pipelines/home_feed.jsonc",
            "\"order\": 100, \"op\": \"MultiSourceRecall@1\",", "\"order\": 100, \"op\": \"MultiSourceRecall@1\", \"timeout_ms\": 1000,"));
        using var users = new GatedConnectors("user_store");
        using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var popularToken = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sources = new ProbeSources
        {
            BeforeRetrieve = (query, token) =>
            {
                if (query.SourceName != "popular")
                {
                    return ValueTask.CompletedTask;
                }

                popularToken.TrySetResult(token);
                return new ValueTask(answer.Task);
            },
        };
        await using var engine = Engine(host, sources, users);
        var running = engine.ExecuteAsync(Request("req-wait-callback"), TestContext.Current.CancellationToken).AsTask();
        try
        {
            var token = await popularToken.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
            await Task.Delay(200, TestContext.Current.CancellationToken); // the node is waiting for user_store by now
            using var callback = token.Register(() =>
            {
                entered.TrySetResult();
                resume.Wait(Patience);
            });
            await entered.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

            var result = await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); // the callback still runs

            Assert.Equal("timed out after 1000 ms", result.Trace!.Single(n => n.NodeId == "recall").Detail);
            Assert.False(sources.Called("two_tower").IsCompleted);
        }
        finally
        {
            resume.Set();
            answer.TrySetResult();
            users.Open();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFeatureStoredWhileTheSourcesAreChecked_StartsTheNextRound_AndSourcesStoredTogetherGoInSourceOrder(bool together)
    {
        // Source order two_tower, popular, regional. two_tower reads the user's embedding and the country's region (geo_store,
        // held); popular the user's tenure, stored at once; regional the region's boost (region_store, keyed by the region and
        // held) or, together, the country's region as two_tower does. popular's call opens geo_store and waits until
        // region_store has started, so the country's region is stored after two_tower was checked and before the round
        // waits. Each source observed the tasks on its own (review 82): that store was missed until region_store answered,
        // and with together, regional, checked after the store, was sent before two_tower.
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"two_tower\": 40, \"popular\": 20", "\"two_tower\": 40, \"popular\": 20, \"regional\": 5");
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"source_order\": [\"two_tower\", \"popular\"]", "\"source_order\": [\"two_tower\", \"popular\", \"regional\"]");
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"queries\": { \"two_tower\": { \"user_embedding\": \"ctx.user.embedding\" } }",
                "\"queries\": { \"two_tower\": { \"user_embedding\": \"ctx.user.embedding\", \"country_region\": \"ctx.country.region\" }, " +
                "\"popular\": { \"tenure\": \"ctx.user.tenure_days\" }, " +
                (together ? "\"regional\": { \"region\": \"ctx.country.region\" } }" : "\"regional\": { \"boost\": \"ctx.region.boost\" } }"));
            ReplayTests.Edit(root, "bindings/local.jsonc", "\"popular\":   { \"client\": \"memory_list@1\", \"data_file\": \"fixtures/recall_popular.json\" }",
                "\"popular\":   { \"client\": \"memory_list@1\", \"data_file\": \"fixtures/recall_popular.json\" }, \"regional\": { \"client\": \"memory_list@1\", \"items\": [7, 8, 9] }");
        });
        using var connectors = new TwoGateConnectors();
        var sources = new ProbeSources
        {
            BeforeRetrieve = (query, _) =>
            {
                if (query.SourceName == "popular")
                {
                    connectors.OpenGeo.TrySetResult();
                    connectors.RegionStarted.Task.WaitAsync(Patience).GetAwaiter().GetResult(); // the country's region is stored
                }

                return ValueTask.CompletedTask;
            },
        };
        await using var engine = Engine(host, sources, connectors);
        var running = engine.ExecuteAsync(WithCountry($"req-stored-mid-round-{together}"), TestContext.Current.CancellationToken).AsTask();
        try
        {
            await sources.Called("two_tower").WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.False(connectors.OpenRegion.Task.IsCompleted); // sent while region_store is still held
            if (together)
            {
                await sources.Called("regional").WaitAsync(Patience, TestContext.Current.CancellationToken);
            }
            else
            {
                Assert.False(sources.Called("regional").IsCompleted);
            }
        }
        finally
        {
            connectors.OpenGeo.TrySetResult();
            connectors.OpenRegion.TrySetResult();
        }

        var result = await running.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("eu", sources.Query("two_tower")["country_region"].AsString());
        Assert.Equal(["popular", "two_tower", "regional"], result.Recording!.Runs[0].CandidateCalls.OrderBy(c => c.Seq).Select(c => c.Source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AQueryReadingADerivedFeature_IsRefusedAsSoonAsOneOfItsInputsFails(bool derived)
    {
        // user_store fails; geo_store, which region.boost waits for, is held past the node's budget. A query reading tenure
        // and boost itself is refused at once; one reading a feature derived from both waited for boost, up to the node's
        // budget (review 82).
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            ReplayTests.Edit(root, "bindings/local.jsonc", "\"user_store\":   { \"connector\": \"memory@1\",", "\"user_store\":   { \"connector\": \"memory@1\", \"fail\": true,");
            ReplayTests.Edit(root, "catalog/home_feed.jsonc", "\"derived_features\": {",
                "\"derived_features\": { \"user.query_gate\": { \"type\": \"float64\", \"expr\": \"ctx.user.tenure_days + ctx.region.boost\" },");
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"user_embedding\": \"ctx.user.embedding\"", derived
                ? "\"user_embedding\": \"[1.0, 0.0, 0.0]\", \"signal\": \"ctx.user.query_gate\""
                : "\"user_embedding\": \"[1.0, 0.0, 0.0]\", \"tenure\": \"ctx.user.tenure_days\", \"region\": \"ctx.region.boost\"");
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"order\": 100, \"op\": \"MultiSourceRecall@1\",", "\"order\": 100, \"op\": \"MultiSourceRecall@1\", \"timeout_ms\": 500,");
        });
        using var geo = new GatedConnectors("geo_store");
        var sources = new ProbeSources();
        await using var engine = Engine(host, sources, geo);
        var running = engine.ExecuteAsync(WithCountry($"req-derived-failed-{derived}"), TestContext.Current.CancellationToken).AsTask();
        await geo.Started.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        _ = Task.Delay(1500, TestContext.Current.CancellationToken).ContinueWith(_ => geo.Open(), TaskScheduler.Default);

        var result = await running.WaitAsync(Patience, TestContext.Current.CancellationToken);

        var recall = result.Trace!.Single(n => n.NodeId == "recall");
        Assert.Equal(NodeOutcomeKind.Degraded, recall.Kind);
        Assert.StartsWith("recall sources failed: two_tower: its query reads a context feature that failed (", recall.Detail, StringComparison.Ordinal);
        Assert.False(sources.Called("two_tower").IsCompleted);
    }

    internal static RecRequest WithCountry(string requestId)
    {
        var request = Request(requestId);
        request.Inputs.Set("country", "DE");
        return request;
    }

    internal static RecRequest Request(string requestId, long? userId = 1001) => new()
    {
        PipelineId = "home_feed",
        RequestId = requestId,
        Inputs = HomeFeedTestHost.Inputs(userId, "home"),
        Deadline = HomeFeedTestHost.Deadline,
        TimeMs = 1_767_225_600_000,
        Debug = new DebugOptions { Record = true, Trace = true, ForceArms = ReplayTests.ControlArms },
    };

    internal static RecEngine Engine(HomeFeedTestHost host, ProbeSources sources, IFeatureConnectorFactory? connectors = null)
    {
        var engine = new RecEngine(new PlanEnvironment
        {
            BaseDirectory = host.Root,
            Connectors = connectors is null ? FeatureConnectorRegistry.CreateDefault() : FeatureConnectorRegistry.CreateDefault().Add(connectors),
            CandidateSources = CandidateSourceRegistry.CreateDefault().Add(sources.Lists).Add(sources.Vectors),
        });
        var load = engine.Load(host.Request);
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        return engine;
    }

    private static List<(long, int)> Items(RecResult result) => result.Items.Select(i => (i.Id.Int64Value, i.Rank)).ToList();

    /// <summary>memory@1 connectors where geo_store waits for <see cref="OpenGeo"/> and region_store for <see cref="OpenRegion"/>.</summary>
    private sealed class TwoGateConnectors : IFeatureConnectorFactory, IDisposable
    {
        private readonly MemoryFeatureConnectorFactory _inner = new();

        public TaskCompletionSource OpenGeo { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource OpenRegion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when region_store is asked: the country's region it is keyed by is stored.</summary>
        public TaskCompletionSource RegionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ConnectorType => _inner.ConnectorType;

        public Type SettingsType => _inner.SettingsType;

        public IFeatureSourceConnector Create(BindingContext context) => context.Name switch
        {
            "geo_store" => new Connector(_inner.Create(context), OpenGeo.Task, null),
            "region_store" => new Connector(_inner.Create(context), OpenRegion.Task, RegionStarted),
            _ => _inner.Create(context),
        };

        public void Dispose()
        {
            OpenGeo.TrySetResult();
            OpenRegion.TrySetResult();
        }

        private sealed class Connector(IFeatureSourceConnector inner, Task gate, TaskCompletionSource? started) : IFeatureSourceConnector
        {
            public string ConnectorType => inner.ConnectorType;

            public bool Supports(EntityKeyLayout layout) => inner.Supports(layout);

            public async ValueTask<FetchStatus> FetchAsync(
                EntityKeyBatch keys, ReadOnlyMemory<CompiledFieldBinding> fields, IFeatureBatchWriter writer, CancellationToken cancellationToken)
            {
                started?.TrySetResult();
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                return await inner.FetchAsync(keys, fields, writer, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The built-in lists and vector index, noting each call's query when it is sent.</summary>
    internal sealed class ProbeSources
    {
        private readonly ConcurrentDictionary<string, (TaskCompletionSource Called, CandidateQuery? Query)> _calls = new(StringComparer.Ordinal);

        public ProbeSources()
        {
            Lists = new Factory(new MemoryListCandidateSourceFactory(), this);
            Vectors = new Factory(new MemoryVectorsCandidateSourceFactory(), this);
        }

        public ICandidateSourceFactory Lists { get; }

        public ICandidateSourceFactory Vectors { get; }

        /// <summary>Runs when a source is sent, before it answers, with the call's token.</summary>
        public Func<CandidateQuery, CancellationToken, ValueTask>? BeforeRetrieve { get; init; }

        /// <summary>Completes when <paramref name="source"/> is sent.</summary>
        public Task Called(string source) => Entry(source).Called.Task;

        public CandidateQueryValues Query(string source) => Entry(source).Query?.Values ?? throw new InvalidOperationException($"{source} was not sent");

        private (TaskCompletionSource Called, CandidateQuery? Query) Entry(string source) =>
            _calls.GetOrAdd(source, _ => (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), null));

        private void Sent(CandidateQuery query)
        {
            var entry = Entry(query.SourceName);
            _calls[query.SourceName] = (entry.Called, query);
            entry.Called.TrySetResult();
        }

        private sealed class Factory(ICandidateSourceFactory inner, ProbeSources owner) : ICandidateSourceFactory
        {
            public string ClientType => inner.ClientType;

            public Type SettingsType => inner.SettingsType;

            public ICandidateSource Create(BindingContext context) => new Source(inner.Create(context), owner);
        }

        private sealed class Source(ICandidateSource inner, ProbeSources owner) : ICandidateSource
        {
            public string ClientType => inner.ClientType;

            public async ValueTask<CandidateSourceStatus> RetrieveAsync(CandidateQuery query, ICandidateSink sink, CancellationToken cancellationToken)
            {
                owner.Sent(query);
                if (owner.BeforeRetrieve is { } before)
                {
                    await before(query, cancellationToken).ConfigureAwait(false);
                }

                return await inner.RetrieveAsync(query, sink, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// memory@1 connectors where <paramref name="gated"/>'s fetches wait for the gate (open it before a request that should
    /// not wait), and with <paramref name="partial"/> report a partial answer: its features are degraded.
    /// </summary>
    internal sealed class GatedConnectors(string gated, bool partial = false) : IFeatureConnectorFactory, IDisposable
    {
        private readonly MemoryFeatureConnectorFactory _inner = new();
        private readonly ManualResetEventSlim _gate = new();

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ConnectorType => _inner.ConnectorType;

        public Type SettingsType => _inner.SettingsType;

        public void Open() => _gate.Set();

        public void Dispose() => _gate.Set();

        public IFeatureSourceConnector Create(BindingContext context)
        {
            var connector = _inner.Create(context);
            return context.Name == gated ? new Connector(connector, this) : connector;
        }

        private sealed class Connector(IFeatureSourceConnector inner, GatedConnectors owner) : IFeatureSourceConnector
        {
            public string ConnectorType => inner.ConnectorType;

            public bool Supports(EntityKeyLayout layout) => inner.Supports(layout);

            public async ValueTask<FetchStatus> FetchAsync(EntityKeyBatch keys, ReadOnlyMemory<CompiledFieldBinding> fields, IFeatureBatchWriter writer,
                CancellationToken cancellationToken)
            {
                owner.Started.TrySetResult();
                await Task.Run(() => owner._gate.Wait(Patience * 2), CancellationToken.None).ConfigureAwait(false);
                var status = await inner.FetchAsync(keys, fields, writer, cancellationToken).ConfigureAwait(false);
                return owner._partialAnswer ? new FetchStatus(FetchOutcome.Partial, 0, "one of the user store's shards did not answer") : status;
            }
        }

        private bool _partialAnswer => partial;
    }
}

/// <summary>
/// A prefetch that faults while a recall source waits for it (here a host's metric listener throwing a TimeoutException as
/// it measures user_store's values) is the node's exception, the same whether it faulted before the wait or during it: the
/// wait took a fault of the one task it waited for for the node's deadline (review 82). Process-wide: a MeterListener.
/// </summary>
[Collection(ProcessWideListenersCollection.Name)]
public sealed class RecallQueryListenerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APrefetchThatFaults_IsTheNodesException_NotItsDeadline(bool whileWaiting)
    {
        var host = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "pipelines/home_feed.jsonc",
            "\"order\": 100, \"op\": \"MultiSourceRecall@1\",", "\"order\": 100, \"op\": \"MultiSourceRecall@1\", \"timeout_ms\": 4000,"));
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Meter.Name == StrataRecTelemetry.Name && instrument.Name == StrataRecMetrics.Names.SourceValues)
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var (key, value) in tags)
            {
                if (key == StrataRecTags.Source && (string?)value == "user_store")
                {
                    throw new TimeoutException("a host's metric listener timed out");
                }
            }
        });
        listener.Start();
        using var users = new RecallQueryTests.GatedConnectors("user_store"); // when waiting; otherwise memory@1 answers at once
        var sources = new RecallQueryTests.ProbeSources();
        await using var engine = RecallQueryTests.Engine(host, sources, whileWaiting ? users : null);
        var running = engine.ExecuteAsync(RecallQueryTests.Request($"req-prefetch-fault-{whileWaiting}"), TestContext.Current.CancellationToken).AsTask();
        if (whileWaiting)
        {
            await sources.Called("popular").WaitAsync(RecallQueryTests.Patience, TestContext.Current.CancellationToken); // the node waits
            users.Open();
        }

        var result = await running.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Equal("TimeoutException: a host's metric listener timed out", result.Trace!.Single(n => n.NodeId == "recall").Detail);
    }
}
