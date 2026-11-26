using System.Text.Json;
using StrataRec.Core.Bindings;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Core.Memory;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Sources;
using StrataRec.Core.Services;

namespace StrataRec.UnitTests.Pipeline;

/// <summary><c>memory_vectors@1</c>, the two-tower service's stand-in: a dot-product scan over its items with the query vector.</summary>
public sealed class MemoryVectorsServiceTests
{
    private const string Items = """
        "items": [
          { "id": 3, "vector": [0.5, 0.5] },
          { "id": "b", "vector": [1.0, 0.0] },
          { "id": 1, "vector": [1.0, 0.0] },
          { "id": 2, "vector": [0.0, 1.0] },
          { "id": "a", "vector": [1.0, 0.0] },
          { "id": 4, "vector": [-1.0, 0.0] }
        ]
        """;

    [Fact]
    public async Task TheBestDotProductsComeFirst_TiesByIdWithInt64IdsBeforeStrings_UpToTheLimit()
    {
        var source = Create($$"""{ "query": "user_embedding", {{Items}} }""");

        var (status, found) = await RetrieveAsync(source, Values(ScalarValue.FromFloat64List([1.0, 0.5])), limit: 5);

        Assert.True(status.Succeeded, status.Error);
        Assert.Equal(["1:1", "a:1", "b:1", "3:0.75", "2:0.5"], found);
    }

    /// <summary>
    /// A float32 embedding (<c>list&lt;float32,2&gt;</c>, as a catalog declares it) is a query vector: a query sends a
    /// reference at its declared type, and the scan scores it in float64 as it scores a float64 one.
    /// </summary>
    [Fact]
    public async Task AFloat32QueryVector_IsScoredAsAFloat64One()
    {
        var source = Create($$"""{ "query": "user_embedding", {{Items}} }""");

        var (status, found) = await RetrieveAsync(source, Values(ScalarValue.FromFloat64List([1.0, 0.5]), ColumnType.List(StorageKind.Float32, fixedLength: 2)), limit: 5);

        Assert.True(status.Succeeded, status.Error);
        Assert.Equal(["1:1", "a:1", "b:1", "3:0.75", "2:0.5"], found);
    }

    [Fact]
    public async Task MinScore_LeavesOutTheItemsBelowIt()
    {
        var source = Create($$"""{ "query": "user_embedding", "min_score": 0.6, {{Items}} }""");

        var (status, found) = await RetrieveAsync(source, Values(ScalarValue.FromFloat64List([1.0, 0.5])), limit: 100);

        Assert.True(status.Succeeded, status.Error);
        Assert.Equal(["1:1", "a:1", "b:1", "3:0.75"], found);
    }

    [Fact]
    public async Task AMissingQueryVector_ReturnsNothing_ForTheNodesOtherSourcesToAnswer()
    {
        var source = Create($$"""{ "query": "user_embedding", {{Items}} }""");

        var (status, found) = await RetrieveAsync(source, Values(ScalarValue.Missing));

        Assert.True(status.Succeeded, status.Error);
        Assert.Empty(found);
    }

    [Fact]
    public async Task AQueryWithoutTheValue_OrWithAVectorOfAnotherLength_IsAFailedCall_NamingIt()
    {
        var source = Create($$"""{ "query": "user_embedding", {{Items}} }""");

        var (absent, _) = await RetrieveAsync(source, []);
        Assert.False(absent.Succeeded);
        Assert.Contains("reads the query vector 'user_embedding', and the query of 'two_tower' has no such value", absent.Error, StringComparison.Ordinal);
        Assert.Contains("\"queries\": { \"two_tower\": { \"user_embedding\": \"ctx.user.embedding\" } }", absent.Error, StringComparison.Ordinal);

        var (length, _) = await RetrieveAsync(source, Values(ScalarValue.FromFloat64List([1.0, 0.0, 0.0])));
        Assert.Equal("The query vector 'user_embedding' has 3 elements; the items' vectors have 2.", length.Error);

        var (kind, _) = await RetrieveAsync(source, Values(ScalarValue.FromString("x"), ColumnType.String));
        Assert.False(kind.Succeeded);
        Assert.StartsWith("The query vector 'user_embedding' must be a list of floats", kind.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheVectorsLength_IsItsShapes_NotItsBuffers()
    {
        // A longer buffer than the shape holds is allowed (a rented one): a vector of shape [0] with two values in its
        // buffer has no elements. It searched with the two (review of IO step 2).
        var source = Create($$"""{ "query": "user_embedding", {{Items}} }""");

        var (status, found) = await RetrieveAsync(source, [NamedInput.FromFloat64("user_embedding", [0], new[] { 1.0, 0.5 })]);

        Assert.Equal("The query vector 'user_embedding' has 0 elements; the items' vectors have 2.", status.Error);
        Assert.Empty(found);
    }

    [Fact]
    public async Task AnIndexWithoutItems_FindsNothing_ForAnyVector()
    {
        var source = Create("""{ "query": "user_embedding", "items": [] }""");

        var (status, found) = await RetrieveAsync(source, Values(ScalarValue.FromFloat64List([1.0, 2.0])));

        Assert.True(status.Succeeded, status.Error); // it said "the items' vectors have 0"
        Assert.Empty(found);
    }

    [Fact]
    public async Task AQueryValueNotOfAFloatListType_OrOfAFixedLengthNotTheItems_FailsEvenWhenMissing()
    {
        var source = Create($$"""{ "query": "user_embedding", {{Items}} }""");

        // A missing value of a wrong type was taken as nothing to search with.
        var (wrongType, _) = await RetrieveAsync(source, Values(ScalarValue.Missing, ColumnType.Int64.AsNullable()));
        Assert.Equal("The query vector 'user_embedding' must be a list of floats; it is int64 of shape [].", wrongType.Error);

        var (wrongLength, _) = await RetrieveAsync(source, Values(ScalarValue.Missing, ColumnType.List(StorageKind.Float64, nullable: true, fixedLength: 3)));
        Assert.Equal("The query vector 'user_embedding' has 3 elements; the items' vectors have 2.", wrongLength.Error);
    }

    [Fact]
    public async Task AScoreThatOverflows_FailsTheCall_InsteadOfRankingByNaN()
    {
        // 1e308 · 2 overflows to +∞ and 1e308 · −2 to −∞: their sum is NaN, which dropped the right first item.
        var source = Create("""
            { "query": "user_embedding", "min_score": 1, "items": [
              { "id": 1, "vector": [1e308, 1e308, 2.0] },
              { "id": 2, "vector": [0.0, 0.0, 1.0] } ] }
            """);

        var (status, found) = await RetrieveAsync(
            source, Values(ScalarValue.FromFloat64List([2.0, -2.0, 1.0]), ColumnType.List(StorageKind.Float64, fixedLength: 3)));

        Assert.False(status.Succeeded);
        Assert.Equal("The dot product of item 1 with the query vector 'user_embedding' is not finite; scale the vectors so it fits a float64.", status.Error);
        Assert.Empty(found);
    }

    [Fact]
    public async Task TheDataFileSuppliesTheItems_AndItsErrorsAreAtTheSetting_NamingThePlaceInTheFile()
    {
        var dir = Directory.CreateTempSubdirectory("stratarec-vectors-").FullName;
        File.WriteAllText(Path.Combine(dir, "vectors.json"), "{ " + Items + " }");
        var source = Create("""{ "query": "user_embedding", "data_file": "vectors.json" }""", dir);

        var (status, found) = await RetrieveAsync(source, Values(ScalarValue.FromFloat64List([0.0, 1.0])), limit: 1);
        Assert.True(status.Succeeded, status.Error);
        Assert.Equal(["2:1"], found);

        File.WriteAllText(Path.Combine(dir, "bad.json"), """{ "items": [{ "id": 1, "vector": [1.0] }, { "id": 2, "vector": [1.0, 2.0] }] }""");
        var error = Assert.Throws<BindingException>(() => Create("""{ "query": "user_embedding", "data_file": "bad.json" }""", dir));
        Assert.Equal("/data_file", error.Pointer);
        Assert.EndsWith("bad.json' at /items/1/vector: Item 2 has a vector of 2 elements; the first item's has 1.", error.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "query": "", "items": [{ "id": 1, "vector": [1.0] }] }""", "/query")]
    [InlineData("""{ "query": "q", "latency_ms": -1, "items": [{ "id": 1, "vector": [1.0] }] }""", "/latency_ms")]
    [InlineData("""{ "query": "q" }""", "")]
    [InlineData("""{ "query": "q", "items": [{ "id": 1.5, "vector": [1.0] }] }""", "/items/0/id")]
    [InlineData("""{ "query": "q", "items": [{ "id": 1, "vector": [] }] }""", "/items/0/vector")]
    [InlineData("""{ "query": "q", "items": [{ "id": 1, "vector": [1.0] }, { "id": 2, "vector": [1.0, 0.0] }] }""", "/items/1/vector")]
    public void AnInvalidSetting_IsAtItsPointer(string settings, string pointer)
    {
        var error = Assert.Throws<BindingException>(() => Create(settings));

        Assert.Equal(pointer, error.Pointer);
    }

    private static IServiceClient Create(string settings, string? directory = null) =>
        new MemoryVectorsServiceFactory().Create(
            new BindingContext("two_tower", JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(settings)!, directory ?? Path.GetTempPath(), null));

    /// <summary>The query as a recall node sends it (<see cref="ServiceInputs.Create"/>).</summary>
    private static NamedInput[] Values(ScalarValue value, ColumnType? type = null) =>
        [ServiceInputs.Create("user_embedding", type ?? ColumnType.List(StorageKind.Float64, nullable: true, fixedLength: 2), value, new RequestMemory())];

    private static async Task<(ServiceStatus Status, List<string> Found)> RetrieveAsync(IServiceClient source, NamedInput[] values, int limit = 10)
    {
        var sink = new Sink();
        var call = new ServiceCall { Service = "two_tower", Limit = limit, Inputs = values, RequestId = "r" };
        var status = await source.CallAsync(call, sink, CancellationToken.None);
        return (status, sink.Found);
    }

    private sealed class Sink : IServiceResponse
    {
        public Memory<T> GetValues<T>(string name, int elementCount) => throw new NotSupportedException("a recall answers with items");

        public void SetText(string name, string text) => throw new NotSupportedException("a recall answers with items");

        public List<string> Found { get; } = new();

        public void AddItem(long entityId, double score) => Found.Add(FormattableString.Invariant($"{entityId}:{score}"));

        public void AddItem(string entityId, double score) => Found.Add(FormattableString.Invariant($"{entityId}:{score}"));
    }
}
