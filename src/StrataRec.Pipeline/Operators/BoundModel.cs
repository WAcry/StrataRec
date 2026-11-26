using StrataRec.Core.Diagnostics;
using StrataRec.Core.Types;
using StrataRec.Pipeline.Planning;
using StrataRec.Core.Services;

namespace StrataRec.Pipeline.Operators;

/// <summary>
/// The model a node calls, bound at compile time from the config keys every model operator shares:
/// <c>model</c>, the logical name of the service bound per environment, and <c>version</c> or <c>version_param</c>, a
/// string parameter whose <c>enum</c> lists the versions a request may choose. Immutable and shared by every request of
/// the node.
/// </summary>
/// <remarks>
/// <para>Every version a request may use is in <see cref="Versions"/>, so the operator checks each one's signature at
/// load (<see cref="Signature"/>): a version that does not fit would load cleanly and then fail on every request that
/// chose it. Calls go through <see cref="OperatorContext.CallAsync"/> with the request's seed
/// (<c>ctx.request.seed</c>), so they are bounded, measured, recorded and replayed like every other
/// model call.</para>
/// <para>Public so that operators outside this assembly (the generative ones in StrataRec.Llm) bind and call models with
/// the same keys, error codes and messages as the built-in model operators, instead of a copy that would drift.</para>
/// </remarks>
public sealed class BoundModel
{
    private readonly string? _version;
    private readonly ParamRef? _versionParam;
    private readonly string?[] _versions;

    private BoundModel(BoundService service, string? version, ParamRef? versionParam, string?[] versions)
    {
        Service = service;
        _version = version;
        _versionParam = versionParam;
        _versions = versions;
    }

    /// <summary>The service the model is, called in the <c>model</c> role.</summary>
    public BoundService Service { get; }

    /// <summary>The logical model name: the service's.</summary>
    public string Model => Service.Name;

    /// <summary>The model's telemetry target: its calls' duration, errors and rows are tagged with the model's name.</summary>
    public IoTarget Target => Service.Target;

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
        var bound = context.Service(model, "/model", IoRoles.Model);
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
                    "every version is signature-checked at load time");
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

        return new BoundModel(bound, version, parameter, versions);
    }

    /// <summary>A version as messages name it: <c>Model 'ctr_model' version 'v2'</c> (or <c>'default'</c>).</summary>
    public string Describe(string? version) => $"Model '{Model}' version '{version ?? "default"}'";

    /// <summary>
    /// The signature of <paramref name="version"/> (one of <see cref="Versions"/>), for checking the node's config against
    /// it when it is known; a failed lookup is reported at <c>/model</c> (see
    /// <see cref="OperatorCompileContext.Signature"/>).
    /// </summary>
    public SignatureLookup Signature(OperatorCompileContext context, string? version)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Signature(Service, version, "/model");
    }

    /// <summary>The version this request calls.</summary>
    public string? Version(OperatorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _versionParam is { } p ? context.Param(p).AsString() : _version;
    }

    /// <summary>
    /// Sends one call through <see cref="OperatorContext.CallAsync"/> with the request's seed and, for a generative call,
    /// <paramref name="maxOutputTokens"/>. <paramref name="rows"/> are the physical rows of the batch for a call whose
    /// outputs are per row (its items), or empty for a call recorded and replayed by the digest of its whole input;
    /// <paramref name="outputs"/> name the outputs the operator reads.
    /// </summary>
    public Task<ServiceStatus> InvokeAsync(
        OperatorContext context, string? version, NamedInput[] inputs, int batchSize, ServiceResponse response, ReadOnlySpan<int> rows,
        ReadOnlyMemory<string> outputs, int? maxOutputTokens = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var call = new ServiceCall
        {
            Service = Model,
            Version = version,
            Inputs = inputs,
            BatchSize = batchSize,
            Items = context.KeysOf(rows),
            Outputs = outputs,
            RequestId = context.RequestId,
            Options = new ServiceCallOptions(Seed: context.Seed, MaxOutputTokens: maxOutputTokens),
        };
        return context.CallAsync(Service, call, response);
    }
}
