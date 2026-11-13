using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;

namespace StrataRec.Experiments.Compilation;

/// <summary>A parameter leaf an arm overrides (design 11.8, 12.2).</summary>
/// <param name="Leaf">Flattened leaf name, e.g. <c>recall_quotas.cf</c>.</param>
/// <param name="Value">The patch value.</param>
/// <param name="Pointer">Where the value is written in the experiment artifact.</param>
internal readonly record struct ParamOverride(string Leaf, JsonNode? Value, string Pointer);

/// <summary>
/// One experiment arm after strict analysis of its patch against the patchable view (design 11.3–11.5): its path
/// footprint (without add-only no-ops), parameter overrides, add-only additions and the nodes it touches.
/// </summary>
internal sealed class ArmPatch
{
    public required int Index { get; init; }

    /// <summary><c>experiment/arm</c>.</summary>
    public required string Id { get; init; }

    public required string ArmName { get; init; }

    public required int ExperimentIndex { get; init; }

    public required string ExperimentId { get; init; }

    public required int LayerIndex { get; init; }

    /// <summary>Traffic domain of the arm's layer, when known; arms in different domains never co-occur.</summary>
    public string? Domain { get; init; }

    public required JsonObject Patch { get; init; }

    /// <summary>Diagnostic artifact label, e.g. <c>experiment:exp_mmr</c>.</summary>
    public required string Artifact { get; init; }

    /// <summary>Pointer of the arm's patch inside the experiment artifact, e.g. <c>/arms/mmr</c>.</summary>
    public required string PointerPrefix { get; init; }

    /// <summary>Path footprint (design 11.5 item 1) with add-only no-ops removed.</summary>
    public List<PatchOperation> Footprint { get; } = new();

    public List<ParamOverride> ParamOverrides { get; } = new();

    /// <summary>Add-only additions by section name (<c>columns</c>, <c>features</c>, …) then entry name.</summary>
    public Dictionary<string, SortedDictionary<string, (JsonNode Value, string Pointer)>> Additions { get; } = new(StringComparer.Ordinal);

    /// <summary>Parameter leaves that exist only because of this arm (new map keys, arm-declared parameters).</summary>
    public List<string> ExtraParamLeaves { get; } = new();

    /// <summary>Nodes with a footprint path under <c>/nodes/&lt;id&gt;</c> (they get variants, design 12.3).</summary>
    public SortedSet<string> TouchedNodes { get; } = new(StringComparer.Ordinal);

    /// <summary>Nodes the arm adds (absent from the baseline).</summary>
    public HashSet<string> AddedNodes { get; } = new(StringComparer.Ordinal);

    /// <summary>Log taps with a footprint path under <c>/log_taps/&lt;id&gt;</c>.</summary>
    public SortedSet<string> TouchedTaps { get; } = new(StringComparer.Ordinal);

    /// <summary>False when the patch itself is invalid; such an arm is excluded from further checks.</summary>
    public bool Valid { get; set; } = true;

    public override string ToString() => Id;

    /// <summary>True when the footprint touches <paramref name="pointer"/> (either is a prefix of the other).</summary>
    public bool Relates(string pointer)
    {
        if (!JsonPointer.TryParse(pointer, out var parsed))
        {
            return false;
        }

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

/// <summary>Strict analysis of one arm's patch (design 11.3, 11.4, 11.8).</summary>
internal static class ArmAnalyzer
{
    /// <summary>Stable ids of nodes, columns and parameters: they appear in pointers, expressions and logs.</summary>
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

    public static void Analyze(ArmPatch arm, PatchableView view, PolicyArtifact policy, DiagnosticBag diagnostics)
    {
        var analysis = MergePatchAnalyzer.Analyze(view.Document, arm.Patch);
        foreach (var error in analysis.Errors)
        {
            diagnostics.Error(error.Code, arm.Artifact, arm.PointerPrefix + error.Path, error.Message, error.Hint);
            arm.Valid = false;
        }

        var patchable = ParsePrefixes(policy.ExperimentPatchable);
        var addOnly = ParsePrefixes(policy.ExperimentAddOnly);
        var paramOps = new List<PatchOperation>();
        foreach (var op in analysis.Footprint)
        {
            var pointer = arm.PointerPrefix + op.Path;
            if (FirstPrefix(patchable, op.Path) is null)
            {
                if (FirstPrefix(addOnly, op.Path) is not { } root)
                {
                    diagnostics.Error("PATCH_NOT_ALLOWED", arm.Artifact, pointer,
                        $"Experiments may not change '{op.Path}'.",
                        $"patchable paths: {string.Join(", ", policy.ExperimentPatchable)}; add-only: {string.Join(", ", policy.ExperimentAddOnly)} (config/policy.jsonc)");
                    arm.Valid = false;
                    continue;
                }

                if (op.Kind == PatchOpKind.Add && op.Path.Depth == root.Depth + 1)
                {
                    var section = root.LastToken ?? string.Empty;
                    if (!arm.Additions.TryGetValue(section, out var entries))
                    {
                        entries = new SortedDictionary<string, (JsonNode, string)>(StringComparer.Ordinal);
                        arm.Additions[section] = entries;
                    }

                    if (!IsValidId(op.Path.LastToken!) && section is "columns" or "param_schema")
                    {
                        diagnostics.Error("BAD_ID", arm.Artifact, pointer, $"'{op.Path.LastToken}' is not a valid name.",
                            "names start with a letter or '_' and contain letters, digits, '_', '.' or '-'");
                        arm.Valid = false;
                        continue;
                    }

                    entries[op.Path.LastToken!] = (op.Value!, pointer);
                    arm.Footprint.Add(op);
                    continue;
                }

                // Re-adding an entry identical to the baseline is a no-op (design 11.4), e.g. after another
                // experiment graduated the same addition.
                if (op.Kind == PatchOpKind.Replace && op.Path.TryEvaluate(view.Document, out var existing) && JsonEquals(existing, op.Value))
                {
                    continue;
                }

                diagnostics.Error("PATCH_ADD_ONLY", arm.Artifact, pointer,
                    $"'{root}' is add-only for experiments; this patch {(op.Kind == PatchOpKind.Delete ? "deletes" : "changes")} an existing entry.",
                    "add an entry under a new name instead; one name has one meaning in logs and models (design 11.4)");
                arm.Valid = false;
                continue;
            }

            arm.Footprint.Add(op);
            var tokens = op.Path.Tokens;
            switch (tokens[0])
            {
                case "params":
                    paramOps.Add(op);
                    break;
                case "nodes" when tokens.Length >= 2:
                    arm.TouchedNodes.Add(tokens[1]);
                    if (op.Kind == PatchOpKind.Add && tokens.Length == 2)
                    {
                        arm.AddedNodes.Add(tokens[1]);
                        if (!IsValidId(tokens[1]))
                        {
                            diagnostics.Error("BAD_ID", arm.Artifact, pointer, $"'{tokens[1]}' is not a valid node id.",
                                "node ids start with a letter or '_' and contain letters, digits, '_', '.' or '-'");
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
            }
        }

        AnalyzeParams(arm, view, paramOps, diagnostics);
    }

    /// <summary>
    /// Flattens parameter patches to leaf overrides (design 11.8). A leaf must be declared by the baseline schema or by
    /// a <c>param_schema</c> entry this arm adds; map parameters take new keys.
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

    /// <summary>RFC 8785 equality: equal canonical forms (numbers compare by value, member order is irrelevant).</summary>
    public static bool JsonEquals(JsonNode? a, JsonNode? b) =>
        string.Equals(JsonCanonicalizer.CanonicalizeToString(a), JsonCanonicalizer.CanonicalizeToString(b), StringComparison.Ordinal);

    private static List<JsonPointer> ParsePrefixes(List<string> prefixes)
    {
        var list = new List<JsonPointer>(prefixes.Count);
        foreach (var p in prefixes)
        {
            if (JsonPointer.TryParse(p, out var parsed))
            {
                list.Add(parsed);
            }
        }

        return list;
    }

    private static JsonPointer? FirstPrefix(List<JsonPointer> prefixes, JsonPointer path)
    {
        foreach (var prefix in prefixes)
        {
            if (prefix.IsPrefixOf(path))
            {
                return prefix;
            }
        }

        return null;
    }
}
