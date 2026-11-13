using System.Diagnostics;
using System.Runtime.InteropServices;
using StrataRec.Core.Columns;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features;

/// <summary>Regression tests for the feature-runtime findings of the artifacts/feature-runtime review (eb7e005).</summary>
public class FeatureRuntimeReviewRegressionTests
{
    private static readonly long[] TwoItems = { 1, 2 };

    // ---- Finding 1: the context prefetch must hold the request memory through its last staging read ----------------

    [Fact]
    public async Task ContextPrefetch_OutlivingTheRequest_ReadsStagingBeforeBuffersReturn()
    {
        var gate = new TaskCompletionSource();
        var connector = new ScriptedConnector(async (keys, fields, writer, _) =>
        {
            await gate.Task;
            ScriptedConnector.WriteAll(keys, fields, writer, (_, _) => 21.0);
            return FetchStatus.Ok;
        });
        var rig = new FeatureTestRig().Source("user_store", connector);
        var score = rig.Physical("user.score", FeatureTestRig.User, ColumnType.Float64, "user_store");
        var doubled = rig.Derived("user.doubled", FeatureTestRig.User, ColumnType.Float64.AsNullable(),
            TestExpression.ContextTimes(rig.ContextIdOf(score), 2), new[] { score });
        rig.Build();
        var pool = new PoisoningPool();
        var request = rig.NewRequest(TwoItems, userId: 7, pool);

        request.Fetcher.StartContextFetch(new[] { doubled }, CancellationToken.None);
        request.Memory.Release(); // the request fails (or never reads context features) and returns first

        Assert.False(request.Memory.IsReturned);
        gate.SetResult();
        await request.Fetcher.WhenIoCompleteAsync();

        Assert.True(request.Memory.IsReturned);
        Assert.Equal(21.0, request.ContextValue(score).AsFloat64());
        Assert.Equal(42.0, request.ContextValue(doubled).AsFloat64());
        Assert.Equal(FeatureState.Ok, request.Fetcher.GetState(doubled));
    }

    [Fact]
    public async Task WhenIoComplete_CoversTheWholeContextOperation()
    {
        var gate = new TaskCompletionSource();
        var connector = new ScriptedConnector(async (keys, fields, writer, _) =>
        {
            await gate.Task;
            ScriptedConnector.WriteAll(keys, fields, writer, (_, _) => 1.5);
            return FetchStatus.Ok;
        });
        var rig = new FeatureTestRig().Source("user_store", connector);
        var score = rig.Physical("user.score", FeatureTestRig.User, ColumnType.Float64, "user_store");
        rig.Build();
        var request = rig.NewRequest(TwoItems, userId: 7);

        request.Fetcher.StartContextFetch(new[] { score }, CancellationToken.None);
        var io = request.Fetcher.WhenIoCompleteAsync();
        Assert.False(io.IsCompleted);

        gate.SetResult();
        await io;
        Assert.Equal(1.5, request.ContextValue(score).AsFloat64());
    }

    // ---- Finding 2: concurrent max_batch chunks must not share staging state ----------------------------------------

    [Fact]
    public async Task Batches_EachChunkWritesItsOwnWindowOfTheFetchStaging()
    {
        // The chunks of a fetch share its staging arrays (one per field, not one per chunk), each through a window of
        // its own keys: windows never overlap, so concurrent chunks never write the same element.
        var windows = new List<(int Keys, ArraySegment<double> Segment)>();
        var connector = new ScriptedConnector((keys, fields, writer, _) =>
        {
            Assert.True(MemoryMarshal.TryGetArray<double>(writer.GetValues<double>(0), out var segment));
            windows.Add((writer.KeyCount, segment));
            ScriptedConnector.WriteAll(keys, fields, writer, (key, _) => key * 1.5);
            return ValueTask.FromResult(FetchStatus.Ok);
        });
        var rig = new FeatureTestRig().Source("item_store", connector, maxBatch: 2);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        var request = rig.NewRequest(new long[] { 1, 2, 3, 4, 5 });

        await request.EnsureAsync(score);

        Assert.Equal(new[] { 2, 2, 1 }, windows.Select(w => w.Keys));
        Assert.Single(windows.Select(w => w.Segment.Array).Distinct(ReferenceEqualityComparer.Instance));
        Assert.Equal(new[] { (0, 2), (2, 2), (4, 1) }, windows.Select(w => (w.Segment.Offset, w.Segment.Count)));
        for (var row = 0; row < 5; row++)
        {
            Assert.Equal((row + 1) * 1.5, request.Value(score, row).AsFloat64());
        }
    }

    [Fact]
    public async Task Batches_ConcurrentChunks_KeepEveryNumericStringAndListValue()
    {
        const int Count = 512;
        var start = new TaskCompletionSource();
        var connector = new ScriptedConnector(async (keys, fields, writer, _) =>
        {
            await start.Task;
            await Task.Yield();
            ScriptedConnector.WriteAll(keys, fields, writer, (key, field) => field switch
            {
                "score" => key * 0.5,
                "tag" => "t" + key,
                "history" => new[] { key, key + 1, key + 2 },
                _ => null,
            });
            return FetchStatus.Ok;
        });
        var rig = new FeatureTestRig().Source("item_store", connector, timeoutMs: 30_000, maxBatch: 1);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        var tag = rig.Physical("item.tag", FeatureTestRig.Item, ColumnType.String, "item_store");
        var history = rig.Physical("item.history", FeatureTestRig.Item, ColumnType.ListInt64, "item_store");
        rig.Build();
        var request = rig.NewRequest(Enumerable.Range(1, Count).Select(i => (long)i).ToArray(), deadlineMs: 30_000);

        var ensure = request.EnsureAsync(score, tag, history).AsTask();
        start.SetResult();
        var result = await ensure;

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(Count, connector.Calls.Count);
        for (var row = 0; row < Count; row++)
        {
            long key = row + 1;
            Assert.Equal(key * 0.5, request.Value(score, row).AsFloat64());
            Assert.Equal("t" + key, request.Value(tag, row).AsString());
            Assert.Equal(new[] { key, key + 1, key + 2 }, request.Value(history, row).AsInt64List());
        }
    }

    // ---- Finding 3: failed chunks and unwritten fields are never published --------------------------------------------

    [Fact]
    public async Task FailedChunk_FromMemoryConnectorDecodeError_IsNotPublished()
    {
        // Key 1's record cannot be decoded (a string where a float is expected): memory@1 throws for that chunk only.
        var rig = new FeatureTestRig().MemorySource("item_store", """{"1": {"score": "oops"}, "2": {"score": 2.5}}""", maxBatch: 1);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        var request = rig.NewRequest(TwoItems, pool: new PoisoningPool());

        var result = await request.EnsureAsync(score);

        Assert.Equal(FeatureState.Degraded, result.State);
        Assert.Contains("failed", result.Error);
        Assert.True(request.Value(score, 0).IsMissing); // not the pooled garbage left in the staging array
        Assert.Equal(2.5, request.Value(score, 1).AsFloat64());
    }

    [Fact]
    public async Task FailedChunk_KeysTakeTheDefaultLikeAbsentKeys()
    {
        var rig = new FeatureTestRig().MemorySource("item_store", """{"1": {"score": "oops"}, "2": {"score": 2.5}}""", maxBatch: 1);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store", ScalarValue.FromFloat64(0.25));
        rig.Build();
        var request = rig.NewRequest(TwoItems, pool: new PoisoningPool());

        var result = await request.EnsureAsync(score);

        Assert.Equal(FeatureState.Degraded, result.State);
        Assert.Equal(0.25, request.Value(score, 0).AsFloat64());
        Assert.Equal(2.5, request.Value(score, 1).AsFloat64());
    }

    [Fact]
    public async Task FailedChunk_ValuesMarkedValidBeforeTheFailure_AreDiscarded()
    {
        var connector = new ScriptedConnector((keys, fields, writer, _) =>
        {
            ScriptedConnector.WriteAll(keys, fields, writer, (key, _) => key * 10.0);
            if (keys.CandidateKeys.Span[0].Int64Value == 1)
            {
                throw new InvalidOperationException("connection reset after the first record");
            }

            return ValueTask.FromResult(FetchStatus.Ok);
        });
        var rig = new FeatureTestRig().Source("item_store", connector, maxBatch: 1);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        var request = rig.NewRequest(TwoItems);

        var result = await request.EnsureAsync(score);

        Assert.Equal(FeatureState.Degraded, result.State);
        Assert.True(request.Value(score, 0).IsMissing);
        Assert.Equal(20.0, request.Value(score, 1).AsFloat64());
    }

    [Fact]
    public async Task AllChunksFailed_FailsTheFeatureAndLeavesRowsUnfilled()
    {
        var rig = new FeatureTestRig().MemorySource("item_store", """{"1": {"score": "x"}, "2": {"score": "y"}}""", maxBatch: 1);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store", ScalarValue.FromFloat64(0.25));
        rig.Build();
        var request = rig.NewRequest(TwoItems);

        var result = await request.EnsureAsync(score);

        Assert.True(result.Failed); // a source failure never degrades into defaults (design 6.7)
        Assert.False(request.IsFilled(score, 0));
        Assert.False(request.IsFilled(score, 1));
    }

    [Fact]
    public async Task PartialResult_OnlyFieldsTheConnectorMarkedArePublished()
    {
        var connector = new ScriptedConnector((keys, fields, writer, _) =>
        {
            var a = fields.Single(f => f.Field == "a");
            var b = fields.Single(f => f.Field == "b");
            ScriptedConnector.Write(writer, a, 0, 1.0);
            ScriptedConnector.Write(writer, a, 1, 2.0);
            ScriptedConnector.Write(writer, b, 0, 10.0);
            writer.GetValues<double>(b.FieldIndex).Span[1] = 99.0; // written but never marked valid (record cut short)
            return ValueTask.FromResult(new FetchStatus(FetchOutcome.Partial, 1, "1 key truncated"));
        });
        var rig = new FeatureTestRig().Source("item_store", connector);
        var a = rig.Physical("item.a", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        var b = rig.Physical("item.b", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        var request = rig.NewRequest(TwoItems, pool: new PoisoningPool());

        var result = await request.EnsureAsync(a, b);

        Assert.Equal(FeatureState.Degraded, result.State);
        Assert.Equal(1.0, request.Value(a, 0).AsFloat64());
        Assert.Equal(2.0, request.Value(a, 1).AsFloat64());
        Assert.Equal(10.0, request.Value(b, 0).AsFloat64());
        Assert.True(request.Value(b, 1).IsMissing);
    }

    [Fact]
    public async Task FieldsNeverWritten_AreMissingEvenWhenTheKeyWasInTheResponse()
    {
        // Pooled staging arrays are full of a previous user's bytes; only explicit writes may be read.
        var connector = ScriptedConnector.Values((key, field) => field == "a" ? key * 1.0 : null);
        var rig = new FeatureTestRig().Source("item_store", connector);
        var a = rig.Physical("item.a", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        var b = rig.Physical("item.b", FeatureTestRig.Item, ColumnType.Float32, "item_store");
        var c = rig.Physical("item.c", FeatureTestRig.Item, ColumnType.Int64, "item_store", ScalarValue.FromInt64(-1));
        rig.Build();
        var request = rig.NewRequest(TwoItems, pool: new PoisoningPool());

        var result = await request.EnsureAsync(a, b, c);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(2.0, request.Value(a, 1).AsFloat64());
        Assert.True(request.Value(b, 0).IsMissing);
        Assert.Equal(-1, request.Value(c, 1).AsInt64());
    }

    // ---- Finding 4: derived foreign keys are computed before the groups keyed by them ---------------------------------

    [Fact]
    public void Waves_FollowTheDependencyGraphThroughDerivedKeys()
    {
        var (rig, raw, creatorId, followers) = CreatorRig(out _, out _);
        var plan = rig.Plan;

        Assert.Equal(0, plan.Features[raw].Wave);
        Assert.Equal(0, plan.Features[creatorId].Wave); // computed right after wave 0 fetched its input
        Assert.Equal(1, plan.Features[followers].Wave);
        Assert.Equal(1, plan.MaxWave);
    }

    [Fact]
    public async Task DerivedForeignKey_IsComputedBeforeTheGroupKeyedByIt()
    {
        var (rig, _, creatorId, followers) = CreatorRig(out var items, out var creators);
        var request = rig.NewRequest(new long[] { 1, 2, 3 });

        var result = await request.EnsureAsync(followers);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(new long[] { 100, 200 }, creators.Calls.Single().Keys.Select(k => k.Int64Value).OrderBy(k => k).ToArray());
        Assert.Equal(100, request.Value(creatorId, 0).AsInt64());
        Assert.Equal(1000.0, request.Value(followers, 0).AsFloat64());
        Assert.Equal(2000.0, request.Value(followers, 1).AsFloat64());
        Assert.True(request.Value(followers, 2).IsMissing); // item 3 has no creator: legitimately missing
        Assert.Single(items.Calls);

        // A later ensure finds the correct values already filled.
        await request.EnsureAsync(followers);
        Assert.Single(creators.Calls);
        Assert.Equal(1000.0, request.Value(followers, 0).AsFloat64());
    }

    [Fact]
    public async Task DerivedKeyWithoutFetchInputs_PrefetchDoesNotPublishDefaults()
    {
        var creators = ScriptedConnector.Values((key, _) => key * 1.0);
        var rig = new FeatureTestRig().Source("creator_store", creators);
        var creatorId = rig.Derived("item.creator_id", FeatureTestRig.Item, ColumnType.Int64, TestExpression.EntityIdDividedBy(10), Array.Empty<int>());
        var followers = rig.Physical("creator.followers", FeatureTestRig.ForeignKey("creator", "item.creator_id"), ColumnType.Float64,
            "creator_store", ScalarValue.FromFloat64(-1));
        rig.Build();
        Assert.Equal(-1, rig.Plan.Features[creatorId].Wave);
        Assert.Equal(0, rig.Plan.Features[followers].Wave);
        var request = rig.NewRequest(new long[] { 10, 21, 35 });

        request.Fetcher.Prefetch(new[] { followers }, CancellationToken.None); // key not computed yet
        var result = await request.EnsureAsync(followers);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(1.0, request.Value(followers, 0).AsFloat64());
        Assert.Equal(2.0, request.Value(followers, 1).AsFloat64());
        Assert.Equal(3.0, request.Value(followers, 2).AsFloat64());
    }

    [Fact]
    public async Task FailedForeignKey_FailsTheFeaturesKeyedByIt()
    {
        var items = new ScriptedConnector((_, _, _, _) => ValueTask.FromResult(FetchStatus.Failed("item store down")));
        var creators = ScriptedConnector.Values((key, _) => key * 1.0);
        var rig = new FeatureTestRig().Source("item_store", items).Source("creator_store", creators);
        var raw = rig.Physical("item.raw_creator_id", FeatureTestRig.Item, ColumnType.Int64, "item_store");
        var creatorId = rig.Derived("item.creator_id", FeatureTestRig.Item, ColumnType.Int64.AsNullable(),
            TestExpression.CopyInt64("item.raw_creator_id", rig.SlotOf(raw)), new[] { raw });
        var followers = rig.Physical("creator.followers", FeatureTestRig.ForeignKey("creator", "item.creator_id"), ColumnType.Float64,
            "creator_store", ScalarValue.FromFloat64(0));
        rig.Build();
        var request = rig.NewRequest(TwoItems);

        var result = await request.EnsureAsync(followers);

        Assert.True(result.Failed); // not silently the default 0 (design 6.7)
        Assert.Equal(FeatureState.Failed, request.Fetcher.GetState(creatorId));
        Assert.Equal(FeatureState.Failed, request.Fetcher.GetState(followers));
        Assert.Empty(creators.Calls);
    }

    private static (FeatureTestRig Rig, int Raw, int CreatorId, int Followers) CreatorRig(
        out ScriptedConnector items, out ScriptedConnector creators)
    {
        items = ScriptedConnector.Values((key, _) => key == 3 ? null : key * 100);
        creators = ScriptedConnector.Values((key, _) => key * 10.0);
        var rig = new FeatureTestRig().Source("item_store", items).Source("creator_store", creators);
        var raw = rig.Physical("item.raw_creator_id", FeatureTestRig.Item, ColumnType.Int64, "item_store");
        var creatorId = rig.Derived("item.creator_id", FeatureTestRig.Item, ColumnType.Int64.AsNullable(),
            TestExpression.CopyInt64("item.raw_creator_id", rig.SlotOf(raw)), new[] { raw });
        var followers = rig.Physical("creator.followers", FeatureTestRig.ForeignKey("creator", "item.creator_id"), ColumnType.Float64, "creator_store");
        rig.Build();
        return (rig, raw, creatorId, followers);
    }

    // ---- Finding 5: waits are bounded by the source timeout and the request deadline ----------------------------------

    [Fact]
    public async Task HungConnector_WaitEndsAtSourceTimeout_AndMemoryWaitsForTheRealCompletion()
    {
        var release = new TaskCompletionSource();
        var connector = new ScriptedConnector(async (keys, fields, writer, _) =>
        {
            await release.Task; // ignores cancellation
            ScriptedConnector.WriteAll(keys, fields, writer, (_, _) => 5.0); // late write into its staging buffer
            return FetchStatus.Ok;
        });
        var rig = new FeatureTestRig().Source("item_store", connector, timeoutMs: 50);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        var pool = new PoisoningPool();
        var request = rig.NewRequest(TwoItems, pool: pool);

        var elapsed = Stopwatch.StartNew();
        var result = await request.EnsureAsync(score);
        elapsed.Stop();

        Assert.True(result.Failed);
        Assert.Contains("timed out", result.Error);
        Assert.True(elapsed.ElapsedMilliseconds < 5000, $"waited {elapsed.ElapsedMilliseconds} ms");

        request.Memory.Release(); // the request finishes while the connector is still in flight
        Assert.False(request.Memory.IsReturned);
        Assert.Equal(0, pool.Returned);

        release.SetResult();
        await request.Fetcher.WhenIoCompleteAsync();
        Assert.True(request.Memory.IsReturned);
    }

    [Fact]
    public async Task HungConnector_WaitEndsAtRequestDeadline()
    {
        var release = new TaskCompletionSource();
        var connector = new ScriptedConnector(async (_, _, _, _) =>
        {
            await release.Task;
            return FetchStatus.Ok;
        });
        var rig = new FeatureTestRig().Source("item_store", connector, timeoutMs: 600_000);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        var request = rig.NewRequest(TwoItems, deadlineMs: 100);

        var elapsed = Stopwatch.StartNew();
        var result = await request.EnsureAsync(score);
        elapsed.Stop();

        Assert.True(result.Failed);
        Assert.True(elapsed.ElapsedMilliseconds < 10_000, $"waited {elapsed.ElapsedMilliseconds} ms");
        release.SetResult();
        await request.Fetcher.WhenIoCompleteAsync();
    }

    [Fact]
    public async Task HungChunk_IsATimeoutFailure_AndItsLateResultIsRejected()
    {
        var release = new TaskCompletionSource();
        var connector = new ScriptedConnector(async (keys, fields, writer, _) =>
        {
            if (keys.CandidateKeys.Span[0].Int64Value == 1)
            {
                await release.Task;
            }

            ScriptedConnector.WriteAll(keys, fields, writer, (key, _) => key * 3.0);
            return FetchStatus.Ok;
        });
        var rig = new FeatureTestRig().Source("item_store", connector, timeoutMs: 50, maxBatch: 1);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        var request = rig.NewRequest(TwoItems);

        var result = await request.EnsureAsync(score);
        release.SetResult();
        await request.Fetcher.WhenIoCompleteAsync();

        Assert.Equal(FeatureState.Degraded, result.State);
        Assert.Contains("timed out", result.Error);
        Assert.True(request.Value(score, 0).IsMissing);
        Assert.Equal(6.0, request.Value(score, 1).AsFloat64());
    }

    [Fact]
    public async Task ContextFetch_HungConnectorIsBoundedToo()
    {
        var release = new TaskCompletionSource();
        var connector = new ScriptedConnector(async (_, _, _, _) =>
        {
            await release.Task;
            return FetchStatus.Ok;
        });
        var rig = new FeatureTestRig().Source("user_store", connector, timeoutMs: 50);
        var score = rig.Physical("user.score", FeatureTestRig.User, ColumnType.Float64, "user_store");
        rig.Build();
        var request = rig.NewRequest(TwoItems, userId: 7);

        request.Fetcher.StartContextFetch(new[] { score }, CancellationToken.None);
        var result = await request.EnsureAsync(score).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Failed);
        release.SetResult();
        await request.Fetcher.WhenIoCompleteAsync();
    }

    // ---- Finding 6: only the required members of a group are fetched -------------------------------------------------

    [Fact]
    public async Task Ensure_FetchesOnlyTheRequestedMembersOfAGroup()
    {
        var connector = ScriptedConnector.Values((key, field) => field switch
        {
            "clicks_7d" => key * 1.0,
            "embedding" => new[] { (float)key, 0f },
            _ => key * 2.0,
        });
        var rig = new FeatureTestRig().Source("item_store", connector);
        var clicks = rig.Physical("item.clicks_7d", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        var embedding = rig.Physical("item.embedding", FeatureTestRig.Item, ColumnType.Vector(2), "item_store");
        var unused = rig.Physical("item.unused", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        var request = rig.NewRequest(TwoItems);

        await request.EnsureAsync(clicks);

        Assert.Equal(new[] { "clicks_7d" }, connector.Calls.Single().Fields);
        Assert.Equal(2.0, request.Value(clicks, 1).AsFloat64());
        Assert.False(request.IsFilled(embedding, 0));
        Assert.False(request.IsFilled(unused, 0));
        Assert.Equal(FeatureState.Unrequested, request.Fetcher.GetState(embedding));

        // Late materialization: the same group is fetched again for the next field.
        await request.EnsureAsync(embedding);

        var calls = connector.Calls.ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Equal(new[] { "embedding" }, calls[1].Fields);
        Assert.Equal(new[] { 2f, 0f }, request.Value(embedding, 1).AsVector());
        Assert.False(request.IsFilled(unused, 0));
    }

    [Fact]
    public async Task Prefetch_CarriesTheSubset_AndEnsureFetchesOnlyWhatIsStillMissing()
    {
        var connector = ScriptedConnector.Values((key, field) => field == "a" ? key * 1.0 : key * 2.0);
        var rig = new FeatureTestRig().Source("item_store", connector);
        var a = rig.Physical("item.a", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        var b = rig.Physical("item.b", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        var c = rig.Physical("item.c", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        var derived = rig.Derived("item.d", FeatureTestRig.Item, ColumnType.Float64.AsNullable(), TestExpression.ConstantFloat64(1), new[] { c });
        rig.Build();
        var request = rig.NewRequest(TwoItems);

        request.Fetcher.Prefetch(new[] { a }, CancellationToken.None);
        request.Fetcher.Prefetch(new[] { a }, CancellationToken.None); // already in flight: no second call
        await request.EnsureAsync(a, derived);

        var calls = connector.Calls.ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Equal(new[] { "a" }, calls[0].Fields);
        Assert.Equal(new[] { "c" }, calls[1].Fields); // the derived feature's dependency, not b
        Assert.Equal(2.0, request.Value(a, 1).AsFloat64());
        Assert.Equal(4.0, request.Value(c, 1).AsFloat64());
        Assert.False(request.IsFilled(b, 0));
    }

    // ---- Finding 7: every floating-point feature write is sanitized ---------------------------------------------------

    [Fact]
    public async Task Backfill_NonFiniteValues_BecomeDefaultOrMissing_AndAreCounted()
    {
        var values = new[] { double.NaN, double.PositiveInfinity, 1.0, double.NegativeInfinity };
        var connector = ScriptedConnector.Values((key, _) => values[key - 1]);
        var rig = new FeatureTestRig().Source("item_store", connector);
        var nullable64 = rig.Physical("item.x64", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        var defaulted32 = rig.Physical("item.x32", FeatureTestRig.Item, ColumnType.Float32, "item_store", ScalarValue.FromFloat64(0.25));
        rig.Build();
        var request = rig.NewRequest(new long[] { 1, 2, 3, 4 });

        await request.EnsureAsync(nullable64, defaulted32);

        Assert.True(request.Value(nullable64, 0).IsMissing);
        Assert.True(request.Value(nullable64, 1).IsMissing);
        Assert.Equal(1.0, request.Value(nullable64, 2).AsFloat64());
        Assert.True(request.Value(nullable64, 3).IsMissing);
        Assert.Equal(0.25, request.Value(defaulted32, 0).AsFloat64());
        Assert.Equal(1.0, request.Value(defaulted32, 2).AsFloat64());
        Assert.Equal(0.25, request.Value(defaulted32, 3).AsFloat64());
        Assert.Equal(6, request.Fetcher.NonFiniteReplaced);
    }

    [Fact]
    public async Task Derived_NonFiniteWithoutDefault_BecomesMissing_AndIsCounted()
    {
        var rig = new FeatureTestRig();
        var ratio = rig.Derived("item.ratio", FeatureTestRig.Item, ColumnType.Float64.AsNullable(),
            TestExpression.ConstantFloat64(double.PositiveInfinity), Array.Empty<int>());
        var withDefault = rig.Derived("item.ratio_or_zero", FeatureTestRig.Item, ColumnType.Float64,
            TestExpression.ConstantFloat64(double.NaN), Array.Empty<int>(), ScalarValue.FromFloat64(0));
        rig.Build();
        var request = rig.NewRequest(TwoItems);

        var result = await request.EnsureAsync(ratio, withDefault);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.True(request.Value(ratio, 0).IsMissing);
        Assert.True(request.Value(ratio, 1).IsMissing);
        Assert.Equal(0.0, request.Value(withDefault, 1).AsFloat64());
        Assert.Equal(4, request.Fetcher.NonFiniteReplaced);
    }

    [Fact]
    public async Task Derived_NonFiniteInNonNullableColumnWithoutDefault_FailsTheFeature()
    {
        var rig = new FeatureTestRig();
        var ratio = rig.Derived("item.ratio", FeatureTestRig.Item, ColumnType.Float64,
            TestExpression.ConstantFloat64(double.PositiveInfinity, nullable: false), Array.Empty<int>());
        rig.Build();
        var request = rig.NewRequest(TwoItems);

        var result = await request.EnsureAsync(ratio);

        Assert.True(result.Failed);
        Assert.Contains("non-finite", result.Error);
        Assert.Equal(2, request.Fetcher.NonFiniteReplaced);
    }

    [Fact]
    public async Task Derived_ThrowingExpression_FailsOnlyThatFeature()
    {
        var connector = ScriptedConnector.Values((key, _) => key * 1.0);
        var rig = new FeatureTestRig().Source("item_store", connector);
        var physical = rig.Physical("item.a", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        var broken = rig.Derived("item.broken", FeatureTestRig.Item, ColumnType.Float64.AsNullable(),
            new TestExpression("boom()", ColumnType.Float64.AsNullable(), (in EvalContext _, ReadOnlySpan<int> _, ColumnBuffer _) => throw new InvalidOperationException("boom")),
            Array.Empty<int>());
        rig.Build();
        var request = rig.NewRequest(TwoItems);

        var ok = await request.EnsureAsync(physical);
        var failed = await request.EnsureAsync(broken);

        Assert.Equal(FeatureState.Ok, ok.State);
        Assert.Equal(2.0, request.Value(physical, 1).AsFloat64());
        Assert.True(failed.Failed);
        Assert.Contains("item.broken", failed.Error);
        Assert.Contains("boom", failed.Error);
        Assert.False(request.IsFilled(broken, 0));
    }

    [Fact]
    public async Task ContextDerived_ThrowingExpression_FailsOnlyThatFeature()
    {
        var connector = ScriptedConnector.Values((_, _) => 5.0);
        var rig = new FeatureTestRig().Source("user_store", connector);
        var age = rig.Physical("user.age", FeatureTestRig.User, ColumnType.Float64, "user_store");
        var broken = rig.Derived("user.broken", FeatureTestRig.User, ColumnType.Float64.AsNullable(),
            new TestExpression("boom()", ColumnType.Float64.AsNullable(), scalar: _ => throw new InvalidOperationException("boom")),
            Array.Empty<int>());
        rig.Build();
        var request = rig.NewRequest(TwoItems, userId: 7);

        request.Fetcher.StartContextFetch(new[] { age, broken }, CancellationToken.None);
        var ok = await request.EnsureAsync(age);
        var failed = await request.EnsureAsync(broken);

        Assert.Equal(FeatureState.Ok, ok.State);
        Assert.Equal(5.0, request.ContextValue(age).AsFloat64());
        Assert.True(failed.Failed);
        Assert.Contains("user.broken", failed.Error);
    }

    [Fact]
    public async Task Context_NonFiniteValues_BecomeDefaultOrMissing_AndAreCounted()
    {
        var connector = ScriptedConnector.Values((_, field) => field == "nan" ? double.NaN : double.PositiveInfinity);
        var rig = new FeatureTestRig().Source("user_store", connector);
        var nan = rig.Physical("user.nan", FeatureTestRig.User, ColumnType.Float64, "user_store");
        var inf = rig.Physical("user.inf", FeatureTestRig.User, ColumnType.Float32, "user_store", ScalarValue.FromFloat64(3));
        var safe = rig.Physical("user.safe", FeatureTestRig.User, ColumnType.Float64, "user_store", ScalarValue.FromFloat64(1));
        var derived = rig.Derived("user.derived", FeatureTestRig.User, ColumnType.Float64.AsNullable(),
            new TestExpression("1.0 / 0.0", ColumnType.Float64.AsNullable(), scalar: _ => ScalarValue.FromFloat64(double.PositiveInfinity)),
            Array.Empty<int>());
        rig.Build();
        var request = rig.NewRequest(TwoItems, userId: 7);

        request.Fetcher.StartContextFetch(new[] { nan, inf, derived }, CancellationToken.None);
        await request.EnsureAsync(nan, inf, derived);

        Assert.True(request.ContextValue(nan).IsMissing);
        Assert.Equal(3.0, request.ContextValue(inf).AsFloat64());
        Assert.True(request.ContextValue(derived).IsMissing);
        Assert.True(request.ContextValue(safe).IsMissing); // not requested: not fetched
        Assert.Equal(3, request.Fetcher.NonFiniteReplaced);
    }
}
