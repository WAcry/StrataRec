using System.Text.Json;
using StrataRec.Core.Bindings;
using StrataRec.Models;

namespace StrataRec.UnitTests.Models;

/// <summary>
/// <c>linear@1</c> takes float32 inputs with one value per row, as its signature declares: <c>ModelScorer@1</c> reports an
/// int or vector column mapped to it at load time, and a call that sends another tensor anyway fails instead of reading
/// past or beside its data.
/// </summary>
public sealed class LinearModelClientTests
{
    private const string Settings = """{ "outputs": { "p": { "bias": 1.0, "weights": { "x": 2.0 } } } }""";

    [Fact]
    public async Task TheSignature_IsFloat32Rows()
    {
        var signature = await Create().GetSignatureAsync("m", null, CancellationToken.None);

        Assert.Equal(new[] { ("x", TensorElementType.Float32, "-1") }, signature.Inputs.Select(s => (s.Name, s.ElementType, string.Join(",", s.Shape))));
        Assert.Equal(new[] { ("p", TensorElementType.Float32, "-1") }, signature.Outputs.Select(s => (s.Name, s.ElementType, string.Join(",", s.Shape))));
    }

    [Fact]
    public async Task Float32Rows_AreScored()
    {
        var outputs = new Outputs();

        var status = await Create().InvokeAsync(Call(2, NamedInput.FromFloat32("x", new[] { 2 }, new[] { 0.5f, -1f })), outputs, CancellationToken.None);

        Assert.True(status.Succeeded, status.Error);
        Assert.Equal(new[] { 2f, -1f }, outputs.Values["p"]);
    }

    [Fact]
    public async Task AnotherTensor_FailsTheCall()
    {
        var client = Create();
        var int64 = Call(2, NamedInput.FromInt64("x", new[] { 2 }, new long[] { 1, 2 }));
        var vector = Call(2, NamedInput.FromFloat32("x", new[] { 2, 2 }, new float[4])); // enough floats, two per row
        var shortInput = Call(3, NamedInput.FromFloat32("x", new[] { 2 }, new float[2]));

        Assert.Equal(ModelStatus.Failed("Input 'x' must be float32 with one value per row (2)."), await client.InvokeAsync(int64, new Outputs(), CancellationToken.None));
        Assert.Equal(ModelStatus.Failed("Input 'x' must be float32 with one value per row (2)."), await client.InvokeAsync(vector, new Outputs(), CancellationToken.None));
        Assert.Equal(ModelStatus.Failed("Input 'x' must be float32 with one value per row (3)."), await client.InvokeAsync(shortInput, new Outputs(), CancellationToken.None));
    }

    [Fact]
    public async Task AnInputWhoseShapeIsNotTheBatch_FailsTheCall_EvenWithEnoughData()
    {
        var client = Create();
        var longer = Call(2, NamedInput.FromFloat32("x", new[] { 3 }, new float[3])); // three rows' worth for a batch of two
        var shorter = Call(2, NamedInput.FromFloat32("x", new[] { 1 }, new float[2]));

        Assert.Equal(ModelStatus.Failed("Input 'x' must be float32 with one value per row (2)."), await client.InvokeAsync(longer, new Outputs(), CancellationToken.None));
        Assert.Equal(ModelStatus.Failed("Input 'x' must be float32 with one value per row (2)."), await client.InvokeAsync(shorter, new Outputs(), CancellationToken.None));
    }

    [Fact]
    public async Task ABackingBufferLongerThanTheTensor_IsAllowed()
    {
        var outputs = new Outputs();

        var status = await Create().InvokeAsync(Call(2, NamedInput.FromFloat32("x", new[] { 2 }, new[] { 0.5f, -1f, 99f, 99f })), outputs, CancellationToken.None);

        Assert.True(status.Succeeded, status.Error);
        Assert.Equal(new[] { 2f, -1f }, outputs.Values["p"]);
    }

    [Fact]
    public async Task ANegativeBatchSize_FailsTheCall()
    {
        var status = await Create().InvokeAsync(Call(-1, NamedInput.FromFloat32("x", new[] { -1 }, new float[1])), new Outputs(), CancellationToken.None);

        Assert.Equal(ModelStatus.Failed("The call's batch size is -1; it must not be negative."), status);
    }

    private static IModelClient Create() =>
        new LinearModelClientFactory().Create(new BindingContext("m", JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Settings)!, AppContext.BaseDirectory, null));

    private static ModelCall Call(int rows, NamedInput input) => new() { Model = "m", BatchSize = rows, Inputs = new[] { input } };

    private sealed class Outputs : IModelOutputWriter
    {
        public Dictionary<string, float[]> Values { get; } = new();

        public Memory<float> GetFloat32(string name, int elementCount) => Values[name] = new float[elementCount];

        public void SetText(string name, string text) => throw new NotSupportedException();
    }
}
