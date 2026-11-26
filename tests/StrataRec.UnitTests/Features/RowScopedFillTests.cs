using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Features.Catalog;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features;

/// <summary>
/// The row-scoped fill (<see cref="FeatureFetcher.EnsureAsync(ReadOnlySpan{int}, ReadOnlySpan{int}, CancellationToken)"/>),
/// which the output uses for its returned rows: the waves of a whole fill, but the keys fetched or read, the derived values
/// computed and the rows marked filled are those of the given rows; a later whole fill fills the rest. A value of another
/// row cannot fail it.
/// </summary>
public class RowScopedFillTests
{
    private static readonly long[] FourItems = [1, 2, 3, 4];

    [Fact]
    public async Task ARowScopedFill_FetchesTheKeysOfItsRowsOnly_AndComputesItsDerivedValuesForThemOnly()
    {
        var (rig, connector, @base, tenfold) = BaseRig("col.item.base * 10", ColumnType.Int64);
        var request = rig.NewRequest(FourItems);

        var result = await request.Fetcher.EnsureAsync([tenfold], [2, 0], CancellationToken.None);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(new long[] { 3, 1 }, Assert.Single(connector.Calls).Keys.Select(k => k.Int64Value)); // in the rows' order
        Assert.Equal(10, request.Value(tenfold, 0).AsInt64());
        Assert.Equal(30, request.Value(tenfold, 2).AsInt64());
        foreach (var row in new[] { 1, 3 })
        {
            Assert.False(request.IsFilled(@base, row), $"base filled on row {row}");
            Assert.False(request.IsFilled(tenfold, row), $"tenfold filled on row {row}");
        }
    }

    [Fact]
    public async Task AWholeFillAfterARowScopedOne_FillsTheOtherRows_AndAFilledRowIsNotFetchedAgain()
    {
        var (rig, connector, _, tenfold) = BaseRig("col.item.base * 10", ColumnType.Int64);
        var request = rig.NewRequest(FourItems);
        await request.Fetcher.EnsureAsync([tenfold], [2, 0], CancellationToken.None);

        var again = await request.Fetcher.EnsureAsync([tenfold], [0], CancellationToken.None);
        var whole = await request.EnsureAsync(tenfold);

        Assert.Equal(FeatureState.Ok, again.State);
        Assert.Equal(FeatureState.Ok, whole.State);
        var calls = connector.Calls.ToArray();
        Assert.Equal(2, calls.Length); // none for the rows already filled
        Assert.Equal(new long[] { 2, 4 }, calls[1].Keys.Select(k => k.Int64Value));
        Assert.Equal(new long[] { 10, 20, 30, 40 }, Enumerable.Range(0, 4).Select(r => request.Value(tenfold, r).AsInt64()));
    }

    [Fact]
    public async Task AValueOutsideTheRows_ThatTheTypeCannotHold_DoesNotFailTheFill_AWholeFillThenFailsOnIt()
    {
        // The review's case: item 1's 1.5e9 fits a non-nullable int32 feature, item 2's 3e9 does not.
        var (rig, _, _, narrow) = BaseRig("col.item.base * 1500000000", ColumnType.Int32);
        var request = rig.NewRequest([1, 2]);

        var returned = await request.Fetcher.EnsureAsync([narrow], [0], CancellationToken.None);

        Assert.Equal(FeatureState.Ok, returned.State);
        Assert.Equal(1_500_000_000, request.Value(narrow, 0).AsInt64());
        Assert.False(request.IsFilled(narrow, 1));
        Assert.Equal(0, request.Fetcher.Counters.OutOfRange);

        // The state recorded covers row 0 only: a whole fill computes row 1 and records its failure.
        var whole = await request.EnsureAsync(narrow);

        Assert.Equal(FeatureState.Failed, whole.State);
        Assert.Contains("outside the int32 range", whole.Error);
        Assert.Equal(1, request.Fetcher.Counters.OutOfRange);
    }

    [Fact]
    public async Task AValueInsideTheRows_ThatTheTypeCannotHold_FailsTheFill_AndTheFeatureStaysFailed()
    {
        var (rig, _, _, narrow) = BaseRig("col.item.base * 1500000000", ColumnType.Int32);
        var request = rig.NewRequest([1, 2]);

        var returned = await request.Fetcher.EnsureAsync([narrow], [1], CancellationToken.None);

        Assert.Equal(FeatureState.Failed, returned.State);
        Assert.Contains("outside the int32 range", returned.Error);
        Assert.Contains("declare a default", returned.Error);
        Assert.False(request.IsFilled(narrow, 0));

        // Row 1 is live: a whole fill fails on it as well, and fails.
        var whole = await request.EnsureAsync(narrow);
        Assert.Equal(FeatureState.Failed, whole.State);
        Assert.Equal(FeatureState.Failed, request.Fetcher.GetState(narrow));
    }

    [Fact]
    public async Task NoRows_FillNothing_AndCallNothing()
    {
        var (rig, connector, @base, tenfold) = BaseRig("col.item.base * 10", ColumnType.Int64);
        var request = rig.NewRequest(FourItems);

        var result = await request.Fetcher.EnsureAsync([tenfold], [], CancellationToken.None);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Empty(connector.Calls);
        Assert.Equal(FeatureState.Unrequested, request.Fetcher.GetState(tenfold));
        Assert.Equal(FeatureState.Unrequested, request.Fetcher.GetState(@base));
    }

    [Fact]
    public async Task AForeignKeyChain_IsFetchedForTheRowsOnly_WaveByWave()
    {
        var items = ScriptedConnector.Values((key, _) => key * 10);
        var categories = ScriptedConnector.Values((key, _) => key * 0.5);
        var rig = new FeatureTestRig().Source("item_store", items).Source("category_store", categories);
        var categoryId = rig.Physical("item.category_id", FeatureTestRig.Item, ColumnType.Int64, "item_store");
        var boost = rig.Physical("category.boost", FeatureTestRig.ForeignKey("category", "item.category_id"), ColumnType.Float64, "category_store",
            ScalarValue.FromFloat64(1.0));
        rig.Build();
        var request = rig.NewRequest([1, 2, 3]);

        var result = await request.Fetcher.EnsureAsync([boost], [1], CancellationToken.None);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(new long[] { 2 }, Assert.Single(items.Calls).Keys.Select(k => k.Int64Value));
        Assert.Equal(new long[] { 20 }, Assert.Single(categories.Calls).Keys.Select(k => k.Int64Value));
        Assert.Equal(10.0, request.Value(boost, 1).AsFloat64());
        Assert.False(request.IsFilled(categoryId, 0));
        Assert.False(request.IsFilled(boost, 2));
    }

    [Fact]
    public async Task AFetchInFlight_IsCompletedForEveryLiveRowItCovers()
    {
        var (rig, connector, @base, tenfold) = BaseRig("col.item.base * 10", ColumnType.Int64);
        var request = rig.NewRequest(FourItems);
        request.Fetcher.Prefetch([@base], CancellationToken.None);

        var result = await request.Fetcher.EnsureAsync([tenfold], [3], CancellationToken.None);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, Assert.Single(connector.Calls).Keys.Select(k => k.Int64Value)); // the prefetch's
        Assert.All(Enumerable.Range(0, 4), row => Assert.True(request.IsFilled(@base, row)));
        Assert.Equal(40, request.Value(tenfold, 3).AsInt64());
        Assert.False(request.IsFilled(tenfold, 0)); // computed for the rows only
    }

    /// <summary>
    /// A prefetch started for other rows (a fallback's declared reads, after rows were appended), which fails for
    /// all its keys, is not waited for by a row-scoped fill whose rows have what it needs: waiting set the feature failed
    /// and failed the fill. Its rows' derived values are computed; the prefetch is left in flight, for the request's end.
    /// When a row of the fill is unfilled, the prefetch is waited for, and its failure fails the fill, not fetched again.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFetchInFlight_ThatTheRowsNeedNothingFrom_IsNotWaitedFor(bool overlapping)
    {
        var connector = new ScriptedConnector((keys, fields, writer, _) =>
        {
            if (keys.Key(0, 0).Int64Value == 2)
            {
                return ValueTask.FromResult(FetchStatus.Failed("only key 2 failed"));
            }

            ScriptedConnector.WriteAll(keys, fields, writer, (key, _) => key);
            return ValueTask.FromResult(FetchStatus.Ok);
        });
        var rig = new FeatureTestRig().Source("item_store", connector);
        var @base = rig.Physical("item.base", FeatureTestRig.Item, ColumnType.Int64, "item_store", ScalarValue.FromInt64(0));
        var scope = new DictionaryScope().Add(new Symbol(SymbolKind.Column, "col.item.base", ColumnType.Int64, 1, rig.SlotOf(@base)));
        var tenfold = rig.Derived("item.derived", FeatureTestRig.Item, ColumnType.Int64,
            ExpressionCompiler.Compile("col.item.base * 10", scope, CompileOptions.Any), [@base]);
        rig.Build();
        var request = rig.NewRequest([1, 2]);
        Assert.Equal(FeatureState.Ok, (await request.Fetcher.EnsureAsync([@base], [0], CancellationToken.None)).State);
        request.Fetcher.Prefetch([@base], CancellationToken.None); // key 2, which fails

        var result = await request.Fetcher.EnsureAsync([tenfold], overlapping ? [0, 1] : [0], CancellationToken.None);

        Assert.Equal(new long[][] { [1], [2] }, connector.Calls.Select(c => c.Keys.Select(k => k.Int64Value).ToArray()));
        if (overlapping)
        {
            Assert.Equal(FeatureState.Failed, result.State);
            Assert.Contains("only key 2 failed", result.Error);
        }
        else
        {
            Assert.Equal(FeatureState.Ok, result.State);
            Assert.Equal(10, request.Value(tenfold, 0).AsInt64());
            Assert.False(request.IsFilled(@base, 1));
            request.Fetcher.EndRequest(); // settles the unread prefetch
        }
    }

    /// <summary>
    /// A prefetch that asked for other keys is not waited for, although a row of the fill is unfilled: a row
    /// appended after it started. Waiting set the feature failed when the prefetch failed for all its keys and failed the
    /// fill, though the connector was never asked for the row's key; the fill now fetches that key itself. A prefetch that
    /// asked for the key of one of the fill's rows is waited for, as before, though another row's key is not among its
    /// keys: its failure fails the fill, and that key is not fetched.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFetchInFlight_IsWaitedForOnlyWhenItAskedForTheKeyOfARowToFill(bool overlapping)
    {
        var connector = new ScriptedConnector((keys, fields, writer, _) =>
        {
            if (keys.Key(0, 0).Int64Value == 1)
            {
                return ValueTask.FromResult(FetchStatus.Failed("only key 1 failed"));
            }

            ScriptedConnector.WriteAll(keys, fields, writer, (key, _) => key);
            return ValueTask.FromResult(FetchStatus.Ok);
        });
        var (rig, @base, tenfold) = BaseRig(connector);
        var request = rig.NewRequest([1], capacity: 3);
        request.Fetcher.Prefetch([@base], CancellationToken.None); // key 1, which fails
        request.Table.AppendRows([2, 3]); // after it started: keys it never asked for

        var result = await request.Fetcher.EnsureAsync([tenfold], overlapping ? [1, 0] : [1], CancellationToken.None);

        if (overlapping)
        {
            Assert.Equal(new long[][] { [1] }, connector.Calls.Select(c => c.Keys.Select(k => k.Int64Value).ToArray()));
            Assert.Equal(FeatureState.Failed, result.State);
            Assert.Contains("only key 1 failed", result.Error);
        }
        else
        {
            Assert.Equal(new long[][] { [1], [2] }, connector.Calls.Select(c => c.Keys.Select(k => k.Int64Value).ToArray()));
            Assert.Equal(FeatureState.Ok, result.State);
            Assert.Equal(20, request.Value(tenfold, 1).AsInt64());
            Assert.False(request.IsFilled(@base, 0)); // the prefetch is still unread
            Assert.False(request.IsFilled(@base, 2));
            request.Fetcher.EndRequest(); // settles it
        }
    }

    /// <summary>
    /// A prefetch that asked for some of the fill's keys is waited for, as before, and fills every live row it covers; the
    /// fill then fetches the keys it lacks, and only those.
    /// </summary>
    [Fact]
    public async Task AFetchInFlight_ThatAskedForSomeOfTheRowsKeys_IsWaitedFor_AndTheOtherKeysAreFetchedAfterIt()
    {
        var (rig, connector, @base, tenfold) = BaseRig("col.item.base * 10", ColumnType.Int64);
        var request = rig.NewRequest([1, 2], capacity: 3);
        request.Fetcher.Prefetch([@base], CancellationToken.None); // keys 1 and 2
        request.Table.AppendRows([3]);

        var result = await request.Fetcher.EnsureAsync([tenfold], [2, 1], CancellationToken.None);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(new long[][] { [1, 2], [3] }, connector.Calls.Select(c => c.Keys.Select(k => k.Int64Value).ToArray()));
        Assert.Equal((20L, 30L), (request.Value(tenfold, 1).AsInt64(), request.Value(tenfold, 2).AsInt64()));
        Assert.True(request.IsFilled(@base, 0)); // the prefetch's other row
        Assert.False(request.IsFilled(tenfold, 0));
    }

    public enum KeyKind
    {
        /// <summary>A foreign key: <c>item.creator_id</c>, a derived candidate feature (the entity id / 10).</summary>
        Creator,

        /// <summary>Two varying parts: the entity id and <c>item.creator_id</c>.</summary>
        ItemAndCreator,

        /// <summary>A varying part beside a shared one: the entity id and the request's country.</summary>
        ItemAndCountry,
    }

    /// <summary>
    /// Whether a prefetch holds the key of a row of the fill is judged by the whole key, as its backfill matches
    /// rows: a key read from a candidate feature (computed by the fill for an appended row before its fetches), two
    /// varying parts, a varying part beside a request input. The prefetch of items 11 and 21 fails; item 12 is appended
    /// after it. Its foreign key, creator 1, is the prefetch's (11 / 10): waited for, the fill fails. Its pairs (12, 1)
    /// and (12, "US") are not, though a part of each is: the fill fetches them itself.
    /// </summary>
    [Theory]
    [InlineData(KeyKind.Creator, true)]
    [InlineData(KeyKind.ItemAndCreator, false)]
    [InlineData(KeyKind.ItemAndCountry, false)]
    public async Task AKeyOfSeveralPartsOrReadFromAFeature_IsMatchedWhole(KeyKind kind, bool waited)
    {
        var sent = 0;
        var connector = new ScriptedConnector((keys, fields, writer, _) =>
        {
            if (Interlocked.Increment(ref sent) == 1)
            {
                return ValueTask.FromResult(FetchStatus.Failed("the prefetch failed"));
            }

            ScriptedConnector.WriteAll(keys, fields, writer, (key, _) => key * 1.0);
            return ValueTask.FromResult(FetchStatus.Ok);
        });
        var rig = new FeatureTestRig().Source("score_store", connector);
        var country = rig.RequestInput("country");
        var creatorId = rig.Derived("item.creator_id", FeatureTestRig.Item, ColumnType.Int64, TestExpression.EntityIdDividedBy(10), []);
        var entity = kind switch
        {
            KeyKind.Creator => FeatureTestRig.ForeignKey("creator", "item.creator_id"),
            KeyKind.ItemAndCreator => EntityDefinition.Of(
                "item_creator", EntityKeyPartDefinition.EntityId(EntityKeyType.Int64), EntityKeyPartDefinition.Feature("item.creator_id", EntityKeyType.Int64)),
            _ => EntityDefinition.Of(
                "item_country", EntityKeyPartDefinition.EntityId(EntityKeyType.Int64), EntityKeyPartDefinition.RequestInput("country", EntityKeyType.String)),
        };
        var score = rig.Physical(entity.Name + ".score", entity, ColumnType.Float64, "score_store");
        rig.Build();
        Assert.Equal(0, rig.Plan.Features[score].Wave); // prefetched
        var request = rig.NewRequest([11, 21], capacity: 3);
        request.Context[country] = ScalarValue.FromString("US");
        Assert.Equal(FeatureState.Ok, (await request.EnsureAsync(creatorId)).State); // the key a prefetch takes
        request.Fetcher.Prefetch([score], CancellationToken.None);
        request.Table.AppendRows([12]);

        var result = await request.Fetcher.EnsureAsync([score], [2], CancellationToken.None);

        if (waited)
        {
            Assert.Single(connector.Calls);
            Assert.Equal(FeatureState.Failed, result.State);
            Assert.Contains("the prefetch failed", result.Error);
        }
        else
        {
            Assert.Equal(2, connector.Calls.Count);
            Assert.Equal(
                kind == KeyKind.ItemAndCreator ? [EntityKey.FromInt64(12), EntityKey.FromInt64(1)] : new[] { EntityKey.FromInt64(12), EntityKey.FromString("US") },
                Assert.Single(connector.Calls.Last().Tuples));
            Assert.Equal(FeatureState.Ok, result.State);
            Assert.Equal(12.0, request.Value(score, 2).AsFloat64());
            Assert.False(request.IsFilled(score, 0));
            request.Fetcher.EndRequest();
        }
    }

    [Fact]
    public async Task ALocalTable_IsReadForTheRowsOnly()
    {
        var rig = new FeatureTestRig().LocalSource("item_store", writer =>
        {
            foreach (var key in FourItems)
            {
                var record = writer.Begin(key, 1);
                record.Set(writer.Schema.FieldIndex("base"), ScalarValue.FromInt64(key));
                Assert.True(record.Commit());
            }
        });
        var @base = rig.Physical("item.base", FeatureTestRig.Item, ColumnType.Int64, "item_store", ScalarValue.FromInt64(0));
        rig.Build();
        var request = rig.NewRequest(FourItems);

        var result = await request.Fetcher.EnsureAsync([@base], [1, 3], CancellationToken.None);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(2, request.Value(@base, 1).AsInt64());
        Assert.Equal(4, request.Value(@base, 3).AsInt64());
        Assert.False(request.IsFilled(@base, 0));
        Assert.False(request.IsFilled(@base, 2));

        await request.EnsureAsync(@base);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, Enumerable.Range(0, 4).Select(r => request.Value(@base, r).AsInt64()));
    }

    [Fact]
    public async Task ACacheRead_ResolvesTheMissesOfItsRowsOnly()
    {
        var connector = ScriptedConnector.Values((key, _) => key * 1.0);
        var rig = new FeatureTestRig().CacheSource("item_store", connector);
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        var request = rig.NewRequest([1, 2, 3]);

        var result = await request.Fetcher.EnsureAsync([clicks], [0, 2], CancellationToken.None);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(new long[] { 1, 3 }, Assert.Single(connector.Calls).Keys.Select(k => k.Int64Value));
        Assert.Equal(3.0, request.Value(clicks, 2).AsFloat64());
        Assert.False(request.IsFilled(clicks, 1));

        await request.EnsureAsync(clicks);
        Assert.Equal(new long[] { 2 }, connector.Calls.Last().Keys.Select(k => k.Int64Value));
        Assert.Equal(2.0, request.Value(clicks, 1).AsFloat64());
    }

    /// <summary><c>item.base</c> from <paramref name="connector"/> and <c>item.derived</c>, ten times it.</summary>
    private static (FeatureTestRig Rig, int Base, int Derived) BaseRig(ScriptedConnector connector)
    {
        var rig = new FeatureTestRig().Source("item_store", connector);
        var @base = rig.Physical("item.base", FeatureTestRig.Item, ColumnType.Int64, "item_store", ScalarValue.FromInt64(0));
        var scope = new DictionaryScope().Add(new Symbol(SymbolKind.Column, "col.item.base", ColumnType.Int64, 1, rig.SlotOf(@base)));
        var tenfold = rig.Derived("item.derived", FeatureTestRig.Item, ColumnType.Int64,
            ExpressionCompiler.Compile("col.item.base * 10", scope, CompileOptions.Any), [@base]);
        rig.Build();
        return (rig, @base, tenfold);
    }

    /// <summary>
    /// <c>item.base</c> (non-nullable int64, the item's id, fetched from <c>item_store</c>) and <c>item.derived</c> computed
    /// from it by <paramref name="expression"/>, of <paramref name="type"/> with no default.
    /// </summary>
    private static (FeatureTestRig Rig, ScriptedConnector Connector, int Base, int Derived) BaseRig(string expression, ColumnType type)
    {
        var connector = ScriptedConnector.Values((key, _) => key);
        var rig = new FeatureTestRig().Source("item_store", connector);
        var @base = rig.Physical("item.base", FeatureTestRig.Item, ColumnType.Int64, "item_store", ScalarValue.FromInt64(0));
        var scope = new DictionaryScope().Add(new Symbol(SymbolKind.Column, "col.item.base", ColumnType.Int64, 1, rig.SlotOf(@base)));
        var derived = rig.Derived("item.derived", FeatureTestRig.Item, type, ExpressionCompiler.Compile(expression, scope, CompileOptions.Any), [@base]);
        rig.Build();
        return (rig, connector, @base, derived);
    }
}
