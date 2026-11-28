using System.Diagnostics;
using System.Text;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Planning;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// A plan holds the catalog features it reads, with what they are computed from and keyed by, and nothing else of the
/// catalog: a catalog is shared by many pipelines, and what a request sizes by feature grew with all of it.
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
    public void ADerivedForeignKeyComputedFromItsOwnEntity_IsAKeyCycle_AtTheKey()
    {
        // owner is keyed by item.owner_id, computed from a feature of owner: the key would be needed to fetch itself. A cycle
        // through a key is a key cycle, at the key part, whether a derived feature is on it or not.
        var compilation = TryCompile(
            entities: """, "owner": { "key": "col.item.owner_id" }""",
            features: """, "owner.rank": { "type": "float32", "source": "item_store", "field": "o_rank" }""",
            derived: """, "item.owner_id": { "type": "int64", "expr": "int(col.owner.rank ?? 0.0)" }""");

        Assert.Equal([("ENTITY_KEY_CYCLE", "/entities/owner/key")], Errors(compilation));
        var error = compilation.Diagnostics.First(d => d.IsError);
        Assert.Contains("item.owner_id → owner.rank → item.owner_id", error.Message);
        Assert.Contains("through derived feature 'item.owner_id'", error.Message);
        Assert.NotNull(error.Hint);
    }

    /// <summary>
    /// The same for context keys: an account keyed by a derived user feature computed from the account's own
    /// feature, directly or through an entity keyed by it, never has a key. Reported once at the key, whatever reads it.
    /// </summary>
    [Theory]
    [InlineData("col.item.boost")]
    [InlineData("col.item.boost * (ctx.account.tier ?? 1.0)")]
    public void ADerivedContextKeyComputedFromItsOwnEntity_IsAKeyCycle_AtTheKey(string expr)
    {
        var compilation = TryCompile(
            entities: """, "account": { "key": ["ctx.request.user_id", "ctx.user.account_id"] }, "household": { "key": "ctx.account.household_id" }""",
            features: """
                , "account.tier": { "type": "float32", "source": "user_store", "field": "a_tier" }
                , "account.household_id": { "type": "int64", "source": "user_store", "field": "a_household" }
                , "household.account_id": { "type": "int64", "source": "user_store", "field": "h_account" }
                """,
            derived: """, "user.account_id": { "type": "int64", "expr": "ctx.household.account_id ?? 0" }""",
            expr: expr);

        var error = Assert.Single(compilation.Diagnostics, d => d.IsError);
        Assert.Equal("ENTITY_KEY_CYCLE", error.Code);
        Assert.StartsWith("/entities/", error.Pointer, StringComparison.Ordinal);
        Assert.Contains("through derived feature 'user.account_id'", error.Message);
        Assert.Contains("account.household_id", error.Message); // the path goes through the household's key
    }

    /// <summary>
    /// A feature of an entity keyed by a context feature brings the key into the plan, and into the context
    /// prefetch with it: the chain is fetched at request start, each group once what its key reads is stored.
    /// </summary>
    [Fact]
    public void AFeatureOfAnEntityKeyedByAContextFeature_BringsInItsKey_AndIsPrefetched()
    {
        var plan = Plan(
            entities: """, "account": { "key": "ctx.user.account_id" }""",
            features: """
                , "user.account_id": { "type": "int64", "source": "user_store", "field": "u_account" }
                , "account.tier": { "type": "float32", "source": "author_store", "field": "a_tier", "default": 1.0 }
                """,
            expr: "col.item.boost * ctx.account.tier");

        Assert.Equal(["account.tier", "item.boost", "item.score", "user.account_id"], Names(plan));
        var key = plan.Features.Features.Single(f => f.Definition.Name == "user.account_id").Index;
        var tier = plan.Features.Features.Single(f => f.Definition.Name == "account.tier").Index;
        var account = plan.Features.Groups.Single(g => g.Entity.Name == "account");
        Assert.Equal([key], account.KeyFeatures);
        Assert.Equal([key], account.ContextKeyFeatures);
        Assert.Equal([key], plan.Features.Features[tier].Dependencies);
        Assert.Equal([key, tier], plan.ContextFeatures.Order()); // prefetched with the request, the key with it
        Assert.True(account.KeyLayout[0].IsShared);
    }

    [Fact]
    public void ADeprecatedContextKey_WarnsWhenItsEntitysFeatureIsRead()
    {
        var compilation = TryCompile(
            entities: """, "account": { "key": "ctx.user.account_id" }""",
            features: """
                , "user.account_id": { "type": "int64", "source": "user_store", "field": "u_account", "deprecated": true }
                , "account.tier": { "type": "float32", "source": "author_store", "field": "a_tier", "default": 1.0 }
                """,
            expr: "col.item.boost * ctx.account.tier");

        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        Assert.Equal(("FEATURE_DEPRECATED", "/features/user.account_id"), (Assert.Single(compilation.Diagnostics).Code, compilation.Diagnostics[0].Pointer));
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
        Assert.Equal([plan.Features.Features.Single(f => f.Definition.Name == "item.author_id").Index], author.KeyFeatures);
    }

    [Fact]
    public void ACacheSource_BringsInEveryFeatureOfItsTable_AFetchedOneOnlyWhatIsRead()
    {
        const string Extra = """, "item.extra": { "type": "int64", "source": "item_store", "field": "i_extra" }""";
        var fetched = Plan(features: Extra);
        var cached = Plan(features: Extra,
            itemStore: """{ "connector": "memory@1", "data": {}, "timeout_ms": 30000, "cache": { "ttl_ms": 60000, "memory": { "max_bytes": "1MiB" } } }""");

        var unkept = Plan(features: Extra, itemStore: """{ "connector": "memory@1", "data": {}, "timeout_ms": 30000, "cache": { } }""");

        Assert.Equal(["item.boost", "item.score"], Names(fetched));
        Assert.Equal(Names(fetched), Names(unkept)); // a cache that keeps nothing writes no record back: the candidates' features are a fetch's

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
    /// A plan that reads a deprecated feature warns once, at the feature's declaration, however many reads
    /// there are and whether they are direct or through a derived feature. The flag was accepted and never read.
    /// </summary>
    [Fact]
    public void ReadingADeprecatedFeature_WarnsOnce_AtItsDeclaration()
    {
        var compilation = TryCompile(
            features: """, "item.old": { "type": "float32", "source": "item_store", "field": "i_score", "default": 0.0, "deprecated": true }""",
            derived: """, "item.old_boost": { "type": "float32", "expr": "col.item.old * 2.0" }""",
            expr: "col.item.old + col.item.old + col.item.old_boost");

        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        var warning = Assert.Single(compilation.Diagnostics);
        Assert.Equal("FEATURE_DEPRECATED", warning.Code);
        Assert.Equal("catalog:c", warning.Artifact);
        Assert.Equal("/features/item.old", warning.Pointer);
        Assert.Contains("pipeline 'p' reads it", warning.Message);
    }

    /// <summary>
    /// A feature keyed by a deprecated key reads the key (it is fetched first), so the key warns though no
    /// expression names it.
    /// </summary>
    [Fact]
    public void ADeprecatedForeignKey_WarnsWhenItsEntitysFeatureIsRead()
    {
        var compilation = TryCompile(
            entities: """, "editor": { "key": "col.item.editor_id" }""",
            features: """, "item.editor_id": { "type": "int64", "source": "item_store", "field": "i_author", "deprecated": true }""" +
                      """, "editor.followers": { "type": "float32", "source": "author_store", "field": "a_followers", "default": 0.0 }""",
            expr: "col.editor.followers");

        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        var warning = Assert.Single(compilation.Diagnostics);
        Assert.Equal(("FEATURE_DEPRECATED", "/features/item.editor_id"), (warning.Code, warning.Pointer));
    }

    /// <summary>
    /// A deprecated feature nothing reads does not warn, even when a cache table's whole record registers it with the
    /// sibling a node reads: registered is not read.
    /// </summary>
    [Fact]
    public void ADeprecatedFeatureNothingReads_DoesNotWarn_EvenWhenItsCacheTableIsRegistered()
    {
        var compilation = TryCompile(
            features: """, "item.old": { "type": "float32", "source": "item_store", "field": "i_score", "default": 0.0, "deprecated": true }""",
            itemStore: """{ "connector": "memory@1", "data": { "1": { "i_score": 0.5, "i_author": 10 } }, "timeout_ms": 30000, "cache": { "ttl_ms": 60000, "memory": { "max_bytes": "1MiB" } } }""");

        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        Assert.Empty(compilation.Diagnostics);
        Assert.Contains(compilation.GetPlanOrThrow().Features.Features, f => f.Definition.Name == "item.old"); // the table's member
    }

    /// <summary>
    /// Reading one feature of a cache source registers its whole table once. Each member used to expand the
    /// table again, so a table of N features took N² registrations: 40,000 features compiled in 40 s, where the whole
    /// catalog used to take 1 s. The bound is far above the linear time and far below the quadratic one.
    /// </summary>
    [Fact]
    public void AWideCacheTable_IsRegisteredInLinearTime()
    {
        const int extra = 40_000;
        var features = new StringBuilder();
        for (var i = 0; i < extra; i++)
        {
            features.Append($$""", "item.extra_{{i}}": { "type": "float32", "source": "item_store", "field": "i_score", "default": 0.0 }""");
        }

        var started = Stopwatch.GetTimestamp();
        var compilation = TryCompile(features: features.ToString(),
            itemStore: """{ "connector": "memory@1", "data": { "1": { "i_score": 0.5, "i_author": 10 } }, "timeout_ms": 30000, "cache": { "ttl_ms": 60000, "memory": { "max_bytes": "1MiB" } } }""");
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        Assert.True(compilation.GetPlanOrThrow().Features.Features.Count > extra, "the whole cache table is in the plan");
        Assert.True(elapsed < TimeSpan.FromSeconds(15), $"compiled in {elapsed.TotalSeconds:0.0} s");
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
              "services": { "s": { "client": "memory_list@1", "items": [1, 2, 3] } } }
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
