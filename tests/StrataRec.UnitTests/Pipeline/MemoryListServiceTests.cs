using System.Text.Json;
using StrataRec.Core.Bindings;
using StrataRec.Core.Values;
using StrataRec.Pipeline.Sources;
using StrataRec.Core.Services;

namespace StrataRec.UnitTests.Pipeline;

public sealed class MemoryListServiceTests
{
    private sealed class Sink : IServiceResponse
    {
        public Memory<T> GetValues<T>(string name, int elementCount) => throw new NotSupportedException("a recall answers with items");

        public void SetText(string name, string text) => throw new NotSupportedException("a recall answers with items");

        public List<long> Ids { get; } = new();

        public void AddItem(long entityId, double score) => Ids.Add(entityId);

        public void AddItem(string entityId, double score) => throw new NotSupportedException();
    }

    private static Dictionary<string, JsonElement> Settings(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private static async Task<(ServiceStatus Status, List<long> Ids)> RetrieveAsync(IServiceClient source)
    {
        var sink = new Sink();
        var call = new ServiceCall { Service = "s", Limit = 10, RequestId = "r" };
        var status = await source.CallAsync(call, sink, CancellationToken.None);
        return (status, sink.Ids);
    }

    [Fact]
    public async Task DataFileSuppliesTheList()
    {
        var dir = Directory.CreateTempSubdirectory("stratarec-list-").FullName;
        File.WriteAllText(Path.Combine(dir, "list.json"), """{ "items": [7, 8, 9] }""");

        var source = new MemoryListServiceFactory().Create(new BindingContext("s", Settings("""{ "data_file": "list.json" }"""), dir, null));
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

        var source = new MemoryListServiceFactory().Create(new BindingContext("s", Settings("""{ "data_file": "list.json", "fail": true }"""), dir, null));
        var (status, ids) = await RetrieveAsync(source);

        Assert.False(status.Succeeded);
        Assert.Empty(ids);
    }
}
