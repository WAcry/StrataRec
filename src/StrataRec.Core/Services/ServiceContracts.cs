using System.Buffers;
using System.Runtime.InteropServices;

namespace StrataRec.Core.Services;

/// <summary>
/// Element type of a service tensor: one per declared column type a service can take (design 4.3), so a value reaches
/// the service client at the width it was declared and fetched with, plus <see cref="Text"/>.
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

/// <summary>Name, element type and shape of a service input or output. A dimension of -1 is dynamic (batch).</summary>
public sealed record TensorSpec(string Name, TensorElementType ElementType, IReadOnlyList<int> Shape);

/// <summary>Inputs and outputs a version of a service's operation accepts and produces; checked at load time (design 6.9).</summary>
public sealed record ServiceSignature(IReadOnlyList<TensorSpec> Inputs, IReadOnlyList<TensorSpec> Outputs)
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

/// <summary>
/// One named input of a service call: a tensor of a declared type's values, a text value, or a value that is missing
/// (<see cref="IsMissing"/>: a user without an embedding), which keeps its declared element type and shape.
/// </summary>
public readonly struct NamedInput
{
    private readonly string? _text;
    private readonly TensorElementType _missing;
    private readonly int[] _shape;

    private NamedInput(string name, int[] shape, TensorValues values, string? text, TensorElementType missing = 0)
    {
        Name = name;
        _shape = shape;
        Values = values;
        _text = text;
        _missing = missing;
    }

    public string Name { get; }

    /// <summary>The declared element type, of a missing value too.</summary>
    public TensorElementType ElementType => _missing != 0 ? _missing : _text is null ? Values.ElementType : TensorElementType.Text;

    /// <summary>
    /// The value is missing (design 4.3: missing is explicit, never a zero): the service decides what it does without
    /// it. Its <see cref="ElementType"/> and <see cref="Shape"/> are the declared ones, so a service checks them whether or
    /// not a request has the value; it has no elements.
    /// </summary>
    public bool IsMissing => _missing != 0;

    /// <summary>
    /// The tensor's shape; empty for text. A missing value's is the declared one: <c>[]</c> for a scalar, <c>[N]</c> for a
    /// <c>list&lt;E,N&gt;</c>, <c>[-1]</c> for a list of any length. Read-only to the service: the input is the caller's,
    /// as its <see cref="Values"/> are, and other calls may share it.
    /// </summary>
    /// <remarks>Neither the shape array nor the data a factory is given is copied: the caller does not change them from the
    /// moment it creates the input for as long as the input is used (the factory's checks hold for what it was given, and a
    /// cache or a recording reads the input after the call too).</remarks>
    public ReadOnlySpan<int> Shape => _shape;

    /// <summary>
    /// The tensor's elements, exactly the product of the <see cref="Shape"/>'s dimensions; default (none) for text and for a
    /// missing value. Built from a longer buffer (one rented from a pool), it is that buffer's first elements: the slack is
    /// no part of the value, for a client or a digest.
    /// </summary>
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
    /// <exception cref="ArgumentException">A dimension is negative, or <paramref name="data"/> holds fewer elements than the shape.</exception>
    public static NamedInput FromValues<T>(string name, int[] shape, ReadOnlyMemory<T> data)
    {
        ArgumentNullException.ThrowIfNull(shape);
        long elements = 1;
        var empty = false;
        foreach (var dimension in shape)
        {
            if (dimension < 0)
            {
                throw new ArgumentException($"Input '{name}' has shape [{string.Join(", ", shape)}]: a value's dimensions are 0 or more (-1 is only a missing value's declared length).", nameof(shape));
            }

            empty |= dimension == 0;
            if (elements <= data.Length)
            {
                elements *= dimension; // at most int.MaxValue squared: multiplying stops once past the data, so it cannot overflow
            }
        }

        elements = empty ? 0 : elements;
        if (data.Length < elements)
        {
            throw new ArgumentException($"Input '{name}' has shape [{string.Join(", ", shape)}] and {data.Length} value(s), fewer than the shape holds.", nameof(data));
        }

        // The value is the shape's elements: a longer (pooled) buffer's slack is no part of it, for a client or a digest.
        return new(name, shape, TensorValues.Of(data[..(int)elements]), null);
    }

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

    /// <summary>A missing value of <paramref name="elementType"/> and the declared <paramref name="shape"/> (see <see cref="IsMissing"/>).</summary>
    public static NamedInput FromMissing(string name, TensorElementType elementType, int[] shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (elementType is 0 or TensorElementType.Text)
        {
            throw new ArgumentException("A missing value has the element type of a tensor: bool, int32, int64, float32, float64 or string.", nameof(elementType));
        }

        return new(name, shape, default, null, elementType);
    }
}

/// <summary>Per-call options.</summary>
/// <param name="Seed">The request's seed (<c>ctx.request.seed</c>), for a service that samples (design 5.9 rule 5).</param>
/// <param name="MaxOutputTokens">
/// The most tokens a generative service may produce for this call (design 5.9 rule 2); a client that stops there says so
/// with <see cref="ServiceStatus.Truncated"/>. Null for calls that generate nothing.
/// </param>
public readonly record struct ServiceCallOptions(long? Seed = null, int? MaxOutputTokens = null);

/// <summary>
/// One call of a service (design 6.9): named inputs in; named outputs, text and items out. What a call carries depends on
/// its use. A model scoring rows sends a tensor per input whose leading dimension is the rows, the rows'
/// <see cref="Items"/>, and reads an output per row; a recall sends its query's values and reads the items the service
/// adds, up to <see cref="Limit"/>; a generative call sends text and reads text; a filter sends the candidates as
/// <see cref="Items"/> and reads a flag per item.
/// </summary>
/// <remarks>
/// <para>A call with <see cref="Items"/> is about those items: every tensor output is per item, <c>BatchSize ×
/// k</c> elements, the <c>k</c> of item <c>i</c> at <c>i × k</c>, and it adds no items (its <see cref="Limit"/> is 0).
/// That is what lets a replay answer an item from any recorded call that had it with the same inputs, however the items
/// were batched (design 13.4). A call without items is answered as a whole.</para>
/// <para>A service sees only what the call sends: its inputs, not the request's. A value of the request it needs is an
/// input the operator sends (a recall node's <c>queries</c>, e.g. <c>ctx.request.region</c>), so the digest a replay
/// matches by covers everything the service saw.</para>
/// <para>The instance and everything it holds are the caller's, valid only during the call.</para>
/// </remarks>
public sealed class ServiceCall
{
    /// <summary>The service's logical name (its binding's), for messages.</summary>
    public required string Service { get; init; }

    /// <summary>The version the call asks for; null for the service's default.</summary>
    public string? Version { get; init; }

    public ReadOnlyMemory<NamedInput> Inputs { get; init; }

    /// <summary>Number of rows in this batch (the leading dimension of row-wise tensors); 0 for a call about no rows.</summary>
    public int BatchSize { get; init; }

    /// <summary>
    /// The entity of each row of the batch, in the order of the rows of row-wise tensors, for a call about rows (a model
    /// scoring them, a service filtering them); empty for a call about no rows.
    /// </summary>
    /// <remarks>
    /// One per row (<see cref="BatchSize"/> of them). In such a call an input whose leading dimension is the batch size is
    /// per item, row <c>i</c> its slice <c>i</c>; every other input is shared by the items, and its leading dimension is 1
    /// (<c>[1, d]</c>, as the model operators send a broadcast value), or it is a scalar or a text. The engine splits
    /// inputs by row on this rule: a replay's digest of each item, a cache's call of the items it misses.
    /// </remarks>
    public ReadOnlyMemory<Keys.EntityKey> Items { get; init; }

    /// <summary>
    /// The named outputs the caller reads; empty when it reads none (a recall reads only the items it adds). A service may
    /// compute only these. Only these are recorded and replayed.
    /// </summary>
    public ReadOnlyMemory<string> Outputs { get; init; }

    /// <summary>
    /// The most items the call may add (<see cref="IServiceResponse.AddItem(long, double)"/>); 0 for a call that adds none,
    /// as every call with <see cref="Items"/> does.
    /// </summary>
    /// <remarks>
    /// A service's items for a smaller limit are the start of its items for a larger one, as a ranked list's are: a cache
    /// and a replay answer a call that reads no named <see cref="Outputs"/> with the start of a longer list recorded for the
    /// same inputs, or with a list the service could not fill. A call that reads named outputs is answered only by an
    /// answer to its own limit: an output or a text may say how many items it was asked for. The items a service adds past
    /// a limit (<c>ServiceResponse.Dropped</c>) count only for that limit: a longer list's start answers as a service
    /// keeping to the smaller limit would, with none dropped.
    /// </remarks>
    public int Limit { get; init; }

    /// <summary>The request's id, for a service that logs or traces calls by it; it does not change the answer.</summary>
    public string RequestId { get; init; } = string.Empty;

    public ServiceCallOptions Options { get; init; }

    /// <summary>The input named <paramref name="name"/>, if the call has one; a missing value is sent, marked <see cref="NamedInput.IsMissing"/>.</summary>
    public bool TryGetInput(string name, out NamedInput input)
    {
        foreach (var candidate in Inputs.Span)
        {
            if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
            {
                input = candidate;
                return true;
            }
        }

        input = default;
        return false;
    }
}

/// <summary>
/// Receives a call's answer. Its buffers are the caller's: the client writes into them during the call and keeps none of
/// them afterwards; an answer written after the call completed is ignored.
/// </summary>
public interface IServiceResponse
{
    /// <summary>
    /// A buffer for an output of <paramref name="elementCount"/> elements of <typeparamref name="T"/>'s element type:
    /// <c>bool</c>, <c>int</c> (int32), <c>long</c> (int64), <c>float</c> (float32), <c>double</c> (float64) or
    /// <c>string</c>, the type the service's signature declares for the output. Write every element.
    /// </summary>
    /// <exception cref="ArgumentException"><typeparamref name="T"/> is not an element type.</exception>
    Memory<T> GetValues<T>(string name, int elementCount);

    /// <summary>Sets a text output (<see cref="TensorElementType.Text"/>).</summary>
    void SetText(string name, string text);

    /// <summary>
    /// Adds an item, in rank order, with its score: what a recall returns. Items beyond the call's
    /// <see cref="ServiceCall.Limit"/> are dropped and counted; a non-finite score is stored as 0.
    /// </summary>
    void AddItem(long entityId, double score);

    /// <inheritdoc cref="AddItem(long, double)"/>
    void AddItem(string entityId, double score);
}

/// <summary>Outcome of a service call.</summary>
/// <remarks>
/// A generative client reports what it knows about its output with the optional properties, e.g.
/// <c>ServiceStatus.Ok with { OutputTokens = 37, Truncated = false }</c>. The engine counts them for every call
/// (<c>stratarec.io.output_tokens</c>, <c>stratarec.io.truncated</c>) and records them with the call, so a replay returns
/// the status the request saw.
/// </remarks>
public readonly record struct ServiceStatus(bool Succeeded, string? Error = null)
{
    public static ServiceStatus Ok => new(true);

    /// <summary>Tokens the service generated for this call, when the client knows; null otherwise.</summary>
    public int? OutputTokens { get; init; }

    /// <summary>The service stopped at <see cref="ServiceCallOptions.MaxOutputTokens"/>: its text output may be cut short.</summary>
    public bool Truncated { get; init; }

    public static ServiceStatus Failed(string error) => new(false, error);
}

/// <summary>
/// An external service the pipeline calls (design 6.9): a model server, a retrieval index, a filter, any business logic
/// behind a protocol. It knows nothing about candidates, rows or columns: operators bind columns, queries and parameters
/// to inputs, and outputs and items to columns and rows. Thread-safe: every request of every plan that binds it calls the
/// one instance concurrently (the lifetime contract is on <see cref="Bindings.BindingContext"/>).
/// </summary>
public interface IServiceClient
{
    /// <summary>
    /// The inputs and outputs of a version of the service, for checking the operators that call it at load time (design
    /// 6.9). It may need I/O (a model server's metadata): the engine asks only when it loads a plan, after the client's
    /// own load (<see cref="Bindings.ILoadable"/>), for every version the plan's nodes may call, concurrently, on the
    /// thread pool (synchronous work before the first await included) and within the load's bound; the token is cancelled
    /// when nobody waits for the answer any more, and the instance is not disposed before the method returned. A plan
    /// compiled without an engine (<c>stratarec validate</c>) asks a client that needs no load, on its own thread, and
    /// uses the answer only if it is immediate.
    /// </summary>
    /// <remarks>
    /// Only operators that check a signature ask: the model operators. A service no model operator calls (a recall, a
    /// filter) need not answer; by default it throws, and a model operator bound to it reports that at its node.
    /// </remarks>
    /// <exception cref="ArgumentException">The service has no such version; reported at the node. A
    /// <see cref="Bindings.BindingException"/> adds a hint.</exception>
    ValueTask<ServiceSignature> DescribeAsync(string service, string? version, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            $"Service '{service}' does not describe its inputs and outputs ({GetType().Name} implements no DescribeAsync), so a model operator cannot check its calls.");

    /// <summary>
    /// Answers one call into <paramref name="response"/>. A failure is a failed status (or an exception, which the engine
    /// turns into one); the engine stops waiting at the call's timeout and the node's budget, and cancels the token.
    /// </summary>
    ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse response, CancellationToken cancellationToken);
}
