using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Services;

namespace StrataRec.Samples.Host.Components;

/// <summary>
/// <c>stock@1</c>: a business service that says which items are in stock, from an in-memory list of the ones that are not.
/// A production service would be the shop's inventory system, remote and fast-changing; the call would stay the same.
/// </summary>
/// <remarks>
/// <para>It answers a call about items (<see cref="ServiceCall.Items"/>, the candidates a filter sends): output
/// <c>in_stock</c>, one bool per item, true unless the item is listed as out of stock. A call about no items, or one that
/// reads another output, is not one it can answer: it fails, saying what it answers.</para>
/// <para>The rules of the contract, and how this service keeps them:</para>
/// <list type="bullet">
/// <item>An item's answer depends on the item alone, as a call about items must: the engine may batch the items as it
/// likes, cache each item's answer on its own (the binding declares a <c>cache</c>), and replay an item from any recorded
/// call that had it.</item>
/// <item>It writes exactly one value per item, into the buffer the response hands it, and allocates nothing. It answers
/// synchronously, so the engine's bounded wait allocates no task either.</item>
/// <item>A failure is a <see cref="ServiceStatus.Failed"/> with a reason; the node's <c>on_failure</c> decides what the
/// pipeline does without an answer.</item>
/// </list>
/// </remarks>
public sealed class StockService(HashSet<long> outOfStock) : IServiceClient
{
    /// <summary>The output it answers: whether each item is in stock.</summary>
    public const string Output = "in_stock";

    public ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse response, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<ServiceStatus>(cancellationToken);
        }

        var items = call.Items.Span;
        if (items.IsEmpty)
        {
            return new ValueTask<ServiceStatus>(ServiceStatus.Failed($"stock@1 answers a call about items, and the call of '{call.Service}' has none: send the candidates as its items."));
        }

        foreach (var output in call.Outputs.Span)
        {
            if (output != Output)
            {
                return new ValueTask<ServiceStatus>(ServiceStatus.Failed($"stock@1 answers output '{Output}' only; the call reads '{output}'."));
            }
        }

        var inStock = response.GetValues<bool>(Output, items.Length).Span;
        for (var i = 0; i < items.Length; i++)
        {
            if (items[i].StringValue is not null)
            {
                return new ValueTask<ServiceStatus>(ServiceStatus.Failed("stock@1 knows int64 item ids; the call's items are strings."));
            }

            inStock[i] = !outOfStock.Contains(items[i].Int64Value);
        }

        return new ValueTask<ServiceStatus>(ServiceStatus.Ok);
    }
}

/// <summary>The settings of <c>stock@1</c>, bound strictly by the engine; the bindings schema is generated from this type.</summary>
public sealed class StockSettings
{
    /// <summary>The ids of the items out of stock, e.g. <c>[1, 22]</c>; every other item is in stock.</summary>
    public required List<long> OutOfStock { get; init; }
}

/// <summary>Creates <c>stock@1</c> services; registered in <see cref="SampleEnvironment"/>.</summary>
public sealed class StockServiceFactory : BindingFactory<IServiceClient, StockSettings>
{
    public const string TypeName = "stock@1";

    public override string Type => TypeName;

    protected override IServiceClient Create(StockSettings settings, BindingContext context) => new StockService([.. settings.OutOfStock]);
}
