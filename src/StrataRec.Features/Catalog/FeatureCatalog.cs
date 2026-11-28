using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Core.Columns;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.Features.Catalog;

/// <summary>What one part of an entity key reads.</summary>
public enum EntityKeyPartKind : byte
{
    /// <summary>A request input, <c>ctx.request.&lt;input&gt;</c>: one value per request, shared by every key of a batch.</summary>
    RequestInput = 1,

    /// <summary>The candidate id, <c>col.entity_id</c>.</summary>
    EntityId,

    /// <summary>A candidate-scope feature, <c>col.&lt;entity&gt;.&lt;feature&gt;</c> (physical or derived): fetched before the
    /// entity's features, as a foreign key.</summary>
    Feature,

    /// <summary>
    /// A context-scope feature, <c>ctx.&lt;entity&gt;.&lt;feature&gt;</c> (physical or derived): one value per request, shared
    /// like a request input, which the context prefetch stores before it fetches the entities keyed by it (a key resolved by
    /// another source, such as a user id looked up from a login key).
    /// </summary>
    ContextFeature,
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
    /// <c>item.creator_id</c> or <c>identity.user_id</c> (<see cref="EntityKeyPartKind.Feature"/>,
    /// <see cref="EntityKeyPartKind.ContextFeature"/>), or <c>entity_id</c>.</summary>
    public string Name { get; }

    /// <summary>Whether the part reads a feature (candidate or context): the entity's features then wait for it.</summary>
    public bool IsFeature => Kind is EntityKeyPartKind.Feature or EntityKeyPartKind.ContextFeature;

    /// <summary>The part's type: the input's, the pipeline's <c>entity_key_type</c>, or the key feature's (set when the
    /// catalog's features are built); null until then.</summary>
    public EntityKeyType? Type { get; internal set; }

    /// <summary>As the catalog writes it: <c>ctx.request.country</c>, <c>col.entity_id</c>, <c>col.item.creator_id</c>,
    /// <c>ctx.identity.user_id</c>.</summary>
    public string Reference => Kind switch
    {
        EntityKeyPartKind.RequestInput => "ctx.request." + Name,
        EntityKeyPartKind.EntityId => "col.entity_id",
        EntityKeyPartKind.Feature => "col." + Name,
        EntityKeyPartKind.ContextFeature => "ctx." + Name,
    };

    /// <summary>One value per request (a context part: a request input or a context feature), so every key of a batch shares it.</summary>
    public bool IsShared => Kind is EntityKeyPartKind.RequestInput or EntityKeyPartKind.ContextFeature;

    public static EntityKeyPartDefinition RequestInput(string input, EntityKeyType type) => new(EntityKeyPartKind.RequestInput, input, type);

    public static EntityKeyPartDefinition EntityId(EntityKeyType type) => new(EntityKeyPartKind.EntityId, "entity_id", type);

    /// <param name="feature">The key feature's catalog name, e.g. <c>item.creator_id</c>.</param>
    /// <param name="type">The key feature's type; null for the catalog to set when it builds the features.</param>
    public static EntityKeyPartDefinition Feature(string feature, EntityKeyType? type = null) => new(EntityKeyPartKind.Feature, feature, type);

    /// <param name="feature">The context key feature's catalog name, e.g. <c>identity.user_id</c>.</param>
    /// <param name="type">The key feature's type; null for the catalog to set when it builds the features.</param>
    public static EntityKeyPartDefinition ContextFeature(string feature, EntityKeyType? type = null) => new(EntityKeyPartKind.ContextFeature, feature, type);

    public override string ToString() => Reference;
}

/// <summary>
/// One argument of an entity a service computes: the name the service gets it under, the request input or
/// context feature it takes, and that value's type.
/// </summary>
public sealed class EntityArgDefinition
{
    internal EntityArgDefinition(string name, EntityKeyPartKind kind, string target, ColumnType? type, string pointer)
    {
        Name = name;
        Kind = kind;
        Target = target;
        Type = type;
        Pointer = pointer;
    }

    /// <summary>The service's input.</summary>
    public string Name { get; }

    /// <summary><see cref="EntityKeyPartKind.RequestInput"/> or <see cref="EntityKeyPartKind.ContextFeature"/>.</summary>
    public EntityKeyPartKind Kind { get; }

    /// <summary>The request input's name, or the context feature's catalog name (<c>user.embedding</c>).</summary>
    public string Target { get; }

    /// <summary>The argument as the catalog writes it: <c>ctx.request.&lt;input&gt;</c> or <c>ctx.&lt;entity&gt;.&lt;feature&gt;</c>.</summary>
    public string Reference => Kind == EntityKeyPartKind.RequestInput ? "ctx.request." + Target : "ctx." + Target;

    public bool IsFeature => Kind == EntityKeyPartKind.ContextFeature;

    /// <summary>The value's declared type: the request input's, or the context feature's (set when the catalog builds the features).</summary>
    public ColumnType? Type { get; internal set; }

    /// <summary>Where the catalog declares it: <c>/entities/&lt;entity&gt;/args/&lt;name&gt;</c>.</summary>
    public string Pointer { get; }

    public override string ToString() => Name + " = " + Reference;
}

/// <summary>A feature an entity's key or arguments read: fetched (or computed) before the entity's features.</summary>
/// <param name="Feature">The feature's catalog name.</param>
/// <param name="Context">Whether it is a context feature (<c>ctx.</c>), as the reference says.</param>
/// <param name="Reference">The key part or argument as the catalog writes it.</param>
/// <param name="Pointer">Where the catalog declares the part or argument.</param>
public readonly record struct EntityFeatureRead(string Feature, bool Context, string Reference, string Pointer);

/// <summary>
/// A compiled entity declaration: its name and its key, an ordered tuple of 1 to 8 parts; or, for an entity
/// a service computes, its arguments and no key.
/// </summary>
/// <remarks>
/// The key decides the rest. The entity is context-scoped (one key per request, <c>ctx.&lt;entity&gt;.*</c>) when every part
/// is a context part (a request input or a context feature), candidate-scoped (one key per row, <c>col.&lt;entity&gt;.*</c>)
/// otherwise; a part that is a feature makes the entity's features wait for that feature, as a foreign key does, on either
/// side: a context entity keyed by a context feature is fetched once the source of that feature has answered.
/// </remarks>
public sealed class EntityDefinition
{
    private EntityKeyLayout? _layout;

    public required string Name { get; init; }

    /// <summary>The key's parts, in declared order: the order of the tuple. Empty for an entity with <see cref="Args"/>.</summary>
    public required IReadOnlyList<EntityKeyPartDefinition> Key { get; init; }

    /// <summary>
    /// What a service computes the entity's features from, in declared order; empty for a keyed entity. An
    /// entity with arguments is per request, and its features come from a source bound to a service.
    /// </summary>
    public IReadOnlyList<EntityArgDefinition> Args { get; init; } = [];

    public bool HasArgs => Args.Count > 0;

    /// <summary>
    /// The features the key's parts and the arguments read, in declared order: they are fetched before the entity's
    /// features, whose state they decide, and an entity reached again through them is a cycle.
    /// </summary>
    public IReadOnlyList<EntityFeatureRead> FeatureReads => _reads ??= BuildReads();

    private EntityFeatureRead[]? _reads;

    internal bool KeyIsList { get; init; }

    /// <summary>Whether a part or an argument reads a feature: the entity's features are then fetched after it, as a foreign key's are.</summary>
    public bool HasFeatureParts => FeatureReads.Count > 0;

    /// <summary>Every part is a context part (a request input or a context feature), or the entity has arguments: one
    /// key (or call) per request.</summary>
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

    private byte _scope;

    /// <summary>
    /// The key's layout: each part's reference, type and shared flag, in order. Available once every part has its type
    /// (a key feature's is set when the catalog builds its features).
    /// </summary>
    /// <exception cref="InvalidOperationException">A part's type is not known.</exception>
    public EntityKeyLayout KeyLayout => _layout ??= BuildLayout();

    /// <summary>An entity keyed by <paramref name="parts"/>, in order.</summary>
    public static EntityDefinition Of(string name, params EntityKeyPartDefinition[] parts) => new() { Name = name, Key = parts };

    /// <summary>The catalog pointer of key part <paramref name="part"/>, where a problem with it is reported:
    /// <c>/entities/&lt;name&gt;/key/&lt;i&gt;</c>, or <c>/entities/&lt;name&gt;/key</c> for a key written as one reference.</summary>
    public string KeyPointer(int part) =>
        "/entities/" + FeatureCatalog.EscapePointer(Name) + "/key"
        + (Key.Count == 1 && !KeyIsList ? string.Empty : "/" + part.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The key part or argument that reads feature <paramref name="feature"/>; null when none does.</summary>
    public EntityFeatureRead? ReadOf(FeatureDefinition feature)
    {
        foreach (var read in FeatureReads)
        {
            if (read.Context == feature.IsContext && string.Equals(read.Feature, feature.Name, StringComparison.Ordinal))
            {
                return read;
            }
        }

        return null;
    }

    private EntityFeatureRead[] BuildReads()
    {
        var reads = new List<EntityFeatureRead>();
        for (var p = 0; p < Key.Count; p++)
        {
            var part = Key[p];
            if (part.IsFeature)
            {
                reads.Add(new EntityFeatureRead(part.Name, part.Kind == EntityKeyPartKind.ContextFeature, part.Reference, KeyPointer(p)));
            }
        }

        foreach (var arg in Args)
        {
            if (arg.IsFeature)
            {
                reads.Add(new EntityFeatureRead(arg.Target, true, arg.Reference, arg.Pointer));
            }
        }

        return reads.ToArray();
    }

    private EntityKeyLayout BuildLayout()
    {
        if (HasArgs)
        {
            throw new InvalidOperationException($"Entity '{Name}' is computed from its arguments: it has no key.");
        }

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
    /// What a row or request gets when the feature has no value: the default, or missing. The one
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
/// Validated feature catalog: entities, physical features and derived features, keyed by name.
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
        var broken = new HashSet<string>(StringComparer.Ordinal);
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
                Default = ColumnValues.AsStored(def, type),
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
                Default = ColumnValues.AsStored(def, type),
                Expr = spec.Expr,
                Owner = spec.Owner,
                Description = spec.Description,
                Pointer = pointer,
            };
        }

        // An entity keyed by a feature that cannot key it (not declared, of another type, of the other scope than its
        // reference says, or declared with an error) has no key: it and its features fail, and so does an entity keyed
        // by one of their features.
        ResolveFeatureParts(entities, features, unusable, diagnostics, artifactName);
        ValidateKeyCycles(entities, features, diagnostics, artifactName);
        return new FeatureCatalog(artifact.CatalogId, entities, features, unusable);
    }

    private const string KeyShapeHint =
        "\"key\" is one reference or a list of 1 to 8 distinct ones, e.g. \"col.entity_id\" or [\"col.entity_id\", \"ctx.request.country\"]";

    private static EntityDefinition? BuildEntity(
        string name, EntitySpec spec, IReadOnlyDictionary<string, ColumnType> requestSchema, EntityKeyType entityKeyType,
        IReadOnlySet<string>? unusableInputs, DiagnosticBag diagnostics, string artifactName)
    {
        var entityPointer = "/entities/" + EscapePointer(name);
        if ((spec.Key is null) == (spec.Args is null))
        {
            diagnostics.Error(spec.Key is null ? "ENTITY_BAD_KEY" : "ENTITY_BAD_ARGS", artifactName, entityPointer,
                spec.Key is null ? $"Entity '{name}' declares neither a key nor args." : $"Entity '{name}' declares both a key and args.",
                "a stored entity has a \"key\" (looked up by a source); one a service computes has \"args\" (the service's inputs)");
            return null;
        }

        if (spec.Args is { } args)
        {
            return BuildComputedEntity(name, args, entityPointer + "/args", requestSchema, unusableInputs, diagnostics, artifactName);
        }

        var pointer = entityPointer + "/key";
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

    private static EntityDefinition? BuildComputedEntity(
        string name, Dictionary<string, string> args, string pointer, IReadOnlyDictionary<string, ColumnType> requestSchema,
        IReadOnlySet<string>? unusableInputs, DiagnosticBag diagnostics, string artifactName)
    {
        const string Hint = "an argument is a request input (\"ctx.request.query\") or a context feature (\"ctx.user.embedding\")";
        if (args.Count == 0)
        {
            diagnostics.Error("ENTITY_BAD_ARGS", artifactName, pointer, $"Entity '{name}' has no args.",
                "list what its service computes its features from, e.g. { \"query\": \"ctx.request.query\" }, or key the entity");
            return null;
        }

        var parsed = new List<EntityArgDefinition>(args.Count);
        foreach (var (arg, reference) in args)
        {
            var at = pointer + "/" + EscapePointer(arg);
            if (arg.Length == 0)
            {
                diagnostics.Error("ENTITY_BAD_ARGS", artifactName, at, $"An argument of '{name}' has no name.", "name each argument as the service's input");
                return null;
            }

            if (reference.StartsWith("ctx.request.", StringComparison.Ordinal))
            {
                var input = reference["ctx.request.".Length..];
                if (!requestSchema.TryGetValue(input, out var inputType))
                {
                    if (unusableInputs is null || !unusableInputs.Contains(input))
                    {
                        diagnostics.Error("ENTITY_BAD_ARGS", artifactName, at, $"Request input '{input}' is not declared in the pipeline's request_schema.",
                            NameSuggestions.DidYouMean(input, requestSchema.Keys) ?? "declare the input in the pipeline's request_schema");
                    }

                    return null;
                }

                parsed.Add(new EntityArgDefinition(arg, EntityKeyPartKind.RequestInput, input, inputType, at));
                continue;
            }

            if (reference.StartsWith("ctx.", StringComparison.Ordinal) && reference.Length > "ctx.".Length)
            {
                parsed.Add(new EntityArgDefinition(arg, EntityKeyPartKind.ContextFeature, reference["ctx.".Length..], null, at));
                continue;
            }

            diagnostics.Error("ENTITY_BAD_ARGS", artifactName, at,
                reference.StartsWith("col.", StringComparison.Ordinal)
                    ? $"Argument '{arg}' of '{name}' reads '{reference}', a candidate value; an entity with args is computed once per request."
                    : $"Argument '{arg}' of '{name}' is '{reference}', not a value it can take.",
                Hint);
            return null;
        }

        return new EntityDefinition { Name = name, Key = [], Args = parsed };
    }

    private static EntityKeyPartDefinition? ParsePart(
        string reference, string at, IReadOnlyDictionary<string, ColumnType> requestSchema, EntityKeyType entityKeyType,
        IReadOnlySet<string>? unusableInputs, DiagnosticBag diagnostics, string artifactName)
    {
        const string Parts = "a key part is a request input (ctx.request.*), the candidate id (col.entity_id), a candidate feature (col.<entity>.<feature>) or a context feature (ctx.<entity>.<feature>)";
        if (reference.StartsWith("ctx.request.", StringComparison.Ordinal))
        {
            var input = reference["ctx.request.".Length..];
            if (!requestSchema.TryGetValue(input, out var inputType))
            {
                if (unusableInputs is not null && unusableInputs.Contains(input))
                {
                    return null;
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

        if (reference == "col.entity_id")
        {
            return EntityKeyPartDefinition.EntityId(entityKeyType);
        }

        if (reference.StartsWith("ctx.", StringComparison.Ordinal) && reference.Length > "ctx.".Length)
        {
            return EntityKeyPartDefinition.ContextFeature(reference["ctx.".Length..]);
        }

        if (reference.StartsWith("col.", StringComparison.Ordinal) && reference.Length > "col.".Length)
        {
            return EntityKeyPartDefinition.Feature(reference["col.".Length..]);
        }

        diagnostics.Error("ENTITY_BAD_KEY", artifactName, at, $"Key part '{reference}' is not a reference an entity can be keyed by.", Parts);
        return null;
    }

    private static EntityKeyType? KeyTypeOf(ColumnType type) => type.Promoted().Kind switch
    {
        StorageKind.Int64 => EntityKeyType.Int64,
        StorageKind.String => EntityKeyType.String,
        _ => null,
    };

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

    private static bool ResolveParts(
        EntityDefinition entity, Dictionary<string, FeatureDefinition> features, SortedSet<string> unusable, DiagnosticBag diagnostics, string artifactName)
    {
        foreach (var arg in entity.Args)
        {
            if (!arg.IsFeature || arg.Type is not null)
            {
                continue;
            }

            if (!features.TryGetValue(arg.Target, out var argFeature))
            {
                if (!unusable.Contains(arg.Target))
                {
                    diagnostics.Error("ENTITY_BAD_ARGS", artifactName, arg.Pointer, $"Argument '{arg.Name}' reads '{arg.Reference}', which is not a declared feature.",
                        NameSuggestions.DidYouMean(arg.Target, features.Keys) ?? "read a declared context feature, or declare it");
                }

                return false;
            }

            if (!argFeature.IsContext)
            {
                diagnostics.Error("ENTITY_BAD_ARGS", artifactName, arg.Pointer,
                    $"Argument '{arg.Name}' reads '{argFeature.Name}', a candidate feature; an entity with args is computed once per request.",
                    "read a request input or a context feature");
                return false;
            }

            arg.Type = argFeature.Type;
        }

        for (var i = 0; i < entity.Key.Count; i++)
        {
            var part = entity.Key[i];
            if (!part.IsFeature || part.Type is not null)
            {
                continue;
            }

            var context = part.Kind == EntityKeyPartKind.ContextFeature;
            var scope = context ? "context" : "candidate";
            var at = KeyPointer(entity, i);
            if (!features.TryGetValue(part.Name, out var keyFeature))
            {
                if (!unusable.Contains(part.Name))
                {
                    diagnostics.Error("ENTITY_BAD_KEY", artifactName, at, $"Key part '{part.Reference}' is not a declared feature.",
                        NameSuggestions.DidYouMean(part.Name, features.Keys) ?? $"key the entity by a declared {scope} feature, or declare it");
                }

                return false;
            }

            if (keyFeature.IsContext != context)
            {
                diagnostics.Error("ENTITY_BAD_KEY", artifactName, at,
                    $"Key part '{part.Reference}' names {(keyFeature.IsContext ? "a context" : "a candidate")} feature, which is written {keyFeature.ExpressionName}.",
                    $"write the part as \"{keyFeature.ExpressionName}\"");
                return false;
            }

            if (KeyTypeOf(keyFeature.Type) is not { } keyType)
            {
                diagnostics.Error("ENTITY_BAD_KEY", artifactName, at,
                    $"Key part '{keyFeature.Name}' must be a {scope} feature of type int64 or string, got {keyFeature.Type}.",
                    $"key the entity by a {scope} feature holding int64 or string ids");
                return false;
            }

            part.Type = keyType;
        }

        return true;
    }

    /// <summary>
    /// Reports <c>ENTITY_KEY_CYCLE</c> at the key part that leads into a cycle. An entity found to lead into none is
    /// not walked again, from any start. A cycle through a derived feature is the plan compiler's to report: the
    /// catalog does not compile expressions.
    /// </summary>
    private static void ValidateKeyCycles(
        Dictionary<string, EntityDefinition> entities, Dictionary<string, FeatureDefinition> features, DiagnosticBag diagnostics, string artifactName)
    {
        var names = new List<string>(entities.Keys);
        names.Sort(StringComparer.Ordinal);
        var acyclic = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var entity = entities[name];
            var path = new HashSet<string>(StringComparer.Ordinal) { entity.Name };
            foreach (var read in entity.FeatureReads)
            {
                if (KeyEntity(read, features) is { } next && CycleFrom(next, path, acyclic, features) is { } through)
                {
                    diagnostics.Error("ENTITY_KEY_CYCLE", artifactName, read.Pointer,
                        $"{(read.Pointer.Contains("/args/", StringComparison.Ordinal) ? "Argument" : "Key part")} '{read.Reference}' of '{entity.Name}' leads to a key cycle through entity '{through}'.",
                        "key (or compute) one of the entities on the cycle from something that does not depend on it");
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

            foreach (var read in entity.FeatureReads)
            {
                if (KeyEntity(read, features) is { } next && CycleFrom(next, path, acyclic, features) is { } through)
                {
                    return through;
                }
            }

            path.Remove(entity.Name);
            acyclic.Add(entity.Name);
            return null;
        }

        static EntityDefinition? KeyEntity(EntityFeatureRead read, Dictionary<string, FeatureDefinition> features) =>
            features.TryGetValue(read.Feature, out var feature) && !feature.IsDerived ? feature.Entity : null;
    }

    internal static string KeyPointer(EntityDefinition entity, int part) => entity.KeyPointer(part);

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
            return false;
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
