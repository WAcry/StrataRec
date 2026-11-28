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
