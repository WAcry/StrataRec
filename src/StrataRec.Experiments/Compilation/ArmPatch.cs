using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;

namespace StrataRec.Experiments.Compilation;

/// <param name="Leaf">Flattened leaf name, e.g. <c>recall_quotas.cf</c>.</param>
/// <param name="Pointer">Where the value is written in the experiment artifact.</param>
internal readonly record struct ParamOverride(string Leaf, JsonNode? Value, string Pointer);

internal sealed class ArmPatch
{
    public required int Index { get; init; }

    /// <summary><c>experiment/arm</c>.</summary>
    public required string Id { get; init; }

    public required string ArmName { get; init; }

    public required int ExperimentIndex { get; init; }

    public required string ExperimentId { get; init; }

    public required int LayerIndex { get; init; }

    public required JsonObject Patch { get; init; }

    /// <summary>Diagnostic artifact label, e.g. <c>experiment:exp_mmr</c>.</summary>
    public required string Artifact { get; init; }

    /// <summary>Pointer of the arm's patch inside the experiment artifact, e.g. <c>/arms/mmr</c>.</summary>
    public required string PointerPrefix { get; init; }

    /// <summary>Path footprint with add-only no-ops removed.</summary>
    public List<PatchOperation> Footprint { get; } = new();

    public List<ParamOverride> ParamOverrides { get; } = new();

    /// <summary>Add-only additions by section name (<c>columns</c>, <c>features</c>, …) then entry name.</summary>
    public Dictionary<string, SortedDictionary<string, (JsonNode Value, string Pointer)>> Additions { get; } = new(StringComparer.Ordinal);

    public List<string> ExtraParamLeaves { get; } = new();

    /// <summary>Nodes with a footprint path under <c>/nodes/&lt;id&gt;</c> (they get variants).</summary>
    public SortedSet<string> TouchedNodes { get; } = new(StringComparer.Ordinal);

    public HashSet<string> AddedNodes { get; } = new(StringComparer.Ordinal);

    public SortedSet<string> TouchedTaps { get; } = new(StringComparer.Ordinal);

    /// <summary>False when the patch itself is invalid; such an arm is excluded from further checks.</summary>
    public bool Valid { get; set; } = true;

    public override string ToString() => Id;

    public bool Relates(string pointer) => JsonPointer.TryParse(pointer, out var parsed) && Relates(parsed);

    public bool Relates(JsonPointer parsed)
    {
        foreach (var op in Footprint)
        {
            if (op.Path.IsPrefixOf(parsed) || parsed.IsPrefixOf(op.Path))
            {
                return true;
            }
        }

        return false;
    }
}

internal static class ArmAnalyzer
{
    /// <summary>
    /// Node ids are plain identifiers (<c>[A-Za-z_][A-Za-z0-9_]*</c>): expressions name them in version references
    /// (<c>col.score.final@fusion</c>), where a '.' or '-' would read as field access or subtraction.
    /// </summary>
    public static bool IsValidNodeId(string id)
    {
        if (id.Length == 0 || !(char.IsAsciiLetter(id[0]) || id[0] == '_'))
        {
            return false;
        }

        foreach (var c in id)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    public const string NodeIdHint = "node ids are identifiers (letters, digits and '_', not starting with a digit) so that 'col.<column>@<node>' can name them";

    public static bool IsValidId(string id)
    {
        if (id.Length == 0 || !(char.IsAsciiLetter(id[0]) || id[0] == '_'))
        {
            return false;
        }

        foreach (var c in id)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    public static void Analyze(ArmPatch arm, PatchableView view, DiagnosticBag diagnostics)
    {
        var analysis = MergePatchAnalyzer.Analyze(view.Document, arm.Patch);
        foreach (var error in analysis.Errors)
        {
            diagnostics.Error(error.Code, arm.Artifact, arm.PointerPrefix + error.Path, error.Message, error.Hint);
            arm.Valid = false;
        }

        var paramOps = new List<PatchOperation>();
        foreach (var op in analysis.Footprint)
        {
            var pointer = arm.PointerPrefix + op.Path;
            var tokens = op.Path.Tokens;

            if (tokens.Length >= 1 && Array.IndexOf(SchemaSections, tokens[0]) >= 0)
            {
                if (op.Kind != PatchOpKind.Add || tokens.Length != 2)
                {
                    if (!IsNoOp(op, view))
                    {
                        AddOnlyError(arm, diagnostics, pointer, "/" + tokens[0], op);
                    }

                    continue;
                }

                if (!IsValidId(tokens[1]) && tokens[0] is "columns" or "request_columns" or "param_schema")
                {
                    diagnostics.Error("BAD_ID", arm.Artifact, pointer, $"'{tokens[1]}' is not a valid name.",
                        "names start with a letter or '_' and contain letters, digits, '_', '.' or '-'");
                    arm.Valid = false;
                    continue;
                }

                if (!arm.Additions.TryGetValue(tokens[0], out var entries))
                {
                    entries = new SortedDictionary<string, (JsonNode, string)>(StringComparer.Ordinal);
                    arm.Additions[tokens[0]] = entries;
                }

                entries[tokens[1]] = (op.Value!, pointer);
                arm.Footprint.Add(op);
                continue;
            }

            arm.Footprint.Add(op);
            switch (tokens.Length == 0 ? string.Empty : tokens[0])
            {
                case "params":
                    paramOps.Add(op);
                    break;
                case "nodes" when tokens.Length >= 2:
                    arm.TouchedNodes.Add(tokens[1]);
                    if (op.Kind == PatchOpKind.Add && tokens.Length == 2)
                    {
                        arm.AddedNodes.Add(tokens[1]);
                        if (!IsValidNodeId(tokens[1]))
                        {
                            diagnostics.Error("BAD_ID", arm.Artifact, pointer, $"'{tokens[1]}' is not a valid node id.", NodeIdHint);
                            arm.Valid = false;
                        }
                    }

                    break;
                case "log_taps" when tokens.Length >= 2:
                    arm.TouchedTaps.Add(tokens[1]);
                    break;
                case "nodes" or "log_taps" or "params":
                    diagnostics.Error("PATCH_NOT_ALLOWED", arm.Artifact, pointer, $"Experiments may not replace or delete the whole '/{tokens[0]}' section.",
                        "patch individual entries");
                    arm.Valid = false;
                    break;
                default:
                    diagnostics.Error("PATCH_NOT_ALLOWED", arm.Artifact, pointer, $"'{op.Path}' is not part of the patchable view.",
                        "experiments patch /params, /nodes, /log_taps and add to /columns, /request_columns, /param_schema, /features, /derived_features, /funnel_groups");
                    arm.Valid = false;
                    break;
            }
        }

        AnalyzeParams(arm, view, paramOps, diagnostics);
    }

    /// <summary>Sections compiled once into the shared plan schema; experiments may only add to them.</summary>
    internal static readonly string[] SchemaSections = { "columns", "request_columns", "param_schema", "features", "derived_features", "funnel_groups" };

    private static bool IsNoOp(PatchOperation op, PatchableView view) =>
        op.Kind == PatchOpKind.Replace && op.Path.TryEvaluate(view.Document, out var existing) && JsonEquals(existing, op.Value);

    private static void AddOnlyError(ArmPatch arm, DiagnosticBag diagnostics, string pointer, string root, PatchOperation op)
    {
        diagnostics.Error("PATCH_ADD_ONLY", arm.Artifact, pointer,
            $"'{root}' is add-only for experiments; this patch {(op.Kind == PatchOpKind.Delete ? "deletes" : "changes")} an existing entry.",
            "add an entry under a new name instead; one name has one meaning in logs and models");
        arm.Valid = false;
    }

    /// <summary>
    /// A leaf must be declared by the baseline schema or by a <c>param_schema</c> entry this arm adds; map parameters
    /// take new keys.
    /// </summary>
    private static void AnalyzeParams(ArmPatch arm, PatchableView view, List<PatchOperation> ops, DiagnosticBag diagnostics)
    {
        var baseSchema = (JsonObject)view.Document["param_schema"]!;
        arm.Additions.TryGetValue("param_schema", out var addedSchema);
        foreach (var op in ops)
        {
            var tokens = op.Path.Tokens;
            var pointer = arm.PointerPrefix + op.Path;
            if (tokens.Length < 2)
            {
                continue; // reported above
            }

            var name = tokens[1];
            JsonNode? spec = null;
            if (!baseSchema.TryGetPropertyValue(name, out spec) && addedSchema is not null && addedSchema.TryGetValue(name, out var added))
            {
                spec = added.Value;
            }

            if (spec is not null and not JsonObject)
            {
                diagnostics.Error("PATCH_SCHEMA", arm.Artifact, arm.PointerPrefix + "/param_schema/" + JsonPointer.Escape(name),
                    $"The param_schema entry for '{name}' must be an object.", "e.g. { \"type\": \"float64\", \"min\": 0, \"max\": 1 }");
                arm.Valid = false;
                continue;
            }

            if (spec is null)
            {
                var declared = new List<string>();
                foreach (var (key, _) in baseSchema)
                {
                    declared.Add(key);
                }

                diagnostics.Error("PARAM_UNDECLARED", arm.Artifact, pointer, $"Parameter '{name}' is not declared.",
                    NameSuggestions.DidYouMean(name, declared) ?? $"declare it in the same arm under /param_schema/{name}");
                arm.Valid = false;
                continue;
            }

            if (op.Kind == PatchOpKind.Delete)
            {
                diagnostics.Error("PARAM_DELETE", arm.Artifact, pointer, "Experiments cannot delete parameter values.",
                    "set the value the arm should use instead");
                arm.Valid = false;
                continue;
            }

            var type = spec["type"] is JsonValue t && t.TryGetValue<string>(out var s) ? s : string.Empty;
            var isMap = type.StartsWith("map<string,", StringComparison.Ordinal);
            if (!isMap)
            {
                if (tokens.Length != 2)
                {
                    diagnostics.Error("PARAM_BAD_VALUE", arm.Artifact, pointer, $"Parameter '{name}' is a scalar of type {type}.");
                    arm.Valid = false;
                    continue;
                }

                arm.ParamOverrides.Add(new ParamOverride(name, op.Value, pointer));
                continue;
            }

            if (tokens.Length == 2)
            {
                if (op.Value is not JsonObject map)
                {
                    diagnostics.Error("PARAM_BAD_VALUE", arm.Artifact, pointer, $"Map parameter '{name}' needs an object value.");
                    arm.Valid = false;
                    continue;
                }

                foreach (var (key, value) in map)
                {
                    arm.ParamOverrides.Add(new ParamOverride(name + "." + key, value, pointer + "/" + JsonPointer.Escape(key)));
                    arm.ExtraParamLeaves.Add(name + "." + key);
                }

                continue;
            }

            if (tokens.Length != 3)
            {
                diagnostics.Error("PARAM_BAD_VALUE", arm.Artifact, pointer, $"Map parameter '{name}' has one level of keys.");
                arm.Valid = false;
                continue;
            }

            var leaf = name + "." + tokens[2];
            arm.ParamOverrides.Add(new ParamOverride(leaf, op.Value, pointer));
            if (op.Kind == PatchOpKind.Add)
            {
                arm.ExtraParamLeaves.Add(leaf);
            }
        }
    }

    public static bool JsonEquals(JsonNode? a, JsonNode? b) =>
        string.Equals(JsonCanonicalizer.CanonicalizeToString(a), JsonCanonicalizer.CanonicalizeToString(b), StringComparison.Ordinal);
}
