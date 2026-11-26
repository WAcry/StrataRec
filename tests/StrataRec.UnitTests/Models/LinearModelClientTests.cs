using System.Text.Json;
using StrataRec.Core.Bindings;
using StrataRec.Models;
using StrataRec.Core.Services;

namespace StrataRec.UnitTests.Models;

/// <summary>
/// <c>linear@1</c> takes float32 inputs with one value per row, as its signature declares: <c>ModelScorer@1</c> reports an
/// bool, int or list column mapped to it at load time, and a call that sends another tensor anyway fails instead of reading
/// past or beside its data.
/// </summary>
public sealed class LinearModelClientTests
{
    private const string Settings = """{ "outputs": { "p": { "bias": 1.0, "weights": { "x": 2.0 } } } }""";

    [Fact]
    public async Task TheSignature_IsFloat32Rows()
    {
        var signature = await Create().DescribeAsync("m", null, CancellationToken.None);

        Assert.Equal(new[] { ("x", TensorElementType.Float32, "-1") }, signature.Inputs.Select(s => (s.Name, s.ElementType, string.Join(",", s.Shape))));
        Assert.Equal(new[] { ("p", TensorElementType.Float32, "-1") }, signature.Outputs.Select(s => (s.Name, s.ElementType, string.Join(",", s.Shape))));
    }

    [Fact]
    public async Task Float32Rows_AreScored()
    {
        var outputs = new Outputs();

        var status = await Create().CallAsync(Call(2, NamedInput.FromFloat32("x", new[] { 2 }, new[] { 0.5f, -1f })), outputs, CancellationToken.None);

        Assert.True(status.Succeeded, status.Error);
        Assert.Equal(new[] { 2f, -1f }, outputs.Values["p"]);
    }

    /// <summary>
    /// A call of no batch is one row of float32 scalars (shape <c>[]</c>), one value per output: what a feature source in
    /// mode <c>service</c> sends for an entity with args (IO step 5). A list in such a call fails.
    /// </summary>
    [Fact]
    public async Task ACallOfNoBatch_OfScalars_IsComputedOnce()
    {
        var client = Create();
        var outputs = new Outputs();

        var status = await client.CallAsync(Call(0, NamedInput.FromFloat32("x", [], new[] { 0.5f })), outputs, CancellationToken.None);

        Assert.True(status.Succeeded, status.Error);
        Assert.Equal(new[] { 2f }, outputs.Values["p"]);
        Assert.Equal(
            ServiceStatus.Failed("Input 'x' must be a float32 scalar in a call of no batch."),
            await client.CallAsync(Call(0, NamedInput.FromFloat32("x", new[] { 1 }, new[] { 0.5f })), new Outputs(), CancellationToken.None));
    }

    /// <summary>
    /// The weights are added in the order of their inputs' names, not in the order the settings spell them: a float32
    /// sum depends on its order, and two bindings that differ only in the order of a JSON object's members are the same
    /// content (audit of f47a255: they scored 0 and 1).
    /// </summary>
    [Fact]
    public async Task TheScore_DoesNotDependOnTheOrderTheWeightsAreWrittenIn()
    {
        var a = new Dictionary<string, double> { ["x"] = 1.0, ["y"] = -16777216.0 };
        var b = new Dictionary<string, double> { ["y"] = -16777216.0, ["x"] = 1.0 };
        var scores = new List<float>();
        foreach (var weights in new[] { a, b })
        {
            var client = new LinearModelClient("m", [new LinearOutput("p", 16777216.0, weights, Sigmoid: false)]);
            var outputs = new Outputs();
            var status = await client.CallAsync(
                new ServiceCall { Service = "m", BatchSize = 1, Inputs = new[] { NamedInput.FromFloat32("x", new[] { 1 }, new[] { 1f }), NamedInput.FromFloat32("y", new[] { 1 }, new[] { 1f }) } },
                outputs, CancellationToken.None);
            Assert.True(status.Succeeded, status.Error);
            scores.Add(outputs.Values["p"][0]);
        }

        Assert.Equal(scores[0], scores[1]);
    }

    [Fact]
    public async Task AnotherTensor_FailsTheCall()
    {
        var client = Create();
        var int64 = Call(2, NamedInput.FromInt64("x", new[] { 2 }, new long[] { 1, 2 }));
        var vector = Call(2, NamedInput.FromFloat32("x", new[] { 2, 2 }, new float[4])); // enough floats, two per row
        var shortInput = Call(3, NamedInput.FromFloat32("x", new[] { 2 }, new float[2]));

        Assert.Equal(ServiceStatus.Failed("Input 'x' must be float32 with one value per row (2)."), await client.CallAsync(int64, new Outputs(), CancellationToken.None));
        Assert.Equal(ServiceStatus.Failed("Input 'x' must be float32 with one value per row (2)."), await client.CallAsync(vector, new Outputs(), CancellationToken.None));
        Assert.Equal(ServiceStatus.Failed("Input 'x' must be float32 with one value per row (3)."), await client.CallAsync(shortInput, new Outputs(), CancellationToken.None));
    }

    [Fact]
    public async Task AnInputWhoseShapeIsNotTheBatch_FailsTheCall_EvenWithEnoughData()
    {
        var client = Create();
        var longer = Call(2, NamedInput.FromFloat32("x", new[] { 3 }, new float[3])); // three rows' worth for a batch of two
        var shorter = Call(2, NamedInput.FromFloat32("x", new[] { 1 }, new float[2]));

        Assert.Equal(ServiceStatus.Failed("Input 'x' must be float32 with one value per row (2)."), await client.CallAsync(longer, new Outputs(), CancellationToken.None));
        Assert.Equal(ServiceStatus.Failed("Input 'x' must be float32 with one value per row (2)."), await client.CallAsync(shorter, new Outputs(), CancellationToken.None));
    }

    [Fact]
    public async Task ABackingBufferLongerThanTheTensor_IsAllowed()
    {
        var outputs = new Outputs();

        var status = await Create().CallAsync(Call(2, NamedInput.FromFloat32("x", new[] { 2 }, new[] { 0.5f, -1f, 99f, 99f })), outputs, CancellationToken.None);

        Assert.True(status.Succeeded, status.Error);
        Assert.Equal(new[] { 2f, -1f }, outputs.Values["p"]);
    }

    [Fact]
    public async Task AMissingInput_FailsTheCall_NamingIt()
    {
        // It read the missing value's empty buffer and threw.
        var status = await Create().CallAsync(Call(2, NamedInput.FromMissing("x", TensorElementType.Float32, [2])), new Outputs(), CancellationToken.None);

        Assert.Equal(ServiceStatus.Failed("Input 'x' is missing; linear@1 needs a value per row."), status);
    }

    [Fact]
    public async Task ANegativeBatchSize_FailsTheCall()
    {
        var status = await Create().CallAsync(Call(-1, NamedInput.FromFloat32("x", new[] { 1 }, new float[1])), new Outputs(), CancellationToken.None);

        Assert.Equal(ServiceStatus.Failed("The call's batch size is -1; it must not be negative."), status);
    }

    private static IServiceClient Create() =>
        new LinearModelClientFactory().Create(new BindingContext("m", JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Settings)!, AppContext.BaseDirectory, null));

    private static ServiceCall Call(int rows, NamedInput input) => new() { Service = "m", BatchSize = rows, Inputs = new[] { input } };

    private sealed class Outputs : IServiceResponse
    {
        public void AddItem(long entityId, double score) => throw new NotSupportedException("a model answers with outputs");

        public void AddItem(string entityId, double score) => throw new NotSupportedException("a model answers with outputs");

        public Dictionary<string, float[]> Values { get; } = new();

        public Memory<T> GetValues<T>(string name, int elementCount)
        {
            var buffer = new T[elementCount];
            Values[name] = buffer as float[] ?? throw new InvalidOperationException($"linear@1 wrote a {typeof(T).Name} output");
            return buffer;
        }

        public void SetText(string name, string text) => throw new NotSupportedException();
    }
}
