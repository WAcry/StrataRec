using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Hosting.Plans;
using StrataRec.Pipeline.Planning;

namespace StrataRec.CommandLine.Commands;

/// <summary>
/// <c>stratarec diff</c> (design 13.4 item 3): the semantic difference of two configurations by arm combination, from
/// their compiled plans (<see cref="PlanDiff"/>), instead of a line diff of their JSON. Exit codes follow diff(1).
/// </summary>
internal static class DiffCommand
{
    private const string Description =
        "Compare two configurations by arm combination: the nodes (merged definitions), parameters, taps, funnel groups and output " +
        "a request runs, and the layers, experiments and arms either side lacks. Exits with 0 when nothing differs, 1 when something does, 2 on errors.\n" +
        "--format json: { from: {ref, pipeline}, to: {ref, pipeline}, differences: bool, " +
        "structure: [{kind: layer|experiment|arm, id, change: added|removed|moved, from_layer, to_layer}], compared: [combination], " +
        "combinations: [{combination, arms: [arm], missing_on?: from|to, reason?, changes: [{pointer, change: added|removed|changed, from?, to?}]}] }. " +
        "A pointer is RFC 6901 into the combination's plan: /nodes/<id>/..., /params/<leaf>, /taps/<id>/..., /funnel_groups/<group>/..., /output/... " +
        "(explain-plan --arms --format json shows the same members). Only combinations that differ are listed; 'compared' names every one compared.";

    public static Command Create(PlanEnvironment provided)
    {
        var from = new Option<string?>("--from")
        {
            Description = "The configuration before: a workspace directory, a manifest content id (sha256:<hex>) in --store, or a manifest file. " +
                          "Defaults to the manifest --to names as 'previous'.",
        };
        var to = new Option<string>("--to") { Description = "The configuration after, in the same forms as --from.", Required = true };
        var store = CommonOptions.Store();
        var environment = CommonOptions.Environment();
        environment.Description = "Environment of a workspace side: selects releases/<pipeline>.<env>.jsonc and bindings/<env>.jsonc. A manifest names its own.";
        var pipeline = CommonOptions.Pipeline();
        var format = CommonOptions.Format();
        var arms = new Option<string[]>("--arms", "-a")
        {
            Description = "Compare exactly this combination (experiment/arm, comma or space separated), as explain-plan --arms selects it, " +
                          "instead of the baseline and each arm alone.",
            AllowMultipleArgumentsPerToken = true,
        };
        var command = new Command("diff", Description) { from, to, store, environment, pipeline, format, arms };
        command.SetAction((parse, cancellationToken) => RunAsync(provided, parse.GetValue(from), parse.GetValue(to)!, parse.GetValue(store),
            parse.GetValue(environment)!, parse.GetValue(pipeline), parse.GetValue(arms) ?? Array.Empty<string>(), parse.GetValue(format)!,
            parse.InvocationConfiguration.Output, cancellationToken, parse.InvocationConfiguration.Error));
        return command;
    }

    /// <param name="from">The configuration before; null for the <c>previous</c> manifest of <paramref name="to"/>.</param>
    /// <param name="errors">Where diagnostics go; standard error by default.</param>
    /// <param name="provided">What the binary provides to plans (<see cref="StrataRecCommandLine"/>).</param>
    internal static async Task<int> RunAsync(
        PlanEnvironment provided, string? from, string to, string? store, string environment, string? pipeline, string[] armArgs, string format, TextWriter output,
        CancellationToken cancellationToken, TextWriter? errors = null)
    {
        errors ??= Console.Error;
        var diagnostics = new DiagnosticBag();
        var toRef = ConfigRef.Parse(to, store, "--to", diagnostics, provided);
        var fromRef = from is null ? null : ConfigRef.Parse(from, store, "--from", diagnostics, provided);
        if (toRef is not null && from is null)
        {
            fromRef = await PreviousAsync(provided, toRef, store, diagnostics, cancellationToken).ConfigureAwait(false);
        }

        if (toRef is null || fromRef is null || diagnostics.HasErrors)
        {
            Output.Diagnostics(diagnostics.Items, format, errors);
            return 2;
        }

        // Both sides compile before either is reported: a broken side does not hide the other's errors.
        var fromPlan = await CompileAsync(fromRef, "--from", environment, pipeline, diagnostics, cancellationToken).ConfigureAwait(false);
        var toPlan = await CompileAsync(toRef, "--to", environment, pipeline, diagnostics, cancellationToken).ConfigureAwait(false);
        if (fromPlan is null || toPlan is null)
        {
            var locator = new DiagnosticLocator();
            if (!fromRef.IsManifest)
            {
                locator.Add("--from", fromRef.Text);
            }

            if (!toRef.IsManifest)
            {
                locator.Add("--to", toRef.Text);
            }

            Output.Diagnostics(diagnostics.Items, format, errors, locator: locator);
            return 2;
        }

        var arms = ExplainPlanCommand.SplitArms(armArgs);
        PlanDiff diff;
        try
        {
            diff = PlanDiff.Compare(fromPlan, toPlan, arms);
        }
        catch (ArgumentException ex)
        {
            errors.WriteLine(ex.Message);
            return 2;
        }

        if (format == "json")
        {
            output.WriteLine(Output.ToJson(json => WriteJson(json, diff, fromRef, fromPlan, toRef, toPlan)));
        }
        else
        {
            WriteText(output, diff, fromRef, fromPlan, toRef, toPlan);
        }

        return diff.HasDifferences ? 1 : 0;
    }

    /// <summary>The manifest <paramref name="to"/> names as <c>previous</c> (design 10.2: kept for audit and diff), in the same store.</summary>
    private static async Task<ConfigRef?> PreviousAsync(PlanEnvironment provided, ConfigRef to, string? store, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        if (!to.IsManifest)
        {
            diagnostics.Error("FROM_REQUIRED", "--from", string.Empty, $"--to '{to}' is a workspace, which names no previous manifest to compare with.",
                "pass --from <workspace|sha256:id|manifest file>");
            return null;
        }

        if (await to.ReadManifestAsync(diagnostics, cancellationToken).ConfigureAwait(false) is not { } read)
        {
            return null;
        }

        if (read.Manifest.Previous is not { } previous)
        {
            diagnostics.Error("MANIFEST_NO_PREVIOUS", "manifest:" + read.Id, "/previous", "The manifest names no previous manifest to compare with.",
                "pass --from <workspace|sha256:id|manifest file>");
            return null;
        }

        if (!ContentHash.IsValid(previous))
        {
            diagnostics.Error("MANIFEST_PREVIOUS_INVALID", "manifest:" + read.Id, "/previous", $"'{previous}' is not a manifest content id.",
                "previous holds the sha256:<hex> id of the manifest this one replaced; pass --from explicitly");
            return null;
        }

        return ConfigRef.Parse(previous, store, "/previous", diagnostics, provided);
    }

    /// <summary>Compiles one side; its diagnostics are labelled with the side (<c>--from</c>, <c>--to</c>), which both sides' artifacts share.</summary>
    private static async Task<ExecutionPlan?> CompileAsync(
        ConfigRef reference, string side, string environment, string? pipeline, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        var own = new DiagnosticBag();
        var compilation = await reference.CompileAsync(environment, pipeline, own, cancellationToken).ConfigureAwait(false);
        foreach (var d in own.Items)
        {
            diagnostics.Add(d with { Artifact = side + " " + d.Artifact });
        }

        return compilation?.Plan;
    }

    private static void WriteText(TextWriter o, PlanDiff diff, ConfigRef fromRef, ExecutionPlan from, ConfigRef toRef, ExecutionPlan to)
    {
        o.WriteLine($"--- {fromRef} (pipeline {from.PipelineId})");
        o.WriteLine($"+++ {toRef} (pipeline {to.PipelineId})");
        if (diff.Structure.Count > 0)
        {
            o.WriteLine();
            o.WriteLine("structure:");
            foreach (var change in diff.Structure)
            {
                o.WriteLine(change switch
                {
                    { Change: StructureChangeKind.Moved } => $"  ~ {change.Kind} {change.Id}: layer {change.FromLayer} -> {change.ToLayer}",
                    { Kind: "layer" } => $"  {Marker(change.Change)} layer {change.Id}",
                    _ => $"  {Marker(change.Change)} {change.Kind} {change.Id} (layer {change.FromLayer ?? change.ToLayer})",
                });
            }
        }

        foreach (var combination in diff.Combinations)
        {
            o.WriteLine();
            if (combination.MissingOn is not null)
            {
                o.WriteLine($"{combination.Label}: exists only in the {(combination.MissingOn == "from" ? "to" : "from")} configuration ({combination.MissingReason}); not compared");
                continue;
            }

            o.WriteLine($"{combination.Label}:");
            string? entry = null;
            foreach (var change in combination.Changes)
            {
                var (name, rest) = Entry(change.Pointer);
                if (rest.Length == 0)
                {
                    // The entry itself: a node, tap or group added or removed, or a parameter's value.
                    entry = null;
                    o.WriteLine($"  {Marker(change.Kind)} {name}: {Values(change)}");
                    continue;
                }

                if (entry != name)
                {
                    entry = name;
                    o.WriteLine($"  ~ {name}");
                }

                o.WriteLine($"      {Marker(change.Kind)} {rest}: {Values(change)}");
            }
        }

        var differing = 0;
        foreach (var combination in diff.Combinations)
        {
            differing += combination.MissingOn is null ? 1 : 0;
        }

        var oneSided = diff.Combinations.Count - differing;
        o.WriteLine();
        o.WriteLine(diff.HasDifferences
            ? $"{diff.Compared.Count} combination(s) compared: {differing} differ" +
              (oneSided > 0 ? $", {oneSided} exist on one side only" : string.Empty) +
              (diff.Structure.Count > 0 ? $"; {diff.Structure.Count} structural change(s)" : string.Empty)
            : $"no differences in {diff.Compared.Count} combination(s): {string.Join(", ", diff.Compared)}");

        static string Values(JsonChange change) => change.Kind switch
        {
            JsonChangeKind.Added => Output.Compact(change.After),
            JsonChangeKind.Removed => Output.Compact(change.Before),
            _ => $"{Output.Compact(change.Before)} -> {Output.Compact(change.After)}",
        };
    }

    /// <summary>The entry a change belongs to (<c>node fusion</c>, <c>param final_top_k</c>, <c>output</c>) and its pointer inside the entry.</summary>
    private static (string Entry, string Inner) Entry(string pointer)
    {
        var tokens = JsonPointer.Parse(pointer).Tokens;
        var section = tokens[0] switch
        {
            "nodes" => "node",
            "params" => "param",
            "taps" => "tap",
            "funnel_groups" => "funnel group",
            var other => other,
        };
        var inner = section == "output" ? 1 : 2;
        var name = section == "output" || tokens.Length < 2 ? section : section + " " + tokens[1];
        var rest = new System.Text.StringBuilder();
        for (var i = inner; i < tokens.Length; i++)
        {
            rest.Append('/').Append(JsonPointer.Escape(tokens[i]));
        }

        return (name, rest.ToString());
    }

    private static string Marker(JsonChangeKind kind) => kind switch
    {
        JsonChangeKind.Added => "+",
        JsonChangeKind.Removed => "-",
        _ => "~",
    };

    private static string Marker(StructureChangeKind kind) => kind switch
    {
        StructureChangeKind.Added => "+",
        StructureChangeKind.Removed => "-",
        _ => "~",
    };

    private static void WriteJson(Utf8JsonWriter json, PlanDiff diff, ConfigRef fromRef, ExecutionPlan from, ConfigRef toRef, ExecutionPlan to)
    {
        json.WriteStartObject();
        WriteSide(json, "from", fromRef, from);
        WriteSide(json, "to", toRef, to);
        json.WriteBoolean("differences", diff.HasDifferences);
        json.WriteStartArray("structure");
        foreach (var change in diff.Structure)
        {
            json.WriteStartObject();
            json.WriteString("kind", change.Kind);
            json.WriteString("id", change.Id);
            json.WriteString("change", change.Change.ToString().ToLowerInvariant());
            WriteNullable(json, "from_layer", change.FromLayer);
            WriteNullable(json, "to_layer", change.ToLayer);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        ReplayCommand.WriteStrings(json, "compared", diff.Compared);
        json.WriteStartArray("combinations");
        foreach (var combination in diff.Combinations)
        {
            json.WriteStartObject();
            json.WriteString("combination", combination.Label);
            ReplayCommand.WriteStrings(json, "arms", combination.Arms);
            if (combination.MissingOn is not null)
            {
                json.WriteString("missing_on", combination.MissingOn);
                json.WriteString("reason", combination.MissingReason);
            }

            json.WriteStartArray("changes");
            foreach (var change in combination.Changes)
            {
                json.WriteStartObject();
                json.WriteString("pointer", change.Pointer);
                json.WriteString("change", change.Kind.ToString().ToLowerInvariant());
                if (change.Kind != JsonChangeKind.Added)
                {
                    json.WritePropertyName("from");
                    WriteNode(json, change.Before);
                }

                if (change.Kind != JsonChangeKind.Removed)
                {
                    json.WritePropertyName("to");
                    WriteNode(json, change.After);
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void WriteSide(Utf8JsonWriter json, string name, ConfigRef reference, ExecutionPlan plan)
    {
        json.WriteStartObject(name);
        json.WriteString("ref", reference.Text);
        json.WriteString("pipeline", plan.PipelineId);
        json.WriteEndObject();
    }

    private static void WriteNode(Utf8JsonWriter json, JsonNode? node)
    {
        if (node is null)
        {
            json.WriteNullValue();
        }
        else
        {
            node.WriteTo(json);
        }
    }

    private static void WriteNullable(Utf8JsonWriter json, string name, string? value)
    {
        if (value is null)
        {
            json.WriteNull(name);
        }
        else
        {
            json.WriteString(name, value);
        }
    }
}
