using System.Collections.Concurrent;
using System.Collections.Immutable;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Catalog;
using StrataRec.Features.Connectors;
using StrataRec.Features.Local;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features;

/// <summary>
/// Keys read from context features (design 6.2, 6.8 item 5): a request carries a login key, one source resolves it to a
/// user id, another is keyed by the user id. The context prefetch starts each group once what its key reads is stored, and
/// waits for nothing else; a missing key part is no key, a failed one fails the features as a foreign key does, and the
/// request memory is held until every group started is stored.
/// </summary>
public class ChainedContextKeyTests
{
    private static readonly long[] TwoItems = [1, 2];

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Keyed by the request's login key.</summary>
    private static readonly EntityDefinition Identity = EntityDefinition.Of("identity", EntityKeyPartDefinition.RequestInput("user_key", EntityKeyType.String));

    /// <summary>Keyed by the user id the identity source resolved: the chain's second level.</summary>
    private static readonly EntityDefinition Account = EntityDefinition.Of("account", EntityKeyPartDefinition.ContextFeature("identity.user_id", EntityKeyType.Int64));

    /// <summary>The third level: keyed by the account's household.</summary>
    private static readonly EntityDefinition Household = EntityDefinition.Of("household", EntityKeyPartDefinition.ContextFeature("account.household_id", EntityKeyType.Int64));

    /// <summary>The fourth: keyed by the household's region (a string).</summary>
    private static readonly EntityDefinition Region = EntityDefinition.Of("region", EntityKeyPartDefinition.ContextFeature("household.region", EntityKeyType.String));

    /// <summary>Keyed by the login key too, but read by no key: an unrelated source.</summary>
    private static readonly EntityDefinition Session = EntityDefinition.Of("session", EntityKeyPartDefinition.RequestInput("user_key", EntityKeyType.String));

    /// <summary>A candidate-scoped entity with a shared context-feature part: the item in the user's segment.</summary>
    private static readonly EntityDefinition ItemSegment = EntityDefinition.Of(
        "item_segment", EntityKeyPartDefinition.EntityId(EntityKeyType.Int64), EntityKeyPartDefinition.ContextFeature("user.segment", EntityKeyType.String));

    /// <summary>
    /// A connector that writes <paramref name="valueOf"/>(key part 0, field) for each key, after <paramref name="gate"/>
    /// when given (asynchronously then), and answers <paramref name="status"/>.
    /// </summary>
    private static ScriptedConnector Answering(Func<EntityKey, string, object?> valueOf, Task? gate = null, FetchStatus? status = null) =>
        new(async (keys, fields, writer, _) =>
        {
            if (gate is not null)
            {
                await gate;
            }

            for (var k = 0; k < keys.Count; k++)
            {
                foreach (var field in fields)
                {
                    ScriptedConnector.Write(writer, field, k, valueOf(keys.Key(k, 0), field.Field));
                }
            }

            return status ?? FetchStatus.Ok;
        });

    /// <summary>Resolves login key k-&lt;n&gt; to user id n; any other key to nothing.</summary>
    private static object? UserIdOf(EntityKey key, string field) =>
        key.StringValue is { } text && text.StartsWith("k-", StringComparison.Ordinal)
            && long.TryParse(text.AsSpan(2), System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>The rig of a two-level chain: identity (login key → user id), then account (user id → tier = id / 1000).</summary>
    private static (FeatureTestRig Rig, int UserKey, int UserId, int Tier) Chain(
        IFeatureSourceConnector identity, IFeatureSourceConnector accounts, ScalarValue? tierDefault = null, ScalarValue? userIdDefault = null)
    {
        var rig = new FeatureTestRig().Source("identity_store", identity).Source("account_store", accounts);
        var userKey = rig.RequestInput("user_key");
        var userId = rig.Physical("identity.user_id", Identity, ColumnType.Int64, "identity_store", userIdDefault);
        var tier = rig.Physical("account.tier", Account, ColumnType.Float64, "account_store", tierDefault);
        rig.Build();
        return (rig, userKey, userId, tier);
    }

    private static TestRequest Request(FeatureTestRig rig, int userKey, string? key, IBufferPool? pool = null, IFeatureCallHook? hook = null)
    {
        var request = rig.NewRequest(TwoItems, pool: pool, hook: hook);
        if (key is not null)
        {
            request.Context[userKey] = ScalarValue.FromString(key);
        }

        return request;
    }

    [Fact]
    public async Task AGroupKeyedByAContextFeature_StartsOnceItIsStored_WithTheStoredValueAsItsKey()
    {
        var gate = new TaskCompletionSource();
        var accounts = Answering((key, _) => key.Int64Value / 1000.0);
        var (rig, userKey, userId, tier) = Chain(Answering(UserIdOf, gate.Task), accounts);
        Assert.Equal([userId], rig.Plan.Groups.Single(g => g.SourceName == "account_store").ContextKeyFeatures);
        Assert.Equal([userId], rig.Plan.Features[tier].Dependencies);
        var request = Request(rig, userKey, "k-1001");

        request.Fetcher.StartContextFetch([tier], CancellationToken.None);
        var read = request.Fetcher.EnsureAsync([tier], CancellationToken.None).AsTask();
        Assert.Empty(accounts.Calls); // the key is not stored yet
        Assert.False(read.IsCompleted);

        gate.SetResult();
        var result = await read.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(1001, request.ContextValue(userId).AsInt64());
        Assert.Equal(1.001, request.ContextValue(tier).AsFloat64());
        Assert.Equal(EntityKey.FromInt64(1001), Assert.Single(accounts.Calls).ContextKey);
        await request.Fetcher.WhenIoCompleteAsync();
    }

    /// <summary>B6 for starts: a chained group waits for the group it reads, not for a slower one it does not.</summary>
    [Fact]
    public async Task AChainedGroup_DoesNotWaitForAnUnrelatedSlowGroup()
    {
        var slow = new TaskCompletionSource();
        var identityGate = new TaskCompletionSource();
        var accounts = Answering((key, _) => key.Int64Value / 1000.0);
        var sessions = Answering((_, _) => 2.5, slow.Task);
        var rig = new FeatureTestRig().Source("identity_store", Answering(UserIdOf, identityGate.Task)).Source("account_store", accounts).Source("session_store", sessions);
        var userKey = rig.RequestInput("user_key");
        rig.Physical("identity.user_id", Identity, ColumnType.Int64, "identity_store");
        var tier = rig.Physical("account.tier", Account, ColumnType.Float64, "account_store");
        var session = rig.Physical("session.score", Session, ColumnType.Float64, "session_store");
        rig.Build();
        var request = Request(rig, userKey, "k-7");

        request.Fetcher.StartContextFetch([session, tier], CancellationToken.None);
        var sessionRead = request.Fetcher.EnsureAsync([session], CancellationToken.None).AsTask();
        identityGate.SetResult();
        await request.Fetcher.EnsureAsync([tier], CancellationToken.None).AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(0.007, request.ContextValue(tier).AsFloat64());
        Assert.Single(accounts.Calls);
        Assert.False(sessionRead.IsCompleted); // the slow group is still out

        slow.SetResult();
        await sessionRead.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(2.5, request.ContextValue(session).AsFloat64());
        await request.Fetcher.WhenIoCompleteAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThreeLevelsOfChain_EachStartWithTheKeyTheLevelBeforeStored(bool asynchronous)
    {
        // identity (login key → user 1001) → account (1001 → household 77) → household (77 → region "eu") → region ("eu" → 1.25).
        var order = new ConcurrentQueue<string>();
        ScriptedConnector Level(string name, Func<EntityKey, object?> value) => new(async (keys, fields, writer, _) =>
        {
            if (asynchronous)
            {
                await Task.Yield(); // answers on another thread
            }

            order.Enqueue(name + ":" + keys.Key(0, 0));
            ScriptedConnector.Write(writer, fields[0], 0, value(keys.Key(0, 0)));
            return FetchStatus.Ok;
        });
        var rig = new FeatureTestRig()
            .Source("identity_store", Level("identity", key => UserIdOf(key, "user_id")))
            .Source("account_store", Level("account", key => key.Int64Value == 1001 ? 77L : null))
            .Source("household_store", Level("household", key => key.Int64Value == 77 ? "eu" : null))
            .Source("region_store", Level("region", key => key.StringValue == "eu" ? 1.25 : null));
        var userKey = rig.RequestInput("user_key");
        var userId = rig.Physical("identity.user_id", Identity, ColumnType.Int64, "identity_store");
        var householdId = rig.Physical("account.household_id", Account, ColumnType.Int64, "account_store");
        var regionName = rig.Physical("household.region", Household, ColumnType.String, "household_store");
        var boost = rig.Physical("region.boost", Region, ColumnType.Float64, "region_store", ScalarValue.FromFloat64(1.0));
        rig.Build();
        var request = Request(rig, userKey, "k-1001");

        request.Fetcher.StartContextFetch([boost], CancellationToken.None);
        if (!asynchronous)
        {
            // Synchronous sources: the whole chain is stored before the start returns, with nothing scheduled.
            Assert.True(request.Fetcher.WhenIoCompleteAsync().IsCompletedSuccessfully);
        }

        var result = await request.Fetcher.EnsureAsync([boost], CancellationToken.None).AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal((1001L, 77L, "eu", 1.25), (request.ContextValue(userId).AsInt64(), request.ContextValue(householdId).AsInt64(),
            request.ContextValue(regionName).AsString(), request.ContextValue(boost).AsFloat64()));
        Assert.Equal(["identity:k-1001", "account:1001", "household:77", "region:eu"], order);
        await request.Fetcher.WhenIoCompleteAsync();
    }

    /// <summary>
    /// Fourteen levels, each keyed by eight features of the level before (review 76's shape, as context keys): each group
    /// starts once, after its parent, with its parent's eight values as its key.
    /// </summary>
    [Fact]
    public async Task AChainOfEightPartKeys_StartsEachGroupOnce()
    {
        var calls = new ConcurrentQueue<(string Source, long[] Key)>();
        ScriptedConnector Level(string name) => new(async (keys, fields, writer, _) =>
        {
            await Task.Yield();
            var key = new long[keys.PartCount];
            for (var p = 0; p < key.Length; p++)
            {
                key[p] = keys.Key(0, p).Int64Value;
            }

            calls.Enqueue((name, key));
            for (var f = 0; f < fields.Length; f++)
            {
                ScriptedConnector.Write(writer, fields[f], 0, key.Sum() + f); // the next level's key parts
            }

            return FetchStatus.Ok;
        });
        var rig = new FeatureTestRig();
        var entity = FeatureTestRig.User;
        var last = -1;
        for (var level = 0; level < 14; level++)
        {
            var source = $"l{level:D2}_store";
            rig.Source(source, Level(source));
            for (var p = 0; p < 8; p++)
            {
                last = rig.Physical($"l{level:D2}.k{p}", entity, ColumnType.Int64, source);
            }

            var parts = new EntityKeyPartDefinition[8];
            for (var p = 0; p < 8; p++)
            {
                parts[p] = EntityKeyPartDefinition.ContextFeature($"l{level:D2}.k{p}", EntityKeyType.Int64);
            }

            entity = EntityDefinition.Of($"l{level + 1:D2}", parts);
        }

        rig.Build();
        var request = rig.NewRequest(TwoItems, userId: 1);

        request.Fetcher.StartContextFetch([last], CancellationToken.None);
        var result = await request.Fetcher.EnsureAsync([last], CancellationToken.None).AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(Enumerable.Range(0, 14).Select(l => $"l{l:D2}_store"), calls.Select(c => c.Source)); // once each, in order
        Assert.Equal(1, Assert.Single(calls.First().Key));
        Assert.Equal([1L, 2, 3, 4, 5, 6, 7, 8], calls.ElementAt(1).Key); // level 0 answered 1 + f for its eight fields
        await request.Fetcher.WhenIoCompleteAsync();
    }

    /// <summary>
    /// A key read through a derived context feature: computed as soon as its inputs are stored, so the group keyed by it
    /// does not wait for the whole prefetch; a derived context feature no key reads still does.
    /// </summary>
    [Fact]
    public async Task AKeyThroughADerivedContextFeature_IsComputedAsSoonAsItsInputsAreStored()
    {
        var slow = new TaskCompletionSource();
        var shards = Answering((key, _) => key.Int64Value * 1.5);
        var rig = new FeatureTestRig()
            .Source("identity_store", Answering(UserIdOf))
            .Source("shard_store", shards)
            .Source("session_store", Answering((_, _) => 2.5, slow.Task));
        var userKey = rig.RequestInput("user_key");
        var userId = rig.Physical("identity.user_id", Identity, ColumnType.Int64, "identity_store");
        var userIdContext = rig.ContextIdOf(userId);
        var shardOf = new TestExpression("ctx.identity.user_id / 100", ColumnType.Int64.AsNullable(), scalar: context =>
            context[userIdContext].IsMissing ? ScalarValue.Missing : ScalarValue.FromInt64(context[userIdContext].AsInt64() / 100));
        var shard = rig.Derived("identity.shard", Identity, ColumnType.Int64.AsNullable(), shardOf, [userId]);
        var shardEntity = EntityDefinition.Of("shard", EntityKeyPartDefinition.ContextFeature("identity.shard", EntityKeyType.Int64));
        var weight = rig.Physical("shard.weight", shardEntity, ColumnType.Float64, "shard_store");
        var session = rig.Physical("session.score", Session, ColumnType.Float64, "session_store");
        var late = rig.Derived("identity.doubled", Identity, ColumnType.Float64.AsNullable(), TestExpression.ContextTimes(userIdContext, 2), [userId]);
        rig.Build();
        Assert.True(rig.Plan.Features[shard].ComputedEarly);
        Assert.False(rig.Plan.Features[late].ComputedEarly);
        var request = Request(rig, userKey, "k-1001");

        request.Fetcher.StartContextFetch([weight, session, late], CancellationToken.None);
        var weightRead = await request.Fetcher.EnsureAsync([weight], CancellationToken.None).AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
        var lateRead = request.Fetcher.EnsureAsync([late], CancellationToken.None).AsTask();

        Assert.Equal(FeatureState.Ok, weightRead.State);
        Assert.Equal(10, request.ContextValue(shard).AsInt64());
        Assert.Equal(15.0, request.ContextValue(weight).AsFloat64());
        Assert.Equal(EntityKey.FromInt64(10), Assert.Single(shards.Calls).ContextKey);
        Assert.False(lateRead.IsCompleted); // computed with the whole prefetch, which waits for the slow session

        slow.SetResult();
        await lateRead.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(2002.0, request.ContextValue(late).AsFloat64());
    }

    /// <summary>
    /// A candidate-scoped entity with a shared context-feature part (design 6.8 item 4): fetched once the candidates and the
    /// context feature are there, the part carried once per batch; its fetch waits for the group that stores the part, not
    /// for the whole prefetch, and a prefetch before the part is stored does not start it.
    /// </summary>
    [Fact]
    public async Task ASharedContextFeaturePart_IsReadOnceTheGroupThatStoresItIs()
    {
        var segmentGate = new TaskCompletionSource();
        var slow = new TaskCompletionSource();
        var seen = new ConcurrentQueue<(bool Shared, int Length)>();
        var boosts = new ScriptedConnector((keys, fields, writer, _) =>
        {
            seen.Enqueue((keys.IsShared(1), keys.Part(1).Length));
            for (var k = 0; k < keys.Count; k++)
            {
                ScriptedConnector.Write(writer, fields[0], k, (keys.Key(k, 0).Int64Value * 10.0) + (keys.Key(k, 1).StringValue == "a" ? 1 : 2));
            }

            return ValueTask.FromResult(FetchStatus.Ok);
        });
        var rig = new FeatureTestRig()
            .Source("user_store", Answering((_, _) => "a", segmentGate.Task))
            .Source("is_store", boosts)
            .Source("session_store", Answering((_, _) => 2.5, slow.Task));
        var userKey = rig.RequestInput("user_key");
        var segment = rig.Physical("user.segment", FeatureTestRig.User, ColumnType.String, "user_store");
        var boost = rig.Physical("item_segment.boost", ItemSegment, ColumnType.Float64, "is_store", ScalarValue.FromFloat64(1.0));
        var session = rig.Physical("session.score", Session, ColumnType.Float64, "session_store");
        rig.Build();
        var group = rig.Plan.Groups.Single(g => g.SourceName == "is_store");
        Assert.Equal([segment], group.KeyFeatures);
        Assert.Equal([segment], group.ContextKeyFeatures);
        Assert.Equal(0, group.Wave); // a context part is there before any candidate wave
        var request = rig.NewRequest([3, 1, 3], userId: 7);
        request.Context[userKey] = ScalarValue.FromString("k-7");

        request.Fetcher.StartContextFetch([segment, session], CancellationToken.None);
        request.Fetcher.Prefetch([boost], CancellationToken.None);
        Assert.Empty(boosts.Calls); // the shared part is not stored yet: nothing to prefetch with
        var read = request.Fetcher.EnsureAsync([boost], CancellationToken.None).AsTask();
        Assert.False(read.IsCompleted);

        segmentGate.SetResult();
        var result = await read.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(FeatureState.Ok, result.State);
        var call = Assert.Single(boosts.Calls);
        Assert.Equal(new EntityKey[][] { [EntityKey.FromInt64(3), EntityKey.FromString("a")], [EntityKey.FromInt64(1), EntityKey.FromString("a")] }, call.Tuples);
        Assert.Equal([(true, 1)], seen);
        Assert.Equal([31.0, 11.0, 31.0], Enumerable.Range(0, 3).Select(row => request.Value(boost, row).AsFloat64()));
        Assert.Equal(FeatureState.Unrequested, request.Fetcher.GetState(session)); // still out: the fetch did not wait for it

        slow.SetResult();
        await request.Fetcher.WhenIoCompleteAsync();
    }

    [Fact]
    public async Task ASharedContextFeaturePart_ThatIsStored_IsPrefetched_AndOneThatIsMissing_IsNoKey()
    {
        var boosts = Answering((key, _) => key.Int64Value * 10.0);
        var rig = new FeatureTestRig().Source("user_store", Answering((key, _) => key.Int64Value == 7 ? "a" : null)).Source("is_store", boosts);
        var segment = rig.Physical("user.segment", FeatureTestRig.User, ColumnType.String, "user_store");
        var boost = rig.Physical("item_segment.boost", ItemSegment, ColumnType.Float64, "is_store", ScalarValue.FromFloat64(1.0));
        rig.Build();

        var known = rig.NewRequest([3, 1], userId: 7);
        known.Fetcher.StartContextFetch([segment], CancellationToken.None);
        known.Fetcher.Prefetch([boost], CancellationToken.None); // the segment is stored (a synchronous source): prefetched
        Assert.Single(boosts.Calls);
        await known.EnsureAsync(boost);
        Assert.Single(boosts.Calls);
        Assert.Equal(30.0, known.Value(boost, 0).AsFloat64());

        var unknown = rig.NewRequest([3, 1], userId: 8); // no segment for user 8: no key, the default
        unknown.Fetcher.StartContextFetch([segment], CancellationToken.None);
        Assert.Equal(FeatureState.Ok, (await unknown.EnsureAsync(boost)).State);
        Assert.Single(boosts.Calls);
        Assert.Equal([1.0, 1.0], Enumerable.Range(0, 2).Select(row => unknown.Value(boost, row).AsFloat64()));
    }

    /// <summary>A key part whose feature is missing is no key: nothing is called and the features are absent, ok.</summary>
    [Theory]
    [InlineData("k-unknown")] // the identity source has no user for it
    [InlineData(null)] // no login key: the identity is not looked up either
    public async Task AMissingKeyFeature_IsNoKey_NothingIsCalled_AndTheFeaturesAreAbsent(string? key)
    {
        var identity = Answering(UserIdOf);
        var accounts = Answering((k, _) => k.Int64Value / 1000.0);
        var (rig, userKey, userId, tier) = Chain(identity, accounts, tierDefault: ScalarValue.FromFloat64(0.5));
        var request = Request(rig, userKey, key);

        request.Fetcher.StartContextFetch([tier], CancellationToken.None);
        var result = await request.EnsureAsync(tier);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.True(request.ContextValue(userId).IsMissing);
        Assert.Equal(0.5, request.ContextValue(tier).AsFloat64()); // the default, as for a missing request input
        Assert.Empty(accounts.Calls);
        Assert.Equal(key is null ? 0 : 1, identity.Calls.Count);
    }

    [Fact]
    public async Task AKeyFeatureWithADefault_KeysTheGroupByTheDefault_WhenItsSourceHasNoValue()
    {
        var accounts = Answering((k, _) => k.Int64Value + 0.5);
        var (rig, userKey, _, tier) = Chain(Answering(UserIdOf), accounts, userIdDefault: ScalarValue.FromInt64(0));
        var request = Request(rig, userKey, "k-unknown");

        request.Fetcher.StartContextFetch([tier], CancellationToken.None);
        await request.EnsureAsync(tier);

        Assert.Equal(EntityKey.FromInt64(0), Assert.Single(accounts.Calls).ContextKey); // absent is the default: a value
        Assert.Equal(0.5, request.ContextValue(tier).AsFloat64());
    }

    /// <summary>
    /// The foreign-key rule (design 6.7): a failed key feature fails the features keyed by it, without a call, and a reader
    /// fails; a degraded one degrades them.
    /// </summary>
    [Theory]
    [InlineData(FetchOutcome.Failed)]
    [InlineData(FetchOutcome.Partial)]
    public async Task AFailedKeyFeature_FailsTheFeaturesWithoutACall_ADegradedOneDegradesThem(FetchOutcome upstream)
    {
        var status = upstream == FetchOutcome.Failed ? FetchStatus.Failed("identity service unavailable") : new FetchStatus(FetchOutcome.Partial, 1, "one shard of identity timed out");
        var accounts = Answering((k, _) => k.Int64Value / 1000.0);
        var (rig, userKey, userId, tier) = Chain(Answering(UserIdOf, status: status), accounts, tierDefault: ScalarValue.FromFloat64(0.5));
        var request = Request(rig, userKey, "k-1001");

        request.Fetcher.StartContextFetch([tier], CancellationToken.None);
        var result = await request.EnsureAsync(tier);

        if (upstream == FetchOutcome.Failed)
        {
            Assert.Equal(FeatureState.Failed, result.State);
            Assert.Equal(FeatureState.Failed, request.Fetcher.GetState(tier));
            Assert.Contains("identity service unavailable", result.Error, StringComparison.Ordinal);
            Assert.Empty(accounts.Calls);
        }
        else
        {
            Assert.Equal(FeatureState.Degraded, result.State);
            Assert.Equal(FeatureState.Degraded, request.Fetcher.GetState(tier));
            Assert.Equal(1.001, request.ContextValue(tier).AsFloat64());
            Assert.Single(accounts.Calls);
        }

        Assert.Equal(upstream == FetchOutcome.Failed ? FeatureState.Failed : FeatureState.Degraded, request.Fetcher.GetState(userId));
    }

    [Fact]
    public async Task AFailedSharedContextFeaturePart_FailsTheCandidateFeatures_WithoutACall()
    {
        var boosts = Answering((key, _) => key.Int64Value * 10.0);
        var rig = new FeatureTestRig()
            .Source("user_store", Answering((_, _) => "a", status: FetchStatus.Failed("user store down")))
            .Source("is_store", boosts);
        var segment = rig.Physical("user.segment", FeatureTestRig.User, ColumnType.String, "user_store");
        var boost = rig.Physical("item_segment.boost", ItemSegment, ColumnType.Float64, "is_store", ScalarValue.FromFloat64(1.0));
        rig.Build();
        var request = rig.NewRequest([3, 1], userId: 7);

        request.Fetcher.StartContextFetch([segment], CancellationToken.None);
        var result = await request.EnsureAsync(boost);

        Assert.Equal(FeatureState.Failed, result.State);
        Assert.Equal(FeatureState.Failed, request.Fetcher.GetState(boost));
        Assert.Empty(boosts.Calls);
    }

    /// <summary>
    /// Review 72 for chains: a group started once its key was stored, after the request let go of its memory, holds it
    /// until it is stored: it reads its staging and writes its value before the buffers go back.
    /// </summary>
    [Fact]
    public async Task AChainedGroupStartedAfterTheRequestReturned_IsStoredBeforeTheMemoryGoesBack()
    {
        var identityGate = new TaskCompletionSource();
        var accountGate = new TaskCompletionSource();
        var accounts = Answering((k, _) => k.Int64Value / 1000.0, accountGate.Task);
        var (rig, userKey, _, tier) = Chain(Answering(UserIdOf, identityGate.Task), accounts);
        var request = Request(rig, userKey, "k-1001", new PoisoningPool());

        request.Fetcher.StartContextFetch([tier], CancellationToken.None);
        request.Memory.Release(); // the request fails, or never reads the feature, and returns first
        identityGate.SetResult();
        Assert.True(SpinWait.SpinUntil(() => !accounts.Calls.IsEmpty, Timeout)); // started with the stored key
        Assert.False(request.Memory.IsReturned);

        accountGate.SetResult();
        await request.Fetcher.WhenIoCompleteAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.True(request.Memory.IsReturned);
        Assert.Equal(FeatureState.Ok, request.Fetcher.GetState(tier));
        Assert.Equal(1.001, request.ContextValue(tier).AsFloat64()); // read from the staging before it went back
    }

    /// <summary>
    /// Review 72 for chains: when a later start throws (here a recording hook, as a second chained group is sent), the
    /// chained group already sent is still stored before the memory goes back, and the prefetch still fails.
    /// </summary>
    [Fact]
    public async Task AChainedGroupSentBeforeALaterStartThrew_IsStoredBeforeTheMemoryGoesBack()
    {
        var accountGate = new TaskCompletionSource();
        var accounts = Answering((k, _) => k.Int64Value / 1000.0, accountGate.Task);
        var rig = new FeatureTestRig()
            .Source("identity_store", Answering(UserIdOf))
            .Source("account_store", accounts)
            .Source("profile_store", Answering((_, _) => 9.0));
        var userKey = rig.RequestInput("user_key");
        rig.Physical("identity.user_id", Identity, ColumnType.Int64, "identity_store");
        var tier = rig.Physical("account.tier", Account, ColumnType.Float64, "account_store");
        var profile = EntityDefinition.Of("profile", EntityKeyPartDefinition.ContextFeature("identity.user_id", EntityKeyType.Int64));
        var bio = rig.Physical("profile.score", profile, ColumnType.Float64, "profile_store", ScalarValue.FromFloat64(7));
        rig.Build();
        var hook = new ThrowingHook(begin: 3); // identity is begun first, then account, then profile
        var request = Request(rig, userKey, "k-1001", new PoisoningPool(), hook);

        request.Fetcher.StartContextFetch([tier, bio], CancellationToken.None);
        request.Memory.Release();
        accountGate.SetResult();
        await Assert.ThrowsAsync<IOException>(() => request.Fetcher.WhenIoCompleteAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken));

        Assert.True(request.Memory.IsReturned);
        Assert.Equal(FeatureState.Ok, request.Fetcher.GetState(tier));
        Assert.Equal(1.001, request.ContextValue(tier).AsFloat64());
        Assert.Equal(["identity_store", "account_store"], hook.Begun);
    }

    /// <summary>
    /// A group whose key feature's store failed with an exception (here recording its call) is not started: nothing is left
    /// running, the memory goes back and the prefetch fails.
    /// </summary>
    [Fact]
    public async Task AGroupWhoseKeyFailedWithAnException_IsNotStarted()
    {
        var identityGate = new TaskCompletionSource();
        var accounts = Answering((k, _) => k.Int64Value / 1000.0);
        var (rig, userKey, _, tier) = Chain(Answering(UserIdOf, identityGate.Task), accounts);
        var request = Request(rig, userKey, "k-1001", new PoisoningPool(), new ThrowingHook(recordSource: "identity_store"));

        request.Fetcher.StartContextFetch([tier], CancellationToken.None);
        var read = request.Fetcher.EnsureAsync([tier], CancellationToken.None).AsTask();
        request.Memory.Release();
        identityGate.SetResult();

        await Assert.ThrowsAsync<IOException>(() => request.Fetcher.WhenIoCompleteAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<IOException>(() => read.WaitAsync(Timeout, TestContext.Current.CancellationToken)); // the reader sees the failure
        Assert.Empty(accounts.Calls);
        Assert.True(request.Memory.IsReturned);
    }

    /// <summary>A key stored after the request ended is not sent: nothing can read it any more.</summary>
    [Fact]
    public async Task AKeyStoredAfterTheRequestEnded_IsNotSent()
    {
        var identityGate = new TaskCompletionSource();
        var accounts = Answering((k, _) => k.Int64Value / 1000.0);
        var (rig, userKey, userId, tier) = Chain(Answering(UserIdOf, identityGate.Task), accounts);
        var request = Request(rig, userKey, "k-1001", new PoisoningPool());

        request.Fetcher.StartContextFetch([tier], CancellationToken.None);
        request.Fetcher.EndRequest();
        request.Memory.Release();
        identityGate.SetResult();
        await request.Fetcher.WhenIoCompleteAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.True(request.Memory.IsReturned);
        Assert.Empty(accounts.Calls);
        Assert.Equal(FeatureState.Ok, request.Fetcher.GetState(userId)); // what was sent before the end is stored
        Assert.Equal(FeatureState.Failed, request.Fetcher.GetState(tier));
    }

    [Fact]
    public async Task AChainedGroup_IsNotSentOnceTheCallerHasCancelled()
    {
        // The identity is local, so its read does not see the cancellation; the account is a call, which is not sent.
        var accounts = Answering((k, _) => k.Int64Value / 1000.0);
        var rig = new FeatureTestRig()
            .LocalSource("identity_store", writer => WriteRecord(writer, EntityKey.FromString("k-1001"), "user_id", ScalarValue.FromInt64(1001)))
            .Source("account_store", accounts);
        var userKey = rig.RequestInput("user_key");
        var userId = rig.Physical("identity.user_id", Identity, ColumnType.Int64, "identity_store");
        var tier = rig.Physical("account.tier", Account, ColumnType.Float64, "account_store");
        rig.Build();
        var request = Request(rig, userKey, "k-1001");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        request.Fetcher.StartContextFetch([tier], cancelled.Token);
        await request.Fetcher.WhenIoCompleteAsync();

        Assert.Equal(1001, request.ContextValue(userId).AsInt64());
        Assert.Empty(accounts.Calls);
        Assert.Equal(FeatureState.Failed, request.Fetcher.GetState(tier));
    }

    /// <summary>Local and cache supply of a chained entity (design 6.6): read once the key is stored, by the same rules.</summary>
    [Fact]
    public async Task AChainedEntity_ReadsItsLocalTable_OnceItsKeyIsStored()
    {
        var identityGate = new TaskCompletionSource();
        var rig = new FeatureTestRig()
            .Source("identity_store", Answering(UserIdOf, identityGate.Task))
            .LocalSource("account_store", writer => WriteRecord(writer, EntityKey.FromInt64(1001), "tier", ScalarValue.FromFloat64(2.5)));
        var userKey = rig.RequestInput("user_key");
        rig.Physical("identity.user_id", Identity, ColumnType.Int64, "identity_store");
        var tier = rig.Physical("account.tier", Account, ColumnType.Float64, "account_store", ScalarValue.FromFloat64(0.5));
        rig.Build();

        foreach (var (key, expected) in new[] { ("k-1001", 2.5), ("k-1002", 0.5) })
        {
            var request = Request(rig, userKey, key);
            request.Fetcher.StartContextFetch([tier], CancellationToken.None);
            var read = request.Fetcher.EnsureAsync([tier], CancellationToken.None).AsTask();
            if (!identityGate.Task.IsCompleted)
            {
                Assert.False(read.IsCompleted);
                identityGate.SetResult();
            }

            Assert.Equal(FeatureState.Ok, (await read.WaitAsync(Timeout, TestContext.Current.CancellationToken)).State);
            Assert.Equal(expected, request.ContextValue(tier).AsFloat64());
        }
    }

    [Fact]
    public async Task AChainedEntity_KeyedByALocalOne_IsFetchedAtOnce()
    {
        var accounts = Answering((k, _) => k.Int64Value / 1000.0);
        var rig = new FeatureTestRig()
            .LocalSource("identity_store", writer => WriteRecord(writer, EntityKey.FromString("k-1001"), "user_id", ScalarValue.FromInt64(1001)))
            .Source("account_store", accounts);
        var userKey = rig.RequestInput("user_key");
        rig.Physical("identity.user_id", Identity, ColumnType.Int64, "identity_store");
        var tier = rig.Physical("account.tier", Account, ColumnType.Float64, "account_store");
        rig.Build();
        var request = Request(rig, userKey, "k-1001");

        request.Fetcher.StartContextFetch([tier], CancellationToken.None);
        Assert.Single(accounts.Calls); // the local read stored the key at once

        await request.EnsureAsync(tier);
        Assert.Equal(1.001, request.ContextValue(tier).AsFloat64());
    }

    [Fact]
    public async Task AChainedCacheGroup_MissesOnceForItsStoredKey_ThenHits()
    {
        var accounts = Answering((k, _) => k.Int64Value / 1000.0);
        var rig = new FeatureTestRig().Source("identity_store", Answering(UserIdOf)).CacheSource("account_store", accounts);
        var userKey = rig.RequestInput("user_key");
        rig.Physical("identity.user_id", Identity, ColumnType.Int64, "identity_store");
        var tier = rig.Physical("account.tier", Account, ColumnType.Float64, "account_store");
        rig.Build();

        var first = Request(rig, userKey, "k-1001");
        first.Fetcher.StartContextFetch([tier], CancellationToken.None);
        Assert.Equal(FeatureState.Ok, (await first.EnsureAsync(tier)).State);
        Assert.Equal(1.001, first.ContextValue(tier).AsFloat64());
        rig.WriteBack("account_store");

        var second = Request(rig, userKey, "k-1001");
        second.Fetcher.StartContextFetch([tier], CancellationToken.None);
        await second.EnsureAsync(tier);

        Assert.Equal(1.001, second.ContextValue(tier).AsFloat64());
        Assert.Equal(EntityKey.FromInt64(1001), Assert.Single(accounts.Calls).ContextKey);
    }

    /// <summary>A recording sees a chained call with the key its upstream stored, begun after the upstream's.</summary>
    [Fact]
    public async Task AChainedCall_IsRecordedWithItsStoredKey()
    {
        var (rig, userKey, _, tier) = Chain(Answering(UserIdOf), Answering((k, _) => k.Int64Value / 1000.0));
        var hook = new ThrowingHook();
        var request = Request(rig, userKey, "k-1001", hook: hook);

        request.Fetcher.StartContextFetch([tier], CancellationToken.None);
        await request.EnsureAsync(tier);

        Assert.Equal(["identity_store", "account_store"], hook.Begun);
        Assert.Equal([EntityKey.FromString("k-1001"), EntityKey.FromInt64(1001)], hook.Keys);
    }

    private static void WriteRecord(IFeatureTableWriter writer, EntityKey key, string field, ScalarValue value)
    {
        var builder = writer.Begin(key, 1);
        builder.Set(writer.Schema.FieldIndex(field), value);
        builder.Commit();
    }

    /// <summary>
    /// Review 77: the drain is a promise that every operation has really finished. A chained group whose key was stored
    /// before the request ended registers its connector call on another thread, possibly after the drain was asked for;
    /// the drain took one snapshot, completed while that call still ran (it ignores cancellation), and the memory was
    /// still held. The drain now waits until a round finds nothing new.
    /// </summary>
    [Fact]
    public async Task TheDrain_WaitsForAChainedCallRegisteredAfterItWasAskedFor()
    {
        var upstream = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pool = new PausingPool();
        var identity = new ScriptedConnector(async (_, fields, writer, _) =>
        {
            await upstream.Task;
            ScriptedConnector.Write(writer, fields[0], 0, 1001L);
            return FetchStatus.Ok;
        });
        var account = new ScriptedConnector(async (_, fields, writer, _) =>
        {
            started.SetResult();
            await actual.Task; // ignores cancellation: the call outlives its budget
            ScriptedConnector.Write(writer, fields[0], 0, 7L);
            return FetchStatus.Ok;
        });
        var accountEntity = EntityDefinition.Of("account", EntityKeyPartDefinition.ContextFeature("identity.user_id", EntityKeyType.Int64));
        var rig = new FeatureTestRig().Source("identity", identity).Source("account", account, timeoutMs: 150);
        var input = rig.RequestInput("user_key");
        rig.Physical("identity.user_id", Identity, ColumnType.Int64, "identity");
        var value = rig.Physical("account.value", accountEntity, ColumnType.Int64, "account");
        rig.Build();
        var request = rig.NewRequest(TwoItems, pool: pool);
        request.Context[input] = ScalarValue.FromString("login");
        request.Fetcher.StartContextFetch([value], TestContext.Current.CancellationToken);
        var released = false;
        pool.Arm = true;
        try
        {
            upstream.SetResult();
            await pool.Entered.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken); // the chained group is starting
            request.Fetcher.EndRequest();
            var drain = request.Fetcher.WhenIoCompleteAsync();
            request.Memory.Release();
            released = true;
            pool.Resume.Set();
            await started.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            await Task.WhenAny(drain, Task.Delay(400, TestContext.Current.CancellationToken)); // past the 150 ms budget
            Assert.False(drain.IsCompleted);
            Assert.Equal(FeatureState.Failed, request.Fetcher.GetState(value)); // timed out
            Assert.False(request.Memory.IsReturned);

            actual.SetResult();
            await drain.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.True(request.Memory.IsReturned);
            Assert.Equal(0, pool.Inner.Outstanding);
        }
        finally
        {
            pool.Resume.Set();
            upstream.TrySetResult();
            actual.TrySetResult();
            if (!released)
            {
                request.Fetcher.EndRequest();
                request.Memory.Release();
            }

            await request.Fetcher.WhenIoCompleteAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Pauses the first rent of entity keys once armed (a chained group about to send), until resumed.</summary>
    private sealed class PausingPool : IBufferPool
    {
        public volatile bool Arm;

        public PoisoningPool Inner { get; } = new();

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Resume { get; } = new();

        public T[] Rent<T>(int count)
        {
            if (typeof(T) == typeof(EntityKey) && Arm)
            {
                Arm = false;
                Entered.TrySetResult();
                if (!Resume.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException();
                }
            }

            return Inner.Rent<T>(count);
        }

        public void Return<T>(T[] value, bool clear) => Inner.Return(value, clear);
    }

    /// <summary>
    /// Records which sources were begun and their keys, and throws an <see cref="IOException"/> when call number
    /// <paramref name="begin"/> is about to be sent, or when a call of <paramref name="recordSource"/> is recorded.
    /// </summary>
    private sealed class ThrowingHook(int begin = 0, string? recordSource = null) : IFeatureCallHook
    {
        private int _begun;

        public ConcurrentQueue<string> Begun { get; } = new();

        public ConcurrentQueue<EntityKey> Keys { get; } = new();

        public bool Replays => false;

        public int Begin(FetchGroup group, in EntityKeyBatch batch, ImmutableArray<CompiledFieldBinding> fields)
        {
            if (Interlocked.Increment(ref _begun) == begin)
            {
                throw new IOException("recording unavailable");
            }

            Begun.Enqueue(group.SourceName);
            Keys.Enqueue(batch.Key(0, 0));
            return _begun;
        }

        public void Record(in FeatureCallInfo call, IStagedFeatureValues? values)
        {
            if (call.Group.SourceName == recordSource)
            {
                throw new IOException("recording unavailable");
            }
        }

        public FetchStatus Replay(FetchGroup group, in EntityKeyBatch batch, ImmutableArray<CompiledFieldBinding> fields, IFeatureBatchWriter writer) =>
            throw new NotSupportedException();
    }
}
