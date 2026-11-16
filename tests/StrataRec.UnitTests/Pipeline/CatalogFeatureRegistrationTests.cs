using System.Text;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Planning;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// A plan holds the catalog features it reads, with what they are computed from and keyed by, and nothing else of the
/// catalog (design 6.3): a catalog is shared by many pipelines, and what a request sizes by feature grew with all of it.
/// The catalog's content is still checked whole (an error or a cycle in a derived feature nothing reads), a source is
/// bound only when a feature the plan reads needs it, and a table keeps the layout of the catalog.
/// </summary>
public sealed class CatalogFeatureRegistrationTests
{
    [Fact]
    public void APlan_HoldsWhatItReads_AndNoneOfTheCatalogsOtherFeatures()
    {
        var lean = Plan();
        var wide = Plan(features: Unused(100), derived: UnusedDerived(100));

        Assert.Equal(["item.boost", "item.score"], Names(lean));
        Assert.Equal(["item.boost", "item.score"], Names(wide));
        Assert.Equal(Layout(lean), Layout(wide)); // slots, context values, columns, fetch groups: what a request sizes by
        Assert.Equal("item_store", Assert.Single(wide.Features.Groups).SourceName); // ghost_store, user_store, author_store: not bound
    }

    [Fact]
    public void AFeatureOfAnUnboundSource_FailsThePlanOnlyWhenThePlanReadsIt()
    {
        const string Ghost = """, "item.ghost": { "type": "float32", "source": "ghost_store", "field": "g" }""";

        Assert.True(TryCompile(features: Ghost).Succeeded);

        var read = TryCompile(features: Ghost, expr: "col.item.boost + (col.item.ghost ?? 0.0)");
        var error = Assert.Single(read.Diagnostics, d => d.IsError);
        Assert.Equal(("SOURCE_UNBOUND", "catalog:c", "/features/item.ghost/source"), (error.Code, error.Artifact, error.Pointer));
    }

    [Fact]
    public void ADerivedFeatureWithAnError_IsReportedWhetherReadOrNot_AndReadingItIsNotASecondError()
    {
        const string Broken = """, "item.broken": { "type": "float32", "expr": "col.item.scroe * 2.0" }""";

        var unread = TryCompile(derived: Broken);
        var error = Assert.Single(unread.Diagnostics, d => d.IsError);
        Assert.Equal(("EXPR_UNKNOWN_NAME", "/derived_features/item.broken/expr"), (error.Code, error.Pointer));
        Assert.Contains("col.item.score", error.Hint); // a feature the plan does not hold is still suggested

        var read = TryCompile(derived: Broken, expr: "col.item.broken ?? 0.0");
        Assert.Equal([("EXPR_UNKNOWN_NAME", "/derived_features/item.broken/expr")], Errors(read));
    }

    [Fact]
    public void ACycleOfDerivedFeatures_IsReportedOnce_WhetherReadOrNot()
    {
        const string Cycle = """
            , "item.a": { "type": "float32", "expr": "col.item.b + 1.0" }
            , "item.b": { "type": "float32", "expr": "col.item.a * 2.0" }
            , "item.after": { "type": "float32", "expr": "col.item.a" }
            , "item.self": { "type": "float32", "expr": "col.item.self + 1.0" }
            """;

        foreach (var expr in new[] { "col.item.boost", "col.item.after ?? 0.0" })
        {
            var compilation = TryCompile(derived: Cycle, expr: expr);

            Assert.Equal([("FEATURE_CYCLE", "/derived_features/item.a/expr"), ("FEATURE_CYCLE", "/derived_features/item.self/expr")], Errors(compilation));
            Assert.Contains("item.a → item.b → item.a", compilation.Diagnostics.First(d => d.IsError).Message);
        }
    }

    [Fact]
    public void ADerivedForeignKeyComputedFromItsOwnEntity_IsACycle()
    {
        // owner is keyed by item.owner_id, computed from a feature of owner: the key would be needed to fetch itself.
        var compilation = TryCompile(
            entities: """, "owner": { "key": "col.item.owner_id" }""",
            features: """, "owner.rank": { "type": "float32", "source": "item_store", "field": "o_rank" }""",
            derived: """, "item.owner_id": { "type": "int64", "expr": "int(col.owner.rank ?? 0.0)" }""");

        Assert.Equal([("FEATURE_CYCLE", "/derived_features/item.owner_id/expr")], Errors(compilation));
        Assert.Contains("item.owner_id → owner.rank → item.owner_id", compilation.Diagnostics.First(d => d.IsError).Message);
    }

    [Fact]
    public void AnUnknownName_SuggestsACatalogFeatureThePlanHasNotRead()
    {
        var expression = TryCompile(expr: "col.item.boost + ctx.user.agee");
        Assert.Contains("ctx.user.age", Assert.Single(expression.Diagnostics, d => d.IsError).Hint);

        var config = TryCompile(expr: "0.0", nodes: """, "top": { "order": 300, "op": "TopK@1", "config": { "score_col": "item.scroe", "k": 2 } }""");
        var error = Assert.Single(config.Diagnostics, d => d.IsError);
        Assert.Equal("COLUMN_UNKNOWN", error.Code);
        Assert.Contains("item.score", error.Hint);
    }

    [Fact]
    public void AContextFeatureNamedLikeARequestInput_IsADuplicate_AndTheInputKeepsItsName()
    {
        // An entity named 'request' makes ctx.request.<feature> collide with ctx.request.<input>; this threw while the plan
        // registered every feature.
        var compilation = TryCompile(
            entities: """, "request": { "key": "ctx.request.user_id" }""",
            features: """, "request.user_id": { "type": "int64", "source": "user_store", "field": "u_id" }""",
            expr: "col.item.boost + (ctx.request.user_id ?? 0)");

        Assert.Equal([("FEATURE_DUPLICATE", "/features/request.user_id")], Errors(compilation));
    }

    [Fact]
    public void AFeatureOfAForeignKeyEntity_BringsInItsKey()
    {
        var plan = Plan(expr: "col.author.followers");

        Assert.Equal(["author.followers", "item.author_id"], Names(plan));
        var author = plan.Features.Groups.Single(g => g.SourceName == "author_store");
        Assert.Equal(plan.Features.Features.Single(f => f.Definition.Name == "item.author_id").Index, author.KeyFeatureIndex);
    }

    [Fact]
    public void ACacheSource_BringsInEveryFeatureOfItsTable_AFetchedOneOnlyWhatIsRead()
    {
        const string Extra = """, "item.extra": { "type": "int64", "source": "item_store", "field": "i_extra" }""";
        var fetched = Plan(features: Extra);
        var cached = Plan(features: Extra,
            itemStore: """{ "mode": "cache", "connector": "memory@1", "data": {}, "timeout_ms": 30000, "max_bytes": "1MiB", "cache": { "ttl_ms": 60000 } }""");

        Assert.Equal(["item.boost", "item.score"], Names(fetched));

        // A miss writes back whole records, so every field of the table is fetched: all of them are in the plan.
        Assert.Equal(["item.author_id", "item.boost", "item.extra", "item.score"], Names(cached));
        var group = Assert.Single(cached.Features.Groups);
        Assert.Equal(3, group.FeatureIndices.Length);
        Assert.Equal(["i_author", "i_extra", "i_score"], group.Supply.Table!.Spec.Schema.Fields.Select(f => f.Name));
    }

    [Fact]
    public void ALocalTable_IsLaidOutFromTheCatalog_SoPlansReadingDifferentFeaturesOfItShareIt()
    {
        const string Extra = """, "author.extra": { "type": "float32", "source": "author_store", "field": "a_extra" }""";
        const string Local = """{ "mode": "local", "max_bytes": "1MiB", "bootstrap": { "source": "snapshot_files@1", "uri": "fixtures/author_snapshot" } }""";
        var root = Directory.CreateTempSubdirectory("stratarec-catalog-features-").FullName; // the identity names the directory
        var one = Plan(features: Extra, expr: "col.author.followers", authorStore: Local, root: root);
        var both = Plan(features: Extra, expr: "col.author.followers + (col.author.extra ?? 0.0)", authorStore: Local, root: root);

        var table = Assert.Single(one.Features.Groups, g => g.SourceName == "author_store");
        Assert.Single(table.FeatureIndices); // the plan holds the feature it reads...
        Assert.Equal(["a_extra", "a_followers"], table.Supply.Table!.Spec.Schema.Fields.Select(f => f.Name)); // ...its table, the catalog's
        Assert.Equal(table.Supply.Table.Spec.Identity, both.Features.Groups.Single(g => g.SourceName == "author_store").Supply.Table!.Spec.Identity);
    }

    private static List<string> Names(ExecutionPlan plan) => plan.Features.Features.Select(f => f.Definition.Name).Order(StringComparer.Ordinal).ToList();

    private static (int, int, int, int, int) Layout(ExecutionPlan plan) =>
        (plan.Schema.SlotCount, plan.Schema.ContextFields.Count, plan.Schema.Columns.Count, plan.Features.Features.Count, plan.Features.Groups.Count);

    private static List<(string, string)> Errors(PlanCompilation compilation) =>
        compilation.Diagnostics.Where(d => d.IsError).Select(d => (d.Code, d.Pointer)).ToList();

    /// <summary>Physical features nobody reads, of every kind of entity, and one of a source the bindings do not bind.</summary>
    private static string Unused(int count)
    {
        var text = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            text.Append($$"""
                , "item.unused_{{i}}": { "type": "float32", "source": "item_store", "field": "i_unused_{{i}}" }
                , "user.unused_{{i}}": { "type": "float32", "source": "user_store", "field": "u_unused_{{i}}", "default": 0.0 }
                , "author.unused_{{i}}": { "type": "float32", "source": "author_store", "field": "a_unused_{{i}}" }
                """);
        }

        return text.Append(""", "item.ghost": { "type": "float32", "source": "ghost_store", "field": "g" }""").ToString();
    }

    /// <summary>Derived features nobody reads, candidate and context, over the unused physical ones.</summary>
    private static string UnusedDerived(int count)
    {
        var text = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            text.Append($$"""
                , "item.unused_d{{i}}": { "type": "float32", "expr": "(col.item.unused_{{i}} ?? 0.0) + col.item.score" }
                , "user.unused_d{{i}}": { "type": "float32", "expr": "ctx.user.unused_{{i}} * 2.0" }
                """);
        }

        return text.ToString();
    }

    private static ExecutionPlan Plan(
        string entities = "", string features = "", string derived = "", string expr = "col.item.boost", string nodes = "", string? itemStore = null,
        string? authorStore = null, string? root = null)
    {
        var compilation = TryCompile(entities, features, derived, expr, nodes, itemStore, authorStore, root);
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        return compilation.Plan!;
    }

    /// <summary>
    /// A workspace of one pipeline: recall of items 1–3, then a scorer of <paramref name="expr"/>. The catalog has a context
    /// entity (user), a candidate entity (item) and a foreign-key entity (author, keyed by <c>item.author_id</c>), each
    /// with its source; the arguments add JSON members to its sections. Written to <paramref name="root"/> (over what is
    /// there), or a new directory.
    /// </summary>
    private static PlanCompilation TryCompile(
        string entities = "", string features = "", string derived = "", string expr = "col.item.boost", string nodes = "", string? itemStore = null,
        string? authorStore = null, string? root = null)
    {
        root ??= Directory.CreateTempSubdirectory("stratarec-catalog-features-").FullName;
        Write(root, "catalog/c.jsonc", $$"""
            { "catalog_id": "c",
              "entities": {
                "user":   { "key": "ctx.request.user_id" },
                "item":   { "key": "col.entity_id" },
                "author": { "key": "col.item.author_id" }
                {{entities}} },
              "features": {
                "user.age":         { "type": "float32", "source": "user_store",   "field": "u_age", "default": 0.0 },
                "item.score":       { "type": "float32", "source": "item_store",   "field": "i_score", "default": 0.0 },
                "item.author_id":   { "type": "int64",   "source": "item_store",   "field": "i_author" },
                "author.followers": { "type": "float32", "source": "author_store", "field": "a_followers", "default": 0.0 }
                {{features}} },
              "derived_features": {
                "item.boost": { "type": "float32", "expr": "col.item.score * 2.0" }
                {{derived}} } }
            """);
        Write(root, "bindings/local.jsonc", $$"""
            { "environment": "local",
              "sources": {
                "user_store":   { "connector": "memory@1", "data": { "7": { "u_age": 30.0 } }, "timeout_ms": 30000 },
                "item_store":   {{itemStore ?? """{ "connector": "memory@1", "data": { "1": { "i_score": 0.5, "i_author": 10 } }, "timeout_ms": 30000 }"""}},
                "author_store": {{authorStore ?? """{ "connector": "memory@1", "data": { "10": { "a_followers": 100.0 } }, "timeout_ms": 30000 }"""}} },
              "candidate_sources": { "s": { "client": "memory_list@1", "items": [1, 2, 3] } } }
            """);
        Write(root, "fixtures/author_snapshot/snapshot.json", """{ "format": "jsonl", "files": ["part-0000.jsonl"] }""");
        Write(root, "fixtures/author_snapshot/part-0000.jsonl", """{"key": 10, "version": 1, "fields": {"a_followers": 100.0}}""" + "\n");
        Write(root, "pipelines/p.jsonc", $$"""
            { "pipeline_id": "p", "entity_key_type": "int64",
              "request_schema": { "user_id": { "type": "int64?" } },
              "columns": { "score.final": { "type": "float64", "default": 0.0 } },
              "param_schema": { "quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "quotas": { "s": 100 }, "k": 100 },
              "nodes": {
                "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "quotas" } },
                "score": { "order": 200, "op": "ExpressionScorer@1", "config": { "output_col": "score.final", "expr": "{{expr}}" } }
                {{nodes}} },
              "output": { "limit_param": "k", "columns": { "score": "col.score.final" } } }
            """);

        var diagnostics = new DiagnosticBag();
        var workspace = ConfigWorkspace.Load(root, diagnostics);
        var request = workspace.CreateCompileRequest("p", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        return PlanCompiler.Compile(request!, new PlanEnvironment { BaseDirectory = root });
    }

    private static void Write(string root, string path, string content)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
