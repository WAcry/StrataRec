using System.ComponentModel;
using StrataRec.Core.Columns;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Pipeline.Operators;

namespace StrataRec.Samples.Host.Components;

/// <summary>Config of <c>DistinctHeads@1</c>; its JSON Schema is generated from this class, as for <see cref="CategoryBoostConfig"/>.</summary>
public sealed class DistinctHeadsConfig
{
    [Description("The list column of each row's item ids (list<int64>, declared with \"flow\": true: the node rewrites it).")]
    public required string IdsCol { get; init; }

    [Description("The list columns that go with the ids element by element (scores, labels), reordered with them. A row " +
        "without a value in one keeps none.")]
    public List<string>? With { get; init; }

    [Description("Parameter (int64) holding how many items a row shows first: the items no row above shows first come first, " +
        "as many as the row has; when it has fewer, the others follow in their order.")]
    public required string HeadParam { get; init; }
}

/// <summary>
/// <c>DistinctHeads@1</c>: a rule across the rows of a page whose rows hold lists (<c>pipelines/shelves.jsonc</c>). What a
/// row shows first, its first few items, are the ones no row above shows first, as far as it has such: an item a row
/// above has in its head moves behind the others here. Nothing is dropped, so a row with too few items of its own still
/// starts with some a row above shows, and an item may be on several rows.
/// </summary>
/// <remarks>
/// <para>How an operator works on lists:</para>
/// <list type="bullet">
/// <item>A row's list is a value of the row, in a list column (<see cref="ListColumn{T}"/>): read as a span, written as
/// a whole. The node reads the version before it and writes a new one, so the columns are declared
/// <c>"flow": true</c>.</item>
/// <item>Lists that go together element by element stay aligned when each takes the same positions
/// (<see cref="ListColumn.SetTaken"/>), whatever its element type: the operator decides an order once, from the ids,
/// and every list follows it.</item>
/// <item>The rule is this page's own. The framework's part is the rows, their lists and the calls that filled them
/// (<c>PipelineColumns@1</c>).</item>
/// </list>
/// </remarks>
public sealed class DistinctHeadsFactory : IOperatorFactory
{
    public OperatorDescriptor Descriptor { get; } = new("DistinctHeads", 1, Effects.ColumnWriter, typeof(DistinctHeadsConfig),
        "Reorders each row's list so that the items no row above shows first come first.");

    public Operator? Compile(OperatorCompileContext context)
    {
        var config = context.BindConfig<DistinctHeadsConfig>();
        if (config is null)
        {
            return null;
        }

        // Each list is read (the version before the node) and written (the node's): the reads come first.
        var names = new List<string> { config.IdsCol };
        names.AddRange(config.With ?? []);
        var reads = new ColumnRef[names.Count];
        var ok = context.TryParam(config.HeadParam, "/head_param", out var head, StorageKind.Int64, (0, 1000));
        for (var i = 0; i < names.Count; i++)
        {
            var key = i == 0 ? "/ids_col" : $"/with/{i - 1}";
            if (!context.TryReadColumn(names[i], key, out reads[i]))
            {
                ok = false;
            }
            else if (!reads[i].Type.IsList || (i == 0 && reads[i].Type.Element != StorageKind.Int64))
            {
                context.Error(key, "COLUMN_TYPE_MISMATCH", $"Column '{names[i]}' is {reads[i].Type}; {(i == 0 ? "the ids are a list<int64>" : "a list goes with the ids")}.");
                ok = false;
            }
        }

        var writes = new OutputColumn[names.Count];
        for (var i = 0; ok && i < names.Count; i++)
        {
            ok = context.TryWriteColumn(names[i], i == 0 ? "/ids_col" : $"/with/{i - 1}", null, out writes[i]);
        }

        return ok ? new DistinctHeads(reads, writes, head) : null;
    }

    private sealed class DistinctHeads(ColumnRef[] reads, OutputColumn[] writes, ParamRef head) : Operator
    {
        public override ValueTask ExecuteAsync(OperatorContext context)
        {
            var from = new ListColumn?[reads.Length];
            var into = new ListColumn[reads.Length];
            for (var c = 0; c < reads.Length; c++)
            {
                from[c] = (ListColumn?)context.Read(reads[c]);
                into[c] = (ListColumn)context.Output(writes[c]);
            }

            if (from[0] is not ListColumn<long> ids)
            {
                context.Fail($"column '{reads[0].Name}' was not produced in this request");
                return ValueTask.CompletedTask;
            }

            var value = context.Param(head);
            var most = value.IsMissing ? 0 : (int)value.AsInt64();
            var shown = new HashSet<long>(); // the items in the heads of the rows above
            var order = new List<int>();
            foreach (var row in context.LiveRows) // in rank order
            {
                if (!ids.IsValid(row))
                {
                    // A row without ids has nothing to reorder. What it has in the other lists is carried into the node's
                    // version as it is: the node writes a new version of each, and a row it left out would have no value.
                    for (var c = 1; c < into.Length; c++)
                    {
                        if (from[c] is { } other && other.IsValid(row))
                        {
                            into[c].CopyFrom(other, row, row);
                        }
                    }

                    continue;
                }

                // The head: the first items no row above shows first. Then the others, in their order.
                var items = ids.Get(row);
                order.Clear();
                for (var i = 0; i < items.Length && order.Count < most; i++)
                {
                    if (!shown.Contains(items[i]))
                    {
                        order.Add(i);
                    }
                }

                var headCount = order.Count;
                for (var i = 0; i < items.Length; i++)
                {
                    if (order.IndexOf(i, 0, headCount) < 0)
                    {
                        order.Add(i);
                    }
                }

                for (var i = 0; i < headCount; i++)
                {
                    shown.Add(items[order[i]]);
                }

                // Every list takes the same positions. One of another length than the ids is not theirs to follow.
                var positions = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(order);
                for (var c = 0; c < into.Length; c++)
                {
                    if (from[c] is not { } list || !list.IsValid(row))
                    {
                        continue;
                    }

                    if (list.LengthOf(row) != items.Length)
                    {
                        context.Fail($"row {context.Table.GetKey(row)} has {items.Length} ids and {list.LengthOf(row)} elements in '{reads[c].Name}'");
                        return ValueTask.CompletedTask;
                    }

                    into[c].SetTaken(row, list, row, positions);
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}
