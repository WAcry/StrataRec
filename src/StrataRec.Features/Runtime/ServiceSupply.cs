using StrataRec.Core.Memory;
using StrataRec.Core.Threading;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Connectors;

namespace StrataRec.Features.Runtime;

/// <summary>
/// The service a source in mode <c>service</c> is bound to (IO step 5), as the plan's service call path gives it: asked
/// once per request for the features of an entity with args, bounded, cached, recorded and replayed as every service call
/// is. The pipeline implements it; the fetcher only gathers the call's arguments and stores what it answers.
/// </summary>
internal interface IServiceSupply
{
    /// <summary>The service's binding name.</summary>
    string ServiceName { get; }

    /// <summary>
    /// Asks the service for one request's values of <paramref name="call"/>'s fields. A failure of the service, a cut-off and
    /// an answer the fields cannot hold are a failed status, never a throw; a stale answer of its cache is a partial one.
    /// </summary>
    ValueTask<FetchStatus> FetchAsync(ServiceSupplyCall call);
}

/// <summary>
/// The supply of the service a source in mode <c>service</c> names (the plan's, called for that source); null when the
/// service cannot be called, reported by the plan at <paramref name="pointer"/> (the binding's <c>service</c>) or at the
/// service's binding.
/// </summary>
internal delegate IServiceSupply? ServiceSupplyResolver(string source, string service, string pointer);

/// <summary>One argument of a call (<see cref="ServiceSupplyCall.Args"/>): its name, declared type and this request's value.</summary>
internal readonly record struct ServiceArg(string Name, ColumnType Type, ScalarValue Value);

/// <summary>One request's call of a <see cref="SupplyMode.Service"/> group (IO step 5).</summary>
internal sealed class ServiceSupplyCall
{
    /// <summary>The group: its source, entity and members.</summary>
    public required FetchGroup Group { get; init; }

    /// <summary>The entity's arguments, in declared order; a missing value is sent as missing.</summary>
    public required ServiceArg[] Args { get; init; }

    /// <summary>The fields the call reads (the members the request needs), each the output of its name.</summary>
    public required CompiledFieldBinding[] Fields { get; init; }

    /// <summary>One value per field, written by the supply: <see cref="ScalarValue.Missing"/> for an output the service did not answer.</summary>
    public required ScalarValue[] Values { get; init; }

    /// <summary>The request's memory: the call's inputs and its answer are rented from it.</summary>
    public required RequestMemory Memory { get; init; }

    /// <summary>The fetch's bound: the request's deadline and the caller's token.</summary>
    public required CallBound Bound { get; init; }

    /// <summary>The request's recorder or replay, when it records or replays.</summary>
    public IFeatureCallHook? Hook { get; init; }

    /// <summary>The request's id and seed, which a service call carries as a node's does (review of IO step 5).</summary>
    public string? RequestId { get; init; }

    /// <inheritdoc cref="RequestId"/>
    public long? Seed { get; init; }

    /// <summary>The fetch, which tracks the call's external operations still running (<see cref="FeatureFetcher.WhenIoCompleteAsync"/>).</summary>
    public IIoTracker? Io { get; init; }
}

/// <summary>Tracks an external operation that did not complete synchronously, until it really has.</summary>
internal interface IIoTracker
{
    void Track(Task done);
}
