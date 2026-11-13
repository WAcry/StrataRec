using System.Text.Json;

namespace StrataRec.Models;

/// <summary>Element type of a model tensor.</summary>
public enum TensorElementType : byte
{
    Float32 = 1,
    Int64,
    Text,
}

/// <summary>Name, element type and shape of a model input or output. A dimension of -1 is dynamic (batch).</summary>
public sealed record TensorSpec(string Name, TensorElementType ElementType, IReadOnlyList<int> Shape);

/// <summary>Inputs and outputs a model version accepts and produces; checked at load time (design 6.9).</summary>
public sealed record ModelSignature(IReadOnlyList<TensorSpec> Inputs, IReadOnlyList<TensorSpec> Outputs)
{
    public TensorSpec? FindInput(string name) => Inputs.FirstOrDefault(i => i.Name == name);

    public TensorSpec? FindOutput(string name) => Outputs.FirstOrDefault(o => o.Name == name);
}

/// <summary>One named model input: a dense tensor or a text value.</summary>
public readonly struct NamedInput
{
    private NamedInput(string name, TensorElementType type, int[] shape, ReadOnlyMemory<float> f32, ReadOnlyMemory<long> i64, string? text)
    {
        Name = name;
        ElementType = type;
        Shape = shape;
        Float32 = f32;
        Int64 = i64;
        Text = text;
    }

    public string Name { get; }

    public TensorElementType ElementType { get; }

    public int[] Shape { get; }

    public ReadOnlyMemory<float> Float32 { get; }

    public ReadOnlyMemory<long> Int64 { get; }

    public string? Text { get; }

    public static NamedInput FromFloat32(string name, int[] shape, ReadOnlyMemory<float> data) =>
        new(name, TensorElementType.Float32, shape, data, default, null);

    public static NamedInput FromInt64(string name, int[] shape, ReadOnlyMemory<long> data) =>
        new(name, TensorElementType.Int64, shape, default, data, null);

    public static NamedInput FromText(string name, string text) =>
        new(name, TensorElementType.Text, Array.Empty<int>(), default, default, text);
}

/// <summary>Per-call options.</summary>
public readonly record struct ModelCallOptions(long? Seed = null, int? MaxOutputTokens = null);

/// <summary>A request to a model: named inputs in, named outputs out (design 6.9).</summary>
public sealed class ModelCall
{
    public required string Model { get; init; }

    public string? Version { get; init; }

    public required ReadOnlyMemory<NamedInput> Inputs { get; init; }

    /// <summary>Number of rows in this batch (the leading dimension of row-wise tensors).</summary>
    public int BatchSize { get; init; }

    public ModelCallOptions Options { get; init; }
}

/// <summary>Receives model outputs. Buffers are owned by the caller; clients write into them.</summary>
public interface IModelOutputWriter
{
    /// <summary>Returns a buffer for a float32 output with <paramref name="elementCount"/> elements.</summary>
    Memory<float> GetFloat32(string name, int elementCount);

    void SetText(string name, string text);
}

/// <summary>Outcome of a model call.</summary>
public readonly record struct ModelStatus(bool Succeeded, string? Error = null)
{
    public static ModelStatus Ok => new(true);

    public static ModelStatus Failed(string error) => new(false, error);
}

/// <summary>
/// A model protocol implementation ("named inputs → named outputs"). It knows nothing about candidates, rows or
/// columns; operators bind columns to inputs and outputs to columns (design 6.9).
/// </summary>
public interface IModelClient
{
    /// <summary>Client type and version, e.g. <c>grpc_tensor@1</c>.</summary>
    string ClientType { get; }

    /// <summary>Signature of a model version; used at load time to check operator bindings.</summary>
    ModelSignature GetSignature(string model, string? version);

    ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken);
}

/// <summary>Creates <see cref="IModelClient"/> instances for bindings of one client type.</summary>
public interface IModelClientFactory
{
    string ClientType { get; }

    /// <param name="modelName">Logical model name from the bindings artifact.</param>
    /// <param name="settings">Client-specific settings from the binding.</param>
    IModelClient Create(string modelName, IReadOnlyDictionary<string, JsonElement> settings);
}
