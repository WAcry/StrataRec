using System.ComponentModel;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Services;
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
/// <item>The answer is read while the call's memory is the request's: the keep flags are the response's own buffer, by
/// rank position, handed to <see cref="OperatorContext.Retain"/>. Nothing is allocated per row.</item>
/// </list>
/// </remarks>
public sealed class StockFilterFactory : IOperatorFactory
{
    public OperatorDescriptor Descriptor { get; } = new("StockFilter", 1, Effects.RowFilter, typeof(StockFilterConfig),
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

        var service = context.Service(config.Service, "/service", "filter");
        return service is null ? null : new StockFilter(service, [config.KeepOutput]);
    }

    private sealed class StockFilter(BoundService service, string[] outputs) : Operator
    {
        public override async ValueTask ExecuteAsync(OperatorContext context)
        {
            var rows = context.LiveRows;
            var count = rows.Length;
            if (count == 0)
            {
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

            context.Retain(values.Bool.Span);
        }
    }
}
