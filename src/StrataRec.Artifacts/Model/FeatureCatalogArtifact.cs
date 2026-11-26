using System.Text.Json.Nodes;

namespace StrataRec.Artifacts.Model;

/// <summary>Feature catalog: entities, physical features and derived features.</summary>
public sealed class FeatureCatalogArtifact
{
    public required string CatalogId { get; init; }

    public string? Description { get; init; }

    public Dictionary<string, EntitySpec> Entities { get; init; } = new();

    public Dictionary<string, FeatureSpec> Features { get; init; } = new();

    public Dictionary<string, DerivedFeatureSpec> DerivedFeatures { get; init; } = new();
}

/// <summary>An entity that features hang off: keyed by <see cref="Key"/>, or computed from <see cref="Args"/>.</summary>
/// <remarks>
/// A keyed entity declares only the key, an ordered tuple of parts. Whether the entity is per request or per candidate, and each
/// part's type, follow from it: a request input (<c>ctx.request.user_id</c>) has its declared type; <c>col.entity_id</c>
/// the pipeline's <c>entity_key_type</c>; a candidate feature (<c>col.item.creator_id</c>, a foreign key) that feature's
/// type. A key of request inputs only keys a context entity; any other a candidate entity. Declaring scope and key type
/// as well only gave them a way to disagree: a <c>key_type</c> of int64 on <c>col.entity_id</c> in a pipeline of string
/// ids made every lookup miss without an error. An entity with <see cref="Args"/> instead is per request: a service
/// computes its features from the arguments (IO step 5).
/// </remarks>
public sealed class EntitySpec
{
    /// <summary>
    /// What keys the entity: one reference, or a list of 1 to 8 distinct ones in the order of the key's parts. A part is a
    /// request input (<c>ctx.request.user_id</c>), the candidate id (<c>col.entity_id</c>), a candidate feature
    /// (<c>col.item.creator_id</c>) or a context feature (<c>ctx.identity.user_id</c>, fetched first), of type int64 or
    /// string, e.g. <c>["col.entity_id", "ctx.request.country"]</c>. A key of request inputs and context features only is
    /// one per request; a key with a missing part is not looked up. An entity declares a key or <see cref="Args"/>.
    /// </summary>
    public JsonNode? Key { get; init; }

    /// <summary>
    /// What a service computes the entity's features from, instead of a key: each argument's name, as the service's input,
    /// and the request input (<c>ctx.request.query</c>) or context feature (<c>ctx.user.embedding</c>, fetched first) it
    /// takes, of any type (a list, a float, a text). One call per request; a missing value is sent as missing. Its features
    /// come from a source bound to a service (<c>"mode": "service"</c>), e.g.
    /// <c>{ "query": "ctx.request.query", "recent": "ctx.request.recent_items" }</c>.
    /// </summary>
    public Dictionary<string, string>? Args { get; init; }

    public string? Description { get; init; }
}

/// <summary>A physical feature served by a data source.</summary>
public sealed class FeatureSpec
{
    public required string Type { get; init; }

    /// <summary>Logical data source name, bound per environment in <see cref="SourceBindingArtifact"/>.</summary>
    public required string Source { get; init; }

    /// <summary>Physical field name in the store.</summary>
    public required string Field { get; init; }

    /// <summary>Value used when the key legitimately has no value; makes the feature non-nullable.</summary>
    public JsonNode? Default { get; init; }

    public string? Owner { get; init; }

    public string? Description { get; init; }

    public bool? Deprecated { get; init; }
}

/// <summary>A feature computed by a ColumnExpr from other features.</summary>
public sealed class DerivedFeatureSpec
{
    public required string Type { get; init; }

    public required string Expr { get; init; }

    public JsonNode? Default { get; init; }

    public string? Owner { get; init; }

    public string? Description { get; init; }
}
