using StrataRec.Core.Bindings;
using StrataRec.Models;
using StrataRec.Samples.Host.Components;

namespace StrataRec.Samples.Host.Tests;

/// <summary>The sample's custom components on their own, for what the service tests cannot reach.</summary>
public sealed class ComponentTests
{
    [Fact]
    public async Task Logistic_LargeCoefficientsThatCancel_GiveTheirProbability_NotNaN()
    {
        // Review 32: the factory accepts any finite double, and the call computed in float, where 1e40 is infinite: the
        // logit was inf - inf, and the call returned NaN as a success.
        var client = new LogisticModelClient("m", "v1", "p", bias: -1e40, inputs: new[] { "x" }, weights: new[] { 1e40 });
        var outputs = new Outputs();

        var status = await client.InvokeAsync(Call(("x", 1f)), outputs, CancellationToken.None);

        Assert.True(status.Succeeded, status.Error);
        Assert.Equal(0.5f, outputs.Values[0]);
    }

    [Fact]
    public async Task Logistic_ALogitThatIsNotANumber_FailsTheCall()
    {
        // Weights whose products overflow double, with opposite signs: no probability exists, and the call says so.
        var client = new LogisticModelClient("m", "v1", "p", bias: 0, inputs: new[] { "a", "b" }, weights: new[] { 1e308, -1e308 });
        var outputs = new Outputs();

        var status = await client.InvokeAsync(Call(("a", 1e38f), ("b", 1e38f)), outputs, CancellationToken.None);

        Assert.False(status.Succeeded);
        Assert.Contains("not a number", status.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeyValueStore_AStringKey_IsALoadErrorAtItsLine()
    {
        // Review 32: TryGetInt64 throws on a string, so the record's line and the binding's pointer were lost.
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

    private static ModelCall Call(params (string Name, float Value)[] inputs)
    {
        var named = new NamedInput[inputs.Length];
        for (var i = 0; i < inputs.Length; i++)
        {
            named[i] = NamedInput.FromFloat32(inputs[i].Name, new[] { 1 }, new[] { inputs[i].Value });
        }

        return new ModelCall { Model = "m", Version = "v1", Inputs = named, BatchSize = 1 };
    }

    private sealed class Outputs : IModelOutputWriter
    {
        public float[] Values { get; private set; } = Array.Empty<float>();

        public Memory<float> GetFloat32(string name, int elementCount) => Values = new float[elementCount];

        public void SetText(string name, string text) => throw new NotSupportedException();
    }
}
