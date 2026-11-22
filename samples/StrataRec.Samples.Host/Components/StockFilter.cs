using System.ComponentModel;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Services;
using StrataRec.Core.Values;
using StrataRec.Pipeline.Operators;

namespace StrataRec.Samples.Host.Components;

/// <summary>Config of <c>StockFilter@1</c>; its JSON Schema is generated from this class, as for <see cref="CategoryBoostConfig"/>.</summary>
public sealed class StockFilterConfig
{
    [Description("Service to ask, as the bindings' 'services' name it, e.g. stock. It is sent the candidates as the call's " +
        "items and answers a bool per item.")]
    public required string Service { get; init; }

    [Description("The service's output that keeps a row when true, e.g. in_stock.")]
    public required string KeepOutput { get; init; }

    [Description("Request column (declared under request_columns, an integer) the filter writes how many candidates the " +
        "service said no to, e.g. stock.dropped; omitted, it writes none.")]
    public string? DroppedColumn { get; init; }
}

/// <summary>
/// <c>StockFilter@1</c>: a filter that asks an external service, here whether each candidate is in stock, and drops the
/// rows it says no to. Any business service that judges items one by one (availability, a blocklist, a compliance check)
/// is the same operator with another binding.
/// </summary>
/// <remarks>
/// <para>How an operator calls a service:</para>
/// <list type="bullet">
/// <item>Compile looks the service up by its binding's name (<see cref="OperatorCompileContext.Service"/>), once, with
/// the role its calls are measured in (<c>filter</c>: <c>stratarec.io.*</c> with that role). A name the bindings do not
/// declare is an error at <c>/service</c>.</item>
/// <item>The call is about the live rows' entities (<see cref="ServiceCall.Items"/>, one per row in rank order), reads
/// one output, and goes through <see cref="OperatorContext.CallAsync"/>, which bounds it by the node's budget and the
/// service's <c>timeout_ms</c>, answers from the service's cache item by item, and records and replays it. The operator
/// writes no I/O code of its own.</item>
/// <item>A failed call fails the node, whose <c>on_failure</c> decides: the sample's pipeline skips the filter, keeping
/// every candidate, rather than failing the request. An answer the operator cannot use (the output missing, or not one bool
/// per row) is a failure too; it never guesses which rows to keep.</item>
/// <item>Replayed after the pipeline was edited, a candidate no recorded call asked about has no answer: the replay
/// reports it as a gap (<c>not_recorded</c>, with the item) and, when a recorded call of the service (and version) had
/// the output, of this node or another, answers it as false, the value a gap of a bool has, so the filter drops it; the
/// report's gaps tell such a row from one out of stock. With no recorded call of the service that had the output (it was
/// not called when the run was recorded), the call fails, and <c>on_failure</c> keeps every candidate.</item>
/// <item>The answer is read while the call's memory is the request's: the keep flags are the response's own buffer, by
/// rank position, handed to <see cref="OperatorContext.Retain"/>. Nothing is allocated per row.</item>
/// </list>
/// <para>How an operator tells the request something (a request column, <c>req.*</c>): it declares
/// <see cref="Effects.RequestWriter"/>, resolves the column when it compiles
/// (<see cref="OperatorCompileContext.TryWriteRequestColumn"/>: it must be declared under <c>request_columns</c>), and
/// stages the value while it runs (<see cref="OperatorContext.SetRequestValue"/>). The value is published when the node
/// commits, so a later node, a tap and the caller (<c>output.values</c>) read it; when the node fails, nothing it staged
/// is. Here, how many candidates were out of stock: the pipeline returns it with the result.</para>
/// </remarks>
public sealed class StockFilterFactory : IOperatorFactory
{
    public OperatorDescriptor Descriptor { get; } = new("StockFilter", 1, Effects.RowFilter | Effects.RequestWriter, typeof(StockFilterConfig),
        "Drops the rows an external service says no to: it is sent the candidates and answers a bool per item.");

    public Operator? Compile(OperatorCompileContext context)
    {
        var config = context.BindConfig<StockFilterConfig>();
        if (config is null)
        {
            return null;
        }

        if (string.IsNullOrEmpty(config.KeepOutput))
        {
            context.Error("/keep_output", "STOCK_FILTER_OUTPUT", "'keep_output' must name the service's output that keeps a row.", "e.g. \"in_stock\"");
            return null;
        }

        RequestOutput? dropped = null;
        if (config.DroppedColumn is { } name)
        {
            if (!context.TryWriteRequestColumn(name, "/dropped_column", out var column))
            {
                return null;
            }

            if (!column.Type.IsInteger)
            {
                context.Error("/dropped_column", "COLUMN_TYPE_MISMATCH", $"Request column '{name}' is {column.Type}; the filter writes a count.",
                    "declare it as int64? (missing when the filter did not run to the end)");
                return null;
            }

            dropped = column;
        }

        var service = context.Service(config.Service, "/service", "filter");
        return service is null ? null : new StockFilter(service, [config.KeepOutput], dropped);
    }

    private sealed class StockFilter(BoundService service, string[] outputs, RequestOutput? dropped) : Operator
    {
        public override async ValueTask ExecuteAsync(OperatorContext context)
        {
            var rows = context.LiveRows;
            var count = rows.Length;
            if (count == 0)
            {
                SetDropped(context, 0);
                return;
            }

            var call = new ServiceCall
            {
                Service = service.Name,
                BatchSize = count,
                Items = context.KeysOf(rows),
                Outputs = outputs,
                RequestId = context.RequestId,
            };
            var response = new ServiceResponse(context.Memory);
            var status = await context.CallAsync(service, call, response);
            if (!status.Succeeded)
            {
                context.Fail($"service '{service.Name}': {status.Error}");
                return;
            }

            var keep = outputs[0];
            if (!response.TryGetValues(keep, out var values) || values.ElementType != TensorElementType.Bool || values.Length != count)
            {
                context.Fail($"service '{service.Name}' answered no '{keep}' of one bool per row ({count} rows)");
                return;
            }

            var keepFlags = values.Bool.Span;
            var kept = 0;
            foreach (var flag in keepFlags)
            {
                kept += flag ? 1 : 0;
            }

            context.Retain(keepFlags);
            SetDropped(context, count - kept);
        }

        private void SetDropped(OperatorContext context, long count)
        {
            if (dropped is { } column)
            {
                context.SetRequestValue(column, ScalarValue.FromInt64(count));
            }
        }
    }
}
