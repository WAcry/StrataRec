using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Logging;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Hosting.Plans;

/// <summary>A node a request with the combination runs: its compiled variant and the timeline position (or fallback slot) it fills.</summary>
/// <param name="Node">The variant selected for the combination; its <see cref="CompiledNode.Definition"/> is the merged definition.</param>
/// <param name="Slot">Where the variant is selected from: a timeline position, or the fallback slot of a fallback-only node.</param>
public sealed record EffectiveNode(CompiledNode Node, TimelineSlot Slot)
{
    public string Id => Node.NodeId;

    /// <summary>True for a node that runs only as another node's fallback (<c>role: fallback</c>).</summary>
    public bool IsFallback => Node.IsFallbackRole;
}

/// <summary>A parameter leaf's value for the combination.</summary>
/// <param name="Value">The snapshot value: the baseline with the combination's overrides applied.</param>
/// <param name="Overridden">True when an arm of the combination sets the value (or adds the leaf).</param>
public sealed record EffectiveParam(ParamLeaf Leaf, ScalarValue Value, bool Overridden);

/// <summary>A log tap that captures for the combination, with the funnel group whose sampling decision it shares.</summary>
public sealed record EffectiveTap(CompiledTap Tap, CompiledFunnelGroup Group);

/// <summary>
/// What a request with one arm combination executes: its nodes with their merged
/// definitions, its parameter snapshot, the log taps that capture and the funnel groups they sample with, and the output.
/// </summary>
/// <remarks>
/// <para>What exists only because of an arm appears only in combinations that include the arm: a node it adds, a
/// parameter leaf it declares (whose baseline is missing), a tap it attaches. Funnel groups are those the selected taps
/// use, so a group an arm adds does not appear in the baseline.</para>
/// </remarks>
public sealed class EffectivePlan
{
    private EffectivePlan(ExecutionPlan plan, RequestArms arms, List<string> armIds, List<EffectiveNode> nodes, List<EffectiveParam> parameters, List<EffectiveTap> taps)
    {
        Plan = plan;
        Arms = arms;
        ArmIds = armIds;
        Nodes = nodes;
        Params = parameters;
        Taps = taps;
    }

    public ExecutionPlan Plan { get; }

    public RequestArms Arms { get; }

    /// <summary>The combination's arms, <c>experiment/arm</c>, in layer order; empty for the baseline.</summary>
    public IReadOnlyList<string> ArmIds { get; }

    /// <summary>Timeline nodes in execution order, then fallback-only nodes by id.</summary>
    public IReadOnlyList<EffectiveNode> Nodes { get; }

    /// <summary>Parameter leaves with a value for the combination, in schema order.</summary>
    public IReadOnlyList<EffectiveParam> Params { get; }

    /// <summary>Taps that exist for the combination, by id.</summary>
    public IReadOnlyList<EffectiveTap> Taps { get; }

    public OutputPlan Output => Plan.Output;

    /// <summary>
    /// The plan a request in <paramref name="arms"/> executes: without an arm whose experiment yields to another's of them
    ///, as the executor runs it, so <see cref="ArmIds"/> are the arms that run. A tool that shows the arms a
    /// user named refuses such a combination first (<see cref="ExecutionPlan.NeverRunTogether"/>).
    /// </summary>
    public static EffectivePlan Create(ExecutionPlan plan, RequestArms arms)
    {
        ArgumentNullException.ThrowIfNull(plan);
        arms = plan.Yields.Resolve(arms);
        var nodes = new List<EffectiveNode>();
        foreach (var slot in plan.Timeline)
        {
            if (slot.Select(arms) is { } node)
            {
                nodes.Add(new EffectiveNode(node, slot));
            }
        }

        var fallbackIds = new List<string>(plan.Fallbacks.Keys);
        fallbackIds.Sort(StringComparer.Ordinal);
        foreach (var id in fallbackIds)
        {
            var slot = plan.Fallbacks[id];
            if (slot.Select(arms) is { } node)
            {
                nodes.Add(new EffectiveNode(node, slot));
            }
        }

        var snapshot = plan.Params.Snapshot(arms);
        var parameters = new List<EffectiveParam>();
        for (var i = 0; i < plan.Schema.Params.Count; i++)
        {
            if (!snapshot[i].IsMissing)
            {
                parameters.Add(new EffectiveParam(plan.Schema.Params[i], snapshot[i], !snapshot[i].Equals(plan.Params.Baseline[i])));
            }
        }

        var taps = new List<EffectiveTap>();
        foreach (var slot in plan.Log.Taps)
        {
            if (slot.Select(arms) is { } tap)
            {
                taps.Add(new EffectiveTap(tap, plan.Log.Groups[tap.Group]));
            }
        }

        taps.Sort(static (a, b) => string.CompareOrdinal(a.Tap.TapId, b.Tap.TapId));
        return new EffectivePlan(plan, arms, plan.ArmIds(arms), nodes, parameters, taps);
    }

    /// <summary>
    /// The combination as one JSON document, the shape <c>diff</c> compares and reports pointers into:
    /// <code>
    /// {
    ///   "nodes":         { "&lt;node&gt;": &lt;merged definition: order, op, config, when, on_failure, budgets, ...&gt; },
    ///   "params":        { "&lt;leaf&gt;": &lt;value&gt; },
    ///   "taps":          { "&lt;tap&gt;": { "attach": {"after": "&lt;node&gt;"} | {"output": true}, "group", "delivery",
    ///                                "rows": {"scope", "rate", "max_rows"?, "filter"?}, "context": {...}, "columns": {...},
    ///                                "features": [...] } },
    ///   "funnel_groups": { "&lt;group&gt;": { "key": [...], "rate": r } },
    ///   "output":        { "limit_param": "&lt;leaf&gt;", "columns": { "&lt;name&gt;": "&lt;expr&gt;" }, "values"?: { "&lt;name&gt;": "&lt;expr&gt;" } }
    /// }
    /// </code>
    /// A tap's columns are the compiled ones (<c>columns_from</c> expanded) and its features those its expressions read.
    /// Below the top level, members are in ordinal order and numbers canonical (RFC 8785), whatever order the artifacts
    /// list them in. A new tree on every call.
    /// </summary>
    public JsonObject ToDocument() => new()
    {
        ["nodes"] = NodesDocument(),
        ["params"] = ParamsDocument(),
        ["taps"] = TapsDocument(),
        ["funnel_groups"] = GroupsDocument(),
        ["output"] = OutputDocument(),
    };

    public JsonObject NodesDocument()
    {
        var nodes = new JsonObject();
        foreach (var node in Nodes)
        {
            nodes[node.Id] = JsonObject.Create(node.Node.Definition); // a tree of its own: the definition is the plan's
        }

        return Sorted(nodes, "nodes");
    }

    public JsonObject ParamsDocument()
    {
        var parameters = new JsonObject();
        foreach (var param in Params)
        {
            parameters[param.Leaf.Name] = ToJson(param.Value);
        }

        return Sorted(parameters, "params");
    }

    public JsonObject TapsDocument()
    {
        var taps = new JsonObject();
        foreach (var (tap, group) in Taps)
        {
            var rows = new JsonObject
            {
                ["scope"] = tap.Scope switch
                {
                    TapScope.Entered => "entered",
                    TapScope.Dropped => "dropped",
                    _ => "survived",
                },
                ["rate"] = tap.Rate,
            };
            if (tap.MaxRows is { } maxRows)
            {
                rows["max_rows"] = maxRows;
            }

            if (tap.Filter is { } filter)
            {
                rows["filter"] = filter.Source;
            }

            taps[tap.TapId] = new JsonObject
            {
                ["attach"] = tap.AttachAfter is { } after ? new JsonObject { ["after"] = after } : new JsonObject { ["output"] = true },
                ["group"] = group.Name,
                ["delivery"] = tap.Delivery == LogDelivery.Durable ? "durable" : "best_effort",
                ["rows"] = rows,
                ["context"] = Expressions(tap.Context),
                ["columns"] = Expressions(tap.Columns),
                ["features"] = Features(tap),
            };
        }

        return Sorted(taps, "taps");
    }

    public JsonObject GroupsDocument()
    {
        var groups = new SortedDictionary<string, CompiledFunnelGroup>(StringComparer.Ordinal);
        foreach (var (_, group) in Taps)
        {
            groups[group.Name] = group;
        }

        var document = new JsonObject();
        foreach (var (name, group) in groups)
        {
            var keys = new JsonArray();
            foreach (var key in group.Keys)
            {
                keys.Add(key.Source);
            }

            document[name] = new JsonObject { ["key"] = keys, ["rate"] = group.Rate };
        }

        return Sorted(document, "funnel_groups");
    }

    public JsonObject OutputDocument()
    {
        var document = new JsonObject
        {
            ["limit_param"] = Output.Limit.Name,
            ["columns"] = Expressions(Output.Columns),
        };
        if (Output.Values.Length > 0)
        {
            document["values"] = Expressions(Output.Values);
        }

        return Sorted(document, "output");
    }

    private static JsonObject Sorted(JsonObject document, string section)
    {
        try
        {
            return (JsonObject)JsonNode.Parse(JsonCanonicalizer.CanonicalizeToString(document))!;
        }
        catch (JsonCanonicalizationException ex)
        {
            throw new JsonCanonicalizationException(ex.Reason, JsonPointer.Parse("/" + section + ex.Pointer), ex.Hint);
        }
    }

    /// <summary>A parameter value as JSON: exact numbers (no rounding), and NaN or ±∞ as their names.</summary>
    public static JsonNode? ToJson(in ScalarValue value)
    {
        switch (value.Kind)
        {
            case 0:
                return null;
            case StorageKind.Bool:
                return value.AsBool();
            case StorageKind.Int64:
                return value.AsInt64();
            case StorageKind.Float64:
                var d = value.AsFloat64();
                return double.IsFinite(d) ? d : d.ToString(CultureInfo.InvariantCulture);
            case StorageKind.String:
                return value.AsString();
            case StorageKind.List:
                var list = new JsonArray();
                for (var i = 0; i < value.ListLength; i++)
                {
                    list.Add(value.ListElement switch
                    {
                        StorageKind.Bool => JsonValue.Create(value.AsBoolList()[i]),
                        StorageKind.Int64 => JsonValue.Create(value.AsInt64List()[i]),
                        StorageKind.Float64 => ToJson(ScalarValue.FromFloat64(value.AsFloat64List()[i])),
                        _ => JsonValue.Create(value.AsStringList()[i]),
                    });
                }

                return list;
            default:
                return value.ToString();
        }
    }

    private static JsonObject Expressions(ImmutableArray<(string Name, Expressions.CompiledExpression Expression)> expressions)
    {
        var document = new JsonObject();
        foreach (var (name, expression) in expressions)
        {
            document[name] = expression.Source;
        }

        return document;
    }

    private JsonArray Features(CompiledTap tap)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var feature in tap.ReadFeatures)
        {
            names.Add(Plan.Features.Features[feature].Definition.Name);
        }

        foreach (var feature in tap.ReadContextFeatures)
        {
            names.Add(Plan.Features.Features[feature].Definition.Name);
        }

        var array = new JsonArray();
        foreach (var name in names)
        {
            array.Add(name);
        }

        return array;
    }
}
