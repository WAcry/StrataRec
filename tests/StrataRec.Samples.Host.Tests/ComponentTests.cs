using System.Text.Json;
using StrataRec.Core.Bindings;
using StrataRec.Core.Keys;
using StrataRec.Samples.Host.Components;
using StrataRec.Core.Services;

namespace StrataRec.Samples.Host.Tests;

/// <summary>The sample's custom components on their own, for what the service tests cannot reach.</summary>
public sealed class ComponentTests
{
    [Fact]
    public async Task Logistic_LargeCoefficientsThatCancel_GiveTheirProbability_NotNaN()
    {
        // The factory accepts any finite double, and the call computed in float, where 1e40 is infinite: the
        // logit was inf - inf, and the call returned NaN as a success.
        var client = new LogisticModelClient("m", "v1", "p", bias: -1e40, inputs: new[] { "x" }, weights: new[] { 1e40 });
        var outputs = new Outputs();

        var status = await client.CallAsync(Call(("x", 1f)), outputs, CancellationToken.None);

        Assert.True(status.Succeeded, status.Error);
        Assert.Equal(0.5f, outputs.Values[0]);
    }

    [Fact]
    public async Task Logistic_ALogitThatIsNotANumber_FailsTheCall()
    {
        // Weights whose products overflow double, with opposite signs: no probability exists, and the call says so.
        var client = new LogisticModelClient("m", "v1", "p", bias: 0, inputs: new[] { "a", "b" }, weights: new[] { 1e308, -1e308 });
        var outputs = new Outputs();

        var status = await client.CallAsync(Call(("a", 1e38f), ("b", 1e38f)), outputs, CancellationToken.None);

        Assert.False(status.Succeeded);
        Assert.Contains("not a number", status.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeyValueStore_AStringKey_IsALoadErrorAtItsLine()
    {
        // TryGetInt64 throws on a string, so the record's line and the binding's pointer were lost.
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(file, new[] { """{"id": 1, "x": 1}""", """{"id": "not-a-number", "x": 1}""" });
            var connector = new KeyValueFeatureConnector(file, "id", latencyMs: 0);

            var error = await Assert.ThrowsAsync<BindingException>(() => connector.LoadAsync(CancellationToken.None).AsTask());

            Assert.Equal("/data_file", error.Pointer);
            Assert.StartsWith("Line 2: the record has no int64 'id'", error.Reason, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// The components' settings bind to the types their factories declare: a mistake is an error at its pointer with a
    /// hint, worded as for every built-in component, and the factories check only what the types cannot say.
    /// </summary>
    [Theory]
    [InlineData("logistic@1", """{ "version": "v3", "weights": { "ctr": 1 }, "bais": 1 }""", "/bais", "did you mean 'bias'?")]
    [InlineData("logistic@1", """{ "weights": { "ctr": 1 } }""", "", "'version': ")]
    [InlineData("logistic@1", """{ "version": "v3", "weights": { "ctr": "high" } }""", "/weights/ctr", "'stratarec schema bindings'")]
    [InlineData("logistic@1", """{ "version": "v3", "weights": { "ctr": 1e400 } }""", "/weights/ctr", "use a smaller magnitude")] // Finite only
    [InlineData("logistic@1", """{ "version": "v3", "weights": {} }""", "/weights", "e.g. { \"ctr\": 20.0")]
    [InlineData("trending@1", """{ "items": [{ "id": 7 }] }""", "/items/0", "'score': ")]
    [InlineData("trending@1", """{ "items": [{ "id": "7", "score": 1 }] }""", "/items/0/id", "'stratarec schema bindings'")]
    [InlineData("trending@1", """{ "items": [], "region_input": "" }""", "/region_input", "which the recall node's queries send")]
    [InlineData("kv_store@1", """{ "data_file": "s3://bucket/users.jsonl" }""", "/data_file", "local directory")]
    [InlineData("kv_store@1", """{ "data_file": "users.jsonl", "key_feild": "id" }""", "/key_feild", "did you mean 'key_field'?")]
    [InlineData("kv_store@1", """{ "data_file": "users.jsonl", "latency_ms": -1 }""", "/latency_ms", "simulated round trip")]
    public void ASettingsMistake_IsAnErrorAtItsPointer_WithAHint(string type, string settings, string pointer, string hint)
    {
        var context = new BindingContext("b", JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(settings)!, AppContext.BaseDirectory, null);

        var error = Assert.Throws<BindingException>(() => Create(type, context));

        Assert.Equal(pointer, error.Pointer);
        Assert.Contains(hint, error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void TheKeyValueStoresFile_ResolvesAgainstTheConfigurationDirectory_AndKeepsItsPointer()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "kv-config");
        var context = new BindingContext("b", JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{ "data_file": "data/missing.jsonl" }""")!, directory, null);
        var connector = (KeyValueFeatureConnector)Create("kv_store@1", context);

        var error = Assert.Throws<BindingException>(() => connector.LoadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult());

        Assert.Equal("/data_file", error.Pointer);
        Assert.Contains(Path.Combine(directory, "data", "missing.jsonl"), error.Reason, StringComparison.Ordinal);
    }

    /// <summary>stock@1 answers a call about items, one bool per item; any other call fails, saying what it answers.</summary>
    [Fact]
    public async Task Stock_AnswersEachItem_AndFailsACallItCannotAnswer()
    {
        var service = new StockService([1, 22]);
        var flags = new Flags();
        static ServiceCall About(long[] ids, params string[] outputs) =>
            new() { Service = "stock", BatchSize = ids.Length, Items = ids.Select(EntityKey.FromInt64).ToArray(), Outputs = outputs };

        var answered = await service.CallAsync(About([1, 2, 22], "in_stock"), flags, CancellationToken.None);
        var noItems = await service.CallAsync(new ServiceCall { Service = "stock", Outputs = new[] { "in_stock" } }, new Flags(), CancellationToken.None);
        var other = await service.CallAsync(About([2], "available"), new Flags(), CancellationToken.None);

        Assert.True(answered.Succeeded, answered.Error);
        Assert.Equal([false, true, false], flags.Values);
        Assert.Contains("send the candidates as its items", noItems.Error, StringComparison.Ordinal);
        Assert.Contains("answers output 'in_stock' only", other.Error, StringComparison.Ordinal);
    }

    private static object Create(string type, BindingContext context) => type switch
    {
        "logistic@1" => new LogisticModelClientFactory().Create(context),
        "trending@1" => new TrendingServiceFactory().Create(context),
        _ => new KeyValueFeatureConnectorFactory().Create(context),
    };

    private static ServiceCall Call(params (string Name, float Value)[] inputs)
    {
        var named = new NamedInput[inputs.Length];
        for (var i = 0; i < inputs.Length; i++)
        {
            named[i] = NamedInput.FromFloat32(inputs[i].Name, new[] { 1 }, new[] { inputs[i].Value });
        }

        return new ServiceCall { Service = "m", Version = "v1", Inputs = named, BatchSize = 1 };
    }

    private sealed class Flags : IServiceResponse
    {
        public bool[] Values { get; private set; } = [];

        public void AddItem(long entityId, double score) => throw new NotSupportedException("a filter's service answers with outputs");

        public void AddItem(string entityId, double score) => throw new NotSupportedException("a filter's service answers with outputs");

        public Memory<T> GetValues<T>(string name, int elementCount)
        {
            var buffer = new T[elementCount];
            Values = buffer as bool[] ?? throw new InvalidOperationException($"stock@1 wrote a {typeof(T).Name} output");
            return buffer;
        }

        public void SetText(string name, string text) => throw new NotSupportedException();
    }

    private sealed class Outputs : IServiceResponse
    {
        public void AddItem(long entityId, double score) => throw new NotSupportedException("a model answers with outputs");

        public void AddItem(string entityId, double score) => throw new NotSupportedException("a model answers with outputs");

        public float[] Values { get; private set; } = Array.Empty<float>();

        public Memory<T> GetValues<T>(string name, int elementCount)
        {
            var buffer = new T[elementCount];
            Values = buffer as float[] ?? throw new InvalidOperationException($"the logistic model wrote a {typeof(T).Name} output");
            return buffer;
        }

        public void SetText(string name, string text) => throw new NotSupportedException();
    }
}
