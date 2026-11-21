using StrataRec.Core.Memory;
using StrataRec.Core.Services;
using StrataRec.Core.Threading;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;
using StrataRec.Pipeline.Replay;

namespace StrataRec.Pipeline.Operators;

/// <summary>
/// A feature source in mode <c>service</c> (IO step 5): asks its service, once per request, for the features of an entity
/// with args, through the one service call path (<see cref="ServiceCallPath"/>). The call has no items and adds none:
/// its inputs are the arguments (a scalar of shape <c>[]</c>, a list of shape <c>[n]</c>, a missing value sent as
/// missing), and it reads one output per feature, the feature's field. It is waited for within the fetch's bound and the
/// service's <c>timeout_ms</c>, answered by the service's cache, measured in role <c>fetch</c>, and recorded and replayed
/// as a call of the source, made by no node.
/// </summary>
internal sealed class FeatureServiceSupply(BoundService service, string source) : IServiceSupply
{
    private readonly ServiceCaller _caller = ServiceCaller.OfSource(source);

    public string ServiceName => service.Name;

    public async ValueTask<FetchStatus> FetchAsync(ServiceSupplyCall call)
    {
        var inputs = new NamedInput[call.Args.Length];
        for (var a = 0; a < inputs.Length; a++)
        {
            var arg = call.Args[a];
            inputs[a] = ServiceInputs.Create(arg.Name, arg.Type, arg.Value, call.Memory);
        }

        var outputs = new string[call.Fields.Length];
        for (var i = 0; i < outputs.Length; i++)
        {
            outputs[i] = call.Fields[i].Field;
        }

        var scope = new Scope(_caller, call);
        var response = new ServiceResponse(call.Memory);
        ServiceStatus status;
        try
        {
            status = await ServiceCallPath.CallAsync(scope, service, new ServiceCall { Service = service.Name, Inputs = inputs, Outputs = outputs }, response)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The fetch's bound ran out (live, or where the recorded call was), or the caller cancelled: the features fail,
            // as a connector's cut off at its bound do; the caller's cancellation is the request's to throw.
            return FetchStatus.Failed(call.Bound.Caller.IsCancellationRequested ? $"source '{source}' was cancelled" : $"source '{source}' timed out");
        }

        if (!status.Succeeded)
        {
            return FetchStatus.Failed($"source '{source}' failed: service '{service.Name}': {status.Error}");
        }

        for (var i = 0; i < call.Fields.Length; i++)
        {
            if (!TryRead(response, call.Fields[i], out call.Values[i], out var error))
            {
                return FetchStatus.Failed($"source '{source}' failed: service '{service.Name}' answered {error}");
            }
        }

        return scope.Degraded is { } degraded ? new FetchStatus(FetchOutcome.Partial, 0, degraded) : FetchStatus.Ok;
    }

    /// <summary>
    /// The value of <paramref name="field"/> in the answer: missing when the service did not write the output; one element
    /// for a scalar, the elements for a list, a text or a string element for a string. Numbers widen (an int32 output to an
    /// int64 or a float feature, a float32 to a float64), and the value is stored as the feature's type holds it (a float
    /// rounded to float32, an integer outside int32 counted out of range); an output of another kind, or of several
    /// elements for a scalar, is no answer the feature can take.
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

        if (!type.IsList)
        {
            if (values.Length != 1)
            {
                error = $"output '{field.Field}' with {values.Length} values; a {type} feature takes one";
                return false;
            }

            return TryScalar(values, type.Kind, field.Field, out value, out error);
        }

        switch (type.Element)
        {
            case StorageKind.Bool when values.ElementType == TensorElementType.Bool:
                value = ScalarValue.FromBoolList(values.Bool.ToArray());
                return true;
            case StorageKind.Int32 or StorageKind.Int64 when values.ElementType is TensorElementType.Int32 or TensorElementType.Int64:
                value = ScalarValue.FromInt64List(Int64s(values));
                return true;
            case StorageKind.Float32 when values.ElementType == TensorElementType.Float32:
                value = ScalarValue.FromFloat32List(values.Float32.Span);
                return true;
            case StorageKind.Float32 or StorageKind.Float64 when IsNumber(values.ElementType):
                value = ScalarValue.FromFloat64List(Float64s(values));
                return true;
            case StorageKind.String when values.ElementType == TensorElementType.String:
                value = ScalarValue.FromStringList(values.String.ToArray());
                return true;
            default:
                error = $"output '{field.Field}' of {values.ElementType} values, which a {type} feature cannot take";
                return false;
        }
    }

    private static bool TryScalar(in TensorValues values, StorageKind kind, string name, out ScalarValue value, out string? error)
    {
        error = null;
        switch (kind)
        {
            case StorageKind.Bool when values.ElementType == TensorElementType.Bool:
                value = ScalarValue.FromBool(values.Bool.Span[0]);
                return true;
            case StorageKind.Int32 or StorageKind.Int64 when values.ElementType == TensorElementType.Int32:
                value = ScalarValue.FromInt64(values.Int32.Span[0]);
                return true;
            case StorageKind.Int32 or StorageKind.Int64 when values.ElementType == TensorElementType.Int64:
                value = ScalarValue.FromInt64(values.Int64.Span[0]);
                return true;
            case StorageKind.Float32 or StorageKind.Float64 when IsNumber(values.ElementType):
                value = ScalarValue.FromFloat64(Float64At(values, 0));
                return true;
            case StorageKind.String when values.ElementType == TensorElementType.String:
                value = ScalarValue.FromString(values.String.Span[0]);
                return true;
            default:
                value = ScalarValue.Missing;
                error = $"output '{name}' of {values.ElementType} values, which a {kind.ToString().ToLowerInvariant()} feature cannot take";
                return false;
        }
    }

    private static bool IsNumber(TensorElementType type) =>
        type is TensorElementType.Int32 or TensorElementType.Int64 or TensorElementType.Float32 or TensorElementType.Float64;

    private static double Float64At(in TensorValues values, int i) => values.ElementType switch
    {
        TensorElementType.Int32 => values.Int32.Span[i],
        TensorElementType.Int64 => values.Int64.Span[i],
        TensorElementType.Float32 => values.Float32.Span[i],
        _ => values.Float64.Span[i],
    };

    private static double[] Float64s(in TensorValues values)
    {
        var list = new double[values.Length];
        for (var i = 0; i < list.Length; i++)
        {
            list[i] = Float64At(values, i);
        }

        return list;
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
    }
}
