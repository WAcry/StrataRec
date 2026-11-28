using System.Collections.Immutable;
using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Plans;
using StrataRec.Pipeline.Planning;

namespace StrataRec.CommandLine.Commands;

/// <summary>
/// <c>stratarec explain-plan</c>: the unified plan's timeline, node variants, experiment sites and
/// parameter overrides; with <c>--arms</c>, exactly what a request with those arms executes (<see cref="EffectivePlan"/>,
/// the view <c>diff</c> compares). A plan without experiments has one combination, which is what it shows: there are
/// no variants or sites to survey, and what each node reads and writes is in that view. Reads a workspace or a release
/// manifest (<see cref="ConfigRef"/>).
/// </summary>
internal static class ExplainPlanCommand
{
    public static Command Create(PlanEnvironment provided)
    {
        var config = CommonOptions.ConfigOrManifest();
        var manifest = CommonOptions.Manifest();
        var store = CommonOptions.Store();
        var environment = CommonOptions.Environment();
        var pipeline = CommonOptions.Pipeline();
        var format = CommonOptions.Format();
        var arms = new Option<string[]>("--arms", "-a")
        {
            Description = "Show the plan a request with these arms executes (experiment/arm, comma or space separated): " +
                          "its nodes with their merged definitions and the columns each reads and writes, parameters, taps, funnel groups and output. " +
                          "A plan without experiments is shown this way without it.",
            AllowMultipleArgumentsPerToken = true,
        };
        var command = new Command("explain-plan", "Explain the compiled plan of a workspace or a release manifest: timeline, variants, sites and parameters; " +
            "for a plan without experiments, the one plan its requests execute (see --arms). " +
            "Exits with 1 when the configuration does not load or compile, and 2 on usage errors.")
        {
            config, manifest, store, environment, pipeline, format, arms,
        };
        command.SetAction((parse, cancellationToken) => RunAsync(provided, parse.GetValue(config), parse.GetValue(environment)!, parse.GetValue(pipeline),
            parse.GetValue(format)!, parse.GetValue(arms) ?? Array.Empty<string>(), parse.InvocationConfiguration.Output, cancellationToken, parse.GetValue(manifest),
            parse.GetValue(store), parse.InvocationConfiguration.Error));
        return command;
    }

    /// <param name="config">The workspace (<c>-c</c>); with <paramref name="manifest"/> null too, the workspace <c>config</c>.</param>
    /// <param name="manifest">A manifest id or file (<c>--manifest</c>), read with <paramref name="store"/>.</param>
    /// <param name="errors">Where diagnostics go; standard error by default.</param>
    /// <param name="provided">What the binary provides to plans (<see cref="StrataRecCommandLine"/>).</param>
    internal static async Task<int> RunAsync(
        PlanEnvironment provided, string? config, string environment, string? pipeline, string format, string[] armArgs, TextWriter output,
        CancellationToken cancellationToken, string? manifest = null, string? store = null, TextWriter? errors = null)
    {
        errors ??= Console.Error;
        var diagnostics = new DiagnosticBag();
        var (reference, ok) = ConfigRef.Select(config, manifest, store, "config", diagnostics, provided);
        if (!ok)
        {
            Output.Diagnostics(diagnostics.Items, format, errors);
            return 2;
        }

        var compilation = await reference!.CompileAsync(environment, pipeline, diagnostics, cancellationToken).ConfigureAwait(false);
        if (compilation?.Plan is not { } plan)
        {
            Output.Diagnostics(diagnostics.Items, format, errors, locator: reference.Locator());
            if (format == "text" && reference.IsManifest && ConfigRef.CurrentDirectoryNote(diagnostics.Items) is { } note)
            {
                errors.WriteLine(note);
            }

            return 1;
        }

        var armIds = SplitArms(armArgs);
        EffectivePlan? selected = null;
        if (armIds.Count > 0 || plan.Experiments.Length == 0)
        {
            try
            {
                var arms = plan.ArmsFromIds(armIds);
                if (plan.NeverRunTogether(arms) is { } never)
                {
                    errors.WriteLine($"These arms never run together: {never}.");
                    return 2;
                }

                selected = EffectivePlan.Create(plan, arms);
            }
            catch (ArgumentException ex)
            {
                errors.WriteLine(ex.Message);
                return 2;
            }

            try
            {
                _ = selected.ToDocument(); // what the output prints: a value it cannot print is a diagnostic, before any output
            }
            catch (JsonCanonicalizationException ex)
            {
                diagnostics.Error("PLAN_NOT_CANONICAL", "--arms", ex.Pointer.ToString(),
                    $"The plan of {PlanDiff.Label(selected.ArmIds)} holds a value RFC 8785 cannot hold exactly, and plans are shown in that form: {ex.Reason}",
                    ex.Hint ?? "write the value so that RFC 8785 holds it exactly");
                Output.Diagnostics(diagnostics.Items, format, errors, locator: reference.Locator());
                return 1;
            }
        }

        if (format == "json")
        {
            output.WriteLine(Output.ToJson(json => WriteJson(json, plan, compilation.Report, selected)));
        }
        else
        {
            WriteText(output, plan, compilation.Report, selected, reference.Locator());
        }

        return 0;
    }

    internal static List<string> SplitArms(string[] args)
    {
        var ids = new List<string>();
        foreach (var arg in args)
        {
            foreach (var part in arg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                ids.Add(part);
            }
        }

        return ids;
    }

    private static void WriteText(TextWriter o, ExecutionPlan plan, PlanReport report, EffectivePlan? selected, DiagnosticLocator? locator)
    {
        o.WriteLine($"pipeline {plan.PipelineId}  ({plan.ManifestId}, compiled in {plan.CompileTime.TotalMilliseconds:0.0} ms)");
        if (plan.Experiments.Length > 0)
        {
            o.WriteLine("experiments:");
            foreach (var e in plan.Experiments)
            {
                var arms = new List<string>();
                foreach (var a in e.ArmIndices)
                {
                    arms.Add(plan.Arms[a].ArmName);
                }

                // Who reviews it (policy.jsonc owners of what its arms touch); per arm in --json.
                var owners = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var arm in report.Arms)
                {
                    if (arm.Id.StartsWith(e.Id + "/", StringComparison.Ordinal))
                    {
                        owners.UnionWith(arm.Owners);
                    }
                }

                o.WriteLine($"  {e.Id,-32} layer {plan.Layers[e.LayerIndex].Name,-10} arms {string.Join(", ", arms)}{(owners.Count > 0 ? "  owners " + string.Join(", ", owners) : string.Empty)}");
            }

            // What a yield costs and what it buys: the review that it is meant.
            foreach (var y in report.Yields)
            {
                o.WriteLine($"  {y.Experiment} yields to {y.To}: does not run on {y.To}'s traffic; keeps apart "
                    + (y.Resolves.Count > 0 ? string.Join(", ", y.Resolves) : "nothing (YIELD_UNUSED)"));
            }
        }

        if (selected is not null)
        {
            WriteSelectedText(o, plan, selected, locator);
            return;
        }

        o.WriteLine("timeline:");
        foreach (var slot in plan.Timeline)
        {
            o.WriteLine($"  {slot.Order,5}  {slot.NodeId,-24} {Operators(slot.Variants),-28} {Variants(plan, slot)}{Sites(plan, slot, fallback: false)}");
        }

        if (plan.Fallbacks.Count > 0)
        {
            o.WriteLine("fallbacks:");
            foreach (var (id, slot) in plan.Fallbacks)
            {
                o.WriteLine($"         {id,-24} {Operators(slot.Variants),-28} {Variants(plan, slot)}{Sites(plan, slot, fallback: true)}");
            }
        }

        o.WriteLine("parameters:");
        for (var i = 0; i < plan.Schema.Params.Count; i++)
        {
            var leaf = plan.Schema.Params[i];
            var overrides = new List<string>();
            for (var arm = 0; arm < plan.Params.OverridesByArm.Length; arm++)
            {
                foreach (var (param, value) in plan.Params.OverridesByArm[arm])
                {
                    if (param == i)
                    {
                        overrides.Add($"{plan.Arms[arm].Id} → {Format(value)}");
                    }
                }
            }

            o.WriteLine($"  {leaf.Name,-28} {Format(plan.Params.Baseline[i]),-12}{(overrides.Count > 0 ? "  " + string.Join("; ", overrides) : string.Empty)}".TrimEnd());
        }

        var total = 0;
        foreach (var node in report.Nodes)
        {
            total += node.Variants;
        }

        o.WriteLine($"{Output.Count(report.Nodes.Count, "node")}, {Output.Count(total, "compiled variant")}, {Output.Count(report.CombinationsChecked, "arm combination")} validated");
        foreach (var warning in plan.Warnings)
        {
            o.WriteLine(Output.Text(warning, locator?.Locate(warning)));
        }
    }

    /// <summary>The plan of one arm combination (<see cref="EffectivePlan"/>): what <c>diff</c> compares, in the same terms.</summary>
    private static void WriteSelectedText(TextWriter o, ExecutionPlan plan, EffectivePlan selected, DiagnosticLocator? locator)
    {
        o.WriteLine($"timeline for {PlanDiff.Label(selected.ArmIds)}:");
        var definitions = selected.NodesDocument();
        var fallbacks = false;
        foreach (var node in selected.Nodes)
        {
            if (node.IsFallback && !fallbacks)
            {
                o.WriteLine("fallbacks:");
                fallbacks = true;
            }

            var order = node.Node.Order is { } at ? at.ToString(CultureInfo.InvariantCulture) : string.Empty;
            o.WriteLine($"  {order,5}  {node.Id,-24} {node.Node.Descriptor.Id,-28}{When(node.Node)}{Sites(plan, node.Slot, node.IsFallback, selected.Arms)}".TrimEnd());
            foreach (var (name, value) in definitions[node.Id]!.AsObject())
            {
                if (name is not ("order" or "op" or "when"))
                {
                    o.WriteLine($"         {name,-24} {Output.Compact(value)}");
                }
            }

            // The data flow of this plan: which node a column comes from and which nodes read it.
            var (reads, writes) = Flow(plan, node.Node);
            o.WriteLine($"         {"<- reads",-24} {(reads.Count == 0 ? "(no columns)" : string.Join(", ", reads))}");
            o.WriteLine($"         {"-> writes",-24} {(writes.Count == 0 ? "(no columns)" : string.Join(", ", writes))}");
        }

        o.WriteLine("parameters:");
        foreach (var param in selected.Params)
        {
            o.WriteLine($"  {param.Leaf.Name,-28} {Format(param.Value)}{(param.Overridden ? "   (overridden)" : string.Empty)}");
        }

        if (selected.Taps.Count > 0)
        {
            o.WriteLine("taps:");
            foreach (var (tap, group) in selected.Taps)
            {
                var rows = new List<string> { tap.Scope.ToString().ToLowerInvariant() + " rows" };
                if (tap.Rate < 1)
                {
                    rows.Add("rate " + Invariant(tap.Rate));
                }

                if (tap.MaxRows is { } max)
                {
                    rows.Add("at most " + max.ToString(CultureInfo.InvariantCulture));
                }

                if (tap.Filter is { } filter)
                {
                    rows.Add("filter " + filter.Source);
                }

                var attach = tap.AttachAfter is { } after ? "after " + after : "output";
                var delivery = tap.Delivery == Logging.LogDelivery.Durable ? "durable" : "best effort";
                o.WriteLine($"  {tap.TapId,-28} {attach}, group {group.Name}, {delivery}, {string.Join(", ", rows)}");
                if (tap.Context.Length > 0)
                {
                    o.WriteLine($"  {"",-28} context  {Expressions(tap.Context)}");
                }

                o.WriteLine($"  {"",-28} columns  {Expressions(tap.Columns)}");
            }

            o.WriteLine("funnel groups:");
            foreach (var (name, group) in selected.GroupsDocument())
            {
                o.WriteLine($"  {name,-28} rate {Output.Compact(group!["rate"])}, key {Output.Compact(group["key"])}");
            }
        }

        o.WriteLine("output:");
        o.WriteLine($"  {"limit_param",-28} {selected.Output.Limit.Name}");
        o.WriteLine($"  {"columns",-28} {Expressions(selected.Output.Columns)}");
        if (selected.Output.Values.Length > 0)
        {
            o.WriteLine($"  {"values",-28} {Expressions(selected.Output.Values)}");
        }

        foreach (var warning in plan.Warnings)
        {
            o.WriteLine(Output.Text(warning, locator?.Locate(warning)));
        }

        static string Expressions(ImmutableArray<(string Name, Expressions.CompiledExpression Expression)> expressions)
        {
            var parts = new List<string>(expressions.Length);
            foreach (var (name, expression) in expressions)
            {
                parts.Add($"{name} = {expression.Source}");
            }

            return parts.Count == 0 ? "(none)" : string.Join("; ", parts);
        }
    }

    /// <summary>
    /// The columns a node reads (but the entity id, which every row has), in name order, and those it writes, as the
    /// compiled node declares them: what the executor fetches before it, and versions after it. Reads are sorted by name,
    /// not listed by column id: a feature's id is the order the plan first read it in, which says nothing. The
    /// request columns it reads and writes follow, as <c>req.&lt;name&gt;</c>, and the pipelines it calls, as
    /// <c>pipeline:&lt;id&gt;</c>.
    /// </summary>
    private static (List<string> Reads, List<string> Writes) Flow(ExecutionPlan plan, CompiledNode node)
    {
        var columns = plan.Schema.Columns;
        var reads = new List<string>(node.ReadColumns.Length);
        foreach (var logical in node.ReadColumns)
        {
            if (columns[logical].Kind != ColumnKind.EntityId)
            {
                reads.Add(columns[logical].Name);
            }
        }

        reads.Sort(StringComparer.Ordinal);
        var requestReads = new List<string>(node.RequestReads.Length);
        foreach (var id in node.RequestReads)
        {
            requestReads.Add(plan.Schema.ContextFields[id].QualifiedName);
        }

        requestReads.Sort(StringComparer.Ordinal);
        reads.AddRange(requestReads);

        // The pipelines it calls, as what it reads last: pipeline:<id>.
        var called = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var call in node.Calls)
        {
            called.Add("pipeline:" + call.PipelineId);
        }

        reads.AddRange(called);

        var writes = new List<string>(node.Outputs.Length + node.RequestOutputs.Length);
        foreach (var output in node.Outputs)
        {
            writes.Add(output.Name);
        }

        foreach (var output in node.RequestOutputs)
        {
            writes.Add("req." + output.Name);
        }

        return (reads, writes);
    }

    private static string When(CompiledNode node) =>
        node.Definition.TryGetProperty("when", out var when) && when.ValueKind != JsonValueKind.Null ? $" when {when.GetString()}" : string.Empty;

    private static string Operators(ImmutableArray<CompiledNode?> variants)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var v in variants)
        {
            if (v is not null)
            {
                ids.Add(v.Descriptor.Id);
            }
        }

        return string.Join("|", ids);
    }

    private static string Variants(ExecutionPlan plan, TimelineSlot slot)
    {
        var distinct = new HashSet<CompiledNode>(ReferenceEqualityComparer.Instance);
        var absent = 0;
        foreach (var v in slot.Variants)
        {
            if (v is null)
            {
                absent++;
            }
            else
            {
                distinct.Add(v);
            }
        }

        if (slot.TouchingLayers.Length == 0)
        {
            return "1 variant";
        }

        var layers = new List<string>();
        foreach (var l in slot.TouchingLayers)
        {
            layers.Add(plan.Layers[l].Name);
        }

        var text = $"{Output.Count(distinct.Count, "variant")} over {Output.Count(slot.Variants.Length, "combination")} of {string.Join(", ", layers)}";
        return absent > 0 ? text + $", absent in {absent}" : text;
    }

    /// <summary>
    /// The experiments reaching a position triggers, and when: the OR of the <c>when</c> of each arm's node
    /// there, together with <paramref name="arms"/> in the other layers (the selected combination; none for the overview,
    /// where another layer's arm may still change it). A fallback triggers whenever it is invoked.
    /// </summary>
    private static string Sites(ExecutionPlan plan, TimelineSlot slot, bool fallback, RequestArms arms = default)
    {
        if (slot.SiteExperiments.Length == 0)
        {
            return string.Empty;
        }

        var sites = new List<string>();
        foreach (var e in slot.SiteExperiments)
        {
            var experiment = plan.Experiments[e];
            var conditions = new List<string>();
            var always = false;
            foreach (var arm in experiment.ArmIndices)
            {
                if (slot.SelectWith(arms, experiment.LayerIndex, arm) is not { } node)
                {
                    continue;
                }

                if (fallback || node.When is null)
                {
                    always = true;
                    break;
                }

                var text = node.When.Describe();
                if (!conditions.Contains(text))
                {
                    conditions.Add(text);
                }
            }

            if (conditions.Count > 1)
            {
                for (var c = 0; c < conditions.Count; c++)
                {
                    conditions[c] = "(" + conditions[c] + ")"; // each a whole condition: `a ? b : c || d` would read as `a ? b : (c || d)`
                }
            }

            sites.Add(always ? experiment.Id : conditions.Count == 0 ? $"{experiment.Id} never" : $"{experiment.Id} if {string.Join(" || ", conditions)}");
        }

        return "  site: " + string.Join("; ", sites);
    }

    private static string Format(in ScalarValue value) => value.IsMissing ? "(none)" : value.ToString();

    private static void WriteJson(Utf8JsonWriter json, ExecutionPlan plan, PlanReport report, EffectivePlan? selected)
    {
        json.WriteStartObject();
        json.WriteString("pipeline_id", plan.PipelineId);
        json.WriteString("manifest", plan.ManifestId);
        json.WriteNumber("compile_ms", Math.Round(plan.CompileTime.TotalMilliseconds, 2));
        if (selected is not null)
        {
            WriteSelectedJson(json, plan, selected);
        }
        else
        {
            json.WriteStartArray("timeline");
            foreach (var slot in plan.Timeline)
            {
                json.WriteStartObject();
                json.WriteNumber("order", slot.Order);
                json.WriteString("node", slot.NodeId);
                json.WriteString("op", Operators(slot.Variants));
                json.WriteNumber("combinations", slot.Variants.Length);
                json.WriteStartArray("layers");
                foreach (var l in slot.TouchingLayers)
                {
                    json.WriteStringValue(plan.Layers[l].Name);
                }

                json.WriteEndArray();
                WriteSites(json, plan, slot);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteStartObject("params");
            for (var i = 0; i < plan.Schema.Params.Count; i++)
            {
                json.WritePropertyName(plan.Schema.Params[i].Name);
                Output.WriteValue(json, plan.Params.Baseline[i]);
            }

            json.WriteEndObject();
        }

        json.WriteStartArray("arms");
        foreach (var arm in report.Arms)
        {
            json.WriteStartObject();
            json.WriteString("id", arm.Id);
            json.WriteString("layer", arm.Layer);
            WriteStrings(json, "footprint", arm.Paths);
            WriteStrings(json, "nodes", arm.Nodes);
            WriteStrings(json, "param_leaves", arm.ParamLeaves);
            WriteStrings(json, "owners", arm.Owners);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteStartArray("yields");
        foreach (var y in report.Yields)
        {
            json.WriteStartObject();
            json.WriteString("experiment", y.Experiment);
            json.WriteString("yields_to", y.To);
            WriteStrings(json, "resolves", y.Resolves);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteNumber("combinations_checked", report.CombinationsChecked);
        json.WriteStartArray("warnings");
        foreach (var warning in plan.Warnings)
        {
            Output.WriteDiagnostic(json, warning);
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    /// <summary>
    /// The plan of one arm combination: <c>combination</c>, <c>timeline</c> and <c>fallbacks</c> with each node's merged
    /// definition, and <c>params</c>, <c>taps</c>, <c>funnel_groups</c> and <c>output</c> exactly as
    /// <see cref="EffectivePlan.ToDocument"/> has them, so a <c>diff</c> pointer such as <c>/taps/served/columns/final</c>
    /// reads the same here.
    /// </summary>
    private static void WriteSelectedJson(Utf8JsonWriter json, ExecutionPlan plan, EffectivePlan selected)
    {
        WriteStrings(json, "combination", selected.ArmIds);
        var definitions = selected.NodesDocument();
        foreach (var fallbacks in new[] { false, true })
        {
            json.WriteStartArray(fallbacks ? "fallbacks" : "timeline");
            foreach (var node in selected.Nodes)
            {
                if (node.IsFallback != fallbacks)
                {
                    continue;
                }

                json.WriteStartObject();
                if (node.Node.Order is { } order)
                {
                    json.WriteNumber("order", order);
                }

                json.WriteString("node", node.Id);
                json.WriteString("op", node.Node.Descriptor.Id);
                json.WritePropertyName("definition");
                definitions[node.Id]!.WriteTo(json);
                var (reads, writes) = Flow(plan, node.Node);
                WriteStrings(json, "reads", reads);
                WriteStrings(json, "writes", writes);
                WriteSites(json, plan, node.Slot);
                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        json.WritePropertyName("params");
        selected.ParamsDocument().WriteTo(json);
        json.WritePropertyName("taps");
        selected.TapsDocument().WriteTo(json);
        json.WritePropertyName("funnel_groups");
        selected.GroupsDocument().WriteTo(json);
        json.WritePropertyName("output");
        selected.OutputDocument().WriteTo(json);
    }

    private static void WriteSites(Utf8JsonWriter json, ExecutionPlan plan, TimelineSlot slot)
    {
        json.WriteStartArray("sites");
        foreach (var e in slot.SiteExperiments)
        {
            json.WriteStringValue(plan.Experiments[e].Id);
        }

        json.WriteEndArray();
    }

    private static void WriteStrings(Utf8JsonWriter json, string name, IReadOnlyList<string> values)
    {
        json.WriteStartArray(name);
        foreach (var v in values)
        {
            json.WriteStringValue(v);
        }

        json.WriteEndArray();
    }

    internal static string Invariant(double value) => value.ToString(CultureInfo.InvariantCulture);
}
