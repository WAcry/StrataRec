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
public sealed class EntitySpec
{
    /// <summary><c>context</c> or <c>candidate</c>.</summary>
    public required string Scope { get; init; }

    /// <summary>Key reference (string) or, for cross entities, a list of references.</summary>
    public required JsonNode Key { get; init; }

    /// <summary><c>int64</c>/<c>string</c>, or a list matching <see cref="Key"/>.</summary>
    public required JsonNode KeyType { get; init; }

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

    /// <summary><c>public</c>, <c>internal</c> or <c>personal</c> (design 6.10).</summary>
    public string? Sensitivity { get; init; }

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
