using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Features.Catalog;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features;

/// <summary>
/// <c>list&lt;int32&gt;</c> features (design 4.3): connector staging, the memory connector's decoding, backfill,
/// configuration values, and the out-of-range rule for derived and context values of int32 types.
/// </summary>
public class ListInt32FeatureTests
{
    private static readonly long[] ThreeItems = { 1, 2, 3 };

    // ---- Staging and the memory connector -------------------------------------------------------------------------------

    [Fact]
    public void MemoryConnector_DecodesInt32ListsStraightIntoInt32Staging()
    {
        CompiledFieldBinding[] fields = { new("tags", 0, ColumnType.ListInt32), new("n", 1, ColumnType.Int32) };
        const string Data = """
            { "1": { "tags": [1, -2, 2147483647, -2147483648], "n": 5 },
              "2": { "tags": [] },
              "3": { "tags": null, "n": -2147483648 } }
            """;

        var staging = Fetch(Data, fields, 1, 2, 3, 4);

        var tags = Assert.IsType<ListColumn<int>>(staging.Field(0));
        Assert.True(staging.HasValue(0, 0));
        Assert.Equal(new[] { 1, -2, int.MaxValue, int.MinValue }, tags.Get(0).ToArray());
        Assert.True(staging.HasValue(0, 1));
        Assert.Empty(tags.Get(1).ToArray());
        Assert.False(staging.HasValue(0, 2)); // null: missing
        Assert.False(staging.HasValue(0, 3)); // absent key
        Assert.Equal(5, ((ScalarColumn<int>)staging.Field(1))[0]);
        Assert.Equal(int.MinValue, ((ScalarColumn<int>)staging.Field(1))[2]);
    }

    [Theory]
    [InlineData("""{ "1": { "tags": [1, 4294967296] } }""", "4294967296")]
    [InlineData("""{ "1": { "tags": [-2147483649] } }""", "-2147483649")]
    [InlineData("""{ "1": { "tags": [1.5] } }""", "1.5")]
    [InlineData("""{ "1": { "n": 2147483648 } }""", "2147483648")]
    public void MemoryConnector_ValueOutsideInt32_FailsTheCall_NamingFieldAndValue(string data, string value)
    {
        CompiledFieldBinding[] fields = { new("tags", 0, ColumnType.ListInt32), new("n", 1, ColumnType.Int32) };

        var error = Assert.Throws<FormatException>(() => Fetch(data, fields, 1));

        Assert.Contains(value, error.Message);
        Assert.Contains("int32", error.Message);
        Assert.Contains("int64", error.Message); // the fix: declare a wider type
    }

    [Fact]
    public void Staging_ListSetterMustMatchTheFieldWidth()
    {
        CompiledFieldBinding[] fields = { new("a", 0, ColumnType.ListInt32), new("b", 1, ColumnType.ListInt64) };
        var staging = new StagingBuffer(fields, 2, new RequestMemory());

        staging.SetInt32List(0, 0, new[] { 1, 2 });
        staging.SetInt64List(1, 0, new long[] { 3 });

        Assert.Equal(new[] { 1, 2 }, ((ListColumn<int>)staging.Field(0)).Get(0).ToArray());
        Assert.True(staging.HasValue(0, 0));
        Assert.Contains("SetInt32List", Assert.Throws<InvalidOperationException>(() => staging.SetInt64List(0, 1, new long[] { 1 })).Message);
        Assert.Contains("SetInt64List", Assert.Throws<InvalidOperationException>(() => staging.SetInt32List(1, 1, new[] { 1 })).Message);
        Assert.False(staging.HasValue(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => staging.SetInt32List(0, 2, new[] { 1 }));
    }

    [Fact]
    public async Task Backfill_CopiesInt32Lists_AndAppliesTheDefault()
    {
        var rig = new FeatureTestRig().MemorySource("item_store", """
            { "1": { "tags": [1, 2, -2147483648], "ids": [5] },
              "2": { "tags": [] } }
            """);
        var tags = rig.Physical("item.tags", FeatureTestRig.Item, ColumnType.ListInt32, "item_store", ScalarValue.FromInt64List(new long[] { 0 }));
        var ids = rig.Physical("item.ids", FeatureTestRig.Item, ColumnType.ListInt32, "item_store");
        rig.Build();
        var request = rig.NewRequest(ThreeItems);

        var result = await request.EnsureAsync(tags, ids);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.IsType<ListColumn<int>>(request.Table.TryGetSlot(rig.SlotOf(tags)));
        Assert.Equal(new long[] { 1, 2, int.MinValue }, request.Value(tags, 0).AsInt64List());
        Assert.Empty(request.Value(tags, 1).AsInt64List());
        Assert.Equal(new long[] { 0 }, request.Value(tags, 2).AsInt64List());
        Assert.Equal(new long[] { 5 }, request.Value(ids, 0).AsInt64List());
        Assert.True(request.Value(ids, 1).IsMissing);
        Assert.True(request.Value(ids, 2).IsMissing);
        Assert.Equal(0, request.Fetcher.Counters.OutOfRange);
    }

    [Fact]
    public async Task Backfill_SourceDataOutsideInt32_FailsTheSource()
    {
        var rig = new FeatureTestRig().MemorySource("item_store", """{ "1": { "tags": [1, 4294967296] } }""");
        var tags = rig.Physical("item.tags", FeatureTestRig.Item, ColumnType.ListInt32, "item_store");
        rig.Build();
        var request = rig.NewRequest(ThreeItems);

        var result = await request.EnsureAsync(tags);

        Assert.True(result.Failed);
        Assert.Contains("item_store", result.Error);
        Assert.Contains("'tags'", result.Error);
    }

    // ---- Derived and context values: the out-of-range rule --------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DerivedInt32_OutOfRange_TakesTheDefaultOrMissing_AndIsCounted(bool withDefault)
    {
        var rig = new FeatureTestRig();
        var type = withDefault ? ColumnType.Int32 : ColumnType.Int32.AsNullable();
        var scaled = rig.Derived("item.scaled", FeatureTestRig.Item, type, Compile("col.entity_id * 1500000000"), Array.Empty<int>(),
            withDefault ? ScalarValue.FromInt64(0) : null);
        rig.Build();
        var request = rig.NewRequest(ThreeItems);

        var result = await request.EnsureAsync(scaled);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(1_500_000_000, request.Value(scaled, 0).AsInt64());
        foreach (var row in new[] { 1, 2 }) // 3e9 and 4.5e9: an unchecked narrowing would store garbage
        {
            var value = request.Value(scaled, row);
            Assert.True(withDefault ? value.AsInt64() == 0 : value.IsMissing, $"row {row}: {value}");
        }

        Assert.Equal(2, request.Fetcher.Counters.OutOfRange);
        Assert.Equal(0, request.Fetcher.NonFiniteReplaced);
    }

    [Theory]
    [InlineData("col.entity_id * 1500000000", "int32")]
    [InlineData("col.entity_id > 1 ? [3000000000] : [7]", "list<int32>")]
    public async Task DerivedInt32_OutOfRange_WithoutDefaultOrNullability_FailsTheFeature(string expr, string type)
    {
        // Review 6 #1: the replacement "missing" cannot live in a non-nullable feature; readers would skip validity and
        // read whatever the storage holds.
        var rig = new FeatureTestRig();
        var feature = rig.Derived("item.narrow", FeatureTestRig.Item, ColumnType.Parse(type), Compile(expr), Array.Empty<int>());
        rig.Build();
        var request = rig.NewRequest(ThreeItems);

        var result = await request.EnsureAsync(feature);

        Assert.Equal(FeatureState.Failed, result.State);
        Assert.Contains("outside the int32 range", result.Error);
        Assert.Contains("declare a default", result.Error);
        Assert.Equal(2, request.Fetcher.Counters.OutOfRange);
    }

    [Fact]
    public async Task DerivedListInt32_WithAnOutOfRangeElement_IsReplacedAsAWhole()
    {
        var rig = new FeatureTestRig();
        var tags = rig.Derived("item.tags", FeatureTestRig.Item, ColumnType.ListInt32,
            Compile("col.entity_id > 1 ? [1, 3000000000] : [1, 2]"), Array.Empty<int>(), ScalarValue.FromInt64List(new long[] { 0 }));
        var raw = rig.Derived("item.raw", FeatureTestRig.Item, ColumnType.ListInt32.AsNullable(),
            Compile("col.entity_id == 2 ? [3000000000] : [7]"), Array.Empty<int>());
        rig.Build();
        var request = rig.NewRequest(ThreeItems);

        await request.EnsureAsync(tags, raw);

        Assert.Equal(new long[] { 1, 2 }, request.Value(tags, 0).AsInt64List());
        Assert.Equal(new long[] { 0 }, request.Value(tags, 1).AsInt64List());
        Assert.Equal(new long[] { 0 }, request.Value(tags, 2).AsInt64List());
        Assert.Equal(new long[] { 7 }, request.Value(raw, 0).AsInt64List());
        Assert.True(request.Value(raw, 1).IsMissing);
        Assert.Equal(new long[] { 7 }, request.Value(raw, 2).AsInt64List());
        Assert.Equal(3, request.Fetcher.Counters.OutOfRange);
    }

    [Fact]
    public async Task ContextValues_OutsideTheirInt32Type_TakeTheDefaultOrMissing_AndAreCounted()
    {
        var rig = new FeatureTestRig();
        var withDefault = rig.Derived("user.bucket", FeatureTestRig.User, ColumnType.Int32,
            new TestExpression("3000000000", ColumnType.Int64, scalar: _ => ScalarValue.FromInt64(3_000_000_000)), Array.Empty<int>(), ScalarValue.FromInt64(5));
        var list = rig.Derived("user.tags", FeatureTestRig.User, ColumnType.ListInt32.AsNullable(),
            new TestExpression("[1, 3000000000]", ColumnType.ListInt64, scalar: _ => ScalarValue.FromInt64List(new long[] { 1, 3_000_000_000 })), Array.Empty<int>());
        var fits = rig.Derived("user.ok", FeatureTestRig.User, ColumnType.ListInt32.AsNullable(),
            new TestExpression("[1, -2]", ColumnType.ListInt64, scalar: _ => ScalarValue.FromInt64List(new long[] { 1, -2 })), Array.Empty<int>());
        rig.Build();
        var request = rig.NewRequest(ThreeItems, userId: 7);

        request.Fetcher.StartContextFetch(new[] { withDefault, list, fits }, CancellationToken.None);
        var result = await request.EnsureAsync(withDefault, list, fits);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(5, request.ContextValue(withDefault).AsInt64());
        Assert.True(request.ContextValue(list).IsMissing);
        Assert.Equal(new long[] { 1, -2 }, request.ContextValue(fits).AsInt64List());
        Assert.Equal(2, request.Fetcher.Counters.OutOfRange);
    }

    [Fact]
    public async Task SharedCounters_ReceiveTheFetchersEvents()
    {
        var counters = new ExpressionCounters();
        var rig = new FeatureTestRig();
        var scaled = rig.Derived("item.scaled", FeatureTestRig.Item, ColumnType.Int32.AsNullable(), Compile("col.entity_id * 1500000000"), Array.Empty<int>());
        rig.Build();
        var memory = new RequestMemory();
        var table = new CandidateTable(new TableSchema(EntityKeyType.Int64, new[] { ColumnType.Int64, ColumnType.Int32.AsNullable() }), 3, memory);
        table.AppendRows(ThreeItems);
        var fetcher = new FeatureFetcher(rig.Plan, table, new ScalarValue[1], new OutputBindings(1, memory), Array.Empty<ScalarValue>(), memory,
            System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency, counters);

        await fetcher.EnsureAsync(new[] { scaled }, CancellationToken.None);

        Assert.Same(counters, fetcher.Counters);
        Assert.Equal(2, counters.OutOfRange);
    }

    // ---- Configuration values -------------------------------------------------------------------------------------------

    [Fact]
    public void ConfigValues_ParseListInt32_HeldAsInt64Lists()
    {
        var value = ConfigValues.ToScalar(JsonNode.Parse("[1, -2, 2147483647, -2147483648]"), ColumnType.ListInt32);
        Assert.Equal(StorageKind.ListInt64, value.Kind);
        Assert.Equal(new long[] { 1, -2, int.MaxValue, int.MinValue }, value.AsInt64List());

        Assert.True(ConfigValues.TryToScalar(null, ColumnType.ListInt32.AsNullable(), out var missing, out _));
        Assert.True(missing.IsMissing);
        Assert.Equal(7, ConfigValues.ToScalar(JsonNode.Parse("7"), ColumnType.Int32).AsInt64());
    }

    [Theory]
    [InlineData("[1, 2147483648]", "list<int32>", "element 1", "list<int64>")]
    [InlineData("[-2147483649]", "list<int32>", "element 0", "list<int64>")]
    [InlineData("3000000000", "int32", "3000000000", "int64")]
    [InlineData("-2147483649.0", "int32", "-2147483649", "int64")]
    public void ConfigValues_OutsideInt32_AreRejected_WithAFix(string json, string type, string where, string fix)
    {
        Assert.False(ConfigValues.TryToScalar(JsonNode.Parse(json), ColumnType.Parse(type), out _, out var error));

        Assert.Contains(where, error);
        Assert.Contains("int32 range", error);
        Assert.Contains("declare the type as " + fix, error);
    }

    private static StagingBuffer Fetch(string json, CompiledFieldBinding[] fields, params long[] keys)
    {
        using var document = JsonDocument.Parse(json);
        var connector = new MemoryFeatureConnector(document.RootElement);
        var staging = new StagingBuffer(fields, keys.Length, new RequestMemory());
        var batch = new EntityKeyBatch { Shape = KeyShape.Many, CandidateKeys = keys.Select(EntityKey.FromInt64).ToArray() };
        var status = connector.FetchAsync(batch, fields, staging, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        Assert.Equal(FetchOutcome.Ok, status.Outcome);
        return staging;
    }

    /// <summary>Compiles a candidate-level derived feature over <c>col.entity_id</c> (the table's key slot).</summary>
    private static CompiledExpression Compile(string source)
    {
        var scope = new DictionaryScope();
        scope.Add(new Symbol(SymbolKind.Column, "col.entity_id", ColumnType.Int64, 0, TableSchema.EntityIdSlot));
        return ExpressionCompiler.Compile(source, scope, CompileOptions.Any);
    }
}
