using System.Collections.Immutable;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Types;
using StrataRec.Expressions;
using StrataRec.Features.Catalog;
using StrataRec.Features.Runtime;

namespace StrataRec.Pipeline.Planning;

/// <summary>
/// Registers catalog features into a plan schema the first time a name resolves to one, with what each
/// needs: a derived feature compiles then, and its own reads register the features it is computed from; a feature of an
/// entity keyed by features (candidate or context) registers them first; a feature of a <c>cache</c> source registers every feature of its table
/// (<see cref="FeaturePlan.NeedsWholeTable"/>). The one place that decides what of a catalog a plan holds, and that
/// compiles derived features and finds their cycles.
/// </summary>
/// <remarks>
/// <para>A catalog is shared by many pipelines, and what a request sizes by feature, slot or context field (the candidate
/// table's slots, the context values, the fetcher's per-feature state, the output bindings) used to grow with all of it:
/// every feature was registered when the schema was defined. Registering on reference, through the schema's lookups,
/// covers every way of reading a feature (expressions, operator configs, <c>when</c>, fallbacks, every arm's variants,
/// taps, the output, funnel keys) without each of them knowing about it; compiling the configuration once more to
/// collect what it reads would double the compile.</para>
/// <para>The plan builder runs one instance over a schema of its own that registers the whole catalog
/// (<see cref="RegisterAll"/>), for the diagnostics on the catalog's content, which must not depend on which pipeline
/// reads what: the errors of a derived feature no node reads, cycles. What fails there is marked unusable in the plan's
/// schema (<see cref="Failed"/>), so the plan's instance never compiles it again and a reference to it is not a second
/// error.</para>
/// <para>Registration order is reference order, so feature indices and slots follow the compile order, which is
/// deterministic; nothing persisted or compared depends on them (logs, replay bundles and plan documents name features).</para>
/// </remarks>
internal sealed class CatalogFeatures : IFeatureRegistrar
{
    private readonly FeatureCatalog _catalog;
    private readonly PlanSchema _schema;
    private readonly IExpressionScope _scope;
    private readonly DiagnosticBag _diagnostics;
    private readonly string _artifact;
    private readonly List<FeatureRuntimeInfo> _features;
    private readonly Func<string, bool>? _needsWholeTable;
    private readonly Dictionary<string, Status> _status = new(StringComparer.Ordinal);
    private readonly List<FeatureDefinition> _registering = new();
    private Dictionary<(string Source, string Entity), List<FeatureDefinition>>? _tables;
    private readonly HashSet<(string Source, string Entity)> _expanded = new();
    private readonly HashSet<string>? _deprecated;
    private readonly SortedSet<string> _deprecatedReads = new(StringComparer.Ordinal);

    /// <param name="features">Where registered features go; a feature's index is its position.</param>
    /// <param name="needsWholeTable">Whether a source's features are registered together (its table's records are
    /// written whole); null registers each feature alone.</param>
    /// <param name="failed">Features that failed when the whole catalog was registered: they are not registered again,
    /// and reading one fails without a second error.</param>
    public CatalogFeatures(
        FeatureCatalog catalog,
        PlanSchema schema,
        IExpressionScope scope,
        DiagnosticBag diagnostics,
        string artifact,
        List<FeatureRuntimeInfo> features,
        Func<string, bool>? needsWholeTable = null,
        IReadOnlyCollection<string>? failed = null)
    {
        _catalog = catalog;
        _schema = schema;
        _scope = scope;
        _diagnostics = diagnostics;
        _artifact = artifact;
        _features = features;
        _needsWholeTable = needsWholeTable;
        foreach (var feature in catalog.Features)
        {
            if (feature.Deprecated)
            {
                (_deprecated ??= new HashSet<string>(StringComparer.Ordinal)).Add(feature.Name);
            }
        }

        if (failed is not null)
        {
            foreach (var name in failed)
            {
                if (_catalog.TryGet(name, out var feature))
                {
                    Fail(feature);
                }
            }
        }
    }

    private enum Status : byte
    {
        Registering = 1,
        Registered,
        Failed,
    }

    /// <summary>The names of the features that could not be registered (an error reported at their declaration, a cycle,
    /// a name taken, or what they read failed), in name order.</summary>
    public List<string> Failed
    {
        get
        {
            var failed = new List<string>();
            foreach (var (name, status) in _status)
            {
                if (status == Status.Failed)
                {
                    failed.Add(name);
                }
            }

            failed.Sort(StringComparer.Ordinal);
            return failed;
        }
    }

    /// <summary>Registers every feature of the catalog, in name order: what plans did before they registered on
    /// reference, kept for the diagnostics on the catalog's content.</summary>
    public void RegisterAll()
    {
        foreach (var feature in _catalog.Features)
        {
            Register(feature);
        }
    }

    public bool TryRegister(string qualifiedName)
    {
        var context = qualifiedName.StartsWith("ctx.", StringComparison.Ordinal);
        if (!context && !qualifiedName.StartsWith("col.", StringComparison.Ordinal))
        {
            return false;
        }

        // The namespace is the entity's: ctx.item.x names no feature, as before.
        return _catalog.TryGet(qualifiedName[4..], out var feature) && feature.IsContext == context && Register(feature);
    }

    /// <summary>The deprecated features something read, directly or through a derived feature, in name order.</summary>
    public IReadOnlyCollection<string> DeprecatedReads => _deprecatedReads;

    public void NoteRead(string name, bool context)
    {
        // A feature of an entity keyed by features reads its key features too (they are fetched first), and
        // so on along the chains (a deprecated key read only through its entity's features did not warn). Each
        // feature once, so a chain that loops (an error reported elsewhere) ends.
        if (_deprecated is null || !_catalog.TryGet(name, out var first) || first.IsContext != context)
        {
            return;
        }

        var pending = new Stack<FeatureDefinition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        pending.Push(first);
        while (pending.Count > 0)
        {
            var feature = pending.Pop();
            if (!seen.Add(feature.Name))
            {
                continue;
            }

            if (feature.Deprecated)
            {
                _deprecatedReads.Add(feature.Name);
            }

            foreach (var read in feature.Entity.FeatureReads)
            {
                if (_catalog.TryGet(read.Feature, out var key) && key.IsContext == read.Context)
                {
                    pending.Push(key);
                }
            }
        }
    }

    public void AddUnregisteredNames(List<string> names, bool candidateColumnsOnly)
    {
        foreach (var feature in _catalog.Features)
        {
            if (_status.ContainsKey(feature.Name) || (candidateColumnsOnly && feature.IsContext) || _schema.IsUnusable(feature.ExpressionName))
            {
                continue;
            }

            names.Add(candidateColumnsOnly ? feature.Name : feature.ExpressionName);
        }
    }

    private bool Register(FeatureDefinition feature)
    {
        if (_status.TryGetValue(feature.Name, out var status))
        {
            return status switch
            {
                Status.Registered => true,
                Status.Failed => false,
                Status.Registering => Cycle(feature),
            };
        }

        if (_schema.IsUnusable(feature.ExpressionName))
        {
            return false; // declared with an error (or what it reads was), reported there
        }

        if (feature.IsContext ? _schema.HoldsContext(feature.Name) : _schema.HoldsColumn(feature.Name))
        {
            // A context feature of an entity named 'request' and a request input of the same name.
            _diagnostics.Error("FEATURE_DUPLICATE", _artifact, feature.Pointer,
                $"'{feature.ExpressionName}' is a feature and another {(feature.IsContext ? "context field" : "column")}.",
                "rename the feature or its entity");
            _status[feature.Name] = Status.Failed; // the other keeps its name
            return false;
        }

        _status[feature.Name] = Status.Registering;
        _registering.Add(feature);
        var registered = feature.IsDerived ? RegisterDerived(feature) : RegisterPhysical(feature);
        _registering.RemoveAt(_registering.Count - 1);
        if (!registered)
        {
            Fail(feature);
            return false;
        }

        // A table is expanded once: its members register the rest of it, so expanding again for each member scanned the
        // table once per member, quadratic in its width (40,000 features took 40 s to compile, not 1 s).
        if (!feature.IsDerived && feature.Source is { } source && _needsWholeTable is not null && _needsWholeTable(source)
            && _expanded.Add((source, feature.Entity.Name)))
        {
            foreach (var sibling in TableOf(feature))
            {
                Register(sibling);
            }
        }

        return true;
    }

    /// <summary>A physical feature: its key features first when its entity's key reads features, candidate or context
    /// (they are fetched first); an entity a key feature of which cannot be registered has no key, and its
    /// features fail with the key's error.</summary>
    private bool RegisterPhysical(FeatureDefinition feature)
    {
        foreach (var read in feature.Entity.FeatureReads)
        {
            if (!TryRegister(read.Reference))
            {
                return false;
            }
        }

        Add(feature, feature.Type, null, []);
        return true;
    }

    private bool RegisterDerived(FeatureDefinition feature)
    {
        var options = new CompileOptions
        {
            ExpectedType = feature.Type.AsNonNullable(),
            AllowNullableResult = true,
            RequestLevelOnly = feature.IsContext,
        };

        // Compiling resolves the expression's names, which registers the features it reads (recursively, so a cycle is met
        // as a feature still being registered).
        if (!ExpressionCompiler.TryCompile(feature.Expr!, _scope, options, out var expression, out var errors))
        {
            // Without an error it reads a name declared with an error (or a cycle reported once): it fails with that.
            ExpressionErrors.Report(_diagnostics, errors, _artifact, feature.Pointer + "/expr", feature.Expr!);
            return false;
        }

        var type = feature.HasDefault ? feature.Type.AsNonNullable() : feature.Type.WithNullable(expression.ResultType.Nullable);
        var dependencies = new List<int>();
        foreach (var symbol in expression.Reads)
        {
            var index = FeatureIndexOf(symbol);
            if (index >= 0)
            {
                dependencies.Add(index);
            }
            else if (symbol.Kind == SymbolKind.Param || (symbol.Kind == SymbolKind.Column && _schema.Columns[symbol.Id].Kind == ColumnKind.Output))
            {
                _diagnostics.Error("FEATURE_BAD_REFERENCE", _artifact, feature.Pointer + "/expr",
                    $"Derived feature '{feature.Name}' reads '{symbol.Name}'; derived features may only read features, request inputs and col.entity_id.");
            }
        }

        Add(feature, type, expression, [.. dependencies]);
        return true;
    }

    /// <summary>
    /// A feature met again while it is being registered: it depends on itself. Through a derived feature this is reported
    /// here, once: a cycle that also passes through an entity's key (a physical feature registers its key's features
    /// first) is an <c>ENTITY_KEY_CYCLE</c> at the first key part on it, the key that can never be computed, candidate or
    /// context alike; a cycle of derived features alone is a <c>FEATURE_CYCLE</c> at the first derived feature of it; a
    /// cycle of keys alone is the catalog's <c>ENTITY_KEY_CYCLE</c>, reported there. The feature is marked unusable at once,
    /// so every expression on the way fails without an error of its own.
    /// </summary>
    private bool Cycle(FeatureDefinition feature)
    {
        if (_schema.IsUnusable(feature.ExpressionName))
        {
            return false; // met again in the same expressions: reported already
        }

        var start = _registering.IndexOf(feature);
        var path = new List<string>(_registering.Count - start + 1);
        FeatureDefinition? derived = null;
        FeatureDefinition? keyed = null; // the first physical feature on the cycle: its entity's key (or args) reads the next one
        EntityFeatureRead keyRead = default;
        for (var i = start; i < _registering.Count; i++)
        {
            var on = _registering[i];
            path.Add(on.Name);
            derived ??= on.IsDerived ? on : null;
            if (keyed is null && !on.IsDerived)
            {
                // A physical feature registers its key's features and nothing else, so the next on the path is one of them.
                var read = on.Entity.ReadOf(i + 1 < _registering.Count ? _registering[i + 1] : feature);
                keyRead = read ?? default;
                keyed = read is not null ? on : null;
            }
        }

        path.Add(feature.Name);
        if (derived is not null && keyed is not null)
        {
            _diagnostics.Error("ENTITY_KEY_CYCLE", _artifact, keyRead.Pointer,
                $"{(keyed.Entity.HasArgs ? "Argument" : "Key part")} '{keyRead.Reference}' of '{keyed.Entity.Name}' leads to a key cycle through derived feature '{derived.Name}': {string.Join(" → ", path)}.",
                $"key one of the entities on the cycle by something that does not depend on it, or compute '{derived.Name}' without it");
        }
        else if (derived is not null)
        {
            _diagnostics.Error("FEATURE_CYCLE", _artifact, derived.Pointer + "/expr",
                $"Derived feature '{derived.Name}' depends on itself: {string.Join(" → ", path)}.",
                "compute one of them without the other, e.g. from the physical features both read");
        }

        _schema.MarkUnusable(feature.ExpressionName);
        return false;
    }

    private void Fail(FeatureDefinition feature)
    {
        _status[feature.Name] = Status.Failed;
        if (!(feature.IsContext ? _schema.HoldsContext(feature.Name) : _schema.HoldsColumn(feature.Name)))
        {
            _schema.MarkUnusable(feature.ExpressionName);
        }
    }

    private void Add(FeatureDefinition feature, ColumnType type, CompiledExpression? expression, ImmutableArray<int> dependencies)
    {
        var index = _features.Count;
        int slot = -1, contextId = -1;
        if (feature.IsContext)
        {
            contextId = _schema.AddContextFeature(feature.Name, type, index).Id;
        }
        else
        {
            slot = _schema.AddFeatureColumn(feature.Name, type, index, feature.Pointer).FixedSlot;
        }

        _features.Add(new FeatureRuntimeInfo
        {
            Index = index,
            Definition = feature,
            Type = type,
            Slot = slot,
            ContextId = contextId,
            Expression = expression,
            Dependencies = dependencies,
        });
        _status[feature.Name] = Status.Registered;
    }

    /// <summary>The physical features of the catalog that share <paramref name="feature"/>'s source and entity (its
    /// table), in name order.</summary>
    private List<FeatureDefinition> TableOf(FeatureDefinition feature)
    {
        if (_tables is null)
        {
            _tables = new Dictionary<(string, string), List<FeatureDefinition>>();
            foreach (var f in _catalog.Features)
            {
                if (f.IsDerived)
                {
                    continue;
                }

                var key = (f.Source!, f.Entity.Name);
                if (!_tables.TryGetValue(key, out var list))
                {
                    list = new List<FeatureDefinition>();
                    _tables[key] = list;
                }

                list.Add(f);
            }
        }

        return _tables[(feature.Source!, feature.Entity.Name)];
    }

    private int FeatureIndexOf(in Symbol symbol) => symbol.Kind switch
    {
        SymbolKind.Column => _schema.Columns[symbol.Id].FeatureIndex,
        SymbolKind.Context => _schema.ContextFields[symbol.Id].FeatureIndex,
        _ => -1,
    };
}
