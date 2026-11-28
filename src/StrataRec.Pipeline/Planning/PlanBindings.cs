using System.Diagnostics.CodeAnalysis;
using StrataRec.Core.Bindings;
using StrataRec.Core.Diagnostics;
using StrataRec.Features.Bindings;
using StrataRec.Features.Local;
using StrataRec.Core.Services;

namespace StrataRec.Pipeline.Planning;

/// <summary>How far a model signature a compile asked for is known (<see cref="SignatureLookup"/>).</summary>
public enum SignatureStatus : byte
{
    /// <summary>The client answered; check the node against <see cref="SignatureLookup.Signature"/>.</summary>
    Known = 1,

    /// <summary>The client has no such model or version, or could not say: an error at the node's model.</summary>
    Failed,

    /// <summary>
    /// The engine is still loading the client or asking it: skip the check.
    /// </summary>
    Pending,

    /// <summary>
    /// Not checked by this compile, and that is no error at the node: the plan is compiled without loading its clients
    /// (<c>validate</c>, a replay), or the client failed to load, which the load reports at its binding.
    /// </summary>
    Unchecked,
}

public readonly record struct SignatureLookup(SignatureStatus Status, ServiceSignature? Signature = null, string? Error = null, string? Hint = null)
{
    public static SignatureLookup Unchecked => new(SignatureStatus.Unchecked);

    /// <summary>True when the client answered: check the node against <see cref="Signature"/>.</summary>
    [MemberNotNullWhen(true, nameof(Signature))]
    public bool IsKnown => Status == SignatureStatus.Known && Signature is not null;

    public static SignatureLookup Pending => new(SignatureStatus.Pending);

    /// <summary>The lookup of a completed answer: its signature, or the error it failed with.</summary>
    public static SignatureLookup Of(Task<ServiceSignature> answered) =>
        answered.IsCompletedSuccessfully ? new(SignatureStatus.Known, answered.Result) : Failure(answered.Exception?.InnerException ?? new TaskCanceledException(answered));

    /// <summary>The one mapping of what a client's <see cref="IServiceClient.DescribeAsync"/> threw to a node error.</summary>
    public static SignatureLookup Failure(Exception exception) => exception switch
    {
        BindingException binding => new(SignatureStatus.Failed, null, binding.Reason, binding.Hint),
        OperationCanceledException => new(SignatureStatus.Failed, null, "The signature lookup was cancelled.", null),
        _ => new(SignatureStatus.Failed, null, ExceptionText.Of(exception), null),
    };
}

/// <summary>
/// What a plan's compile acquires from its engine: its local tables, the instances its bindings name, and the model
/// signatures its nodes are checked against, which may need those instances loaded. The engine's implementation is the
/// plan's binding lease: it shares tables and instances with the other plans by identity, loads them, and keeps them
/// until no request of the plan can still use them. <see cref="UnloadedBindings"/> is the one for compiling without
/// serving.
/// </summary>
public interface IPlanBindings : ILocalTableProvider, IBindingInstances
{
    /// <summary>
    /// The signature of one version of a model, for the compile to check a node against. Only an engine's provider
    /// answers <see cref="SignatureStatus.Pending"/>.
    /// </summary>
    /// <param name="client">An instance this provider handed out.</param>
    SignatureLookup Signature(IServiceClient client, string model, string? version);
}

/// <summary>
/// The bindings of a plan compiled without serving it (<c>validate</c>, <c>build</c>, <c>explain-plan</c>, tests): local
/// tables that are never loaded, and instances of its own, created once per identity and never loaded, so a plan
/// compiles without the files, connections or secrets its instances need at load. A model signature is checked only when
/// the client needs no load and answers at once; the others are checked when an engine loads the plan.
/// </summary>
/// <remarks>Not thread-safe: one per compile (shared by its validation passes). Instances are not disposed.</remarks>
public sealed class UnloadedBindings : IPlanBindings
{
    private readonly Dictionary<TableKey, LocalFeatureTable> _tables = new();
    private readonly Dictionary<BindingKey, object> _instances = new();

    public LocalFeatureTable Acquire(LocalTableSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!_tables.TryGetValue(spec.Key, out var table))
        {
            table = UnloadedLocalTables.Instance.Acquire(spec);
            _tables[spec.Key] = table;
        }

        return table;
    }

    public T Acquire<T>(BindingSpec spec, Func<BindingContext, T> create)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!_instances.TryGetValue(spec.Key, out var instance))
        {
            instance = create(spec.Context(null));
            _instances[spec.Key] = instance;
        }

        return (T)instance;
    }

    public SignatureLookup Signature(IServiceClient client, string model, string? version)
    {
        if (client is ILoadable)
        {
            return SignatureLookup.Unchecked;
        }

        ValueTask<ServiceSignature> answer;
        try
        {
            answer = client.DescribeAsync(model, version, CancellationToken.None);
        }
        catch (Exception ex)
        {
            return SignatureLookup.Failure(ex);
        }

        if (answer.IsCompleted)
        {
            return SignatureLookup.Of(answer.AsTask());
        }

        // The answer is observed, so a late failure is not an unobserved task exception.
        _ = answer.AsTask().ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return SignatureLookup.Unchecked;
    }
}
