using StrataRec.Core.Bindings;
using StrataRec.Features.Bindings;
using StrataRec.Features.Local;
using StrataRec.Models;

namespace StrataRec.Pipeline.Planning;

/// <summary>How far a model signature a compile asked for is known (<see cref="SignatureLookup"/>).</summary>
public enum SignatureStatus : byte
{
    /// <summary>The client answered; check the node against <see cref="SignatureLookup.Signature"/>.</summary>
    Known = 1,

    /// <summary>The client has no such model or version, or could not say: an error at the node's model.</summary>
    Failed,

    /// <summary>
    /// The engine is still loading the client or asking it: skip the check. The engine compiles the plan again once the
    /// answers are in, and only that compile counts.
    /// </summary>
    Pending,

    /// <summary>
    /// Not checked by this compile, and that is no error at the node: the plan is compiled without loading its clients
    /// (<c>validate</c>, a replay), or the client failed to load, which the load reports at its binding.
    /// </summary>
    Unchecked,
}

/// <summary>A model signature as a compile sees it (design 6.9).</summary>
public readonly record struct SignatureLookup(SignatureStatus Status, ModelSignature? Signature = null, string? Error = null, string? Hint = null)
{
    public static SignatureLookup Unchecked => new(SignatureStatus.Unchecked);

    public static SignatureLookup Pending => new(SignatureStatus.Pending);

    /// <summary>The lookup of a completed answer: its signature, or the error it failed with.</summary>
    public static SignatureLookup Of(Task<ModelSignature> answered) =>
        answered.IsCompletedSuccessfully ? new(SignatureStatus.Known, answered.Result) : Failure(answered.Exception?.InnerException ?? new TaskCanceledException(answered));

    /// <summary>The one mapping of what a client's <see cref="IModelClient.GetSignatureAsync"/> threw to a node error.</summary>
    public static SignatureLookup Failure(Exception exception) => exception switch
    {
        BindingException binding => new(SignatureStatus.Failed, null, binding.Reason, binding.Hint),
        OperationCanceledException => new(SignatureStatus.Failed, null, "The signature lookup was cancelled.", null),
        _ => new(SignatureStatus.Failed, null, exception.Message, null),
    };
}

/// <summary>
/// What a plan's compile acquires from its engine (design 6.4, 6.6): its local tables, the instances its bindings name,
/// and the model signatures its nodes are checked against, which may need those instances loaded. The engine's
/// implementation is the plan's binding lease (Hosting): it shares tables and instances with the other plans by identity,
/// loads them, and keeps them until no request of the plan can still use them. <see cref="UnloadedBindings"/> is the one
/// for compiling without serving.
/// </summary>
public interface IPlanBindings : ILocalTableProvider, IBindingInstances
{
    /// <summary>
    /// The signature of one version of a model, for the compile to check a node against. Only an engine's provider
    /// answers <see cref="SignatureStatus.Pending"/>: it then compiles the plan again once the answers are in.
    /// </summary>
    /// <param name="client">An instance this provider handed out.</param>
    SignatureLookup Signature(IModelClient client, string model, string? version);
}

/// <summary>
/// The bindings of a plan compiled without serving it (<c>validate</c>, <c>build</c>, <c>explain-plan</c>, tests): local
/// tables that are never loaded, and instances of its own, created once per identity and never loaded, so a plan
/// compiles without the files, connections or secrets its instances need at load. A model signature is checked only when
/// the client needs no load and answers at once; the others are checked when an engine loads the plan.
/// </summary>
/// <remarks>Not thread-safe: one per compile (shared by its validation passes). Instances are not disposed; none of them
/// was loaded.</remarks>
public sealed class UnloadedBindings : IPlanBindings
{
    private readonly Dictionary<string, LocalFeatureTable> _tables = new(StringComparer.Ordinal);
    private readonly Dictionary<(object Factory, string Identity), object> _instances = new();

    public LocalFeatureTable Acquire(LocalTableSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!_tables.TryGetValue(spec.Identity, out var table))
        {
            table = UnloadedLocalTables.Instance.Acquire(spec);
            _tables[spec.Identity] = table;
        }

        return table;
    }

    public T Acquire<T>(BindingSpec spec, Func<BindingContext, T> create)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!_instances.TryGetValue((spec.Factory, spec.Identity), out var instance))
        {
            instance = create(spec.Context(null)); // never loaded, so it never resolves a secret
            _instances[(spec.Factory, spec.Identity)] = instance;
        }

        return (T)instance;
    }

    public SignatureLookup Signature(IModelClient client, string model, string? version)
    {
        if (client is ILoadable)
        {
            return SignatureLookup.Unchecked; // it could only answer after its load, which needs what validate does not have
        }

        ValueTask<ModelSignature> answer;
        try
        {
            answer = client.GetSignatureAsync(model, version, CancellationToken.None);
        }
        catch (Exception ex)
        {
            return SignatureLookup.Failure(ex);
        }

        if (answer.IsCompleted)
        {
            return SignatureLookup.Of(answer.AsTask());
        }

        // Nothing here may wait for I/O: the engine checks it at load. The answer is observed, so a late failure is not an
        // unobserved task exception.
        _ = answer.AsTask().ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return SignatureLookup.Unchecked;
    }
}
