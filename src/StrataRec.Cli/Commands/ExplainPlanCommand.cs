using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Cli.Commands;

/// <summary>
/// <c>stratarec explain-plan</c> (design 13.4): the unified plan's timeline, node variants, experiment sites and
/// parameter overrides; with <c>--arms</c>, exactly what a request with those arms executes.
/// </summary>
internal static class ExplainPlanCommand
{
    public static Command Create()
    {
        var config = CommonOptions.Config();
        var environment = CommonOptions.Environment();
        var pipeline = CommonOptions.Pipeline();
        var format = CommonOptions.Format();
        var arms = new Option<string[]>("--arms", "-a")
        {
            Description = "Show the plan a request with these arms executes (experiment/arm, comma or space separated).",
            AllowMultipleArgumentsPerToken = true,
        };
        var command = new Command("explain-plan", "Explain the compiled plan: timeline, variants, sites and parameters.") { config, environment, pipeline, format, arms };
        command.SetAction(parse => Run(parse.GetValue(config)!, parse.GetValue(environment)!, parse.GetValue(pipeline), parse.GetValue(format)!,
            parse.GetValue(arms) ?? Array.Empty<string>(), Console.Out));
        return command;
    }

    internal static int Run(string config, string environment, string? pipeline, string format, string[] armArgs, TextWriter output)
    {
        var diagnostics = new DiagnosticBag();
        var (_, _, compilation) = CommonOptions.Compile(config, environment, pipeline, diagnostics);
        if (compilation?.Plan is not { } plan)
        {
            return Output.Diagnostics(diagnostics.Items, format, Console.Error) == 0 ? 1 : 1;
        }

        var armIds = SplitArms(armArgs);
        RequestArms? selected = null;
        if (armIds.Count > 0)
        {
            try
            {
                selected = plan.ArmsFromIds(armIds);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
        }

        if (format == "json")
        {
            output.WriteLine(Output.ToJson(json => WriteJson(json, plan, compilation.Report, selected)));
        }
        else
        {
            WriteText(output, plan, compilation.Report, selected, armIds);
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

    private static void WriteText(TextWriter o, ExecutionPlan plan, PlanReport report, RequestArms? selected, List<string> armIds)
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

                o.WriteLine($"  {e.Id,-32} layer {plan.Layers[e.LayerIndex].Name,-10} arms {string.Join(", ", arms)}");
            }
        }

        o.WriteLine(selected is null ? "timeline:" : $"timeline for {string.Join(" + ", armIds)}:");
        foreach (var slot in plan.Timeline)
        {
            if (selected is { } arms)
            {
                var node = slot.Select(arms);
                if (node is not null)
                {
                    o.WriteLine($"  {slot.Order,5}  {slot.NodeId,-24} {node.Descriptor.Id,-28}{When(node)}{Sites(plan, slot)}");
                }

                continue;
            }

            o.WriteLine($"  {slot.Order,5}  {slot.NodeId,-24} {Operators(slot.Variants),-28} {Variants(plan, slot)}{Sites(plan, slot)}");
        }

        if (plan.Fallbacks.Count > 0)
        {
            o.WriteLine("fallbacks:");
            foreach (var (id, slot) in plan.Fallbacks)
            {
                o.WriteLine($"         {id,-24} {Operators(slot.Variants),-28} {Variants(plan, slot)}{Sites(plan, slot)}");
            }
        }

        o.WriteLine("parameters:");
        var snapshot = selected is { } s ? plan.Params.Snapshot(s) : null;
        for (var i = 0; i < plan.Schema.Params.Count; i++)
        {
            var leaf = plan.Schema.Params[i];
            if (snapshot is not null)
            {
                var changed = !snapshot[i].Equals(plan.Params.Baseline[i]) ? "   (overridden)" : string.Empty;
                o.WriteLine($"  {leaf.Name,-28} {Format(snapshot[i])}{changed}");
                continue;
            }

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

            o.WriteLine($"  {leaf.Name,-28} {Format(plan.Params.Baseline[i]),-12}{(overrides.Count > 0 ? "  " + string.Join("; ", overrides) : string.Empty)}");
        }

        if (selected is null)
        {
            var total = 0;
            foreach (var node in report.Nodes)
            {
                total += node.Variants;
            }

            o.WriteLine($"{report.Nodes.Count} nodes, {total} compiled variants, {report.CombinationsChecked} arm combinations validated");
        }

        foreach (var warning in plan.Warnings)
        {
            o.WriteLine(warning.ToString());
        }
    }

    private static string When(CompiledNode node) =>
        node.Definition["when"] is { } when ? $" when {when.GetValue<string>()}" : string.Empty;

    private static string Operators(CompiledNode?[] variants)
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

        var text = $"{distinct.Count} variant(s) over {slot.Variants.Length} combination(s) of {string.Join(", ", layers)}";
        return absent > 0 ? text + $", absent in {absent}" : text;
    }

    private static string Sites(ExecutionPlan plan, TimelineSlot slot)
    {
        if (slot.SiteExperiments.Length == 0)
        {
            return string.Empty;
        }

        var sites = new List<string>();
        for (var i = 0; i < slot.SiteExperiments.Length; i++)
        {
            var id = plan.Experiments[slot.SiteExperiments[i]].Id;
            sites.Add(slot.SiteConditions[i] is { } c ? $"{id} if {c.Describe()}" : id);
        }

        return "  site: " + string.Join("; ", sites);
    }

    private static string Format(in ScalarValue value) => value.IsMissing ? "(none)" : value.ToString();

    private static void WriteJson(Utf8JsonWriter json, ExecutionPlan plan, PlanReport report, RequestArms? selected)
    {
        json.WriteStartObject();
        json.WriteString("pipeline_id", plan.PipelineId);
        json.WriteString("manifest", plan.ManifestId);
        json.WriteNumber("compile_ms", Math.Round(plan.CompileTime.TotalMilliseconds, 2));
        json.WriteStartArray("timeline");
        foreach (var slot in plan.Timeline)
        {
            var node = selected is { } arms ? slot.Select(arms) : null;
            if (selected is not null && node is null)
            {
                continue;
            }

            json.WriteStartObject();
            json.WriteNumber("order", slot.Order);
            json.WriteString("node", slot.NodeId);
            json.WriteString("op", node?.Descriptor.Id ?? Operators(slot.Variants));
            if (node is not null)
            {
                json.WritePropertyName("definition");
                node.Definition.WriteTo(json);
            }
            else
            {
                json.WriteNumber("combinations", slot.Variants.Length);
                json.WriteStartArray("layers");
                foreach (var l in slot.TouchingLayers)
                {
                    json.WriteStringValue(plan.Layers[l].Name);
                }

                json.WriteEndArray();
            }

            json.WriteStartArray("sites");
            foreach (var e in slot.SiteExperiments)
            {
                json.WriteStringValue(plan.Experiments[e].Id);
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteStartObject("params");
        var snapshot = selected is { } s ? plan.Params.Snapshot(s) : plan.Params.Baseline;
        for (var i = 0; i < plan.Schema.Params.Count; i++)
        {
            json.WritePropertyName(plan.Schema.Params[i].Name);
            Output.WriteValue(json, snapshot[i]);
        }

        json.WriteEndObject();
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
        json.WriteNumber("combinations_checked", report.CombinationsChecked);
        json.WriteStartArray("warnings");
        foreach (var warning in plan.Warnings)
        {
            Output.WriteDiagnostic(json, warning);
        }

        json.WriteEndArray();
        json.WriteEndObject();
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
