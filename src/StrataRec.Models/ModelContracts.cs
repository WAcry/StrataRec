using System.Buffers;
using System.Runtime.InteropServices;
using StrataRec.Core.Bindings;

namespace StrataRec.Models;

/// <summary>
/// Element type of a model tensor: one per declared column type a model can take (design 4.3), so a value reaches the
/// model client at the width it was declared and fetched with, plus <see cref="Text"/>.
/// </summary>
public enum TensorElementType : byte
{
    Bool = 1,
    Int32,
    Int64,
    Float32,
    Float64,
    String,

    /// <summary>
    /// One text value of the whole call (a prompt, a generated answer), not a tensor: it has no shape and no elements, and
    /// a replay serves a recorded one only with its call, where a tensor's elements are matched row by row.
    /// </summary>
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

/// <summary>
/// The elements of a tensor, flat and row-major, of one <see cref="TensorElementType"/> other than text: <c>bool</c>,
/// <c>int</c>, <c>long</c>, <c>float</c>, <c>double</c> or <c>string</c> values.
/// </summary>
/// <remarks>
/// Read them with the accessor of the element type (<see cref="Float32"/>, <see cref="Int32"/>, …) or, in code generic
/// over the element type, <see cref="As{T}"/>; reading them as another type throws. They are the caller's buffers:
/// valid only during the call, and not to be changed.
/// </remarks>
public readonly struct TensorValues
{
    // The source of a ReadOnlyMemory<T> (an array or a MemoryManager<T>) and its window, so one struct holds any element
    // type without a field per type.
    private readonly object? _source;
    private readonly int _start;

    private TensorValues(TensorElementType type, object? source, int start, int length)
    {
        ElementType = type;
        _source = source;
        _start = start;
        Length = length;
    }

    /// <summary>0 for default values, which hold none.</summary>
    public TensorElementType ElementType { get; }

    /// <summary>The number of elements.</summary>
    public int Length { get; }

    public ReadOnlyMemory<bool> Bool => As<bool>();

    public ReadOnlyMemory<int> Int32 => As<int>();

    public ReadOnlyMemory<long> Int64 => As<long>();

    public ReadOnlyMemory<float> Float32 => As<float>();

    public ReadOnlyMemory<double> Float64 => As<double>();

    public ReadOnlyMemory<string> String => As<string>();

    /// <summary>The values of <typeparamref name="T"/>'s element type (see <see cref="TypeOf{T}"/>).</summary>
    /// <exception cref="InvalidOperationException">They are of another element type.</exception>
    public ReadOnlyMemory<T> As<T>()
    {
        var type = TypeOf<T>();
        if (ElementType != type)
        {
            throw new InvalidOperationException($"These are {Name(ElementType)} values, not {Name(type)}.");
        }

        return _source switch
        {
            T[] array => new ReadOnlyMemory<T>(array, _start, Length),
            MemoryManager<T> manager => manager.Memory.Slice(_start, Length),
            _ => ReadOnlyMemory<T>.Empty,
        };
    }

    /// <summary>The values of <paramref name="values"/>, whose element type <typeparamref name="T"/> gives.</summary>
    public static TensorValues Of<T>(ReadOnlyMemory<T> values)
    {
        var type = TypeOf<T>();
        if (MemoryMarshal.TryGetArray(values, out var segment))
        {
            return new TensorValues(type, segment.Array, segment.Offset, segment.Count);
        }

        if (MemoryMarshal.TryGetMemoryManager<T, MemoryManager<T>>(values, out var manager, out var start, out var length))
        {
            return new TensorValues(type, manager, start, length);
        }

        // Only a ReadOnlyMemory<char> over a string has neither, and char is no element type.
        throw new ArgumentException("Tensor values are held in an array or a MemoryManager<T>.", nameof(values));
    }

    /// <summary>
    /// The element type of values of <typeparamref name="T"/>: <c>bool</c>, <c>int</c> (int32), <c>long</c> (int64),
    /// <c>float</c> (float32), <c>double</c> (float64) or <c>string</c>.
    /// </summary>
    /// <exception cref="ArgumentException"><typeparamref name="T"/> is none of those.</exception>
    public static TensorElementType TypeOf<T>() =>
        typeof(T) == typeof(bool) ? TensorElementType.Bool
        : typeof(T) == typeof(int) ? TensorElementType.Int32
        : typeof(T) == typeof(long) ? TensorElementType.Int64
        : typeof(T) == typeof(float) ? TensorElementType.Float32
        : typeof(T) == typeof(double) ? TensorElementType.Float64
        : typeof(T) == typeof(string) ? TensorElementType.String
        : throw new ArgumentException($"{typeof(T).Name} is not a tensor element type: use bool, int, long, float, double or string.");

    /// <summary>An element type as signatures and messages spell it: <c>int32</c>, <c>float32</c>, <c>text</c>.</summary>
    public static string Name(TensorElementType type) => type switch
    {
        TensorElementType.Bool => "bool",
        TensorElementType.Int32 => "int32",
        TensorElementType.Int64 => "int64",
        TensorElementType.Float32 => "float32",
        TensorElementType.Float64 => "float64",
        TensorElementType.String => "string",
        TensorElementType.Text => "text",
        _ => "none",
    };

    /// <summary>The element type <see cref="Name"/> spells as <paramref name="name"/>.</summary>
    public static bool TryParse(string? name, out TensorElementType type)
    {
        type = name switch
        {
            "bool" => TensorElementType.Bool,
            "int32" => TensorElementType.Int32,
            "int64" => TensorElementType.Int64,
            "float32" => TensorElementType.Float32,
            "float64" => TensorElementType.Float64,
            "string" => TensorElementType.String,
            "text" => TensorElementType.Text,
            _ => 0,
        };
        return type != 0;
    }
}

/// <summary>One named model input: a tensor of a declared type's values, or a text value.</summary>
public readonly struct NamedInput
{
    private readonly string? _text;

    private NamedInput(string name, int[] shape, TensorValues values, string? text)
    {
        Name = name;
        Shape = shape;
        Values = values;
        _text = text;
    }

    public string Name { get; }

    public TensorElementType ElementType => _text is null ? Values.ElementType : TensorElementType.Text;

    /// <summary>The tensor's shape; empty for text.</summary>
    public int[] Shape { get; }

    /// <summary>The tensor's elements; default (none) for text.</summary>
    public TensorValues Values { get; }

    /// <summary>The text value.</summary>
    /// <exception cref="InvalidOperationException">The input is a tensor.</exception>
    public string Text => _text ?? throw new InvalidOperationException($"Input '{Name}' is a {TensorValues.Name(ElementType)} tensor, not text.");

    /// <inheritdoc cref="TensorValues.Bool"/>
    public ReadOnlyMemory<bool> Bool => Values.Bool;

    /// <inheritdoc cref="TensorValues.Int32"/>
    public ReadOnlyMemory<int> Int32 => Values.Int32;

    /// <inheritdoc cref="TensorValues.Int64"/>
    public ReadOnlyMemory<long> Int64 => Values.Int64;

    /// <inheritdoc cref="TensorValues.Float32"/>
    public ReadOnlyMemory<float> Float32 => Values.Float32;

    /// <inheritdoc cref="TensorValues.Float64"/>
    public ReadOnlyMemory<double> Float64 => Values.Float64;

    /// <inheritdoc cref="TensorValues.String"/>
    public ReadOnlyMemory<string> String => Values.String;

    /// <summary>A tensor of <typeparamref name="T"/>'s element type (see <see cref="TensorValues.TypeOf{T}"/>).</summary>
    public static NamedInput FromValues<T>(string name, int[] shape, ReadOnlyMemory<T> data) => new(name, shape, TensorValues.Of(data), null);

    public static NamedInput FromBool(string name, int[] shape, ReadOnlyMemory<bool> data) => FromValues(name, shape, data);

    public static NamedInput FromInt32(string name, int[] shape, ReadOnlyMemory<int> data) => FromValues(name, shape, data);

    public static NamedInput FromInt64(string name, int[] shape, ReadOnlyMemory<long> data) => FromValues(name, shape, data);

    public static NamedInput FromFloat32(string name, int[] shape, ReadOnlyMemory<float> data) => FromValues(name, shape, data);

    public static NamedInput FromFloat64(string name, int[] shape, ReadOnlyMemory<double> data) => FromValues(name, shape, data);

    public static NamedInput FromString(string name, int[] shape, ReadOnlyMemory<string> data) => FromValues(name, shape, data);

    public static NamedInput FromText(string name, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new(name, Array.Empty<int>(), default, text);
    }
}

/// <summary>Per-call options.</summary>
/// <param name="Seed">The request's seed (<c>ctx.request.seed</c>), for a client that samples (design 5.9 rule 5).</param>
/// <param name="MaxOutputTokens">
/// The most tokens a generative model may produce for this call (design 5.9 rule 2); a client that stops there says so with
/// <see cref="ModelStatus.Truncated"/>. Null for calls that generate nothing.
/// </param>
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
    /// <summary>
    /// A buffer for an output of <paramref name="elementCount"/> elements of <typeparamref name="T"/>'s element type:
    /// <c>bool</c>, <c>int</c> (int32), <c>long</c> (int64), <c>float</c> (float32), <c>double</c> (float64) or
    /// <c>string</c>, the type the model's signature declares for the output. Write every element.
    /// </summary>
    /// <exception cref="ArgumentException"><typeparamref name="T"/> is not an element type.</exception>
    Memory<T> GetValues<T>(string name, int elementCount);

    /// <summary>Sets a text output (<see cref="TensorElementType.Text"/>).</summary>
    void SetText(string name, string text);
}

/// <summary>Outcome of a model call.</summary>
/// <remarks>
/// A generative client reports what it knows about its output with the optional properties, e.g.
/// <c>ModelStatus.Ok with { OutputTokens = 37, Truncated = false }</c>. The engine counts them for every model call
/// (<c>stratarec.model.output_tokens</c>, <c>stratarec.model.truncated</c>) and records them with the call, so a replay
/// returns the status the request saw.
/// </remarks>
public readonly record struct ModelStatus(bool Succeeded, string? Error = null)
{
    public static ModelStatus Ok => new(true);

    /// <summary>Tokens the model generated for this call, when the client knows; null otherwise (and for scoring models).</summary>
    public int? OutputTokens { get; init; }

    /// <summary>The model stopped at <see cref="ModelCallOptions.MaxOutputTokens"/>: its text output may be cut short.</summary>
    public bool Truncated { get; init; }

    public static ModelStatus Failed(string error) => new(false, error);
}

/// <summary>
/// A model protocol implementation ("named inputs → named outputs"). It knows nothing about candidates, rows or
/// columns; operators bind columns to inputs and outputs to columns (design 6.9).
/// </summary>
public interface IModelClient
{
    /// <summary>Client type and version, e.g. <c>linear@1</c>.</summary>
    string ClientType { get; }

    /// <summary>
    /// The signature of a model version, for checking operator bindings at load time (design 6.9). It may need I/O (a
    /// model server's metadata): the engine asks only when it loads a plan, after the client's own load
    /// (<see cref="ILoadable"/>), for every version of every model the plan's nodes may call, concurrently, on the
    /// thread pool (synchronous work before the first await included) and within the load's bound; the token is
    /// cancelled when nobody waits for the answer any more, and the instance is not disposed before the method returned.
    /// A plan compiled without an engine (<c>stratarec validate</c>) asks a client that needs no load, on its own
    /// thread, and uses the answer only if it is immediate.
    /// </summary>
    /// <exception cref="ArgumentException">The client has no such model or version; reported at the node's <c>model</c>.
    /// A <see cref="BindingException"/> adds a hint.</exception>
    ValueTask<ModelSignature> GetSignatureAsync(string model, string? version, CancellationToken cancellationToken);

    ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken);
}
