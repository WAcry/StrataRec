using StrataRec.Core.Diagnostics;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Models;

namespace StrataRec.Pipeline.Operators;

/// <summary>
/// The model a node calls, bound at compile time from the config keys every model operator shares (design 6.9):
/// <c>model</c>, the logical name bound per environment, and <c>version</c> or <c>version_param</c>, a string parameter
/// whose <c>enum</c> lists the versions a request may choose. Immutable and shared by every request of the node.
/// </summary>
/// <remarks>
/// <para>Every version a request may use is in <see cref="Versions"/>, so the operator checks each one's signature at
/// load (<see cref="Signature"/>): a version that does not fit would load cleanly and then fail on every request that
/// chose it. Calls go through <see cref="OperatorContext.InvokeModelAsync"/> with the request's seed
/// (<c>ctx.request.seed</c>, design 5.9 rule 5), so they are bounded, measured, recorded and replayed like every other
/// model call.</para>
/// <para>Public so that operators outside this assembly (the generative ones in StrataRec.Llm) bind and call models with
/// the same keys, error codes and messages as the built-in model operators, instead of a copy that would drift.</para>
/// </remarks>
public sealed class BoundModel
{
    private readonly string? _version;
    private readonly ParamRef? _versionParam;
    private readonly IModelClient _client;
    private readonly int? _timeoutMs;
    private readonly string?[] _versions;

    private BoundModel(string model, string? version, ParamRef? versionParam, IModelClient client, int? timeoutMs, string?[] versions)
    {
        Model = model;
        _version = version;
        _versionParam = versionParam;
        _client = client;
        _timeoutMs = timeoutMs;
        _versions = versions;
        Target = new IoTarget(IoKind.Model, model);
    }

    /// <summary>The logical model name.</summary>
    public string Model { get; }

    /// <summary>The model's telemetry target: its duration, errors and fallbacks are tagged with the model's name.</summary>
    public IoTarget Target { get; }

    /// <summary>
    /// Every version a request may call, each checked at load: the fixed <c>version</c> (null for the client's default),
    /// or every value of the <c>enum</c> of the parameter <c>version_param</c> names.
    /// </summary>
    public IReadOnlyList<string?> Versions => _versions;

    /// <summary>
    /// Resolves the node's <c>model</c> binding and its versions, reporting errors at <c>/model</c>, <c>/version</c> and
    /// <c>/version_param</c>. Null after reporting an error.
    /// </summary>
    public static BoundModel? Resolve(OperatorCompileContext context, string model, string? version, string? versionParam)
    {
        ArgumentNullException.ThrowIfNull(context);
        var bound = context.Model(model, "/model");
        if (bound is null)
        {
            return null;
        }

        ParamRef? parameter = null;
        string?[] versions;
        if (versionParam is not null)
        {
            if (version is not null)
            {
                context.Error("/version", "MODEL_VERSION", "Set at most one of 'version' and 'version_param'.",
                    "keep 'version' to call one version, or 'version_param' to let a parameter choose it per request");
                return null;
            }

            if (!context.TryParam(versionParam, "/version_param", out var p, StorageKind.String))
            {
                return null;
            }

            parameter = p;
            if (!context.Schema.TryGetParam(p.Name, out var leaf) || leaf.Enum is null)
            {
                context.Error("/version_param", "MODEL_VERSION_ENUM", $"Parameter '{p.Name}' must declare an enum of allowed model versions.",
                    "every version is signature-checked at load time (design 6.9)");
                return null;
            }

            versions = new string?[leaf.Enum.Count];
            for (var i = 0; i < versions.Length; i++)
            {
                versions[i] = leaf.Enum[i].AsString();
            }
        }
        else
        {
            versions = [version];
        }

        return new BoundModel(model, version, parameter, bound.Value.Client, bound.Value.TimeoutMs, versions);
    }

    /// <summary>A version as messages name it: <c>Model 'ctr_model' version 'v2'</c> (or <c>'default'</c>).</summary>
    public string Describe(string? version) => $"Model '{Model}' version '{version ?? "default"}'";

    /// <summary>
    /// The signature of <paramref name="version"/> (one of <see cref="Versions"/>), for checking the node's config against
    /// it; null when it cannot be checked now (see <see cref="OperatorCompileContext.ModelSignature"/>: an error reported at
    /// <c>/model</c>, or the engine checks it when it loads the client).
    /// </summary>
    public ModelSignature? Signature(OperatorCompileContext context, string? version)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.ModelSignature(_client, Model, version, "/model");
    }

    /// <summary>The version this request calls.</summary>
    public string? Version(OperatorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _versionParam is { } p ? context.Param(p).AsString() : _version;
    }

    /// <summary>
    /// Sends one call through <see cref="OperatorContext.InvokeModelAsync"/> with the request's seed and, for a generative
    /// call, <paramref name="maxOutputTokens"/>. <paramref name="rows"/> are the physical rows of the batch for a call whose
    /// outputs are per row, or empty for a call recorded and replayed by the digest of its whole input (see that method).
    /// </summary>
    public Task<ModelStatus> InvokeAsync(
        OperatorContext context, string? version, NamedInput[] inputs, int batchSize, IModelOutputWriter outputs, ReadOnlyMemory<int> rows,
        int? maxOutputTokens = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var call = new ModelCall
        {
            Model = Model,
            Version = version,
            Inputs = inputs,
            BatchSize = batchSize,
            Options = new ModelCallOptions(Seed: context.Seed, MaxOutputTokens: maxOutputTokens),
        };
        return context.InvokeModelAsync(_client, Target, call, outputs, rows, _timeoutMs);
    }
}

/// <summary>
/// Receives one call's outputs: float32 outputs into buffers rented from the request, text outputs as the strings the
/// client sets. A call's writer is read only after the call succeeded: an abandoned client may still be writing it.
/// </summary>
/// <remarks>Public so that model operators outside this assembly read outputs the way the built-in ones do.</remarks>
public sealed class ModelOutputWriter(RequestMemory memory) : IModelOutputWriter
{
    private readonly Dictionary<string, Memory<float>> _outputs = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private Dictionary<string, string>? _texts;

    public Memory<float> GetFloat32(string name, int elementCount)
    {
        var buffer = memory.Rent<float>(Math.Max(1, elementCount)).AsMemory(0, elementCount);
        lock (_gate)
        {
            _outputs[name] = buffer;
        }

        return buffer;
    }

    /// <summary>Keeps the text; a name set twice keeps the last.</summary>
    public void SetText(string name, string text)
    {
        lock (_gate)
        {
            (_texts ??= new Dictionary<string, string>(StringComparer.Ordinal))[name] = text;
        }
    }

    public bool TryGet(string name, out Memory<float> values)
    {
        lock (_gate)
        {
            return _outputs.TryGetValue(name, out values);
        }
    }

    public bool TryGetText(string name, out string text)
    {
        lock (_gate)
        {
            if (_texts is not null && _texts.TryGetValue(name, out var value))
            {
                text = value;
                return true;
            }
        }

        text = string.Empty;
        return false;
    }
}
