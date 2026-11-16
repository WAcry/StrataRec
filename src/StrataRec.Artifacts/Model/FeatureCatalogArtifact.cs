using System.Text.Json.Nodes;

namespace StrataRec.Artifacts.Model;

/// <summary>Feature catalog (design 6.3): entities, physical features and derived features.</summary>
public sealed class FeatureCatalogArtifact
{
    public required string CatalogId { get; init; }

    public string? Description { get; init; }

    public Dictionary<string, EntitySpec> Entities { get; init; } = new();

    public Dictionary<string, FeatureSpec> Features { get; init; } = new();

    public Dictionary<string, DerivedFeatureSpec> DerivedFeatures { get; init; } = new();
}

/// <summary>An entity that features hang off (design 6.2).</summary>
/// <remarks>
/// Only the key is declared. Whether the entity is per request or per candidate, and the key's type, follow from it: a
/// request input (<c>ctx.request.user_id</c>) keys a context entity and has its declared type; <c>col.entity_id</c> keys
/// the candidates and has the pipeline's <c>entity_key_type</c>; a feature (<c>col.item.creator_id</c>) is a foreign key
/// of that feature's type; a request input and a candidate column together key a cross entity. Declaring scope and key
/// type as well only gave them a way to disagree: a <c>key_type</c> of int64 on <c>col.entity_id</c> in a pipeline of
/// string ids made every lookup miss without an error.
/// </remarks>
public sealed class EntitySpec
{
    /// <summary>
    /// What keys the entity: a request input (<c>ctx.request.user_id</c>), the candidate id (<c>col.entity_id</c>), a
    /// candidate feature (<c>col.item.creator_id</c>), or for a cross entity a list of a request input and a candidate
    /// column. Each key is an int64 or a string.
    /// </summary>
    public required JsonNode Key { get; init; }

    public string? Description { get; init; }
}

/// <summary>A physical feature served by a data source (design 6.3).</summary>
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

/// <summary>A feature computed by a ColumnExpr from other features (design 6.3).</summary>
public sealed class DerivedFeatureSpec
{
    public required string Type { get; init; }

    public required string Expr { get; init; }

    public JsonNode? Default { get; init; }

    public string? Owner { get; init; }

    public string? Description { get; init; }
}
