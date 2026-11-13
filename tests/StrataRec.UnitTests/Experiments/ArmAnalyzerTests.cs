using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;

namespace StrataRec.UnitTests.Experiments;

public sealed class ArmAnalyzerTests
{
    internal const string PipelineJson = """
    {
      "pipeline_id": "feed",
      "request_schema": { "user_id": { "type": "int64?" } },
      "columns": { "score.base": { "type": "float32", "flow": true, "default": 0.0 } },
      "param_schema": {
        "final_top_k": { "type": "int64", "min": 1, "max": 100 },
        "recall_quotas": { "type": "map<string,int64>", "min": 0 }
      },
      "params": { "final_top_k": 20, "recall_quotas": { "cf": 100, "pop": 50 } },
      "nodes": {
        "safety_filter": { "order": 200, "op": "ExpressionFilter@1", "config": { "predicate": "true" } },
        "top_k": { "order": 500, "op": "TopK@1", "config": { "score": "score.base", "k_param": "final_top_k" } }
      },
      "output": { "limit_param": "final_top_k", "columns": {} }
    }
    """;

    internal const string CatalogJson = """
    { "catalog_id": "main", "features": { "item.ctr": { "type": "float32?", "source": "item_store", "field": "ctr" } } }
    """;

    private static (ArmPatch Arm, DiagnosticBag Diagnostics) Analyze(string patch, PolicyArtifact? policy = null)
    {
        var view = PatchableView.Create(
            ArtifactSerializer.Deserialize<PipelineArtifact>(PipelineJson),
            ArtifactSerializer.Deserialize<FeatureCatalogArtifact>(CatalogJson));
        var arm = new ArmPatch
        {
            Index = 0,
            Id = "exp/treatment",
            ArmName = "treatment",
            ExperimentIndex = 0,
            ExperimentId = "exp",
            LayerIndex = 0,
            Patch = (JsonObject)Jsonc.Parse(patch)!,
            Artifact = "experiment:exp",
            PointerPrefix = "/arms/treatment",
        };
        var diagnostics = new DiagnosticBag();
        ArmAnalyzer.Analyze(arm, view, policy ?? new PolicyArtifact(), diagnostics);
        return (arm, diagnostics);
    }

    [Fact]
    public void ScalarParameterOverrideBecomesOneLeaf()
    {
        var (arm, diagnostics) = Analyze("""{ "params": { "final_top_k": 50 } }""");

        Assert.Empty(diagnostics.Items);
        var single = Assert.Single(arm.ParamOverrides);
        Assert.Equal("final_top_k", single.Leaf);
        Assert.Equal("/arms/treatment/params/final_top_k", single.Pointer);
        Assert.Equal("replace /params/final_top_k", Assert.Single(arm.Footprint).ToString());
    }

    [Fact]
    public void MapParameterKeysAreIndependentLeavesAndNewKeysAreExtraLeaves()
    {
        var (arm, diagnostics) = Analyze("""{ "params": { "recall_quotas": { "cf": 200, "two_tower": 300 } } }""");

        Assert.Empty(diagnostics.Items);
        Assert.Equal(new[] { "recall_quotas.cf", "recall_quotas.two_tower" }, arm.ParamOverrides.Select(o => o.Leaf).Order());
        Assert.Equal(new[] { "recall_quotas.two_tower" }, arm.ExtraParamLeaves);
    }

    [Fact]
    public void UndeclaredParameterIsRejectedWithSuggestion()
    {
        var (arm, diagnostics) = Analyze("""{ "params": { "final_topk": 50 } }""");

        var error = Assert.Single(diagnostics.Items);
        Assert.Equal("PARAM_UNDECLARED", error.Code);
        Assert.Equal("/arms/treatment/params/final_topk", error.Pointer);
        Assert.Contains("final_top_k", error.Hint);
        Assert.False(arm.Valid);
    }

    [Fact]
    public void ArmMayDeclareAndSetItsOwnParameter()
    {
        var (arm, diagnostics) = Analyze("""
            { "param_schema": { "mmr_lambda": { "type": "float64", "min": 0, "max": 1 } }, "params": { "mmr_lambda": 0.7 } }
            """);

        Assert.Empty(diagnostics.Items);
        Assert.Equal("mmr_lambda", Assert.Single(arm.ParamOverrides).Leaf);
        Assert.True(arm.Additions["param_schema"].ContainsKey("mmr_lambda"));
    }

    [Fact]
    public void DeletingParameterValueIsRejected()
    {
        var (_, diagnostics) = Analyze("""{ "params": { "final_top_k": null } }""");

        Assert.Equal("PARAM_DELETE", Assert.Single(diagnostics.Items).Code);
    }

    [Fact]
    public void DeletingMissingNodeIsCaughtWithDidYouMean()
    {
        var (arm, diagnostics) = Analyze("""{ "nodes": { "safety_filtr": null } }""");

        var error = Assert.Single(diagnostics.Items);
        Assert.Equal(MergePatchAnalyzer.DeleteMissingCode, error.Code);
        Assert.Equal("/arms/treatment/nodes/safety_filtr", error.Pointer);
        Assert.Contains("safety_filter", error.Hint);
        Assert.False(arm.Valid);
    }

    [Fact]
    public void PathsOutsideThePolicyAreRejected()
    {
        var (_, diagnostics) = Analyze("""{ "features": { "item.ctr": { "source": "other" } } }""");

        var error = Assert.Single(diagnostics.Items);
        Assert.Equal("PATCH_ADD_ONLY", error.Code);
        Assert.Equal("/arms/treatment/features/item.ctr/source", error.Pointer);
    }

    [Fact]
    public void PolicyCanRestrictPatchableRoots()
    {
        var policy = new PolicyArtifact { ExperimentPatchable = new() { "/params" } };
        var (_, diagnostics) = Analyze("""{ "nodes": { "top_k": { "order": 450 } } }""", policy);

        var error = Assert.Single(diagnostics.Items);
        Assert.Equal("PATCH_NOT_ALLOWED", error.Code);
        Assert.Contains("/params", error.Hint);
    }

    [Fact]
    public void ReAddingAnIdenticalEntryIsANoOp()
    {
        var (arm, diagnostics) = Analyze("""
            { "columns": { "score.base": { "type": "float32", "flow": true, "default": 0.0 } } }
            """);

        Assert.Empty(diagnostics.Items);
        Assert.Empty(arm.Footprint);
        Assert.True(arm.Valid);
    }

    [Fact]
    public void ChangingAnAddOnlyEntryIsRejected()
    {
        var (_, diagnostics) = Analyze("""{ "columns": { "score.base": { "type": "float64" } } }""");

        var error = Assert.Single(diagnostics.Items);
        Assert.Equal("PATCH_ADD_ONLY", error.Code);
        Assert.Equal("/arms/treatment/columns/score.base/type", error.Pointer);
    }

    [Fact]
    public void NewEntriesInAddOnlySectionsAreAdditions()
    {
        var (arm, diagnostics) = Analyze("""
            { "columns": { "score.mmr": { "type": "float32?" } },
              "features": { "item.bookmarks": { "type": "float32", "source": "item_store", "field": "bmk", "default": 0.0 } } }
            """);

        Assert.Empty(diagnostics.Items);
        Assert.True(arm.Additions["columns"].ContainsKey("score.mmr"));
        Assert.True(arm.Additions["features"].ContainsKey("item.bookmarks"));
    }

    [Fact]
    public void NodeChangesRecordTouchedAndAddedNodes()
    {
        var (arm, diagnostics) = Analyze("""
            { "nodes": {
                "safety_filter": { "config": { "predicate": "false" } },
                "top_k": null,
                "mmr": { "order": 550, "op": "MmrDiversifier@1", "config": {} } } }
            """);

        Assert.Empty(diagnostics.Items);
        Assert.Equal(new[] { "mmr", "safety_filter", "top_k" }, arm.TouchedNodes);
        Assert.Equal(new[] { "mmr" }, arm.AddedNodes);
        Assert.True(arm.Relates("/nodes/safety_filter"));
        Assert.True(arm.Relates("/nodes/safety_filter/config/predicate"));
        Assert.False(arm.Relates("/nodes/safety_filter/order"));
    }

    [Fact]
    public void InvalidNodeIdsAreRejected()
    {
        var (_, diagnostics) = Analyze("""{ "nodes": { "bad id": { "order": 10, "op": "TopK@1" } } }""");

        Assert.Equal("BAD_ID", Assert.Single(diagnostics.Items).Code);
    }

    [Fact]
    public void MalformedAddedParameterSchemaIsADiagnosticNotACrash()
    {
        var (arm, diagnostics) = Analyze("""{ "param_schema": { "new_param": 42 }, "params": { "new_param": 1 } }""");

        var error = Assert.Single(diagnostics.Items);
        Assert.Equal("PATCH_SCHEMA", error.Code);
        Assert.Equal("/arms/treatment/param_schema/new_param", error.Pointer);
        Assert.False(arm.Valid);
    }

    [Fact]
    public void AddOnlyNodePolicyStillRecordsTheAddedNode()
    {
        var policy = new PolicyArtifact { ExperimentPatchable = new() { "/params" }, ExperimentAddOnly = new() { "/nodes", "/columns" } };
        var (arm, diagnostics) = Analyze("""{ "nodes": { "extra": { "order": 600, "op": "TopK@1", "config": { "score_col": "score.base", "k": 5 } } } }""", policy);

        Assert.Empty(diagnostics.Items);
        Assert.Equal(new[] { "extra" }, arm.AddedNodes);
        Assert.Equal(new[] { "extra" }, arm.TouchedNodes);

        var (_, modify) = Analyze("""{ "nodes": { "top_k": { "order": 450 } } }""", policy);
        Assert.Equal("PATCH_ADD_ONLY", Assert.Single(modify.Items).Code);
    }

    [Fact]
    public void SchemaSectionsStayAddOnlyEvenWhenThePolicyMakesThemPatchable()
    {
        var policy = new PolicyArtifact { ExperimentPatchable = new() { "/params", "/nodes", "/columns" } };
        var (_, diagnostics) = Analyze("""{ "columns": { "score.base": { "type": "float64" } } }""", policy);

        Assert.Equal("PATCH_ADD_ONLY", Assert.Single(diagnostics.Items).Code);
    }

    [Fact]
    public void WholeSectionReplacementIsRejected()
    {
        var (_, diagnostics) = Analyze("""{ "nodes": null }""");

        Assert.Contains(diagnostics.Items, d => d.Code == "PATCH_NOT_ALLOWED");
    }
}

public sealed class NodeIdTests
{
    [Theory]
    [InlineData("fusion", true)]
    [InlineData("_mmr_v2", true)]
    [InlineData("fusion.v2", false)] // col.x@fusion.v2 would read as field access
    [InlineData("fusion-v2", false)] // col.x@fusion-v2 would read as subtraction
    [InlineData("2fusion", false)]
    [InlineData("", false)]
    public void NodeIdsAreIdentifiersSoVersionReferencesCanNameThem(string id, bool valid) =>
        Assert.Equal(valid, StrataRec.Experiments.Compilation.ArmAnalyzer.IsValidNodeId(id));
}
