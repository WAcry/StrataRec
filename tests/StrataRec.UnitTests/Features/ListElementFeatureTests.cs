using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Catalog;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features;

/// <summary>
/// Lists of every element type through the feature layer (design 4.3, 6.5): connector staging, the memory connector,
/// backfill and the non-finite rule for float lists, context values, configuration values and type spellings.
/// </summary>
public class ListElementFeatureTests
{
    private static readonly long[] ThreeItems = { 1, 2, 3 };

    [Fact]
    public void MemoryConnector_DecodesEachElementTypeIntoItsOwnStorage()
    {
        CompiledFieldBinding[] fields =
        {
            new("w32", 0, ColumnType.List(StorageKind.Float32)),
            new("w64", 1, ColumnType.List(StorageKind.Float64)),
            new("clicked", 2, ColumnType.List(StorageKind.Bool)),
        };
        const string Data = """
            { "1": { "w32": [0.5, -1], "w64": [0.1, 2e100], "clicked": [true, false] },
              "2": { "w32": [], "clicked": [] } }
            """;

        var staging = Fetch(Data, fields, 1, 2);

        Assert.Equal(new[] { 0.5f, -1f }, Assert.IsType<ListColumn<float>>(staging.Field(0)).Get(0).ToArray());
        Assert.Equal(new[] { 0.1, 2e100 }, Assert.IsType<ListColumn<double>>(staging.Field(1)).Get(0).ToArray());
        Assert.Equal(new[] { true, false }, Assert.IsType<ListColumn<bool>>(staging.Field(2)).Get(0).ToArray());
        Assert.True(staging.HasValue(0, 1));
        Assert.False(staging.HasValue(1, 1));
        Assert.True(staging.Get(2, 1).AsBoolList().IsEmpty);
    }

    [Fact]
    public void Staging_ListSetterMustMatchTheElementType_AndNamesTheRightOne()
    {
        CompiledFieldBinding[] fields =
        {
            new("f", 0, ColumnType.List(StorageKind.Float32)),
            new("d", 1, ColumnType.List(StorageKind.Float64)),
            new("b", 2, ColumnType.List(StorageKind.Bool)),
            new("s", 3, ColumnType.List(StorageKind.String)),
            new("n", 4, ColumnType.Float64),
        };
        var staging = new StagingBuffer(fields, 2, new RequestMemory());

        Assert.Contains("SetFloat32List", Assert.Throws<InvalidOperationException>(() => staging.SetFloat64List(0, 0, new[] { 1.0 })).Message);
        Assert.Contains("SetFloat64List", Assert.Throws<InvalidOperationException>(() => staging.SetFloat32List(1, 0, new[] { 1f })).Message);
        Assert.Contains("SetBoolList", Assert.Throws<InvalidOperationException>(() => staging.SetInt32List(2, 0, new[] { 1 })).Message);
        Assert.Throws<InvalidOperationException>(() => staging.SetBoolList(4, 0, new[] { true })); // not a list at all
        Assert.Throws<ArgumentNullException>(() => staging.SetStringList(3, 0, new[] { "a", null! }));
        Assert.False(staging.HasValue(3, 0));

        staging.SetBoolList(2, 1, new[] { false, true });
        Assert.Equal(ScalarValue.FromBoolList(new[] { false, true }), staging.Get(2, 1));
    }

    [Fact]
    public async Task Backfill_CopiesFloatAndBoolLists_AndReplacesAFloatListHoldingAnInfinity()
    {
        // 1e300 does not fit float32: it reads as +inf, and the list it is in takes the default as a whole (design 4.3).
        var rig = new FeatureTestRig().MemorySource("item_store", """
            { "1": { "w": [0.5, 1.5], "clicked": [true] },
              "2": { "w": [1, 1e300] },
              "3": { "w": [], "clicked": [false, false] } }
            """);
        var w = rig.Physical("item.w", FeatureTestRig.Item, ColumnType.List(StorageKind.Float32), "item_store", ScalarValue.FromFloat64List(new[] { -1.0 }));
        var clicked = rig.Physical("item.clicked", FeatureTestRig.Item, ColumnType.List(StorageKind.Bool), "item_store");
        rig.Build();
        var request = rig.NewRequest(ThreeItems);

        var result = await request.EnsureAsync(w, clicked);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(ScalarValue.FromFloat64List(new[] { 0.5, 1.5 }), request.Value(w, 0));
        Assert.True(request.Value(w, 0).IsFloat32List);
        Assert.Equal(ScalarValue.FromFloat64List(new[] { -1.0 }), request.Value(w, 1));
        Assert.True(request.Value(w, 2).AsFloat64List().IsEmpty);
        Assert.Equal(new[] { true }, request.Value(clicked, 0).AsBoolList().ToArray());
        Assert.True(request.Value(clicked, 1).IsMissing);
        Assert.Equal(new[] { false, false }, request.Value(clicked, 2).AsBoolList().ToArray());
        Assert.Equal(1, request.Fetcher.NonFiniteReplaced);
    }

    [Fact]
    public async Task ContextFloatList_HoldingAnInfinity_TakesTheDefault_AndIsCounted()
    {
        var rig = new FeatureTestRig().MemorySource("user_store", """{ "7": { "w": [1, 1e300], "v": [2, 3] } }""");
        var w = rig.Physical("user.w", FeatureTestRig.User, ColumnType.List(StorageKind.Float32), "user_store", ScalarValue.FromFloat64List(new[] { 0.0 }));
        var v = rig.Physical("user.v", FeatureTestRig.User, ColumnType.List(StorageKind.Float32), "user_store", ScalarValue.FromFloat64List(new[] { 0.0 }));
        rig.Build();
        var request = rig.NewRequest(ThreeItems, userId: 7);

        request.Fetcher.StartContextFetch(new[] { w, v }, CancellationToken.None);
        var result = await request.EnsureAsync(w, v);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(ScalarValue.FromFloat64List(new[] { 0.0 }), request.ContextValue(w));
        Assert.Equal(ScalarValue.FromFloat64List(new[] { 2.0, 3.0 }), request.ContextValue(v));
        Assert.Equal(1, request.Fetcher.NonFiniteReplaced);
    }

    [Fact]
    public void ConfigValues_ParseEveryElementType_HeldPromoted()
    {
        Assert.Equal(ScalarValue.FromBoolList(new[] { true, false }), ConfigValues.ToScalar(JsonNode.Parse("[true, false]"), ColumnType.Parse("list<bool>")));
        Assert.Equal(ScalarValue.FromFloat64List(new[] { 0.5, 2 }), ConfigValues.ToScalar(JsonNode.Parse("[0.5, 2]"), ColumnType.Parse("list<float32>")));
        Assert.Equal(ScalarValue.FromFloat64List(new double[] { 1, 2 }), ConfigValues.ToScalar(JsonNode.Parse("[1, 2]"), ColumnType.Parse("list<float64>")));
        Assert.Equal(ScalarValue.FromStringList(Array.Empty<string>()), ConfigValues.ToScalar(JsonNode.Parse("[]"), ColumnType.Parse("list<string>")));

        // A host's typed value converts as JSON does: integers into a float list.
        Assert.True(ConfigValues.TryConvert(ScalarValue.FromInt64List(new long[] { 1, 2 }), ColumnType.Parse("list<float32>"), out var converted, out _));
        Assert.Equal(ScalarValue.FromFloat64List(new double[] { 1, 2 }), converted);
    }

    [Theory]
    [InlineData("[true, 1]", "list<bool>", "must be an array of true or false (list<bool>); element 1 is the number 1")]
    [InlineData("[0.5, \"x\"]", "list<float32>", "must be an array of numbers (list<float32>); element 1 is the string \"x\"")]
    [InlineData("[1, null]", "list<float64>", "element 1 is null")]
    [InlineData("0.5", "list<float64>", "must be an array of numbers (list<float64>), got the number 0.5")]
    public void ConfigValues_WrongElements_AreNamedByIndex(string json, string type, string error)
    {
        Assert.False(ConfigValues.TryToScalar(JsonNode.Parse(json), ColumnType.Parse(type), out _, out var message));
        Assert.Contains(error, message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("list<double>", "list<float64>")]
    [InlineData("list<float>", "list<float32>")]
    [InlineData("list<text>", "list<string>")]
    [InlineData("array<long>", "list<int64>")]
    [InlineData("int64[]", "list<int64>")]
    [InlineData("list<boolean>?", "list<bool>?")]
    [InlineData("list<int32?>", "list<int32>?")]
    public void ListSpellingsOfOtherLanguages_SuggestThisOne(string written, string suggestion)
    {
        Assert.False(ConfigTypes.TryParse(written, TypeSite.Column, out _, out _, out _, out var hint));
        Assert.Contains($"did you mean '{suggestion}'", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void ListOfText_SaysWhy()
    {
        Assert.False(ConfigTypes.TryParse("list<text>", TypeSite.Feature, out _, out _, out var message, out var hint));
        Assert.Contains("plain strings of any length", message, StringComparison.Ordinal);
        Assert.Contains("list<E> with E one of bool, int32, int64, float32, float64, string", hint, StringComparison.Ordinal);
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
}
