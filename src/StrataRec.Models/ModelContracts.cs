using StrataRec.Core.Bindings;

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
    public TensorSpec? FindInput(string name) => Find(Inputs, name);

    public TensorSpec? FindOutput(string name) => Find(Outputs, name);

    private static TensorSpec? Find(IReadOnlyList<TensorSpec> specs, string name)
    {
        for (var i = 0; i < specs.Count; i++)
        {
            if (string.Equals(specs[i].Name, name, StringComparison.Ordinal))
            {
                return specs[i];
            }
        }

        return null;
    }
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

    /// <summary>
    /// The signature of a model version, for checking operator bindings at load time (design 6.9). It may need I/O (a
    /// model server's metadata): the engine asks only when it loads a plan, after the client's own load
    /// (<see cref="ILoadable"/>), for every version of every model the plan's nodes may call, concurrently and within the
    /// load's bound; a plan compiled without an engine (<c>stratarec validate</c>) asks a client that needs no load and
    /// uses the answer only if it is immediate. Answer synchronously when you can: the plan then compiles once.
    /// </summary>
    /// <exception cref="ArgumentException">The client has no such model or version; reported at the node's <c>model</c>.
    /// A <see cref="BindingException"/> adds a hint.</exception>
    ValueTask<ModelSignature> GetSignatureAsync(string model, string? version, CancellationToken cancellationToken);

    ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken);
}

/// <summary>Creates <see cref="IModelClient"/> instances for bindings of one client type.</summary>
/// <remarks>
/// The engine creates one client per binding identity and shares it across plans and their concurrent requests, so a
/// client lives as long as some plan uses it, not one plan. <see cref="Create"/> only reads the settings; a client that
/// must load a model or connect first implements <see cref="ILoadable"/>, and one holding resources implements
/// <see cref="IAsyncDisposable"/> or <see cref="IDisposable"/>: the engine disposes it once nothing can call it any more.
/// The whole lifetime contract is on <see cref="BindingContext"/>.
/// </remarks>
public interface IModelClientFactory
{
    string ClientType { get; }

    /// <param name="context">The logical model name, the binding's own settings, the base directory relative files
    /// resolve against and the host's secret store.</param>
    /// <exception cref="BindingException">The settings are wrong: at the setting's pointer, with a hint.</exception>
    IModelClient Create(BindingContext context);
}
