using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Catalog;
using StrataRec.Features.Connectors;
using StrataRec.Features.Local;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features;

/// <summary>
/// Fetches and local reads of entities keyed by several parts (design 6.2, 6.5): each row's key deduplicated as a tuple, a
/// request part handed over once per batch, a missing part leaving the row (or, for a request part, the whole fetch)
/// without a key, and the same values whether the records are fetched or read from a local table.
/// </summary>
public class CompositeKeyFetchTests
{
    private static readonly EntityDefinition ItemCountry = EntityDefinition.Of(
        "item_country", EntityKeyPartDefinition.EntityId(EntityKeyType.Int64), EntityKeyPartDefinition.RequestInput("country", EntityKeyType.String));

    private static readonly EntityDefinition ItemCategory = EntityDefinition.Of(
        "item_category", EntityKeyPartDefinition.EntityId(EntityKeyType.Int64), EntityKeyPartDefinition.Feature("item.category", EntityKeyType.String));

    /// <summary>boost = item × 10 + 1 for US, + 2 for any other country.</summary>
    private static double Boost(long item, string country) => (item * 10) + (country == "US" ? 1 : 2);

    private static ScriptedConnector BoostConnector(List<(bool Shared, int SharedLength)>? seen = null) => new((keys, fields, writer, _) =>
    {
        seen?.Add((keys.IsShared(1), keys.Part(1).Length));
        for (var k = 0; k < keys.Count; k++)
        {
            ScriptedConnector.Write(writer, fields[0], k, Boost(keys.Key(k, 0).Int64Value, keys.Key(k, 1).StringValue!));
        }

        return ValueTask.FromResult(FetchStatus.Ok);
    });

    [Fact]
    public async Task ARequestPart_IsHandedOverOnce_AndTheRowsKeysAreDeduplicated()
    {
        var seen = new List<(bool Shared, int SharedLength)>();
        var connector = BoostConnector(seen);
        var rig = new FeatureTestRig().Source("ic_store", connector);
        var country = rig.RequestInput("country");
        var boost = rig.Physical("item_country.boost", ItemCountry, ColumnType.Float64, "ic_store");
        rig.Build();
        var request = rig.NewRequest([3, 1, 3, 2]);
        request.Context[country] = ScalarValue.FromString("US");

        var result = await request.EnsureAsync(boost);

        Assert.Equal(FeatureState.Ok, result.State);
        var call = Assert.Single(connector.Calls);
        Assert.Equal(new EntityKey[][] { [EntityKey.FromInt64(3), EntityKey.FromString("US")], [EntityKey.FromInt64(1), EntityKey.FromString("US")], [EntityKey.FromInt64(2), EntityKey.FromString("US")] }, call.Tuples);
        Assert.Equal(new[] { (true, 1) }, seen);
        Assert.Equal(new[] { 31.0, 11.0, 31.0, 21.0 }, Enumerable.Range(0, 4).Select(row => request.Value(boost, row).AsFloat64()));
        Assert.Equal(EntityKeyLayout.OfTypes(EntityKeyType.Int64, EntityKeyType.String).TypeText, Assert.Single(connector.Layouts).TypeText);
        Assert.True(Assert.Single(connector.Layouts)[1].IsShared);
    }

    [Fact]
    public async Task AMissingRequestPart_LeavesEveryRowWithoutAKey_AndCallsNothing()
    {
        var connector = BoostConnector();
        var rig = new FeatureTestRig().Source("ic_store", connector);
        rig.RequestInput("country");
        var boost = rig.Physical("item_country.boost", ItemCountry, ColumnType.Float64, "ic_store", ScalarValue.FromFloat64(1.0), field: "boost");
        var raw = rig.Physical("item_country.raw", ItemCountry, ColumnType.Float64, "ic_store", field: "boost");
        rig.Build();
        var request = rig.NewRequest([3, 1, 2]); // no country

        var result = await request.EnsureAsync(boost, raw);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Empty(connector.Calls);
        for (var row = 0; row < 3; row++)
        {
            Assert.Equal(1.0, request.Value(boost, row).AsFloat64()); // the default, as for a missing one-part key
            Assert.True(request.Value(raw, row).IsMissing);
        }
    }

    [Fact]
    public async Task TwoVaryingParts_DeduplicateAsTuples_AndARowMissingAPartHasNoKey()
    {
        // item.category is fetched first (the key reads it); item 4 has none, so its row has no key.
        var categories = new Dictionary<long, string?> { [1] = "a", [2] = "b", [3] = "a", [4] = null };
        var items = ScriptedConnector.Values((id, _) => categories[id]);
        var scores = new ScriptedConnector((keys, fields, writer, _) =>
        {
            for (var k = 0; k < keys.Count; k++)
            {
                Assert.False(keys.IsShared(1));
                ScriptedConnector.Write(writer, fields[0], k, (keys.Key(k, 0).Int64Value * 10.0) + (keys.Key(k, 1).StringValue == "a" ? 1 : 2));
            }

            return ValueTask.FromResult(FetchStatus.Ok);
        });
        var rig = new FeatureTestRig().Source("item_store", items).Source("ic_store", scores);
        var category = rig.Physical("item.category", FeatureTestRig.Item, ColumnType.String, "item_store");
        var score = rig.Physical("item_category.score", ItemCategory, ColumnType.Float64, "ic_store");
        rig.Build();
        Assert.Equal(new[] { category }, rig.Plan.Groups.Single(g => g.SourceName == "ic_store").KeyFeatures);
        var request = rig.NewRequest([1, 2, 1, 3, 4]);

        var result = await request.EnsureAsync(score);

        Assert.Equal(FeatureState.Ok, result.State);
        var call = Assert.Single(scores.Calls);
        Assert.Equal(
            new EntityKey[][] { [EntityKey.FromInt64(1), EntityKey.FromString("a")], [EntityKey.FromInt64(2), EntityKey.FromString("b")], [EntityKey.FromInt64(3), EntityKey.FromString("a")] },
            call.Tuples);
        Assert.Equal(new double?[] { 11, 22, 11, 31, null }, Enumerable.Range(0, 5).Select(row => request.Value(score, row) is { IsMissing: false } v ? v.AsFloat64() : (double?)null));
    }

    [Fact]
    public void AConnectorThatDoesNotSupportTheKey_IsRefusedAtLoad_NamingTheLayout()
    {
        var connector = new ScriptedConnector((_, _, _, _) => ValueTask.FromResult(FetchStatus.Ok)) { SupportsLayout = layout => layout.PartCount == 1 };
        var rig = new FeatureTestRig().Source("ic_store", connector);
        rig.RequestInput("country");
        rig.Physical("item_country.boost", ItemCountry, ColumnType.Float64, "ic_store");

        var diagnostics = rig.TryBuild();

        var error = Assert.Single(diagnostics.Items, d => d.IsError);
        Assert.Equal(("CONNECTOR_KEY", "/sources/ic_store"), (error.Code, error.Pointer));
        Assert.Contains("does not support the key of entity 'item_country': [col.entity_id int64, ctx.request.country string (shared)]", error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Hint);
    }

    [Fact]
    public async Task MemoryConnector_NestsOneLevelPerPart()
    {
        var rig = new FeatureTestRig().MemorySource("ic_store", """{ "1": { "US": { "boost": 1.5 }, "DE": { "boost": 2.5 } }, "2": { "DE": { "boost": 3.5 } } }""");
        var country = rig.RequestInput("country");
        var boost = rig.Physical("item_country.boost", ItemCountry, ColumnType.Float64, "ic_store", ScalarValue.FromFloat64(1.0));
        rig.Build();

        foreach (var (code, expected) in new[] { ("US", new[] { 1.5, 1.0, 1.0 }), ("DE", new[] { 2.5, 3.5, 1.0 }) })
        {
            var request = rig.NewRequest([1, 2, 3]);
            request.Context[country] = ScalarValue.FromString(code);
            Assert.Equal(FeatureState.Ok, (await request.EnsureAsync(boost)).State);
            Assert.Equal(expected, Enumerable.Range(0, 3).Select(row => request.Value(boost, row).AsFloat64()));
        }
    }

    [Theory]
    [InlineData("US")]
    [InlineData("DE")]
    [InlineData(null)]
    public async Task ALocalRead_GivesWhatAFetchOfTheSameRecordsGives(string? code)
    {
        // The same records as a local table keyed by [item, country] and as a fetch.
        var records = new (long Item, string Country, double Boost)[] { (1, "US", 1.5), (1, "DE", 2.5), (2, "DE", 3.5), (4, "US", 4.5) };
        var local = new FeatureTestRig().LocalSource("ic_store", writer =>
        {
            foreach (var (item, country, value) in records)
            {
                var builder = writer.Begin([EntityKey.FromInt64(item), EntityKey.FromString(country)], 1);
                builder.Set(writer.Schema.FieldIndex("boost"), ScalarValue.FromFloat64(value));
                builder.Commit();
            }
        });
        var fetched = new FeatureTestRig().Source("ic_store", new ScriptedConnector((keys, fields, writer, _) =>
        {
            for (var k = 0; k < keys.Count; k++)
            {
                foreach (var (item, country, value) in records)
                {
                    if (keys.Key(k, 0).Int64Value == item && keys.Key(k, 1).StringValue == country)
                    {
                        ScriptedConnector.Write(writer, fields[0], k, value);
                    }
                }
            }

            return ValueTask.FromResult(FetchStatus.Ok);
        }));
        var candidates = new long[] { 4, 1, 2, 3, 1 };
        var values = new List<double?[]>();
        foreach (var rig in new[] { local, fetched })
        {
            var country = rig.RequestInput("country");
            var boost = rig.Physical("item_country.boost", ItemCountry, ColumnType.Float64, "ic_store");
            rig.Build();
            var request = rig.NewRequest(candidates);
            if (code is not null)
            {
                request.Context[country] = ScalarValue.FromString(code);
            }

            Assert.Equal(FeatureState.Ok, (await request.EnsureAsync(boost)).State);
            values.Add([.. Enumerable.Range(0, candidates.Length).Select(row => request.Value(boost, row) is { IsMissing: false } v ? v.AsFloat64() : (double?)null)]);
        }

        Assert.Equal(values[1], values[0]);
        Assert.Equal(code switch
        {
            "US" => new double?[] { 4.5, 1.5, null, null, 1.5 },
            "DE" => new double?[] { null, 2.5, 3.5, null, 2.5 },
            _ => new double?[5],
        }, values[0]);
        Assert.True(TableKeys.IsHashed(local.Tables["ic_store"].Schema.Key));
    }

    [Fact]
    public async Task AContextScopedEntityOfTwoRequestParts_IsOneKeyPerRequest()
    {
        var segment = EntityDefinition.Of(
            "user_day", EntityKeyPartDefinition.RequestInput("user_id", EntityKeyType.Int64), EntityKeyPartDefinition.RequestInput("day", EntityKeyType.String));
        var connector = new ScriptedConnector((keys, fields, writer, _) =>
        {
            Assert.Equal(1, keys.Count);
            ScriptedConnector.Write(writer, fields[0], 0, keys.Key(0, 0).Int64Value + (keys.Key(0, 1).StringValue == "mon" ? 0.5 : 0.25));
            return ValueTask.FromResult(FetchStatus.Ok);
        });
        var rig = new FeatureTestRig().Source("ud_store", connector);
        var day = rig.RequestInput("day");
        var activity = rig.Physical("user_day.activity", segment, ColumnType.Float64, "ud_store");
        rig.Build();
        Assert.True(rig.Plan.Features[activity].IsContext);

        var request = rig.NewRequest([1, 2], userId: 7);
        request.Context[day] = ScalarValue.FromString("mon");
        request.Fetcher.StartContextFetch([activity], CancellationToken.None);
        await request.EnsureAsync(activity);

        Assert.Equal(7.5, request.ContextValue(activity).AsFloat64());
        Assert.Equal(new EntityKey[][] { [EntityKey.FromInt64(7), EntityKey.FromString("mon")] }, Assert.Single(connector.Calls).Tuples);

        // Without the day there is no key: no call, the value is missing.
        var anonymous = rig.NewRequest([1], userId: 7);
        anonymous.Fetcher.StartContextFetch([activity], CancellationToken.None);
        await anonymous.EnsureAsync(activity);
        Assert.True(anonymous.ContextValue(activity).IsMissing);
        Assert.Single(connector.Calls);
    }
}
