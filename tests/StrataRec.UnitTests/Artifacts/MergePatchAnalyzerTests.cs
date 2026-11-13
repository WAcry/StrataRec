using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;

namespace StrataRec.UnitTests.Artifacts;

public class MergePatchAnalyzerTests
{
    // A trimmed patchable view (design 11.4) of the running example.
    private const string BaseView = """
        {
          "params": { "recall_quotas": { "two_tower": 1000, "cf": 1000 }, "final_top_k": 50 },
          "nodes": {
            "safety_filter": { "order": 300, "op": "Filter@1", "config": { "predicate": "col.item.is_policy_safe ?? false" } },
            "fusion": {
              "order": 400,
              "op": "CompositeMultiplierScorer@1",
              "config": { "multipliers": { "quality": "col.item.quality", "fresh": "1.0" } }
            },
            "diversity_cap": { "order": 500, "op": "DiversityCap@1", "config": { "max_per_window": 2 } }
          },
          "features": { "item.clicks_7d": { "type": "float32" } },
          "log_taps": { "served": { "columns": { "score": "score.final" } } },
          "tags": ["a", "b"]
        }
        """;

    private static MergePatchAnalysis Analyze(string patch) => MergePatchAnalyzer.Analyze(Jsonc.Parse(BaseView), Jsonc.Parse(patch));

    private static string[] Describe(MergePatchAnalysis analysis) =>
        analysis.Footprint.Select(op => op.ToString()).ToArray();

    [Fact]
    public void LeafOverride_IsReplace()
    {
        var analysis = Analyze("""{"params": {"recall_quotas": {"cf": 2000}}}""");

        Assert.True(analysis.IsValid);
        var op = Assert.Single(analysis.Footprint);
        Assert.Equal("/params/recall_quotas/cf", op.Path.ToString());
        Assert.Equal(PatchOpKind.Replace, op.Kind);
        Assert.Equal(2000, op.Value!.GetValue<int>());
        Assert.Empty(analysis.Additions);
    }

    [Fact]
    public void MovingNode_IsReplaceOfOrder()
    {
        var analysis = Analyze("""{"nodes": {"safety_filter": {"order": 360}}}""");

        Assert.Equal(new[] { "replace /nodes/safety_filter/order" }, Describe(analysis));
    }

    [Fact]
    public void DeletingExistingNode_IsDelete()
    {
        var analysis = Analyze("""{"nodes": {"diversity_cap": null}}""");

        Assert.True(analysis.IsValid);
        var op = Assert.Single(analysis.Footprint);
        Assert.Equal(PatchOpKind.Delete, op.Kind);
        Assert.Equal("/nodes/diversity_cap", op.Path.ToString());
        Assert.Null(op.Value);
    }

    [Fact]
    public void MisspelledNodeWithSubfield_IsAddOfWholeEntry_Design113()
    {
        var analysis = Analyze("""{"nodes": {"diversty_cap": {"config": {"max_per_window": 3}}}}""");

        Assert.True(analysis.IsValid); // the schema check of the new node (missing op/order) is the caller's job
        var op = Assert.Single(analysis.Footprint);
        Assert.Equal(PatchOpKind.Add, op.Kind);
        Assert.Equal("/nodes/diversty_cap", op.Path.ToString());
        Assert.Equal("""{"config":{"max_per_window":3}}""", op.Value!.ToJsonString());
        Assert.Same(op, Assert.Single(analysis.Additions));
    }

    [Fact]
    public void DeletingMisspelledKey_IsErrorWithSuggestion()
    {
        var analysis = Analyze("""{"nodes": {"diversty_cap": null}}""");

        Assert.False(analysis.IsValid);
        Assert.Empty(analysis.Footprint);
        var error = Assert.Single(analysis.Errors);
        Assert.Equal(MergePatchAnalyzer.DeleteMissingCode, error.Code);
        Assert.Equal("/nodes/diversty_cap", error.Path.ToString());
        Assert.Equal("did you mean '/nodes/diversity_cap'?", error.Hint);
        Assert.Contains("PATCH_DELETE_MISSING at '/nodes/diversty_cap'", error.ToString());
    }

    [Fact]
    public void DeletingUnknownKeyWithoutCloseSibling_HintsToRemove()
    {
        var error = Assert.Single(Analyze("""{"nodes": {"zzz": null}}""").Errors);

        Assert.Equal("'/nodes' has no member 'zzz'; fix the key or remove the null", error.Hint);
    }

    [Fact]
    public void DeletingUnderMissingParent_PointsAtFirstMissingKey()
    {
        var analysis = Analyze("""{"nodes": {"diversty_cap": {"config": null}}}""");

        var op = Assert.Single(analysis.Footprint);
        Assert.Equal("add /nodes/diversty_cap", op.ToString());
        Assert.Equal("{}", op.Value!.ToJsonString());
        var error = Assert.Single(analysis.Errors);
        Assert.Equal("/nodes/diversty_cap/config", error.Path.ToString());
        Assert.Equal(
            "'/nodes/diversty_cap' does not exist in the base document; did you mean '/nodes/diversity_cap'?",
            error.Hint);
    }

    [Fact]
    public void DeletingInsideScalar_ExplainsTheKind()
    {
        var analysis = Analyze("""{"params": {"final_top_k": {"x": null}}}""");

        Assert.Equal(new[] { "replace /params/final_top_k" }, Describe(analysis));
        var error = Assert.Single(analysis.Errors);
        Assert.Equal("/params/final_top_k/x", error.Path.ToString());
        Assert.Equal("'/params/final_top_k' is a number in the base document, not an object; remove the null", error.Hint);
    }

    [Fact]
    public void InsertingNode_IsAddWithCompleteValue()
    {
        var analysis = Analyze("""
            {"nodes": {"mmr_diversifier": {"order": 550, "op": "MmrDiversifier@1", "config": {"lambda": 0.7}}}}
            """);

        var op = Assert.Single(analysis.Additions);
        Assert.Equal("/nodes/mmr_diversifier", op.Path.ToString());
        Assert.Equal("""{"order":550,"op":"MmrDiversifier@1","config":{"lambda":0.7}}""", op.Value!.ToJsonString());
    }

    [Fact]
    public void AddWithNestedNull_DropsItFromValueAndReportsIt()
    {
        var analysis = Analyze("""{"nodes": {"mmr": {"order": 550, "op": "Mmr@1", "config": null}}}""");

        var op = Assert.Single(analysis.Additions);
        Assert.Equal("""{"order":550,"op":"Mmr@1"}""", op.Value!.ToJsonString());
        var error = Assert.Single(analysis.Errors);
        Assert.Equal("/nodes/mmr/config", error.Path.ToString());
        Assert.StartsWith("'/nodes/mmr' does not exist in the base document; ", error.Hint);
    }

    [Fact]
    public void AddingAndDeletingMultipliers_Design113()
    {
        var analysis = Analyze("""
            {"nodes": {"fusion": {"config": {"multipliers": {"bookmark": "1.0 + 0.3 * col.item.bookmark_rate", "quality": null}}}}}
            """);

        Assert.True(analysis.IsValid);
        Assert.Equal(
            new[] { "add /nodes/fusion/config/multipliers/bookmark", "delete /nodes/fusion/config/multipliers/quality" },
            Describe(analysis));
    }

    [Fact]
    public void AddingFeaturesAndLogColumns_Design113()
    {
        var analysis = Analyze("""
            {
              "features": {"item.bookmarks_7d": {"type": "float32", "source": "item_store", "field": "i_bmk_7d", "default": 0.0}},
              "log_taps": {"served": {"columns": {"bookmark_rate": "col.item.bookmark_rate"}}}
            }
            """);

        Assert.Equal(
            new[] { "add /features/item.bookmarks_7d", "add /log_taps/served/columns/bookmark_rate" },
            Describe(analysis));
        Assert.Equal(2, analysis.Additions.Count);
    }

    [Fact]
    public void ArrayValue_ReplacesWholeArray()
    {
        Assert.Equal(new[] { "replace /tags" }, Describe(Analyze("""{"tags": ["c"]}""")));
    }

    [Fact]
    public void ObjectOverScalar_AndScalarOverObject_AreReplace()
    {
        var analysis = Analyze("""{"params": {"final_top_k": {"value": 10}}, "nodes": {"fusion": {"config": "none"}}}""");

        Assert.Equal(new[] { "replace /nodes/fusion/config", "replace /params/final_top_k" }, Describe(analysis));
        Assert.Equal("""{"value":10}""", analysis.Footprint[1].Value!.ToJsonString());
    }

    [Fact]
    public void ArrayOverObject_IsReplace()
    {
        Assert.Equal(new[] { "replace /nodes/fusion/config" }, Describe(Analyze("""{"nodes": {"fusion": {"config": []}}}""")));
    }

    [Fact]
    public void EmptyObjectOnExistingObject_HasNoFootprint()
    {
        var analysis = Analyze("""{"nodes": {"fusion": {}}, "params": {}}""");

        Assert.True(analysis.IsValid);
        Assert.Empty(analysis.Footprint);
    }

    [Fact]
    public void KeyPresentWithNullValue_CountsAsExisting()
    {
        var baseDocument = Jsonc.Parse("""{"a": null}""");

        Assert.Equal(PatchOpKind.Replace, Assert.Single(MergePatchAnalyzer.Analyze(baseDocument, Jsonc.Parse("""{"a": 1}""")).Footprint).Kind);
        Assert.Equal(PatchOpKind.Delete, Assert.Single(MergePatchAnalyzer.Analyze(baseDocument, Jsonc.Parse("""{"a": null}""")).Footprint).Kind);
        var replaced = Assert.Single(MergePatchAnalyzer.Analyze(baseDocument, Jsonc.Parse("""{"a": {"b": 1}}""")).Footprint);
        Assert.Equal("replace /a", replaced.ToString());
    }

    [Fact]
    public void NonObjectPatch_ReplacesOrDeletesRoot()
    {
        var replaced = Assert.Single(Analyze("[1]").Footprint);
        Assert.Equal(PatchOpKind.Replace, replaced.Kind);
        Assert.True(replaced.Path.IsRoot);

        var deleted = Assert.Single(Analyze("null").Footprint);
        Assert.Equal(PatchOpKind.Delete, deleted.Kind);
        Assert.True(deleted.Path.IsRoot);
    }

    [Fact]
    public void Footprint_IsSortedRegardlessOfPatchKeyOrder()
    {
        var a = Analyze("""{"nodes": {"safety_filter": {"order": 1}, "diversity_cap": null}, "params": {"final_top_k": 10}}""");
        var b = Analyze("""{"params": {"final_top_k": 10}, "nodes": {"diversity_cap": null, "safety_filter": {"order": 1}}}""");

        var expected = new[] { "delete /nodes/diversity_cap", "replace /nodes/safety_filter/order", "replace /params/final_top_k" };
        Assert.Equal(expected, Describe(a));
        Assert.Equal(expected, Describe(b));
    }

    [Fact]
    public void Errors_AreSortedByPath()
    {
        var analysis = Analyze("""{"nodes": {"zz": null, "aa": null}, "featurez": null}""");

        Assert.Equal(new[] { "/featurez", "/nodes/aa", "/nodes/zz" }, analysis.Errors.Select(e => e.Path.ToString()));
        Assert.Equal("did you mean '/features'?", analysis.Errors[0].Hint);
    }

    [Fact]
    public void Analyze_DoesNotMutateInputsAndValuesAreDetached()
    {
        var baseDocument = Jsonc.Parse(BaseView)!;
        var patch = Jsonc.Parse("""{"nodes": {"mmr": {"order": 550, "op": "Mmr@1"}}, "tags": ["x"]}""")!;
        var baseBefore = baseDocument.ToJsonString();
        var patchBefore = patch.ToJsonString();

        var analysis = MergePatchAnalyzer.Analyze(baseDocument, patch);

        Assert.Equal(baseBefore, baseDocument.ToJsonString());
        Assert.Equal(patchBefore, patch.ToJsonString());
        foreach (var op in analysis.Footprint)
        {
            Assert.Null(op.Value!.Parent);
        }

        analysis.Footprint[0].Value!["order"] = 1;
        Assert.Equal(patchBefore, patch.ToJsonString());
    }

    [Theory]
    [InlineData("""{"params": {"recall_quotas": {"cf": 2000, "new_source": 10}}, "nodes": {"diversity_cap": null}}""")]
    [InlineData("""{"nodes": {"fusion": {"config": {"multipliers": {"quality": null, "x": "1"}}}, "mmr": {"order": 1, "op": "M"}}}""")]
    [InlineData("""{"params": {"final_top_k": {"v": 1}}, "tags": [], "features": {"f": {"type": "int64"}}}""")]
    public void Footprint_DescribesExactlyWhatMergePatchChanges(string patchText)
    {
        var baseDocument = Jsonc.Parse(BaseView);
        var patch = Jsonc.Parse(patchText);

        var analysis = MergePatchAnalyzer.Analyze(baseDocument, patch);
        var merged = JsonMergePatch.Apply(baseDocument, patch);

        Assert.True(analysis.IsValid);
        foreach (var op in analysis.Footprint)
        {
            var present = op.Path.TryEvaluate(merged, out var value);
            if (op.Kind == PatchOpKind.Delete)
            {
                Assert.False(present);
            }
            else
            {
                Assert.True(present);
                Assert.True(JsonNode.DeepEquals(op.Value, value), $"{op}: {op.Value?.ToJsonString()} vs {value?.ToJsonString()}");
            }
        }

        // Reverting every footprint path to the base value restores the base document.
        var restored = merged!.DeepClone();
        foreach (var op in analysis.Footprint)
        {
            var parent = (JsonObject)Navigate(restored, op.Path.Parent!);
            if (op.Path.TryEvaluate(baseDocument, out var original))
            {
                parent[op.Path.LastToken!] = original?.DeepClone();
            }
            else
            {
                parent.Remove(op.Path.LastToken!);
            }
        }

        Assert.Equal(JsonCanonicalizer.CanonicalizeToString(baseDocument), JsonCanonicalizer.CanonicalizeToString(restored));
    }

    private static JsonNode Navigate(JsonNode root, JsonPointer pointer)
    {
        Assert.True(pointer.TryEvaluate(root, out var node));
        return node!;
    }

    [Fact]
    public void Conflicts_EqualPaths()
    {
        var a = Ops((PatchOpKind.Replace, "/nodes/x/order"));
        var b = Ops((PatchOpKind.Delete, "/nodes/x/order"));

        Assert.True(MergePatchAnalyzer.FootprintsConflict(a, b, out var conflicts));
        var conflict = Assert.Single(conflicts);
        Assert.Same(a[0], conflict.First);
        Assert.Same(b[0], conflict.Second);
        Assert.Equal("replace /nodes/x/order overlaps delete /nodes/x/order", conflict.ToString());
    }

    [Fact]
    public void Conflicts_PrefixInEitherDirection()
    {
        var parent = Ops((PatchOpKind.Delete, "/nodes/x"));
        var child = Ops((PatchOpKind.Replace, "/nodes/x/config/y"));

        Assert.True(MergePatchAnalyzer.FootprintsConflict(parent, child, out var forward));
        Assert.True(MergePatchAnalyzer.FootprintsConflict(child, parent, out var backward));
        Assert.Equal("/nodes/x", Assert.Single(forward).First.Path.ToString());
        Assert.Equal("/nodes/x/config/y", Assert.Single(backward).First.Path.ToString());
    }

    [Fact]
    public void Conflicts_StringPrefixIsNotPathPrefix()
    {
        var a = Ops((PatchOpKind.Delete, "/nodes/a"));
        var b = Ops((PatchOpKind.Add, "/nodes/ab"), (PatchOpKind.Add, "/nodes/a~1b"));

        Assert.False(MergePatchAnalyzer.FootprintsConflict(a, b, out var conflicts));
        Assert.Empty(conflicts);
    }

    [Fact]
    public void Conflicts_DifferentLeavesOfSameObjectDoNotConflict_Design118()
    {
        var a = Ops((PatchOpKind.Replace, "/params/recall_quotas/cf"));
        var b = Ops((PatchOpKind.Replace, "/params/recall_quotas/two_tower"));

        Assert.False(MergePatchAnalyzer.FootprintsConflict(a, b, out _));
    }

    [Fact]
    public void Conflicts_ListsAllPairsSorted()
    {
        var a = Ops((PatchOpKind.Replace, "/p/q"), (PatchOpKind.Delete, "/n/x"), (PatchOpKind.Add, "/other"));
        var b = Ops(
            (PatchOpKind.Replace, "/n/x/c"),
            (PatchOpKind.Add, "/z"),
            (PatchOpKind.Replace, "/p"),
            (PatchOpKind.Replace, "/n/x/a"),
            (PatchOpKind.Replace, "/n/xy"));

        var conflicts = MergePatchAnalyzer.FindConflicts(a, b);

        Assert.Equal(
            new[] { "/n/x -> /n/x/a", "/n/x -> /n/x/c", "/p/q -> /p" },
            conflicts.Select(c => $"{c.First.Path} -> {c.Second.Path}"));
    }

    [Fact]
    public void Conflicts_AncestorsAreListedOutermostFirst()
    {
        var a = Ops((PatchOpKind.Replace, "/a/b/c/d"));
        var b = Ops((PatchOpKind.Delete, "/a/b"), (PatchOpKind.Replace, "/a/b/c/d/e"), (PatchOpKind.Delete, "/a"), (PatchOpKind.Replace, ""));

        var conflicts = MergePatchAnalyzer.FindConflicts(a, b);

        Assert.Equal(new[] { "", "/a", "/a/b", "/a/b/c/d/e" }, conflicts.Select(c => c.Second.Path.ToString()));
    }

    [Fact]
    public void Conflicts_RootConflictsWithEverything()
    {
        var root = Ops((PatchOpKind.Replace, ""));
        var other = Ops((PatchOpKind.Add, "/x"), (PatchOpKind.Delete, "/y/z"));

        Assert.Equal(2, MergePatchAnalyzer.FindConflicts(root, other).Count);
        Assert.Equal(2, MergePatchAnalyzer.FindConflicts(other, root).Count);
    }

    [Fact]
    public void Conflicts_EmptyFootprints()
    {
        Assert.False(MergePatchAnalyzer.FootprintsConflict(Ops(), Ops((PatchOpKind.Add, "/x")), out var conflicts));
        Assert.Empty(conflicts);
    }

    [Fact]
    public void Conflicts_BetweenAnalyzedPatches()
    {
        var addMmr = Analyze("""{"nodes": {"mmr_diversifier": {"order": 550, "op": "MmrDiversifier@1"}}}""");
        var addMmrAgain = Analyze("""{"nodes": {"mmr_diversifier": {"order": 551, "op": "MmrDiversifier@1"}}}""");
        var dropCap = Analyze("""{"nodes": {"diversity_cap": null}}""");
        var tuneCap = Analyze("""{"nodes": {"diversity_cap": {"config": {"max_per_window": 3}}}}""");

        Assert.False(MergePatchAnalyzer.FootprintsConflict(addMmr.Footprint, dropCap.Footprint, out _));
        Assert.True(MergePatchAnalyzer.FootprintsConflict(addMmr.Footprint, addMmrAgain.Footprint, out _));
        Assert.True(MergePatchAnalyzer.FootprintsConflict(dropCap.Footprint, tuneCap.Footprint, out var conflicts));
        Assert.Equal("delete /nodes/diversity_cap overlaps replace /nodes/diversity_cap/config/max_per_window", Assert.Single(conflicts).ToString());
    }

    [Fact]
    public void Conflicts_ValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(() => MergePatchAnalyzer.FindConflicts(null!, Ops()));
        Assert.Throws<ArgumentNullException>(() => MergePatchAnalyzer.FindConflicts(Ops(), null!));
        Assert.Throws<ArgumentException>(() => MergePatchAnalyzer.FindConflicts(new PatchOperation[] { null! }, Ops()));
    }

    [Fact]
    public void PatchOperation_ValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new PatchOperation(null!, PatchOpKind.Add));
        Assert.Throws<ArgumentException>(() => new PatchOperation(JsonPointer.Root, PatchOpKind.Delete, JsonValue.Create(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PatchOperation(JsonPointer.Root, (PatchOpKind)42));
    }

    private static PatchOperation[] Ops(params (PatchOpKind Kind, string Path)[] entries) =>
        entries.Select(e => new PatchOperation(JsonPointer.Parse(e.Path), e.Kind)).ToArray();
}
