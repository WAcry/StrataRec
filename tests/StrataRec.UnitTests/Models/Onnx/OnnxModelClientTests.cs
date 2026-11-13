using System.Text.Json;
using StrataRec.Core.Bindings;
using StrataRec.Models;
using StrataRec.Models.Onnx;
using StrataRec.Testing;

namespace StrataRec.UnitTests.Models.Onnx;

/// <summary>
/// <c>local_onnx@1</c> (design 6.9): settings checked without I/O, files opened at load with the signature from the
/// session's metadata, unsupported tensor types as load errors at their setting, and calls over the caller's buffers.
/// The models are encoded by <see cref="OnnxModels"/>, so the tests need nothing outside the repository.
/// </summary>
public sealed class OnnxModelClientTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("stratarec-onnx-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task AModelLoads_WithItsSignatureFromTheSession_AndScoresIntoTheCallersBuffers()
    {
        Write("ctr.onnx", OnnxModels.CtrModel());
        using var client = await Loaded("""{ "uri": "ctr.onnx" }""");

        var signature = await client.GetSignatureAsync("ctr_model", null, CancellationToken.None);
        Assert.Equal(["log_followers", "smoothed_ctr"], signature.Inputs.Select(i => i.Name));
        Assert.All(signature.Inputs, i => Assert.Equal("Float32 [-1]", $"{i.ElementType} [{string.Join(", ", i.Shape)}]"));
        var output = Assert.Single(signature.Outputs);
        Assert.Equal("p_click Float32 [-1]", $"{output.Name} {output.ElementType} [{string.Join(", ", output.Shape)}]");

        float[] ctr = [0.01f, 0.1f, 0.2f];
        float[] followers = [1f, 5f, 9f];
        var writer = new Writer();
        var status = await client.InvokeAsync(Call(null, Input("smoothed_ctr", ctr), Input("log_followers", followers)), writer, CancellationToken.None);

        Assert.True(status.Succeeded, status.Error);
        var scores = writer.Outputs["p_click"];
        for (var i = 0; i < ctr.Length; i++)
        {
            Assert.Equal(1f / (1f + MathF.Exp(-(-1.5f + (12f * ctr[i]) + (0.05f * followers[i])))), scores[i], 5);
        }
    }

    [Fact]
    public async Task VersionsMapToFiles_AndVersionsNamingOneFileShareItsSession()
    {
        Write("a.onnx", OnnxModels.Linear("a", "p_click", 0f, [("x", 1f)], sigmoid: false));
        Write("b.onnx", OnnxModels.Linear("b", "p_click", 10f, [("x", 2f)], sigmoid: false));
        using var client = await Loaded("""{ "uri": "a.onnx", "versions": { "v1": "a.onnx", "v2": "b.onnx" } }""");

        Assert.Equal(new[] { 1f, 3f }, await Score(client, null));
        Assert.Equal(new[] { 1f, 3f }, await Score(client, "v1"));
        Assert.Equal(new[] { 12f, 16f }, await Score(client, "v2"));
        Assert.Equal(2, client.SessionCount); // a.onnx is opened once for the default and v1

        var unknown = await Assert.ThrowsAsync<BindingException>(() => client.GetSignatureAsync("ctr_model", "v3", CancellationToken.None).AsTask());
        Assert.Equal("/versions", unknown.Pointer);
        Assert.Contains("its versions are v1, v2", unknown.Hint, StringComparison.Ordinal);
        var failed = await client.InvokeAsync(Call("v3", Input("x", [1f])), new Writer(), CancellationToken.None);
        Assert.Equal((false, "Model 'ctr_model' has no version 'v3'."), (failed.Succeeded, failed.Error));
    }

    [Fact]
    public async Task Int64InputsAreSupported()
    {
        Write("ids.onnx", OnnxModels.Linear("ids", "score", 0f, [("item_id", 0.5f)], sigmoid: false, inputType: OnnxModels.Int64));
        using var client = await Loaded("""{ "uri": "ids.onnx" }""");

        var input = Assert.Single((await client.GetSignatureAsync("ctr_model", null, CancellationToken.None)).Inputs);
        Assert.Equal(TensorElementType.Int64, input.ElementType);
        var writer = new Writer();
        var status = await client.InvokeAsync(Call(null, NamedInput.FromInt64("item_id", [2], new long[] { 4, 10 })), writer, CancellationToken.None);

        Assert.True(status.Succeeded, status.Error);
        Assert.Equal(new[] { 2f, 5f }, writer.Outputs["score"]);
    }

    [Theory]
    [InlineData(OnnxModels.Double, "Double")]
    [InlineData(OnnxModels.String, "String")]
    public async Task AnUnsupportedTensorType_FailsTheLoadAtTheFilesSetting_WithAHint(int type, string named)
    {
        Write("m.onnx", OnnxModels.Cast("x", type, "y"));
        Write("ok.onnx", OnnxModels.CtrModel());
        var client = Create("""{ "uri": "ok.onnx", "versions": { "v2": "m.onnx" } }""");

        var error = await Assert.ThrowsAsync<BindingException>(() => client.LoadAsync(CancellationToken.None).AsTask());

        Assert.Equal("/versions/v2", error.Pointer);
        Assert.Contains($"The input 'x' of '{Path.Combine(_root, "m.onnx")}' is a tensor of {named}.", error.Reason, StringComparison.Ordinal);
        Assert.Contains("float32 and int64", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingOrBrokenFile_FailsTheLoadAtItsSetting()
    {
        Write("broken.onnx", [1, 2, 3, 4]);

        var missing = await Assert.ThrowsAsync<BindingException>(() => Create("""{ "uri": "missing.onnx" }""").LoadAsync(CancellationToken.None).AsTask());
        var broken = await Assert.ThrowsAsync<BindingException>(() => Create("""{ "uri": "broken.onnx" }""").LoadAsync(CancellationToken.None).AsTask());

        Assert.Equal(("/uri", "/uri"), (missing.Pointer, broken.Pointer));
        Assert.Contains("does not exist", missing.Reason, StringComparison.Ordinal);
        Assert.Contains("relative path resolves against the configuration directory", missing.Hint, StringComparison.Ordinal);
        Assert.Contains("ONNX Runtime cannot load", broken.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "uri": "m.onnx", "inter_op_threads": 2 }""", "/inter_op_threads", "has no setting")]
    [InlineData("""{ }""", "/uri", "'uri' is required")]
    [InlineData("""{ "uri": "s3://bucket/m.onnx" }""", "/uri", "is not a local path")]
    [InlineData("""{ "uri": "m.onnx", "intra_op_threads": 0 }""", "/intra_op_threads", "from 1 to 256")]
    [InlineData("""{ "uri": "m.onnx", "intra_op_threads": 1.5 }""", "/intra_op_threads", "from 1 to 256")]
    [InlineData("""{ "uri": "m.onnx", "versions": ["v1"] }""", "/versions", "must be an object")]
    [InlineData("""{ "uri": "m.onnx", "versions": { "v~1": 3 } }""", "/versions/v~01", "non-empty id")]
    public void SettingsAreCheckedWithoutReadingAFile(string settings, string pointer, string reason)
    {
        var error = Assert.Throws<BindingException>(() => Create(settings));

        Assert.Equal(pointer, error.Pointer);
        Assert.Contains(reason, error.Reason, StringComparison.Ordinal);
        Assert.NotNull(error.Hint);
    }

    [Fact]
    public void AClientIsCreatedWithoutItsFile_SoValidateNeedsNone()
    {
        var client = Create("""{ "uri": "not-there-yet.onnx", "intra_op_threads": 2 }""");

        Assert.IsAssignableFrom<ILoadable>(client);
        Assert.Equal(OnnxModelClientFactory.Type, client.ClientType);
    }

    [Fact]
    public async Task ACallWithACancelledTokenDoesNotRun_AndAWrongCallIsAFailedStatus()
    {
        Write("ctr.onnx", OnnxModels.CtrModel());
        using var client = await Loaded("""{ "uri": "ctr.onnx" }""");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.InvokeAsync(Call(null, Input("smoothed_ctr", [1f]), Input("log_followers", [1f])), new Writer(), new CancellationToken(canceled: true)).AsTask());
        var missing = await client.InvokeAsync(Call(null, Input("smoothed_ctr", [1f])), new Writer(), CancellationToken.None);
        var mistyped = await client.InvokeAsync(Call(null, Input("smoothed_ctr", [1f]), NamedInput.FromInt64("log_followers", [1], new long[] { 1 })), new Writer(),
            CancellationToken.None);
        var misSized = await client.InvokeAsync(Call(null, Input("smoothed_ctr", [1f]), NamedInput.FromFloat32("log_followers", [2], new float[] { 1 })), new Writer(),
            CancellationToken.None);

        Assert.Equal("The call has no input 'log_followers', which the model requires.", missing.Error);
        Assert.Equal("Input 'log_followers' is float32 in the model; the call sent int64.", mistyped.Error);
        Assert.Equal("Input 'log_followers' has 1 values for a shape of 2.", misSized.Error);
    }

    [Fact]
    public async Task ADisposedClientServesNothing_AndCannotLoad()
    {
        Write("ctr.onnx", OnnxModels.CtrModel());
        var client = await Loaded("""{ "uri": "ctr.onnx" }""");

        client.Dispose();
        client.Dispose();

        var status = await client.InvokeAsync(Call(null, Input("smoothed_ctr", [1f]), Input("log_followers", [1f])), new Writer(), CancellationToken.None);
        Assert.Equal("Model 'ctr_model' is not loaded.", status.Error);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.LoadAsync(CancellationToken.None).AsTask());
    }

    private async Task<float[]> Score(OnnxModelClient client, string? version)
    {
        var writer = new Writer();
        var status = await client.InvokeAsync(Call(version, Input("x", [1f, 3f])), writer, CancellationToken.None);
        Assert.True(status.Succeeded, status.Error);
        return writer.Outputs["p_click"];
    }

    private async Task<OnnxModelClient> Loaded(string settings)
    {
        var client = Create(settings);
        await client.LoadAsync(CancellationToken.None);
        return client;
    }

    private OnnxModelClient Create(string settings)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in JsonDocument.Parse(settings).RootElement.EnumerateObject())
        {
            values[property.Name] = property.Value.Clone();
        }

        return (OnnxModelClient)new OnnxModelClientFactory().Create(new BindingContext("ctr_model", values, _root, null));
    }

    private void Write(string name, byte[] model) => File.WriteAllBytes(Path.Combine(_root, name), model);

    internal static NamedInput Input(string name, float[] values) => NamedInput.FromFloat32(name, [values.Length], values);

    internal static ModelCall Call(string? version, params NamedInput[] inputs) => new()
    {
        Model = "ctr_model",
        Version = version,
        Inputs = inputs,
        BatchSize = inputs[0].Shape[0],
    };

    /// <summary>Hands out buffers like the scorer's writer: exactly the size asked for.</summary>
    internal sealed class Writer : IModelOutputWriter
    {
        public Dictionary<string, float[]> Outputs { get; } = new(StringComparer.Ordinal);

        public Memory<float> GetFloat32(string name, int elementCount) => Outputs[name] = new float[elementCount];

        public void SetText(string name, string text)
        {
        }
    }
}
