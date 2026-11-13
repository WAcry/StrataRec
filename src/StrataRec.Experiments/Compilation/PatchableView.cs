using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;

namespace StrataRec.Experiments.Compilation;

/// <summary>
/// The patchable view (design 11.4): the baseline pipeline and feature catalog joined into one JSON document, so that
/// every experiment patch and footprint path is relative to a single document.
/// </summary>
/// <remarks>
/// The typed <see cref="Pipeline"/> and <see cref="Catalog"/> always agree with <see cref="Document"/>, except that
/// nodes and log taps are read from the document only: a merged node may be incomplete (a misspelled node id becomes a
/// new node without <c>op</c>), and the node compiler reports that precisely per node instead of failing the whole
/// pipeline's deserialization.
/// </remarks>
public sealed class PatchableView
{
    /// <summary>Top-level sections of the view, in canonical order.</summary>
    public static readonly string[] Sections =
    {
        "params", "param_schema", "columns", "nodes", "funnel_groups", "log_taps", "features", "derived_features",
    };

    private PatchableView(PipelineArtifact pipeline, FeatureCatalogArtifact catalog, JsonObject document)
    {
        Pipeline = pipeline;
        Catalog = catalog;
        Document = document;
    }

    public PipelineArtifact Pipeline { get; }

    public FeatureCatalogArtifact Catalog { get; }

    /// <summary>The view document. Treat as read-only; it is shared by every compile over this view.</summary>
    public JsonObject Document { get; }

    /// <summary><c>/nodes</c>: node id → definition.</summary>
    public JsonObject Nodes => (JsonObject)Document["nodes"]!;

    public static PatchableView Create(PipelineArtifact pipeline, FeatureCatalogArtifact catalog)
    {
        var document = new JsonObject
        {
            ["params"] = pipeline.Params.DeepClone(),
            ["param_schema"] = ArtifactSerializer.ToNode(pipeline.ParamSchema),
            ["columns"] = ArtifactSerializer.ToNode(pipeline.Columns),
            ["nodes"] = ArtifactSerializer.ToNode(pipeline.Nodes),
            ["funnel_groups"] = ArtifactSerializer.ToNode(pipeline.FunnelGroups),
            ["log_taps"] = ArtifactSerializer.ToNode(pipeline.LogTaps),
            ["features"] = ArtifactSerializer.ToNode(catalog.Features),
            ["derived_features"] = ArtifactSerializer.ToNode(catalog.DerivedFeatures),
        };
        return new PatchableView(pipeline, catalog, document);
    }

    /// <summary>
    /// Applies an RFC 7396 merge patch and re-reads the typed sections. Returns null after reporting errors at
    /// <paramref name="pointerPrefix"/> + the section path. Strict patch checks are the caller's job.
    /// </summary>
    public PatchableView? Apply(JsonObject patch, DiagnosticBag diagnostics, string artifact, string pointerPrefix)
    {
        if (JsonMergePatch.Apply(Document, patch) is not JsonObject merged)
        {
            diagnostics.Error("PATCH_INVALID", artifact, pointerPrefix, "A patch must be an object.");
            return null;
        }

        foreach (var section in Sections)
        {
            if (merged[section] is not JsonObject)
            {
                diagnostics.Error("PATCH_INVALID", artifact, pointerPrefix + "/" + section,
                    $"'{section}' must remain an object keyed by stable ids.", "patch individual entries instead of the whole section");
                return null;
            }
        }

        var ok = true;
        var columns = Read<Dictionary<string, ColumnSpec>>(merged, "columns", diagnostics, artifact, pointerPrefix, ref ok);
        var paramSchema = Read<Dictionary<string, ParamSpec>>(merged, "param_schema", diagnostics, artifact, pointerPrefix, ref ok);
        var funnelGroups = Read<Dictionary<string, FunnelGroupSpec>>(merged, "funnel_groups", diagnostics, artifact, pointerPrefix, ref ok);
        var features = Read<Dictionary<string, FeatureSpec>>(merged, "features", diagnostics, artifact, pointerPrefix, ref ok);
        var derived = Read<Dictionary<string, DerivedFeatureSpec>>(merged, "derived_features", diagnostics, artifact, pointerPrefix, ref ok);
        if (!ok)
        {
            return null;
        }

        var p = Pipeline;
        var pipeline = new PipelineArtifact
        {
            PipelineId = p.PipelineId,
            Description = p.Description,
            EntityKeyType = p.EntityKeyType,
            RequestSchema = p.RequestSchema,
            Columns = columns!,
            ParamSchema = paramSchema!,
            Params = (JsonObject)merged["params"]!,
            Nodes = new Dictionary<string, NodeSpec>(), // read from Document (see remarks)
            FunnelGroups = funnelGroups!,
            LogTaps = new Dictionary<string, LogTapSpec>(),
            Output = p.Output,
            FallbackPipeline = p.FallbackPipeline,
        };
        var catalog = new FeatureCatalogArtifact
        {
            CatalogId = Catalog.CatalogId,
            Description = Catalog.Description,
            Entities = Catalog.Entities,
            Features = features!,
            DerivedFeatures = derived!,
        };
        return new PatchableView(pipeline, catalog, merged);
    }

    private static T? Read<T>(JsonObject merged, string section, DiagnosticBag diagnostics, string artifact, string pointerPrefix, ref bool ok)
        where T : class
    {
        try
        {
            return merged[section]!.Deserialize<T>(ArtifactSerializer.Options);
        }
        catch (JsonException ex)
        {
            diagnostics.Error("PATCH_SCHEMA", artifact, pointerPrefix + "/" + section + ArtifactSerializer.JsonPathToPointer(ex.Path), ex.Message,
                "a new entry must be a complete definition that validates against the collection's schema (design 11.3)");
            ok = false;
            return null;
        }
    }
}
