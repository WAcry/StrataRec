using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Core.Columns;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.Features.Catalog;

/// <summary>What one part of an entity key reads (design 6.2).</summary>
/// <remarks>A context feature (<c>ctx.&lt;entity&gt;.&lt;feature&gt;</c>) as a part is a later step: it would be one more kind,
/// shared like a request input (the plan reads both from the request's context values).</remarks>
public enum EntityKeyPartKind : byte
{
    /// <summary>A request input, <c>ctx.request.&lt;input&gt;</c>: one value per request, shared by every key of a batch.</summary>
    RequestInput = 1,

    /// <summary>The candidate id, <c>col.entity_id</c>.</summary>
    EntityId,

    /// <summary>A candidate-scope feature, <c>col.&lt;entity&gt;.&lt;feature&gt;</c> (physical or derived): fetched before the
    /// entity's features, as a foreign key.</summary>
    Feature,
}

/// <summary>One part of a compiled entity key: what it reads and its type.</summary>
public sealed class EntityKeyPartDefinition
{
    private EntityKeyPartDefinition(EntityKeyPartKind kind, string name, EntityKeyType? type)
    {
        Kind = kind;
        Name = name;
        Type = type;
    }

    public EntityKeyPartKind Kind { get; }

    /// <summary>The request input (<see cref="EntityKeyPartKind.RequestInput"/>), the feature's catalog name such as
    /// <c>item.creator_id</c> (<see cref="EntityKeyPartKind.Feature"/>), or <c>entity_id</c>.</summary>
    public string Name { get; }

    /// <summary>The part's type: the input's, the pipeline's <c>entity_key_type</c>, or the key feature's (set when the
    /// catalog's features are built); null until then.</summary>
    public EntityKeyType? Type { get; internal set; }

    /// <summary>As the catalog writes it: <c>ctx.request.country</c>, <c>col.entity_id</c>, <c>col.item.creator_id</c>.</summary>
    public string Reference => Kind switch
    {
        EntityKeyPartKind.RequestInput => "ctx.request." + Name,
        EntityKeyPartKind.EntityId => "col.entity_id",
        EntityKeyPartKind.Feature => "col." + Name,
    };

    /// <summary>One value per request (a context part), so every key of a batch shares it.</summary>
    public bool IsShared => Kind == EntityKeyPartKind.RequestInput;

    public static EntityKeyPartDefinition RequestInput(string input, EntityKeyType type) => new(EntityKeyPartKind.RequestInput, input, type);

    public static EntityKeyPartDefinition EntityId(EntityKeyType type) => new(EntityKeyPartKind.EntityId, "entity_id", type);

    /// <param name="feature">The key feature's catalog name, e.g. <c>item.creator_id</c>.</param>
    /// <param name="type">The key feature's type; null for the catalog to set when it builds the features.</param>
    public static EntityKeyPartDefinition Feature(string feature, EntityKeyType? type = null) => new(EntityKeyPartKind.Feature, feature, type);

    public override string ToString() => Reference;
}

/// <summary>A compiled entity declaration: its name and its key, an ordered tuple of 1 to 8 parts (design 6.2).</summary>
/// <remarks>
/// The key decides the rest. The entity is context-scoped (one key per request, <c>ctx.&lt;entity&gt;.*</c>) when every part
/// is a request input, candidate-scoped (one key per row, <c>col.&lt;entity&gt;.*</c>) otherwise; a part that is a feature
/// makes the entity's features wait for that feature, as a foreign key does.
/// </remarks>
public sealed class EntityDefinition
{
    private EntityKeyLayout? _layout;

    public required string Name { get; init; }

    /// <summary>The key's parts, in declared order: the order of the tuple.</summary>
    public required IReadOnlyList<EntityKeyPartDefinition> Key { get; init; }

    /// <summary>Whether the catalog wrote the key as a list (a part's pointer is then <c>/key/&lt;i&gt;</c>, even for one part).</summary>
    internal bool KeyIsList { get; init; }

    /// <summary>Whether a part reads a feature: the entity's features are then fetched after it, as a foreign key's are.</summary>
    public bool HasFeatureParts
    {
        get
        {
            foreach (var part in Key)
            {
                if (part.Kind == EntityKeyPartKind.Feature)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Every part is a request input: one key per request (asked per feature by every read, so worked out once).</summary>
    public bool IsContextScoped
    {
        get
        {
            if (_scope == 0)
            {
                var context = true;
                foreach (var part in Key)
                {
                    context &= part.IsShared;
                }

                _scope = context ? (byte)1 : (byte)2;
            }

            return _scope == 1;
        }
    }

    private byte _scope; // 0 = not worked out yet, 1 = context, 2 = candidate

    /// <summary>
    /// The key's layout: each part's reference, type and shared flag, in order. Available once every part has its type
    /// (a key feature's is set when the catalog builds its features).
    /// </summary>
    /// <exception cref="InvalidOperationException">A part's type is not known.</exception>
    public EntityKeyLayout KeyLayout => _layout ??= BuildLayout();

    /// <summary>An entity keyed by <paramref name="parts"/>, in order.</summary>
    public static EntityDefinition Of(string name, params EntityKeyPartDefinition[] parts) => new() { Name = name, Key = parts };

    private EntityKeyLayout BuildLayout()
    {
        var parts = new EntityKeyPart[Key.Count];
        for (var p = 0; p < parts.Length; p++)
        {
            var part = Key[p];
            parts[p] = new EntityKeyPart(
                part.Reference,
                part.Type ?? throw new InvalidOperationException($"Key part '{part.Reference}' of entity '{Name}' has no type yet."),
                part.IsShared);
        }

        return new EntityKeyLayout(parts);
    }
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
    /// <param name="requestSchema">Request input name → type: the type of a context key.</param>
    /// <param name="entityKeyType">The pipeline's <c>entity_key_type</c>: the type of <c>col.entity_id</c> as a key.</param>
    /// <param name="unusableInputs">Request inputs declared with an error reported already (an unknown type): an entity
    /// keyed by one fails without a second error, and so do its features.</param>
    public static FeatureCatalog Build(
        FeatureCatalogArtifact artifact,
        IReadOnlyDictionary<string, ColumnType> requestSchema,
        EntityKeyType entityKeyType,
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
            var entity = BuildEntity(name, spec, requestSchema, entityKeyType, unusableInputs, diagnostics, artifactName);
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
                Default = ColumnValues.AsStored(def, type), // as a column of the type holds it, once, not per request
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
                Default = ColumnValues.AsStored(def, type), // as a column of the type holds it, once, not per request
                Expr = spec.Expr,
                Owner = spec.Owner,
                Description = spec.Description,
                Pointer = pointer,
            };
        }

        // A key part that reads a feature takes the feature's type. An entity keyed by a feature that cannot key it (not
        // declared, of another type, a context feature, or declared with an error) has no key: it and its features fail,
        // and so does an entity keyed by one of their features.
        ResolveFeatureParts(entities, features, unusable, diagnostics, artifactName);
        ValidateKeyCycles(entities, features, diagnostics, artifactName);
        return new FeatureCatalog(artifact.CatalogId, entities, features, unusable);
    }

    /// <summary>The hint of every key that is not a list of 1 to 8 distinct references.</summary>
    private const string KeyShapeHint =
        "\"key\" is one reference or a list of 1 to 8 distinct ones, e.g. \"col.entity_id\" or [\"col.entity_id\", \"ctx.request.country\"]";

    /// <summary>
    /// Parses an entity's key: one reference, or a list of 1 to <see cref="EntityKeyLayout.MaxParts"/> references in the
    /// order of the tuple. Every problem is an <c>ENTITY_BAD_KEY</c> at the part (<c>/key</c> for a single reference,
    /// <c>/key/&lt;i&gt;</c> in a list) with a hint; a key feature's type is checked when the features are built.
    /// </summary>
    private static EntityDefinition? BuildEntity(
        string name, EntitySpec spec, IReadOnlyDictionary<string, ColumnType> requestSchema, EntityKeyType entityKeyType,
        IReadOnlySet<string>? unusableInputs, DiagnosticBag diagnostics, string artifactName)
    {
        var pointer = "/entities/" + EscapePointer(name) + "/key";
        var list = spec.Key as System.Text.Json.Nodes.JsonArray;
        var count = list?.Count ?? 1;
        if (count is < 1 or > EntityKeyLayout.MaxParts)
        {
            diagnostics.Error("ENTITY_BAD_KEY", artifactName, pointer,
                $"The key of '{name}' has {count} parts; a key has 1 to {EntityKeyLayout.MaxParts}.", KeyShapeHint);
            return null;
        }

        var parts = new EntityKeyPartDefinition[count];
        for (var i = 0; i < count; i++)
        {
            var at = list is null ? pointer : pointer + "/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var node = list is null ? spec.Key : list[i];
            if (node is not System.Text.Json.Nodes.JsonValue value || !value.TryGetValue<string>(out var reference))
            {
                diagnostics.Error("ENTITY_BAD_KEY", artifactName, at,
                    $"A key part is a reference such as \"col.entity_id\", got {node?.ToJsonString() ?? "null"}.", KeyShapeHint);
                return null;
            }

            if (ParsePart(reference, at, requestSchema, entityKeyType, unusableInputs, diagnostics, artifactName) is not { } part)
            {
                return null;
            }

            for (var j = 0; j < i; j++)
            {
                if (string.Equals(parts[j].Reference, part.Reference, StringComparison.Ordinal))
                {
                    diagnostics.Error("ENTITY_BAD_KEY", artifactName, at,
                        $"Key part '{reference}' repeats part {j} of the key of '{name}'.", "list each part once");
                    return null;
                }
            }

            parts[i] = part;
        }

        return new EntityDefinition { Name = name, Key = parts, KeyIsList = list is not null };
    }

    /// <summary>
    /// One key part: <c>ctx.request.&lt;input&gt;</c> (an int64 or string input, int32 promoted), <c>col.entity_id</c>, or
    /// <c>col.&lt;entity&gt;.&lt;feature&gt;</c> (typed later, with the features). A context feature is refused for now: as a
    /// part it would be read from the request's context values like a request input, once the context prefetch orders it
    /// before the entities keyed by it.
    /// </summary>
    /// <remarks>Null when the part is wrong (reported here) or reads a request input declared with an error (reported there).</remarks>
    private static EntityKeyPartDefinition? ParsePart(
        string reference, string at, IReadOnlyDictionary<string, ColumnType> requestSchema, EntityKeyType entityKeyType,
        IReadOnlySet<string>? unusableInputs, DiagnosticBag diagnostics, string artifactName)
    {
        const string Parts = "a key part is a request input (ctx.request.*), the candidate id (col.entity_id) or a candidate feature (col.<entity>.<feature>)";
        if (reference.StartsWith("ctx.request.", StringComparison.Ordinal))
        {
            var input = reference["ctx.request.".Length..];
            if (!requestSchema.TryGetValue(input, out var inputType))
            {
                if (unusableInputs is not null && unusableInputs.Contains(input))
                {
                    return null; // its declaration's error is reported; the entity fails with it
                }

                diagnostics.Error("ENTITY_BAD_KEY", artifactName, at,
                    $"Request input '{input}' is not declared in the pipeline's request_schema.",
                    NameSuggestions.DidYouMean(input, requestSchema.Keys) ?? "declare the input in the pipeline's request_schema");
                return null;
            }

            if (KeyTypeOf(inputType) is not { } type)
            {
                diagnostics.Error("ENTITY_BAD_KEY", artifactName, at,
                    $"Request input '{input}' has type {inputType}; an entity key part is an int64 or a string.",
                    "key the entity by an int64 or string input");
                return null;
            }

            return EntityKeyPartDefinition.RequestInput(input, type);
        }

        if (reference.StartsWith("ctx.", StringComparison.Ordinal))
        {
            diagnostics.Error("ENTITY_BAD_KEY", artifactName, at,
                $"Key part '{reference}' is a context feature; an entity key cannot read a context feature yet.",
                "key the entity by the request input the context feature is keyed by (ctx.request.*)");
            return null;
        }

        if (reference == "col.entity_id")
        {
            return EntityKeyPartDefinition.EntityId(entityKeyType);
        }

        if (reference.StartsWith("col.", StringComparison.Ordinal) && reference.Length > "col.".Length)
        {
            return EntityKeyPartDefinition.Feature(reference["col.".Length..]); // typed with the features
        }

        diagnostics.Error("ENTITY_BAD_KEY", artifactName, at, $"Key part '{reference}' is not a reference an entity can be keyed by.", Parts);
        return null;
    }

    /// <summary>The key type a value of <paramref name="type"/> keys an entity with; null when it cannot be a key.</summary>
    private static EntityKeyType? KeyTypeOf(ColumnType type) => type.Promoted().Kind switch
    {
        StorageKind.Int64 => EntityKeyType.Int64,
        StorageKind.String => EntityKeyType.String,
        _ => null,
    };

    /// <summary>
    /// Types every key part that reads a feature (design 6.3: a key feature's type is the part's). A part whose feature is
    /// not declared, is a context feature or is of another type than int64 or string is an <c>ENTITY_BAD_KEY</c> at the
    /// part; one whose feature was declared with an error fails silently (reported there). An entity with such a part has
    /// no key: it is removed with its features, which become unusable, and an entity keyed by one of those fails in turn.
    /// </summary>
    private static void ResolveFeatureParts(
        Dictionary<string, EntityDefinition> entities, Dictionary<string, FeatureDefinition> features, SortedSet<string> unusable,
        DiagnosticBag diagnostics, string artifactName)
    {
        var names = new List<string>(entities.Keys);
        names.Sort(StringComparer.Ordinal);
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var name in names)
            {
                if (!entities.TryGetValue(name, out var entity) || ResolveParts(entity, features, unusable, diagnostics, artifactName))
                {
                    continue;
                }

                entities.Remove(name);
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

    /// <summary>Types the entity's feature parts; false (reported, or silent for a feature declared with an error) when one cannot key it.</summary>
    private static bool ResolveParts(
        EntityDefinition entity, Dictionary<string, FeatureDefinition> features, SortedSet<string> unusable, DiagnosticBag diagnostics, string artifactName)
    {
        for (var i = 0; i < entity.Key.Count; i++)
        {
            var part = entity.Key[i];
            if (part.Kind != EntityKeyPartKind.Feature || part.Type is not null)
            {
                continue;
            }

            var at = KeyPointer(entity, i);
            if (!features.TryGetValue(part.Name, out var keyFeature))
            {
                if (!unusable.Contains(part.Name))
                {
                    diagnostics.Error("ENTITY_BAD_KEY", artifactName, at, $"Key part 'col.{part.Name}' is not a declared feature.",
                        NameSuggestions.DidYouMean(part.Name, features.Keys) ?? "key the entity by a declared candidate feature, or declare it");
                }

                return false;
            }

            if (keyFeature.IsContext || KeyTypeOf(keyFeature.Type) is not { } keyType)
            {
                diagnostics.Error("ENTITY_BAD_KEY", artifactName, at,
                    $"Key part '{keyFeature.Name}' must be a candidate feature of type int64 or string, got {(keyFeature.IsContext ? "a context feature" : keyFeature.Type.ToString())}.",
                    "key the entity by a candidate feature holding int64 or string ids");
                return false;
            }

            part.Type = keyType;
        }

        return true;
    }

    /// <summary>
    /// Key chains must end (design 6.7: cycles are configuration errors): an entity whose key reads a feature of an entity
    /// whose key leads back to one already on the way is an <c>ENTITY_KEY_CYCLE</c>, at the part that leads into it. Every
    /// feature part is an edge, so a cycle through any part of a composite key is found. An entity found to lead into no
    /// cycle is remembered for the whole catalog and not walked again: without it, eight parts reading eight features of
    /// the same parent walk the parent's chain eight times each, and a valid chain of ten such entities took 6 s (review 76).
    /// </summary>
    private static void ValidateKeyCycles(
        Dictionary<string, EntityDefinition> entities, Dictionary<string, FeatureDefinition> features, DiagnosticBag diagnostics, string artifactName)
    {
        var names = new List<string>(entities.Keys);
        names.Sort(StringComparer.Ordinal);
        var acyclic = new HashSet<string>(StringComparer.Ordinal); // entities whose key leads into no cycle
        foreach (var name in names)
        {
            var entity = entities[name];
            var path = new HashSet<string>(StringComparer.Ordinal) { entity.Name };
            for (var i = 0; i < entity.Key.Count; i++)
            {
                if (KeyEntity(entity.Key[i], features) is { } next && CycleFrom(next, path, acyclic, features) is { } through)
                {
                    diagnostics.Error("ENTITY_KEY_CYCLE", artifactName, KeyPointer(entity, i),
                        $"Key part '{entity.Key[i].Reference}' of '{entity.Name}' leads to a key cycle through entity '{through}'.",
                        "key one of the entities on the cycle by something that does not depend on it");
                    break;
                }
            }
        }

        // Whether a cycle can be reached from an entity does not depend on where the walk started, so what is found
        // acyclic once holds for every later walk.
        static string? CycleFrom(EntityDefinition entity, HashSet<string> path, HashSet<string> acyclic, Dictionary<string, FeatureDefinition> features)
        {
            if (acyclic.Contains(entity.Name))
            {
                return null;
            }

            if (!path.Add(entity.Name))
            {
                return entity.Name;
            }

            foreach (var part in entity.Key)
            {
                if (KeyEntity(part, features) is { } next && CycleFrom(next, path, acyclic, features) is { } through)
                {
                    return through;
                }
            }

            path.Remove(entity.Name);
            acyclic.Add(entity.Name);
            return null;
        }

        static EntityDefinition? KeyEntity(EntityKeyPartDefinition part, Dictionary<string, FeatureDefinition> features) =>
            part.Kind == EntityKeyPartKind.Feature && features.TryGetValue(part.Name, out var feature) ? feature.Entity : null;
    }

    /// <summary>The pointer of key part <paramref name="part"/>: <c>/key</c> for a one-part key written as one reference.</summary>
    internal static string KeyPointer(EntityDefinition entity, int part) =>
        "/entities/" + EscapePointer(entity.Name) + "/key" + (entity.Key.Count == 1 && !entity.KeyIsList ? string.Empty : "/" + part.ToString(System.Globalization.CultureInfo.InvariantCulture));

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

    internal static string EscapePointer(string token) => token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
