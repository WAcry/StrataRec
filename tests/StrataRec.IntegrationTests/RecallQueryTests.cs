using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Reflection;
using StrataRec.Core.Bindings;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;
using StrataRec.Hosting;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Replay;
using StrataRec.Pipeline.Sources;
using StrataRec.Core.Services;

namespace StrataRec.IntegrationTests;

/// <summary>
/// A recall source's query: the sample's two_tower searches with the user's embedding from the catalog
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
        var embedding = Assert.Single(sources.Query("two_tower"));
        // A reference is sent at its declared type: the catalog's list<float32,3>.
        Assert.Equal(("user_embedding", TensorElementType.Float32, "3"), (embedding.Name, embedding.Type, string.Join(",", embedding.Shape)));
        Assert.Equal([1.0, 0.0, 0.0], embedding.Floats); // users.json: 1001
        Assert.Empty(sources.Query("popular"));
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
        Assert.Equal(("boost", "surface"), (regional[0].Name, regional[1].Name)); // in name order, whatever the JSON's
        Assert.False(regional["boost"].IsMissing);
        Assert.Equal("home", regional["surface"].String);
        users.Open();
        var result = await running.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(NodeOutcomeKind.Succeeded, result.Trace!.Single(n => n.NodeId == "recall").Kind);
        Assert.Equal([1.0, 0.0, 0.0], sources.Query("two_tower")["user_embedding"].Floats);
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
        Assert.Equal(new[] { "popular" }, result.Recording!.Runs[0].ServiceCalls.FindAll(c => c.Role == "recall").Select(c => c.Service)); // nothing sent, nothing recorded

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
        Assert.Equal([1.0, 0.0, 0.0], sources.Query("two_tower")["user_embedding"].Floats);
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
        Assert.Equal(new[] { "popular" }, timedOut.Recording!.Runs[0].ServiceCalls.FindAll(c => c.Role == "recall").Select(c => c.Service));

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
        Assert.True(query.TryGet("user_embedding", out var sent)); // sent as missing, with its declared element type and length
        Assert.Equal((TensorElementType.Float32, "3", true), (sent.Type, string.Join(",", sent.Shape), sent.IsMissing));
        Assert.False(query.TryGet("no_such_value", out _));
        Assert.Equal(NodeOutcomeKind.Succeeded, result.Trace!.Single(n => n.NodeId == "recall").Kind);
        Assert.All(result.Items, item => Assert.Equal("popular", item.Columns["primary_source"].AsString()));
        var call = result.Recording!.Runs[0].ServiceCalls.Single(c => c.Service == "two_tower");
        Assert.Equal(("recall", "ok", 1), (call.Role, call.Status, call.InputDigests.Count)); // the query is recorded as its digest
    }

    [Fact]
    public async Task AReplayWhoseQueryDiffers_ReportsIt_AndTheSourceIsNotAnswered()
    {
        var recorded = await Engine(HomeFeedTestHost.Load(), new ProbeSources()).ExecuteAsync(Request("req-recorded"), TestContext.Current.CancellationToken);
        Assert.Single(recorded.Recording!.Runs[0].ServiceCalls, c => c.Service == "two_tower");

        // The same configuration replays the same query: no gap.
        var (same, _) = MetricsTests.LoadEngine(HomeFeedTestHost.Load());
        var replayed = await same.ReplayAsync(recorded.Recording, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(replayed.Report.Gaps);
        Assert.True(replayed.Report.Diff.Identical, ReplayTests.Describe(replayed.Report));

        // The query now reads another user feature: the recording has no answer to it, so two_tower is not answered (the
        // node degrades on popular alone), and the replay says why. A recorded answer answers only the inputs it was for.
        var changed = HomeFeedTestHost.LoadModified(root =>
        {
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "{ \"user_embedding\": \"ctx.user.embedding\" }",
                "{ \"tenure\": \"ctx.user.tenure_days\", \"user_embedding\": \"ctx.user.embedding\" }");
        });
        var (candidate, _) = MetricsTests.LoadEngine(changed);
        var outcome = await candidate.ReplayAsync(recorded.Recording, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error);
        var gap = Assert.Single(outcome.Report.Gaps, g => g.Reason == "inputs_changed");
        Assert.Equal(("recall", "two_tower", null, 1, "recall"), (gap.Kind, gap.Source, gap.Field, gap.Count, gap.Examples.Single()));
        Assert.Equal("recall two_tower: 1 item(s) or call(s) have other inputs than recorded; not answered", gap.Description);
        Assert.All(outcome.Result.Items, item => Assert.Equal("popular", item.Columns["primary_source"].AsString()));
    }

    [Fact]
    public async Task AQueryReadingADerivedFeature_IsSentOnceItIsComputed_NotAfterTheWholePrefetch()
    {
        // user.query_embedding is derived from the user's embedding; geo_store (the request's country) is still in flight.
        // The derived feature was computed only once every group was stored, so two_tower waited for geo_store.
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

            Assert.Equal([1.0, 0.0, 0.0], sources.Query("two_tower")["user_embedding"].Floats);
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
        // node's budget: the source is refused when the embedding fails, not timed out with the region.
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
        // first in source_order, so it is sent first, every time (each source waited on its own and raced).
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

            Assert.Equal(["two_tower", "popular"], result.Recording!.Runs[0].ServiceCalls.FindAll(c => c.Role == "recall").OrderBy(c => c.Seq).Select(c => c.Service));
        }
    }

    [Fact]
    public async Task TheWaitForAQuerysFeatures_EndsAtTheDeadline_EvenWhileAClientsCancellationCallbackRuns()
    {
        // popular's client blocks in a callback on its token, registered after the node started waiting for two_tower's
        // embedding; the node's token is cancelled at the deadline on a thread that runs the newest callback first. The
        // wait was cancelled through that token, so it waited for the client's callback to return; it ends at
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
                if (query.Service != "popular")
                {
                    return ValueTask.CompletedTask;
                }

                popularToken.TrySetResult(token);
                return new ValueTask(answer.Task);
            },
        };
        await using var engine = Engine(host, sources, users);
        var running = engine.ExecuteAsync(Request("req-wait-callback"), TestContext.Current.CancellationToken).AsTask();
        CancellationTokenRegistration callback = default;
        try
        {
            var token = await popularToken.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
            await Task.Delay(200, TestContext.Current.CancellationToken); // the node is waiting for user_store by now
            callback = token.Register(() =>
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
            callback.Dispose(); // after the callback was let go: disposing a registration waits for a callback that is running
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
        // waits. Each source observed the tasks on its own: that store was missed until region_store answered,
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
                if (query.Service == "popular")
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
        Assert.Equal("eu", sources.Query("two_tower")["country_region"].String);
        Assert.Equal(["popular", "two_tower", "regional"], result.Recording!.Runs[0].ServiceCalls.FindAll(c => c.Role == "recall").OrderBy(c => c.Seq).Select(c => c.Service));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AQueryReadingADerivedFeature_IsRefusedAsSoonAsOneOfItsInputsFails(bool derived)
    {
        // user_store fails; geo_store, which region.boost waits for, is held past the node's budget. A query reading tenure
        // and boost itself is refused at once; one reading a feature derived from both waited for boost, up to the node's
        // budget.
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

    /// <summary>
    /// Once the node's budget has run out, no read is settled, even when a settled callback ran past it and the
    /// read was stored meanwhile. The first callback (the user's features, stored at once) holds on until the 500 ms budget
    /// cancels the node token, then lets geo_store answer and waits until the country's region is stored (region_store,
    /// keyed by it, has started). The next round found the region stored, Task.WhenAny of a completed task returned at once
    /// and the timed wait never timed out, so the second read was settled after the deadline.
    /// </summary>
    [Fact]
    public async Task AReadStoredAfterTheDeadline_IsNeverSettled_HoweverLongACallbackRan()
    {
        var host = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "pipelines/home_feed.jsonc",
            "\"recall\": {", "\"late_reads\": { \"order\": 50, \"op\": \"LateReads@1\", \"timeout_ms\": 500, \"config\": {} }, \"recall\": {"));
        using var connectors = new TwoGateConnectors();
        var probe = new LateReadsFactory(connectors);
        await using var engine = new RecEngine(new PlanEnvironment
        {
            BaseDirectory = host.Root,
            Bindings = PlanEnvironment.DefaultBindings().Add(connectors),
            Operators = OperatorRegistry.CreateDefault().Add(probe),
        });
        var load = engine.Load(host.Request);
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));

        RecResult result;
        try
        {
            result = await engine.ExecuteAsync(WithCountry("req-late-reads"), TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(Patience, TestContext.Current.CancellationToken);
        }
        finally
        {
            connectors.OpenGeo.TrySetResult();
            connectors.OpenRegion.TrySetResult();
        }

        Assert.Equal("timed out after 500 ms", result.Trace!.Single(n => n.NodeId == "late_reads").Detail);
        Assert.Equal([0], probe.Settled);
    }

    /// <summary><c>LateReads@1</c>: waits for the user's embedding, then the country's region, noting each read settled.</summary>
    private sealed class LateReadsFactory(TwoGateConnectors connectors) : IOperatorFactory
    {
        public TwoGateConnectors Connectors { get; } = connectors;

        public OperatorDescriptor Descriptor { get; } = new("LateReads", 1, Effects.ColumnWriter, typeof(NoSettings), "Settles two reads, the first slowly.");

        public ConcurrentQueue<int> Settled { get; } = new();

        public Operator? Compile(OperatorCompileContext context)
        {
            var user = context.CompileAwaitedExpression("ctx.user.embedding", "/user");
            var region = context.CompileAwaitedExpression("ctx.country.region", "/region");
            return user is null || region is null ? null : new LateReads(this, [context.AwaitedReadsOf([user]), context.AwaitedReadsOf([region])]);
        }

        private sealed class LateReads(LateReadsFactory owner, AwaitedReads[] reads) : Operator
        {
            public override async ValueTask ExecuteAsync(OperatorContext context) =>
                await context.WhenEachReadyAsync(reads, (index, _) =>
                {
                    owner.Settled.Enqueue(index);
                    if (index == 0)
                    {
                        Assert.True(context.CancellationToken.WaitHandle.WaitOne(Patience)); // the budget runs out
                        owner.Connectors.OpenGeo.TrySetResult();
                        owner.Connectors.RegionStarted.Task.WaitAsync(Patience).GetAwaiter().GetResult(); // the region is stored
                    }
                });
        }
    }

    /// <summary>
    /// A float32 request field in a query is sent as float32, its declared type: <c>memory_list@1</c> picks the list of
    /// "0.1", as it did reading the request field before queries. The query was sent at the expression's promoted type,
    /// float64, the value printed as 0.10000000149011612 and picked another list (verification 2 of IO step 2).
    /// </summary>
    [Fact]
    public async Task AFloat32RequestFieldInAQuery_IsSentAsFloat32_AndPicksTheListOfItsValue()
    {
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"country\":    { \"type\": \"string?\" }",
                "\"country\":    { \"type\": \"string?\" }, \"affinity\": { \"type\": \"float32?\" }");
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"two_tower\": 40, \"popular\": 20", "\"two_tower\": 40, \"popular\": 20, \"regional\": 5");
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"source_order\": [\"two_tower\", \"popular\"]", "\"source_order\": [\"two_tower\", \"popular\", \"regional\"]");
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"queries\": { \"two_tower\": { \"user_embedding\": \"ctx.user.embedding\" } }",
                "\"queries\": { \"two_tower\": { \"user_embedding\": \"ctx.user.embedding\" }, \"regional\": { \"affinity\": \"ctx.request.affinity\" } }");
            ReplayTests.Edit(root, "bindings/local.jsonc", "\"popular\":   { \"client\": \"memory_list@1\", \"data_file\": \"fixtures/recall_popular.json\" }",
                "\"popular\":   { \"client\": \"memory_list@1\", \"data_file\": \"fixtures/recall_popular.json\" }, " +
                "\"regional\": { \"client\": \"memory_list@1\", \"by_input\": \"affinity\", \"lists\": { \"0.1\": [7], \"0.10000000149011612\": [9], \"*\": [8] } }");
        });
        var sources = new ProbeSources();
        await using var engine = Engine(host, sources);
        var request = Request("req-float32-query");
        request.Inputs.Set("affinity", StrataRec.Core.Values.ScalarValue.FromFloat64(0.1f));

        var result = await engine.ExecuteAsync(request, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(StrataRec.Core.Services.TensorElementType.Float32, sources.Query("regional")["affinity"].Type);
        var regional = Assert.Single(result.Recording!.Runs[0].ServiceCalls, c => c.Service == "regional");
        Assert.Equal("[7]", regional.Ids!.Value.GetRawText().Replace(" ", "", StringComparison.Ordinal));
    }

    internal static RecRequest WithCountry(string requestId)
    {
        var request = Request(requestId);
        request.Inputs.Set("country", "DE");
        return request;
    }

    /// <summary>
    /// A recall reads only the items a service adds: what else it writes is not recorded. An empty list of outputs read
    /// meant "all", so a service's extra tensor and text went into the recording.
    /// </summary>
    [Fact]
    public async Task ARecall_RecordsTheItems_NotOutputsTheServiceWroteBesides()
    {
        var sources = new ProbeSources
        {
            AlsoWrites = (_, response) =>
            {
                response.GetValues<float>("unused", 1).Span[0] = 1;
                response.SetText("unused_text", "x");
            },
        };

        var result = await Engine(HomeFeedTestHost.Load(), sources).ExecuteAsync(Request("req-extra-outputs"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        var recalls = Assert.Single(result.Recording!.Runs).ServiceCalls.FindAll(c => c.Role == IoRoles.Recall);
        Assert.NotEmpty(recalls);
        Assert.All(recalls, c => Assert.Equal((0, 0, true), (c.Outputs.Count, c.Texts.Count, c.Ids!.Value.GetArrayLength() > 0)));
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

    internal static RecEngine Engine(HomeFeedTestHost host, ProbeSources sources, IBindingFactory<IFeatureSourceConnector>? connectors = null)
    {
        var engine = new RecEngine(new PlanEnvironment
        {
            BaseDirectory = host.Root,
            Bindings = TestBindings.With(connectors, sources.Lists, sources.Vectors),
        });
        var load = engine.Load(host.Request);
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        return engine;
    }

    private static List<(long, int)> Items(RecResult result) => result.Items.Select(i => (i.Id.Int64Value, i.Rank)).ToList();

    /// <summary>memory@1 connectors where geo_store waits for <see cref="OpenGeo"/> and region_store for <see cref="OpenRegion"/>.</summary>
    internal sealed class TwoGateConnectors : IBindingFactory<IFeatureSourceConnector>, IDisposable
    {
        private readonly MemoryFeatureConnectorFactory _inner = new();

        public TaskCompletionSource OpenGeo { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource OpenRegion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when region_store is asked: the country's region it is keyed by is stored.</summary>
        public TaskCompletionSource RegionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Type => _inner.Type;

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
        private readonly ConcurrentDictionary<string, (TaskCompletionSource Called, SentQuery? Query)> _calls = new(StringComparer.Ordinal);

        public ProbeSources()
        {
            Lists = new Factory(new MemoryListServiceFactory(), this);
            Vectors = new Factory(new MemoryVectorsServiceFactory(), this);
        }

        public IBindingFactory<IServiceClient> Lists { get; }

        public IBindingFactory<IServiceClient> Vectors { get; }

        /// <summary>Runs when a source is sent, before it answers, with the call's token.</summary>
        public Func<ServiceCall, CancellationToken, ValueTask>? BeforeRetrieve { get; init; }

        /// <summary>Writes into the response too, before the source answers.</summary>
        public Action<ServiceCall, IServiceResponse>? AlsoWrites { get; init; }

        /// <summary>Completes when <paramref name="source"/> is sent.</summary>
        public Task Called(string source) => Entry(source).Called.Task;

        /// <summary>The inputs <paramref name="source"/> was sent, copied when it was (their buffers are the request's).</summary>
        public SentQuery Query(string source) => Entry(source).Query ?? throw new InvalidOperationException($"{source} was not sent");

        private (TaskCompletionSource Called, SentQuery? Query) Entry(string source) =>
            _calls.GetOrAdd(source, _ => (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), null));

        private void Sent(ServiceCall call)
        {
            var entry = Entry(call.Service);
            _calls[call.Service] = (entry.Called, new SentQuery(call.Inputs.Span));
            entry.Called.TrySetResult();
        }

        private sealed class Factory(IBindingFactory<IServiceClient> inner, ProbeSources owner) : IBindingFactory<IServiceClient>
        {
            public string Type => inner.Type;

            public Type SettingsType => inner.SettingsType;

            public IServiceClient Create(BindingContext context) => new Source(inner.Create(context), owner);
        }

        private sealed class Source(IServiceClient inner, ProbeSources owner) : IServiceClient
        {
            public async ValueTask<ServiceStatus> CallAsync(ServiceCall query, IServiceResponse sink, CancellationToken cancellationToken)
            {
                owner.Sent(query);
                if (owner.BeforeRetrieve is { } before)
                {
                    await before(query, cancellationToken).ConfigureAwait(false);
                }

                owner.AlsoWrites?.Invoke(query, sink);

                return await inner.CallAsync(query, sink, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>One input a source was sent, copied.</summary>
    /// <summary>A value as sent; <see cref="Floats"/> holds a float32 or float64 tensor's elements, widened.</summary>
    internal sealed record SentValue(string Name, TensorElementType Type, int[] Shape, bool IsMissing, double[] Floats, string[] Strings)
    {
        public string String => Strings[0];
    }

    /// <summary>The inputs a source was sent, in the order it got them.</summary>
    internal sealed class SentQuery : List<SentValue>
    {
        public SentQuery(ReadOnlySpan<NamedInput> inputs)
        {
            foreach (var input in inputs)
            {
                Add(new SentValue(
                    input.Name, input.ElementType, input.Shape.ToArray(), input.IsMissing,
                    input.IsMissing ? [] : input.ElementType == TensorElementType.Float64 ? input.Float64.ToArray()
                        : input.ElementType == TensorElementType.Float32 ? [.. input.Float32.ToArray().Select(f => (double)f)] : [],
                    !input.IsMissing && input.ElementType == TensorElementType.String ? input.String.ToArray() : []));
            }
        }

        public SentValue this[string name] => TryGet(name, out var value) ? value : throw new KeyNotFoundException(name);

        public bool TryGet(string name, out SentValue value)
        {
            value = Find(v => v.Name == name)!;
            return value is not null;
        }
    }

    /// <summary>
    /// memory@1 connectors where <paramref name="gated"/>'s fetches wait for the gate (open it before a request that should
    /// not wait), and with <paramref name="partial"/> report a partial answer: its features are degraded.
    /// </summary>
    internal sealed class GatedConnectors(string gated, bool partial = false) : IBindingFactory<IFeatureSourceConnector>, IDisposable
    {
        private readonly MemoryFeatureConnectorFactory _inner = new();
        private readonly ManualResetEventSlim _gate = new();

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Type => _inner.Type;

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
/// wait took a fault of the one task it waited for for the node's deadline. Process-wide: a MeterListener.
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFeatureStoredAfterTheDeadline_IsNotSettled_NorIsItsFaultTheNodes(bool fault)
    {
        // The first read is settled at once; its callback runs past the node's budget, and the second read's feature is
        // stored (or its store faults) before the callback returns. The wait had already seen it done, so the second read
        // was settled after the deadline, or the late fault was the node's exception.
        var host = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "pipelines/home_feed.jsonc",
            "\"recall\": {", "\"late\": { \"order\": 50, \"op\": \"LateSettle@1\", \"timeout_ms\": 300, \"config\": {} }, \"recall\": {"));
        using var listener = new MeterListener();
        if (fault)
        {
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
                    if (key == StrataRecTags.Source && (string?)value == "geo_store")
                    {
                        throw new InvalidOperationException("a late store fault");
                    }
                }
            });
            listener.Start();
        }

        using var connectors = new RecallQueryTests.TwoGateConnectors();
        var late = new LateSettleFactory(connectors);
        await using var engine = new RecEngine(new PlanEnvironment
        {
            BaseDirectory = host.Root,
            Bindings = PlanEnvironment.DefaultBindings().Add(connectors),
            Operators = OperatorRegistry.CreateDefault().Add(late),
        });
        var load = engine.Load(host.Request);
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));

        var result = await engine.ExecuteAsync(RecallQueryTests.WithCountry($"req-late-settle-{fault}"), TestContext.Current.CancellationToken)
            .AsTask().WaitAsync(RecallQueryTests.Patience, TestContext.Current.CancellationToken);

        // The gates are opened before the engine is disposed: a fetch a gate still holds keeps its source's instance, and
        // the disposal would wait its whole shutdown timeout for it.
        connectors.Dispose();
        Assert.True(late.StoredLate);
        Assert.False(late.SettledLate);
        Assert.Equal("timed out after 300 ms", result.Trace!.Single(n => n.NodeId == "late").Detail);
    }

    /// <summary>Awaits the user's embedding, then the country's region (held by geo_store's gate).</summary>
    private sealed class LateSettleFactory(RecallQueryTests.TwoGateConnectors connectors) : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } = new("LateSettle", 1, Effects.None, typeof(NoSettings), "Settles a read past its budget.");

        /// <summary>The second read's feature was stored, or failed, while the first read's callback ran past the budget.</summary>
        public bool StoredLate { get; private set; }

        public bool SettledLate { get; private set; }

        private void OpenGeo() => connectors.OpenGeo.TrySetResult();

        public Operator? Compile(OperatorCompileContext context)
        {
            var user = context.CompileAwaitedExpression("ctx.user.embedding", "/user");
            var region = context.CompileAwaitedExpression("ctx.country.region", "/region");
            return user is null || region is null ? null : new Late(this, [context.AwaitedReadsOf([user]), context.AwaitedReadsOf([region])]);
        }

        private sealed class Late(LateSettleFactory owner, AwaitedReads[] reads) : Operator
        {
            public override ValueTask ExecuteAsync(OperatorContext context) => context.WhenEachReadyAsync(reads, (read, _) =>
            {
                if (read == 1)
                {
                    owner.SettledLate = true;
                    return;
                }

                Assert.True(context.CancellationToken.WaitHandle.WaitOne(RecallQueryTests.Patience), "the budget ran out");
                var state = (RequestState)typeof(OperatorContext).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!;
                var stored = state.Features.ContextStoredTask(reads[1].Features[0]);
                owner.OpenGeo();
                owner.StoredLate = ((IAsyncResult)stored).AsyncWaitHandle.WaitOne(RecallQueryTests.Patience);
            });
        }
    }
}
