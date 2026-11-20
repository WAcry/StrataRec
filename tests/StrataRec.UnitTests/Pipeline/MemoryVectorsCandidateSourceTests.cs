using System.Text.Json;
using StrataRec.Core.Bindings;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Pipeline.Sources;

namespace StrataRec.UnitTests.Pipeline;

/// <summary><c>memory_vectors@1</c>, the two-tower service's stand-in: a dot-product scan over its items with the query vector.</summary>
public sealed class MemoryVectorsCandidateSourceTests
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

        var (absent, _) = await RetrieveAsync(source, CandidateQueryValues.Empty);
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

    private static ICandidateSource Create(string settings, string? directory = null) =>
        new MemoryVectorsCandidateSourceFactory().Create(
            new BindingContext("two_tower", JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(settings)!, directory ?? Path.GetTempPath(), null));

    private static CandidateQueryValues Values(ScalarValue value, ColumnType? type = null) =>
        new(["user_embedding"], [type ?? ColumnType.List(StorageKind.Float64, nullable: true, fixedLength: 2)], [value]);

    private static async Task<(CandidateSourceStatus Status, List<string> Found)> RetrieveAsync(ICandidateSource source, CandidateQueryValues values, int limit = 10)
    {
        var sink = new Sink();
        var query = new CandidateQuery { SourceName = "two_tower", Limit = limit, Inputs = new RequestInputs(), Values = values, RequestId = "r" };
        var status = await source.RetrieveAsync(query, sink, CancellationToken.None);
        return (status, sink.Found);
    }

    private sealed class Sink : ICandidateSink
    {
        public List<string> Found { get; } = new();

        public void Add(long entityId, double score) => Found.Add(FormattableString.Invariant($"{entityId}:{score}"));

        public void Add(string entityId, double score) => Found.Add(FormattableString.Invariant($"{entityId}:{score}"));
    }
}
