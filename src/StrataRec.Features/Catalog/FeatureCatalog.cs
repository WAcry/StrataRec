using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.Features.Catalog;

/// <summary>Where an entity's key comes from (design 6.2).</summary>
public enum EntityKind : byte
{
    /// <summary>Keyed by a request input; one value per request (<c>ctx.&lt;entity&gt;.*</c>).</summary>
    Context = 1,

    /// <summary>Keyed by the candidate primary key <c>col.entity_id</c>.</summary>
    Candidate,

    /// <summary>Keyed by another candidate column, e.g. <c>col.item.creator_id</c>.</summary>
    ForeignKey,

    /// <summary>Keyed by a context key × a candidate key.</summary>
    Cross,
}

/// <summary>A compiled entity declaration.</summary>
public sealed class EntityDefinition
{
    public required string Name { get; init; }

    public required EntityKind Kind { get; init; }

    /// <summary>Request input providing the context key (<see cref="EntityKind.Context"/> and <see cref="EntityKind.Cross"/>).</summary>
    public string? ContextKeyInput { get; init; }

    public EntityKeyType? ContextKeyType { get; init; }

    /// <summary>
    /// Candidate column providing the candidate key: <c>entity_id</c> or a feature name such as <c>item.creator_id</c>
    /// (<see cref="EntityKind.Candidate"/>, <see cref="EntityKind.ForeignKey"/>, <see cref="EntityKind.Cross"/>).
    /// </summary>
    public string? CandidateKeyColumn { get; init; }

    public EntityKeyType? CandidateKeyType { get; init; }

    public bool IsContextScoped => Kind == EntityKind.Context;

    /// <summary>True when the candidate key is a feature column (foreign key) rather than <c>entity_id</c>.</summary>
    public bool KeyIsFeature => CandidateKeyColumn is not null && CandidateKeyColumn != "entity_id";
}

/// <summary>A compiled feature declaration (physical or derived).</summary>
public sealed class FeatureDefinition
{
    /// <summary>Catalog name <c>&lt;entity&gt;.&lt;feature&gt;</c>.</summary>
    public required string Name { get; init; }

    public required EntityDefinition Entity { get; init; }

    /// <summary>Storage type. Physical features are nullable unless they declare a default. Derived features
    /// without a default get their nullability from the expression when the plan is compiled.</summary>
    public required ColumnType Type { get; init; }

    public string? Source { get; init; }

    public string? Field { get; init; }

    public bool HasDefault { get; init; }

    public ScalarValue Default { get; init; }

    /// <summary>
    /// What a row or request gets when the feature has no value (design 6.7 layer 3): the default, or missing. The one
    /// definition every supply path applies — a key a connector did not answer, a key a local table does not hold, a
    /// record without the field, a missing context or foreign key, a non-finite or out-of-range value — so a feature means
    /// the same whether it is fetched, read locally or replayed.
    /// </summary>
    public ScalarValue Absent => HasDefault ? Default : ScalarValue.Missing;

    /// <summary>ColumnExpr source for derived features.</summary>
    public string? Expr { get; init; }

    public bool IsDerived => Expr is not null;

    public bool Deprecated { get; init; }

    public string? Owner { get; init; }

    public string? Description { get; init; }

    /// <summary>Pointer of the declaration in the patchable view, e.g. <c>/features/item.clicks_7d</c>.</summary>
    public required string Pointer { get; init; }

    public bool IsContext => Entity.IsContextScoped;

    /// <summary>Name as written in expressions: <c>ctx.user.tenure_days</c> or <c>col.item.clicks_7d</c>.</summary>
    public string ExpressionName => (IsContext ? "ctx." : "col.") + Name;
}

/// <summary>
/// Validated feature catalog (design 6.3): entities, physical features and derived features, keyed by name.
/// Pure configuration — no slots or connectors; those are bound when a plan is compiled.
/// </summary>
public sealed class FeatureCatalog
{
    private readonly Dictionary<string, FeatureDefinition> _features;

    private FeatureCatalog(
        string catalogId, IReadOnlyDictionary<string, EntityDefinition> entities, Dictionary<string, FeatureDefinition> features, IReadOnlyCollection<string> unusable)
    {
        CatalogId = catalogId;
        Entities = entities;
        Unusable = unusable;
        _features = features;
        var ordered = new List<FeatureDefinition>(features.Values);
        ordered.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        Features = ordered;
    }

    public string CatalogId { get; }

    public IReadOnlyDictionary<string, EntityDefinition> Entities { get; }

    /// <summary>
    /// Features the catalog declares but could not build because of an error reported at their declaration (an unknown
    /// type, a bad default, an entity that failed): a plan marks them unusable, so reading one is not a second error.
    /// </summary>
    public IReadOnlyCollection<string> Unusable { get; }

    /// <summary>All features ordered by name (ordinal), so ids derived from positions are deterministic.</summary>
    public IReadOnlyList<FeatureDefinition> Features { get; }

    public bool TryGet(string name, out FeatureDefinition feature) => _features.TryGetValue(name, out feature!);

    /// <summary>Validates and compiles a catalog artifact. Problems go to <paramref name="diagnostics"/>.</summary>
    /// <param name="requestSchema">Request input name → type, for context-key validation.</param>
    /// <param name="unusableInputs">Request inputs declared with an error reported already (an unknown type): an entity
    /// keyed by one fails without a second error, and so do its features.</param>
    public static FeatureCatalog Build(
        FeatureCatalogArtifact artifact,
        IReadOnlyDictionary<string, ColumnType> requestSchema,
        DiagnosticBag diagnostics,
        string artifactName = "catalog",
        IReadOnlySet<string>? unusableInputs = null)
    {
        var entities = new Dictionary<string, EntityDefinition>(StringComparer.Ordinal);
        var broken = new HashSet<string>(StringComparer.Ordinal); // entities declared with an error, reported or inherited
        var unusable = new SortedSet<string>(StringComparer.Ordinal);
        var entityNames = new List<string>(artifact.Entities.Keys);
        entityNames.Sort(StringComparer.Ordinal);
        foreach (var name in entityNames)
        {
            var spec = artifact.Entities[name];
            var entity = BuildEntity(name, spec, requestSchema, unusableInputs, diagnostics, artifactName);
            if (entity is not null)
            {
                entities[name] = entity;
            }
            else
            {
                broken.Add(name);
            }
        }

        var features = new Dictionary<string, FeatureDefinition>(StringComparer.Ordinal);
        foreach (var (name, spec) in artifact.Features)
        {
            var pointer = "/features/" + EscapePointer(name);
            if (!TryEntity(name, entities, broken, diagnostics, artifactName, pointer, out var entity))
            {
                unusable.Add(name);
                continue;
            }

            if (!ConfigTypes.TryParse(spec.Type, TypeSite.Feature, out var type, out _, out var typeError, out var typeHint))
            {
                diagnostics.Error("FEATURE_BAD_TYPE", artifactName, pointer + "/type", typeError, typeHint);
                unusable.Add(name);
                continue;
            }

            var hasDefault = spec.Default is not null;
            var def = ScalarValue.Missing;
            if (hasDefault && !ConfigValues.TryToScalar(spec.Default, type, out def, out var defError))
            {
                diagnostics.Error("FEATURE_BAD_DEFAULT", artifactName, pointer + "/default", $"The default of '{name}' {defError}.");
                unusable.Add(name);
                continue;
            }

            features[name] = new FeatureDefinition
            {
                Name = name,
                Entity = entity,
                Type = type.WithNullable(!hasDefault),
                Source = spec.Source,
                Field = spec.Field,
                HasDefault = hasDefault,
                Default = def,
                Deprecated = spec.Deprecated ?? false,
                Owner = spec.Owner,
                Description = spec.Description,
                Pointer = pointer,
            };
        }

        foreach (var (name, spec) in artifact.DerivedFeatures)
        {
            var pointer = "/derived_features/" + EscapePointer(name);
            if (features.ContainsKey(name))
            {
                diagnostics.Error("FEATURE_DUPLICATE", artifactName, pointer, $"'{name}' is declared both as a feature and a derived feature.");
                continue;
            }

            if (!TryEntity(name, entities, broken, diagnostics, artifactName, pointer, out var entity))
            {
                unusable.Add(name);
                continue;
            }

            if (!ConfigTypes.TryParse(spec.Type, TypeSite.Feature, out var type, out _, out var typeError, out var typeHint))
            {
                diagnostics.Error("FEATURE_BAD_TYPE", artifactName, pointer + "/type", typeError, typeHint);
                unusable.Add(name);
                continue;
            }

            var hasDefault = spec.Default is not null;
            var def = ScalarValue.Missing;
            if (hasDefault && !ConfigValues.TryToScalar(spec.Default, type, out def, out var defError))
            {
                diagnostics.Error("FEATURE_BAD_DEFAULT", artifactName, pointer + "/default", $"The default of '{name}' {defError}.");
                unusable.Add(name);
                continue;
            }

            features[name] = new FeatureDefinition
            {
                Name = name,
                Entity = entity,
                Type = hasDefault ? type : type.AsNullable(),
                HasDefault = hasDefault,
                Default = def,
                Expr = spec.Expr,
                Owner = spec.Owner,
                Description = spec.Description,
                Pointer = pointer,
            };
        }

        // An entity keyed by a feature declared with an error has no key: it and its features fail with that error, and
        // so does an entity keyed by one of their features.
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var entity in new List<EntityDefinition>(entities.Values))
            {
                if (entity.KeyIsFeature && unusable.Contains(entity.CandidateKeyColumn!))
                {
                    entities.Remove(entity.Name);
                    foreach (var feature in new List<FeatureDefinition>(features.Values))
                    {
                        if (feature.Entity == entity)
                        {
                            features.Remove(feature.Name);
                            unusable.Add(feature.Name);
                        }
                    }

                    changed = true;
                }
            }
        }

        ValidateForeignKeys(entities, features, unusable, diagnostics, artifactName);
        return new FeatureCatalog(artifact.CatalogId, entities, features, unusable);
    }

    private static EntityDefinition? BuildEntity(
        string name, EntitySpec spec, IReadOnlyDictionary<string, ColumnType> requestSchema, IReadOnlySet<string>? unusableInputs,
        DiagnosticBag diagnostics, string artifactName)
    {
        var pointer = "/entities/" + EscapePointer(name);
        var keys = StringsOf(spec.Key);
        var keyTypes = StringsOf(spec.KeyType);
        if (keys.Length != keyTypes.Length || keys.Length is < 1 or > 2)
        {
            diagnostics.Error("ENTITY_BAD_KEY", artifactName, pointer, "'key' and 'key_type' must both have one element, or two for cross entities.");
            return null;
        }

        var parsedTypes = new EntityKeyType[keys.Length];
        for (var i = 0; i < keyTypes.Length; i++)
        {
            switch (keyTypes[i])
            {
                case "int64": parsedTypes[i] = EntityKeyType.Int64; break;
                case "string": parsedTypes[i] = EntityKeyType.String; break;
                default:
                    diagnostics.Error("ENTITY_BAD_KEY", artifactName, pointer + "/key_type", $"Unknown key type '{keyTypes[i]}'.", "use int64 or string");
                    return null;
            }
        }

        string? ctxInput = null;
        EntityKeyType? ctxType = null;
        string? candCol = null;
        EntityKeyType? candType = null;
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            if (key.StartsWith("ctx.request.", StringComparison.Ordinal))
            {
                var input = key["ctx.request.".Length..];
                if (!requestSchema.TryGetValue(input, out var inputType))
                {
                    if (unusableInputs is not null && unusableInputs.Contains(input))
                    {
                        return null; // its declaration's error is reported; the entity fails with it
                    }

                    diagnostics.Error("ENTITY_BAD_KEY", artifactName, pointer + "/key",
                        $"Request input '{input}' is not declared in the pipeline's request_schema.",
                        NameSuggestions.DidYouMean(input, requestSchema.Keys));
                    return null;
                }

                var expected = parsedTypes[i] == EntityKeyType.Int64 ? StorageKind.Int64 : StorageKind.String;
                if (inputType.Promoted().Kind != expected)
                {
                    diagnostics.Error("ENTITY_BAD_KEY", artifactName, pointer + "/key",
                        $"Request input '{input}' has type {inputType}, but the key type is {keyTypes[i]}.");
                    return null;
                }

                ctxInput = input;
                ctxType = parsedTypes[i];
            }
            else if (key.StartsWith("col.", StringComparison.Ordinal))
            {
                candCol = key["col.".Length..];
                candType = parsedTypes[i];
            }
            else
            {
                diagnostics.Error("ENTITY_BAD_KEY", artifactName, pointer + "/key",
                    $"Key '{key}' must reference a request input (ctx.request.*) or a candidate column (col.*).");
                return null;
            }
        }

        EntityKind kind;
        if (spec.Scope == "context")
        {
            if (ctxInput is null || candCol is not null)
            {
                diagnostics.Error("ENTITY_BAD_KEY", artifactName, pointer, "Context entities are keyed by exactly one request input (ctx.request.*).");
                return null;
            }

            kind = EntityKind.Context;
        }
        else if (spec.Scope == "candidate")
        {
            if (candCol is null)
            {
                diagnostics.Error("ENTITY_BAD_KEY", artifactName, pointer, "Candidate entities need a candidate key (col.*).");
                return null;
            }

            kind = ctxInput is not null ? EntityKind.Cross : candCol == "entity_id" ? EntityKind.Candidate : EntityKind.ForeignKey;
        }
        else
        {
            diagnostics.Error("ENTITY_BAD_SCOPE", artifactName, pointer + "/scope", $"Unknown scope '{spec.Scope}'.", "use context or candidate");
            return null;
        }

        return new EntityDefinition
        {
            Name = name,
            Kind = kind,
            ContextKeyInput = ctxInput,
            ContextKeyType = ctxType,
            CandidateKeyColumn = candCol,
            CandidateKeyType = candType,
        };
    }

    private static void ValidateForeignKeys(
        Dictionary<string, EntityDefinition> entities, Dictionary<string, FeatureDefinition> features, SortedSet<string> unusable,
        DiagnosticBag diagnostics, string artifactName)
    {
        foreach (var entity in entities.Values)
        {
            if (!entity.KeyIsFeature)
            {
                continue;
            }

            var pointer = "/entities/" + EscapePointer(entity.Name) + "/key";
            if (!features.TryGetValue(entity.CandidateKeyColumn!, out var keyFeature))
            {
                if (unusable.Contains(entity.CandidateKeyColumn!))
                {
                    continue; // declared with an error reported at its declaration
                }

                diagnostics.Error("ENTITY_BAD_KEY", artifactName, pointer,
                    $"Key column 'col.{entity.CandidateKeyColumn}' is not a declared feature.",
                    NameSuggestions.DidYouMean(entity.CandidateKeyColumn!, features.Keys));
                continue;
            }

            var expected = entity.CandidateKeyType == EntityKeyType.Int64 ? StorageKind.Int64 : StorageKind.String;
            if (keyFeature.IsContext || keyFeature.Type.Promoted().Kind != expected)
            {
                diagnostics.Error("ENTITY_BAD_KEY", artifactName, pointer,
                    $"Key column '{keyFeature.Name}' must be a candidate feature of type {expected}.");
                continue;
            }

            // Foreign-key chains must terminate (design 6.7: cycles are configuration errors).
            var seen = new HashSet<string>(StringComparer.Ordinal) { entity.Name };
            var cursor = keyFeature.Entity;
            while (cursor.KeyIsFeature)
            {
                if (!seen.Add(cursor.Name))
                {
                    diagnostics.Error("ENTITY_KEY_CYCLE", artifactName, pointer, $"Foreign-key cycle through entity '{cursor.Name}'.");
                    break;
                }

                if (!features.TryGetValue(cursor.CandidateKeyColumn!, out var next))
                {
                    break;
                }

                cursor = next.Entity;
            }
        }
    }

    private static bool TryEntity(
        string featureName, Dictionary<string, EntityDefinition> entities, HashSet<string> broken, DiagnosticBag diagnostics, string artifactName,
        string pointer, out EntityDefinition entity)
    {
        entity = null!;
        var dot = featureName.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == featureName.Length - 1)
        {
            diagnostics.Error("FEATURE_BAD_NAME", artifactName, pointer, $"Feature name '{featureName}' must be '<entity>.<feature>'.");
            return false;
        }

        var entityName = featureName[..dot];
        if (broken.Contains(entityName))
        {
            return false; // the entity's declaration has an error, reported there
        }

        if (!entities.TryGetValue(entityName, out entity!))
        {
            diagnostics.Error("FEATURE_UNKNOWN_ENTITY", artifactName, pointer, $"Entity '{entityName}' is not declared.",
                NameSuggestions.DidYouMean(entityName, entities.Keys));
            return false;
        }

        return true;
    }

    private static string[] StringsOf(System.Text.Json.Nodes.JsonNode node)
    {
        if (node is not System.Text.Json.Nodes.JsonArray array)
        {
            return new[] { node.GetValue<string>() };
        }

        var result = new string[array.Count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = array[i]?.GetValue<string>() ?? string.Empty;
        }

        return result;
    }

    internal static string EscapePointer(string token) => token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
