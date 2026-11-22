using System.Globalization;
using StrataRec.Artifacts.Json;
using StrataRec.Expressions;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Experiments.Compilation;

/// <summary>Whether the names the nodes of an arm combination bind are declared there, and as the unified schema declares them (design 11.5 check 3, 16.2).</summary>
internal sealed partial class UnifiedCompilation
{
    private HashSet<string>? _extraLeaves;
    private (int Start, int End) _flowErrors; // the current combination's other checks, for ReportedAt

    /// <summary>
    /// The unified plan compiles every node once, on one schema holding whatever any arm declares; a compile of one arm
    /// combination alone knows a name only once it is declared there. So for a combination with an arm (the bare baseline
    /// is compiled alone, <see cref="ValidateBaselineAlone"/>), every name a node, fallback, tap or the output binds
    /// (<see cref="CompiledNode.Requires"/>) must be declared before it compiles: by the baseline, by an addition of an arm
    /// assigned here, or, for a column, by a node compiled earlier whose write declares it (<see cref="CompiledNode.Declares"/>).
    /// The compile order is the one <see cref="CompileVariants"/> uses: the timeline by order, then id; the fallbacks after
    /// it by id; the taps and the output last. A reader the combination's other checks reported an error at (since
    /// <paramref name="start"/>; a tap reading a column no node produces before it) is not reported again.
    /// </summary>
    /// <remarks>
    /// Each part of this was a gap where the unified plan accepted what a combination's own compile refuses: a column only
    /// an arm's node declared, read by the baseline (external review P1); a writer one arm deletes or moves and a reader
    /// another adds (generated differential test); a node reading its own output before writing it (review 50); a column
    /// declared by an arm not assigned with its reader, a name in a branch the optimizer drops, a write that needs a
    /// declaration (review 51). An arm whose additions are all it changes is never part of a combination checked here (it
    /// changes no node): that it is not assigned is the case to check, since a reader cannot rely on an arm of another
    /// layer, which a request may not get.
    /// </remarks>
    private void ValidateNames(
        List<CompiledNode> nodes, Dictionary<string, CompiledNode> fallbacks, List<CompiledTap> taps, OutputPlan output, List<ArmPatch> assigned,
        int start)
    {
        _flowErrors = (start, _diagnostics.Count);
        var ordered = new List<CompiledNode>(nodes);
        ordered.Sort(static (a, b) => a.Order != b.Order ? a.Order!.Value.CompareTo(b.Order!.Value) : string.CompareOrdinal(a.NodeId, b.NodeId));
        var later = new List<CompiledNode>(fallbacks.Count);
        foreach (var (_, fallback) in fallbacks)
        {
            later.Add(fallback);
        }

        later.Sort(static (a, b) => string.CompareOrdinal(a.NodeId, b.NodeId));
        for (var i = 0; i < ordered.Count; i++)
        {
            var node = ordered[i];
            foreach (var name in node.Requires)
            {
                Declared(name, "Node", node.NodeId, node.Pointer, ordered, i, later, 0, assigned);
            }
        }

        for (var i = 0; i < later.Count; i++)
        {
            var fallback = later[i];
            foreach (var name in fallback.Requires)
            {
                Declared(name, "Fallback", fallback.NodeId, fallback.Pointer, ordered, ordered.Count, later, i, assigned);
            }
        }

        foreach (var tap in taps)
        {
            foreach (var name in tap.Requires)
            {
                Declared(name, "Tap", tap.TapId, "/log_taps/" + JsonPointer.Escape(tap.TapId), ordered, ordered.Count, later, later.Count, assigned);
            }
        }

        foreach (var (column, expression) in output.Columns)
        {
            foreach (var symbol in expression.Names)
            {
                if (symbol.Kind is SymbolKind.Column or SymbolKind.Param or SymbolKind.Context)
                {
                    Declared(new NameRef(symbol.Kind, symbol.Id), "Output column", column, "/output/columns/" + JsonPointer.Escape(column), ordered, ordered.Count, later, later.Count, assigned);
                }
            }
        }

        foreach (var (value, expression) in output.Values)
        {
            foreach (var symbol in expression.Names)
            {
                if (symbol.Kind is SymbolKind.Param or SymbolKind.Context)
                {
                    Declared(new NameRef(symbol.Kind, symbol.Id), "Output value", value, "/output/values/" + JsonPointer.Escape(value), ordered, ordered.Count, later, later.Count, assigned);
                }
            }
        }
    }

    /// <summary>
    /// A column an arm declares exists only where that arm is assigned; elsewhere the writes that declare a column with a
    /// type of their own declare it, as a compile of that combination does. The unified plan has one schema, holding the
    /// arm's declaration: each such write in a combination without the arm (the bare baseline too) must declare the same
    /// column (its type, no default, not a flow column), or the combination would be served with another column than its
    /// own compile has: a reader compiled on a nullable declaration refusing what the write's type allows, a default it
    /// does not have.
    /// </summary>
    private void ValidateImplicitDeclarations(List<CompiledNode> nodes, Dictionary<string, CompiledNode> fallbacks, List<ArmPatch> assigned)
    {
        foreach (var node in nodes)
        {
            ValidateImplicitDeclaration(node, "Node", assigned);
        }

        foreach (var (_, fallback) in fallbacks)
        {
            ValidateImplicitDeclaration(fallback, "Fallback", assigned);
        }
    }

    private void ValidateImplicitDeclaration(CompiledNode node, string kind, List<ArmPatch> assigned)
    {
        foreach (var declaration in node.Declares)
        {
            var column = _builder.Schema.Columns[declaration.Logical];
            if (!IsAddition("columns", column.Name) || AddedBy("columns", column.Name, assigned)
                || (declaration.Type == column.Type && !column.HasDefault && !column.Flow))
            {
                continue;
            }

            var declared = column.Type + (column.HasDefault ? " with a default" : string.Empty) + (column.Flow ? " as a flow column" : string.Empty);
            var arms = DeclaringArms("columns", column.Name, null, out var count);
            _diagnostics.Error("COLUMN_TYPE_MISMATCH", _pipelineLabel, node.Pointer,
                $"{kind} '{node.NodeId}' writes 'col.{column.Name}' as {declaration.Type}, which declares it here, but {arms} "
                + $"{(count == 1 ? "declares" : "declare")} it as {declared}, and {(count == 1 ? "it is" : "none of them is")} not assigned "
                + "with it here: a column is the same in every arm combination.",
                $"declare it in the baseline (e.g. \"{column.Name}\": {{ \"type\": \"{column.Type}\" }}), so that every combination has the "
                + $"declaration; or declare it in the arm as '{node.NodeId}' writes it ({declaration.Type}, without a default)");
        }
    }

    /// <summary>Reports <paramref name="name"/> unless it is declared before the reader: by the first <paramref name="nodes"/> timeline nodes or <paramref name="fallbacks"/> fallbacks.</summary>
    private void Declared(
        NameRef name, string kind, string reader, string pointer, List<CompiledNode> ordered, int nodes, List<CompiledNode> later, int fallbacks,
        List<ArmPatch> assigned)
    {
        var schema = _builder.Schema;
        string written;
        string why;
        string hint;
        switch (name.Kind)
        {
            case SymbolKind.Column when schema.Columns[name.Id] is { Kind: ColumnKind.Output } column:
            {
                if (AddedBy("columns", column.Name, assigned) || (!IsAddition("columns", column.Name) && !column.Implicit)
                    || DeclaresBefore(name.Id, ordered, nodes, later, fallbacks))
                {
                    return;
                }

                string? after = null;
                for (var j = nodes; j < ordered.Count && after is null; j++)
                {
                    if (Writes(ordered[j], name.Id))
                    {
                        after = $"'{ordered[j].NodeId}' (order {ordered[j].Order?.ToString(CultureInfo.InvariantCulture)}) writes it after";
                    }
                }

                written = "col." + column.Name;
                var declaring = AddingArms("columns", column.Name, null);
                why = (declaring.Length > 0 ? declaring + "; " : "the node writing it declares it, and here ") + (after ?? "no node writes it before");
                hint = $"declare it under /columns (e.g. \"{column.Name}\": {{ \"type\": \"{column.Type.AsNullable()}\" }}) in the baseline or in the arm " +
                    $"that reads it, so that it exists wherever '{reader}' does; or keep a node that writes it before '{reader}' in every arm combination";
                break;
            }

            case SymbolKind.Column when schema.Columns[name.Id] is { Kind: ColumnKind.Feature } feature:
                if (FeatureSection(feature.Name) is not { } section || AddedBy(section, feature.Name, assigned))
                {
                    return;
                }

                written = "col." + feature.Name;
                why = AddingArms(section, feature.Name, null);
                hint = Declare(reader);
                break;
            case SymbolKind.Context when schema.ContextFields[name.Id] is { FeatureIndex: >= 0 } field:
                if (FeatureSection(field.Name) is not { } contextSection || AddedBy(contextSection, field.Name, assigned))
                {
                    return;
                }

                written = "ctx." + field.Name;
                why = AddingArms(contextSection, field.Name, null);
                hint = Declare(reader);
                break;
            case SymbolKind.Param:
                var leaf = schema.Params[name.Id];
                if (!IsExtraLeaf(leaf) || LeafAddedBy(leaf, assigned))
                {
                    return;
                }

                written = "param." + leaf.Name;
                why = AddingArms("param_schema", leaf.MapName ?? leaf.Name, leaf.Name);
                hint = Declare(reader);
                break;
            default:
                return; // the entity id and request fields are the baseline's
        }

        if (!ReportedAt(pointer))
        {
            _diagnostics.Error("NAME_NOT_DECLARED", _pipelineLabel, pointer, $"{kind} '{reader}' reads '{written}', which is not declared here: {why}.", hint);
        }
    }

    /// <summary>Whether the combination's other checks reported an error at <paramref name="pointer"/> or under it.</summary>
    private bool ReportedAt(string pointer)
    {
        var items = _diagnostics.Items;
        for (var i = _flowErrors.Start; i < _flowErrors.End; i++)
        {
            var at = items[i].Pointer;
            if (items[i].IsError && at.StartsWith(pointer, StringComparison.Ordinal) && (at.Length == pointer.Length || at[pointer.Length] == '/'))
            {
                return true;
            }
        }

        return false;
    }

    private static string Declare(string reader) =>
        $"declare it in the baseline, or in the arm that adds '{reader}' or changes it to read it: an arm's own declaration exists wherever its readers do";

    private static bool DeclaresBefore(int logical, List<CompiledNode> ordered, int nodes, List<CompiledNode> later, int fallbacks)
    {
        for (var j = 0; j < nodes; j++)
        {
            if (Writes(ordered[j], logical))
            {
                return true;
            }
        }

        for (var j = 0; j < fallbacks; j++)
        {
            if (Writes(later[j], logical))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a write of <paramref name="node"/> declares the column.</summary>
    private static bool Writes(CompiledNode node, int logical)
    {
        foreach (var declaration in node.Declares)
        {
            if (declaration.Logical == logical)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a name's declaration may differ between combinations, so <see cref="ValidateNames"/> depends on which arms
    /// are assigned (<see cref="PlanBuilder.DataFlowScopes"/>): a column without a declaration, or a name an arm adds.
    /// </summary>
    private bool Conditional(NameRef name)
    {
        var schema = _builder.Schema;
        return name.Kind switch
        {
            SymbolKind.Column => schema.Columns[name.Id] is var column
                && (column.Kind == ColumnKind.Output ? column.Implicit || IsAddition("columns", column.Name) : column.Kind == ColumnKind.Feature && FeatureSection(column.Name) is not null),
            SymbolKind.Context => schema.ContextFields[name.Id] is { FeatureIndex: >= 0 } field && FeatureSection(field.Name) is not null,
            SymbolKind.Param => IsExtraLeaf(schema.Params[name.Id]),
            _ => false,
        };
    }

    /// <summary>The catalog section an added feature is in, or null when no arm adds it.</summary>
    private string? FeatureSection(string feature) =>
        IsAddition("features", feature) ? "features" : IsAddition("derived_features", feature) ? "derived_features" : null;

    private bool IsAddition(string section, string entry) => _additions.TryGetValue(section, out var entries) && entries.ContainsKey(entry);

    private static bool AddedBy(string section, string entry, List<ArmPatch> assigned)
    {
        foreach (var arm in assigned)
        {
            if (arm.Additions.TryGetValue(section, out var entries) && entries.ContainsKey(entry))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Which arms declare an entry (an addition, or a parameter leaf), as a message says it; empty when none does.</summary>
    private string AddingArms(string section, string entry, string? leaf)
    {
        var arms = DeclaringArms(section, entry, leaf, out var count);
        return count switch
        {
            0 => string.Empty,
            1 => $"only {arms} declares it, and it is not assigned with it here",
            _ => $"only {arms} declare it, and none of them is assigned with it here",
        };
    }

    /// <summary>The valid arms declaring an entry (an addition, or a parameter leaf), quoted and comma-separated.</summary>
    private string DeclaringArms(string section, string entry, string? leaf, out int count)
    {
        var arms = new List<string>();
        foreach (var arm in _arms)
        {
            if (arm.Valid && ((arm.Additions.TryGetValue(section, out var entries) && entries.ContainsKey(entry)) || (leaf is not null && arm.ExtraParamLeaves.Contains(leaf))))
            {
                arms.Add($"'{arm.Id}'");
            }
        }

        count = arms.Count;
        return string.Join(", ", arms);
    }

    /// <summary>A parameter leaf only arms declare: a parameter an arm adds, or a key an arm adds to a map parameter.</summary>
    private bool IsExtraLeaf(ParamLeaf leaf)
    {
        if (_extraLeaves is null)
        {
            _extraLeaves = new HashSet<string>(StringComparer.Ordinal);
            foreach (var arm in _arms)
            {
                if (arm.Valid)
                {
                    _extraLeaves.UnionWith(arm.ExtraParamLeaves);
                }
            }
        }

        return _extraLeaves.Contains(leaf.Name) || IsAddition("param_schema", leaf.MapName ?? leaf.Name);
    }

    private static bool LeafAddedBy(ParamLeaf leaf, List<ArmPatch> assigned)
    {
        foreach (var arm in assigned)
        {
            if (arm.ExtraParamLeaves.Contains(leaf.Name) || (arm.Additions.TryGetValue("param_schema", out var entries) && entries.ContainsKey(leaf.MapName ?? leaf.Name)))
            {
                return true;
            }
        }

        return false;
    }
}
