using StrataRec.Core.Diagnostics;
using StrataRec.Core.Memory;
using StrataRec.Core.Services;
using StrataRec.Core.Threading;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;
using StrataRec.Pipeline.Operators.Standard;
using StrataRec.Pipeline.Replay;

namespace StrataRec.Pipeline.Operators;

/// <summary>
/// A feature source in mode <c>service</c>: asks its service, once per request, for the features of an entity
/// with args, through the one service call path (<see cref="ServiceCallPath"/>). The call has no items and adds none:
/// its inputs are the arguments (a scalar of shape <c>[]</c>, a list of shape <c>[n]</c>, a missing value sent as
/// missing), and it reads one output per feature, the feature's field. It is waited for within the fetch's bound and the
/// service's <c>timeout_ms</c>, answered by the service's cache, measured in role <c>fetch</c>, and recorded and replayed
/// as a call of the source, made by no node.
/// </summary>
internal sealed class FeatureServiceSupply(BoundService service, string source) : IServiceSupply
{
    public string ServiceName => service.Name;

    public async ValueTask<FetchStatus> FetchAsync(ServiceSupplyCall call)
    {
        var inputs = new NamedInput[call.Args.Length];
        for (var a = 0; a < inputs.Length; a++)
        {
            var arg = call.Args[a];
            inputs[a] = ServiceInputs.Create(arg.Name, arg.Type, arg.Value, call.Memory);
        }

        var scope = new Scope(ServiceCaller.OfSource(source, call.Group.Entity.Name), call);
        var response = new ServiceResponse(call.Memory);
        var serviceCall = new ServiceCall
        {
            Service = service.Name, Inputs = inputs, Outputs = Outputs(call), RequestId = call.RequestId ?? string.Empty,
            Options = new ServiceCallOptions(Seed: call.Seed),
        };
        ServiceStatus status;
        try
        {
            status = await ServiceCallPath.CallAsync(scope, service, serviceCall, response).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            // The fetch's bound ran out, or the caller cancelled, live or where the recorded call was (a
            // recorded cancellation replayed as a timeout, the replay's caller not having cancelled): the features fail,
            // as a connector's cut off at its bound do; the caller's cancellation is the request's to throw.
            var cancelled = ex is ReplayedCutOff replayed ? replayed.Outcome == IoOutcome.Cancelled : call.Bound.Caller.IsCancellationRequested;
            return Counted(FetchStatus.Failed(cancelled ? $"source '{source}' was cancelled" : $"source '{source}' timed out"), scope, call);
        }

        if (!status.Succeeded)
        {
            return Counted(FetchStatus.Failed($"source '{source}' failed: service '{service.Name}': {status.Error}"), scope, call);
        }

        for (var i = 0; i < call.Fields.Length; i++)
        {
            if (!TryRead(response, call.Fields[i], out call.Values[i], out var error))
            {
                return Counted(FetchStatus.Failed($"source '{source}' failed: service '{service.Name}' answered {error}"), scope, call);
            }
        }

        return Counted(scope.Degraded is { } degraded ? new FetchStatus(FetchOutcome.Partial, 0, degraded) : FetchStatus.Ok, scope, call);
    }

    /// <summary>
    /// A replay counts the fields it served, as it counts a fetch's (the feature-missing rate a comparison reports): each
    /// read once, missing when the call failed or the service left its output unwritten, before any default (they
    /// were once left out of the rate).
    /// </summary>
    private FetchStatus Counted(FetchStatus status, Scope scope, ServiceSupplyCall call)
    {
        scope.Replay?.CountServed(source, call.Fields, call.Values, status.Outcome == FetchOutcome.Failed);
        return status;
    }

    /// <summary>
    /// The value of <paramref name="field"/> in the answer: missing when the service did not write the output; one element
    /// for a scalar, the elements for a list, a text or a string element for a string. The output is of the type the
    /// feature is declared with, as a model's output is of its column's: no number is widened or rounded on the way
    /// (docs/development.md, boundaries; an int32 output was widened, and a float64 one rounded
    /// to a float32 feature). An output of another type, or of several elements for a scalar, is no answer the feature can
    /// take: the call fails, and says what the feature takes.
    /// </summary>
    internal static bool TryRead(ServiceResponse response, in CompiledFieldBinding field, out ScalarValue value, out string? error)
    {
        value = ScalarValue.Missing;
        error = null;
        var type = field.Type;
        if (!response.TryGetValues(field.Field, out var values))
        {
            if (response.TryGetText(field.Field, out var text))
            {
                if (type.Kind != StorageKind.String)
                {
                    error = $"output '{field.Field}' as a text, which a {type} feature cannot take";
                    return false;
                }

                value = ScalarValue.FromString(text);
            }

            return true; // not answered: missing
        }

        var kind = type.IsList ? type.Element : type.Kind;
        var expected = ModelTensorRule.ElementTypeOf(kind);
        if (values.ElementType != expected)
        {
            error = $"output '{field.Field}' as {TensorValues.Name(values.ElementType)}, which a {type} feature cannot take: it takes {TensorValues.Name(expected)}, the type it is declared with";
            return false;
        }

        if (!type.IsList)
        {
            if (values.Length != 1)
            {
                error = $"output '{field.Field}' with {values.Length} values; a {type} feature takes one";
                return false;
            }

            value = kind switch
            {
                StorageKind.Bool => ScalarValue.FromBool(values.Bool.Span[0]),
                StorageKind.Int32 => ScalarValue.FromInt64(values.Int32.Span[0]),
                StorageKind.Int64 => ScalarValue.FromInt64(values.Int64.Span[0]),
                StorageKind.Float32 => ScalarValue.FromFloat64(values.Float32.Span[0]), // exact: stored back as the float32 it is
                StorageKind.Float64 => ScalarValue.FromFloat64(values.Float64.Span[0]),
                _ => values.String.Span[0] is { } text ? ScalarValue.FromString(text) : ScalarValue.Missing, // left unwritten
            };
            return true;
        }

        switch (kind)
        {
            case StorageKind.Bool:
                value = ScalarValue.FromBoolList(values.Bool.ToArray());
                return true;
            case StorageKind.Int32 or StorageKind.Int64:
                value = ScalarValue.FromInt64List(Int64s(values));
                return true;
            case StorageKind.Float32:
                value = ScalarValue.FromFloat32List(values.Float32.Span);
                return true;
            case StorageKind.Float64:
                value = ScalarValue.FromFloat64List(values.Float64.ToArray());
                return true;
            default:
                // An element the service left unwritten is null, which no list of strings holds (one
                // reached a later call's digest as an argument, and failed the recording).
                var strings = values.String.Span;
                for (var i = 0; i < strings.Length; i++)
                {
                    if (strings[i] is null)
                    {
                        error = $"output '{field.Field}' with element {i} of {strings.Length} unwritten";
                        return false;
                    }
                }

                value = ScalarValue.FromStringList(strings.ToArray());
                return true;
        }
    }

    private static long[] Int64s(in TensorValues values)
    {
        var list = new long[values.Length];
        for (var i = 0; i < list.Length; i++)
        {
            list[i] = values.ElementType == TensorElementType.Int32 ? values.Int32.Span[i] : values.Int64.Span[i];
        }

        return list;
    }

    /// <summary>
    /// The outputs the call reads: each field's name once, though two features may read one field (a
    /// service was asked for an output twice); each feature still takes the output as its own type does. A whole group's,
    /// built once for it; a subset's (a request that needs only some of the group), for it.
    /// </summary>
    private static string[] Outputs(ServiceSupplyCall call) =>
        ReferenceEquals(call.Fields, call.Group.FieldArray) ? call.Group.FieldNames : FetchGroup.DistinctNames(call.Fields);

    /// <summary>One request's call: the fetch's bound and memory, the request's recorder or replay, and whether a stale answer degraded it.</summary>
    private sealed class Scope(ServiceCaller caller, ServiceSupplyCall call) : IServiceCallScope
    {
        public ServiceCaller Caller => caller;

        public CallBound Budget => call.Bound;

        public RequestMemory Memory => call.Memory;

        public RunRecorder? Recorder => call.Hook as RunRecorder;

        public RunReplay? Replay => call.Hook as RunReplay;

        public string? Degraded { get; private set; }

        public void ReportDegraded(string reason) => Degraded ??= reason;

        public void Pending(Task done) => call.Io?.Track(done);
    }
}
