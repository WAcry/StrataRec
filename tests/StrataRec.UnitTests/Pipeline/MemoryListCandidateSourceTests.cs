using System.Text.Json;
using StrataRec.Core.Bindings;
using StrataRec.Core.Values;
using StrataRec.Pipeline.Sources;

namespace StrataRec.UnitTests.Pipeline;

public sealed class MemoryListCandidateSourceTests
{
    private sealed class Sink : ICandidateSink
    {
        public List<long> Ids { get; } = new();

        public void Add(long entityId, double score) => Ids.Add(entityId);

        public void Add(string entityId, double score) => throw new NotSupportedException();
    }

    private static Dictionary<string, JsonElement> Settings(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private static async Task<(CandidateSourceStatus Status, List<long> Ids)> RetrieveAsync(ICandidateSource source)
    {
        var sink = new Sink();
        var query = new CandidateQuery { SourceName = "s", Limit = 10, Inputs = new RequestInputs(), RequestId = "r" };
        var status = await source.RetrieveAsync(query, sink, CancellationToken.None);
        return (status, sink.Ids);
    }

    [Fact]
    public async Task DataFileSuppliesTheList()
    {
        var dir = Directory.CreateTempSubdirectory("stratarec-list-").FullName;
        File.WriteAllText(Path.Combine(dir, "list.json"), """{ "items": [7, 8, 9] }""");

        var source = new MemoryListCandidateSourceFactory().Create(new BindingContext("s", Settings("""{ "data_file": "list.json" }"""), dir, null));
        var (status, ids) = await RetrieveAsync(source);

        Assert.True(status.Succeeded);
        Assert.Equal(new long[] { 7, 8, 9 }, ids);
    }

    [Fact]
    public async Task BindingSettingsOverrideTheDataFile()
    {
        // Fault injection must work for fixture-backed sources too.
        var dir = Directory.CreateTempSubdirectory("stratarec-list-").FullName;
        File.WriteAllText(Path.Combine(dir, "list.json"), """{ "items": [7, 8, 9], "fail": false }""");

        var source = new MemoryListCandidateSourceFactory().Create(new BindingContext("s", Settings("""{ "data_file": "list.json", "fail": true }"""), dir, null));
        var (status, ids) = await RetrieveAsync(source);

        Assert.False(status.Succeeded);
        Assert.Empty(ids);
    }
}
