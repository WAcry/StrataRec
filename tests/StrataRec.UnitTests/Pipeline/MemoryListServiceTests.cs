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

    private static async Task<(ServiceStatus Status, List<long> Ids)> RetrieveAsync(IServiceClient source, params NamedInput[] inputs)
    {
        var sink = new Sink();
        var call = new ServiceCall { Service = "s", Limit = 10, RequestId = "r", Inputs = inputs };
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

    /// <summary>
    /// The list is picked by an input of the call, which the recall node's queries send: a service sees only what its call
    /// sends, so the digest a replay matches by covers it (it read the request's inputs, which a replay could drop unseen).
    /// </summary>
    [Fact]
    public async Task ByInput_PicksTheListByTheCallsInput_AMissingValueGetsTheFallback_AndAnAbsentInputFails()
    {
        var source = new MemoryListServiceFactory().Create(new BindingContext("s",
            Settings("""{ "by_input": "segment", "lists": { "x": [7], "42": [8], "1.25": [6], "*": [9] } }"""), Path.GetTempPath(), null));
        var memory = new StrataRec.Core.Memory.RequestMemory();
        NamedInput Segment(StrataRec.Core.Types.ColumnType type, ScalarValue value) => StrataRec.Pipeline.Operators.ServiceInputs.Create("segment", type, value, memory);

        Assert.Equal([7L], (await RetrieveAsync(source, Segment(StrataRec.Core.Types.ColumnType.String, ScalarValue.FromString("x")))).Ids);
        Assert.Equal([8L], (await RetrieveAsync(source, Segment(StrataRec.Core.Types.ColumnType.Int64, ScalarValue.FromInt64(42)))).Ids);
        Assert.Equal([6L], (await RetrieveAsync(source, Segment(StrataRec.Core.Types.ColumnType.Float64, ScalarValue.FromFloat64(1.25)))).Ids); // as the value prints
        Assert.Equal([9L], (await RetrieveAsync(source, Segment(StrataRec.Core.Types.ColumnType.String.AsNullable(), ScalarValue.Missing))).Ids);
        Assert.Equal([9L], (await RetrieveAsync(source, Segment(StrataRec.Core.Types.ColumnType.String, ScalarValue.FromString("other")))).Ids);

        var (absent, ids) = await RetrieveAsync(source);
        Assert.Empty(ids);
        Assert.StartsWith("memory_list@1 picks its list by the input 'segment', and the call of 's' has none", absent.Error, StringComparison.Ordinal);
        Assert.Contains("\"queries\": { \"s\": { \"segment\": \"ctx.request.segment\" } }", absent.Error, StringComparison.Ordinal);

        var (list, _) = await RetrieveAsync(source, Segment(StrataRec.Core.Types.ColumnType.List(StrataRec.Core.Types.StorageKind.Int64), ScalarValue.FromInt64List([1, 2])));
        Assert.Equal("The input 'segment' must be a single value; it has shape [2].", list.Error);
    }

    /// <summary>
    /// −0 picks the list of 0, as a call's digest has them equal: it picked the list of "-0", and a replay of a call with
    /// −0 borrowed the answer to 0 and called it identical (verification 2 of IO step 2).
    /// </summary>
    [Fact]
    public async Task ByInput_NegativeZero_PicksTheListOfZero()
    {
        var source = new MemoryListServiceFactory().Create(new BindingContext("s",
            Settings("""{ "by_input": "segment", "lists": { "0": [7], "-0": [8], "*": [9] } }"""), Path.GetTempPath(), null));
        var memory = new StrataRec.Core.Memory.RequestMemory();
        var float64 = StrataRec.Pipeline.Operators.ServiceInputs.Create("segment", StrataRec.Core.Types.ColumnType.Float64, ScalarValue.FromFloat64(-0.0), memory);
        var float32 = NamedInput.FromFloat32("segment", [], new[] { -0f });

        Assert.Equal([7L], (await RetrieveAsync(source, float64)).Ids);
        Assert.Equal([7L], (await RetrieveAsync(source, float32)).Ids);
    }
}
