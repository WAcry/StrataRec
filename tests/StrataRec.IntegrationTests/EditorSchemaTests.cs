using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.CommandLine;
using StrataRec.CommandLine.Commands;
using StrataRec.Hosting.Workspace;
using JsonSchema = global::Json.Schema.JsonSchema;

namespace StrataRec.IntegrationTests;

/// <summary>
/// The schemas editors check workspace files with (design 9.1), checked as an editor checks them: by a JSON Schema
/// 2020-12 validator (JsonSchema.Net) against real documents. Every file of the samples and of <c>init</c> validates
/// against the schema it names in <c>$schema</c>; a misspelled config key, a value of the wrong type, a missing required
/// member and an unknown operator are rejected, as the engine rejects them; an experiment's arms are accepted as merge
/// patches and their mistakes rejected; the engine ignores <c>$schema</c>; the schema files are byte-stable, and
/// <c>validate</c> warns when a workspace's are not the binary's.
/// </summary>
public sealed class EditorSchemaTests
{
    private static string Samples => Path.Combine(AppContext.BaseDirectory, "samples");

    private static readonly string[] Directories = { "catalog", "bindings", "pipelines", "experiments", "releases", "tables", "requests" };

    /// <summary>The kind of a workspace file, by its path relative to the workspace.</summary>
    internal static string KindOf(string path) => path switch
    {
        "policy.jsonc" or "policy.json" => "policy",
        _ when path.StartsWith("catalog/", StringComparison.Ordinal) => "catalog",
        _ when path.StartsWith("bindings/", StringComparison.Ordinal) => "bindings",
        _ when path.StartsWith("pipelines/", StringComparison.Ordinal) => "pipeline",
        _ when path.StartsWith("experiments/", StringComparison.Ordinal) => "experiment",
        _ when path.StartsWith("releases/", StringComparison.Ordinal) => "release",
        _ when path.StartsWith("tables/", StringComparison.Ordinal) => "table",
        _ when path.StartsWith("requests/", StringComparison.Ordinal) => SchemaFiles.RequestKind,
        _ => throw new ArgumentException($"'{path}' is not a workspace file", nameof(path)),
    };

    /// <summary>
    /// Every file of a sample names the schema of its kind in the sample's schemas/, first, and validates against it. The
    /// host sample's request is an HTTP body for curl (no pipeline_id), not a 'stratarec run' request file: the
    /// service's endpoint is strict and would reject a "$schema" in it.
    /// </summary>
    [Theory]
    [InlineData("home_feed", true)]
    [InlineData("StrataRec.Samples.Host/config", false)]
    public void EverySampleFile_NamesItsSchema_AndValidatesAgainstIt(string sample, bool requestsAreRunFiles)
    {
        var root = Path.Combine(Samples, sample);
        var files = WorkspaceFiles(root, requestsAreRunFiles);
        Assert.Contains("pipelines/" + (sample == "home_feed" ? "home_feed.jsonc" : "trending_feed.jsonc"), files);
        Assert.Contains(files, f => f.StartsWith("experiments/", StringComparison.Ordinal));

        foreach (var file in files)
        {
            var document = Assert.IsType<JsonObject>(Jsonc.Parse(File.ReadAllText(Path.Combine(root, file)), file));
            var kind = KindOf(file);
            Assert.True(document.First().Key == "$schema", $"{sample}/{file} does not start with \"$schema\"");
            Assert.Equal(ArtifactSchemas.Reference(file, kind), document["$schema"]!.GetValue<string>());

            var schema = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.Combine(root, file))!, document["$schema"]!.GetValue<string>()));
            var errors = SchemaCheck.Errors(SchemaCheck.Load(File.ReadAllText(schema)), document);
            Assert.True(errors.Count == 0, $"{sample}/{file}:\n{string.Join("\n", errors)}");
        }
    }

    [Fact]
    public void TheHomeFeedSampleSchemas_AreWhatTheToolWrites()
    {
        foreach (var schema in SchemaFiles.Generate(Tool.Environment))
        {
            var path = Path.Combine(Samples, "home_feed", "schemas", schema.Name);
            Assert.True(File.Exists(path) && File.ReadAllText(path) == schema.Text,
                $"samples/home_feed/schemas/{schema.Name} is stale: run 'dotnet run --project src/StrataRec.Cli -- schema --out samples/home_feed/schemas'");
        }
    }

    [Fact]
    public void InitFiles_ValidateAgainstTheSchemasInitWrites()
    {
        var root = Directory.CreateTempSubdirectory("stratarec-editor-init-").FullName;
        try
        {
            Assert.Equal(0, InitCommand.Run(Tool.Environment, root, "feed", force: false, TextWriter.Null, TextWriter.Null));
            var files = WorkspaceFiles(root, requestsAreRunFiles: true);
            Assert.Equal(7, files.Count); // policy, catalog, bindings, pipeline, experiment, release, request
            foreach (var file in files)
            {
                var document = Assert.IsType<JsonObject>(Jsonc.Parse(File.ReadAllText(Path.Combine(root, file)), file));
                var schema = Path.Combine(root, "schemas", ArtifactSchemas.FileName(KindOf(file)));
                var errors = SchemaCheck.Errors(SchemaCheck.Load(File.ReadAllText(schema)), document);
                Assert.True(errors.Count == 0, $"{file}:\n{string.Join("\n", errors)}");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A mistake in a node is an error in the editor where the engine reports one: the schema checks a node's config
    /// against the operator its "op" names. Each edit of the sample's TopK node is rejected by the schema at the pointer,
    /// and by 'stratarec validate'.
    /// </summary>
    [Theory]
    [InlineData("misspelled key", "/nodes/coarse_top_k/config/scor_col", "")]
    [InlineData("wrong type", "/nodes/coarse_top_k/config/k", "type")]
    [InlineData("missing required", "/nodes/coarse_top_k/config", "required")]
    [InlineData("unknown op", "/nodes/coarse_top_k/op", "enum")]
    [InlineData("no config", "/nodes/coarse_top_k", "required")]
    [InlineData("another operator's config", "/nodes/coarse_top_k/config/score_col", "")]
    public void ThePipelineSchema_RejectsANodeTheEngineRejects(string mistake, string at, string keyword)
    {
        var pipeline = SamplePipeline();
        var node = pipeline["nodes"]!["coarse_top_k"]!.AsObject();
        var config = node["config"]!.AsObject();
        Assert.Equal("TopK@1", node["op"]!.GetValue<string>());
        switch (mistake)
        {
            case "misspelled key":
                config["scor_col"] = config["score_col"]!.DeepClone();
                config.Remove("score_col");
                break;
            case "wrong type":
                config["k"] = "30";
                break;
            case "missing required":
                config.Remove("score_col");
                break;
            case "unknown op":
                node["op"] = "TopK@9";
                break;
            case "no config":
                node.Remove("config");
                break;
            case "another operator's config":
                node["op"] = "ExpressionFilter@1";
                break;
        }

        var errors = SchemaCheck.Errors(PipelineSchema.Value, pipeline);
        Assert.True(errors.Any(e => e.At == at && e.Keyword == keyword), $"{mistake}: expected '{keyword}' at {at}, got:\n{string.Join("\n", errors)}");

        var root = CopyOfHomeFeed();
        try
        {
            File.WriteAllText(Path.Combine(root, "pipelines", "home_feed.jsonc"), pipeline.ToJsonString());
            var output = new StringWriter();
            Assert.Equal(1, ValidateCommand.Run(Tool.Environment, root, "local", "home_feed", "json", warningsAsErrors: false, output));
            using var report = JsonDocument.Parse(output.ToString());
            Assert.Contains(report.RootElement.GetProperty("diagnostics").EnumerateArray(),
                d => d.GetProperty("severity").GetString() == "error" && d.GetProperty("pointer").GetString()!.StartsWith("/nodes/coarse_top_k", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// An arm is a merge patch: null deletes, an object names only what it changes, and whether a node is new depends on
    /// the baseline, which the schema does not see. So every patch the engine may accept is valid; a key no node has, a
    /// value of the wrong type, an unknown operator or section is not.
    /// </summary>
    [Theory]
    [InlineData("""{ "nodes": { "diversity_cap": null } }""")]
    [InlineData("""{ "nodes": { "safety_filter": { "order": 360 } } }""")]
    [InlineData("""{ "nodes": { "safety_filter": { "config": { "predicate": "true" } } } }""")]
    [InlineData("""{ "nodes": { "fusion": { "config": { "multipliers": { "bookmark": "1.0", "quality": null } } } } }""")]
    [InlineData("""{ "nodes": { "ctr_ranker": { "timeout_ms": null, "on_failure": "skip" } } }""")]
    [InlineData("""{ "nodes": { "top": { "order": 600, "op": "TopK@1", "config": { "k": 3 } } } }""")] // complete or not: the baseline decides
    [InlineData("""{ "nodes": { "top": { "op": "TopK@1", "config": { "k_param": null } } } }""")]
    [InlineData("""{ "params": { "recall_quotas": { "cf": 2000 } }, "param_schema": { "w": { "type": "float64", "min": 0 } } }""")]
    [InlineData("""{ "log_taps": { "served": { "columns": { "bookmark_rate": "col.item.bookmark_rate" } } } }""")]
    [InlineData("""{ "features": { "item.bookmarks_7d": { "type": "float32", "source": "item_store", "field": "i_bmk_7d", "default": 0.0 } } }""")]
    [InlineData("""{ "columns": { "score.extra": { "type": "float32", "default": 0.0 } }, "derived_features": { "item.x": { "type": "float32", "expr": "1.0" } } }""")]
    public void TheExperimentSchema_AcceptsMergePatches(string patch)
    {
        var errors = SchemaCheck.Errors(ExperimentSchema.Value, Experiment(patch));
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [Theory]
    [InlineData("""{ "nodes": { "top": { "op": "TopK@1", "config": { "scor_col": "s" } } } }""", "/arms/b/nodes/top/config/scor_col", "")]
    [InlineData("""{ "nodes": { "top": { "op": "TopK@1", "config": { "k": "3" } } } }""", "/arms/b/nodes/top/config/k", "type")]
    [InlineData("""{ "nodes": { "top": { "op": "TopK@9" } } }""", "/arms/b/nodes/top/op", "enum")]
    [InlineData("""{ "nodes": { "safety_filter": { "ordr": 360 } } }""", "/arms/b/nodes/safety_filter/ordr", "")]
    [InlineData("""{ "nodes": { "safety_filter": { "order": "late" } } }""", "/arms/b/nodes/safety_filter/order", "type")]
    [InlineData("""{ "nodes": null }""", "/arms/b/nodes", "type")]
    [InlineData("""{ "output": { "limit_param": "k" } }""", "/arms/b/output", "")]
    [InlineData("""{ "features": { "item.x": { "type": "float32", "sorce": "item_store" } } }""", "/arms/b/features/item.x/sorce", "")]
    public void TheExperimentSchema_RejectsMistakesInAPatch(string patch, string at, string keyword)
    {
        var errors = SchemaCheck.Errors(ExperimentSchema.Value, Experiment(patch));
        Assert.True(errors.Any(e => e.At == at && e.Keyword == keyword), $"expected '{keyword}' at {at}, got:\n{string.Join("\n", errors)}");
    }

    /// <summary>
    /// "$schema" is the editor's: a workspace whose files name their schemas has the content ids of one whose files do not,
    /// so adding or removing it changes no release. Only a string at the top level: otherwise it is an error at its place.
    /// </summary>
    [Fact]
    public void TheEngine_IgnoresTheSchemaMember_InContentIds()
    {
        var root = CopyOfHomeFeed();
        try
        {
            // The sample has every kind but a table.
            Directory.CreateDirectory(Path.Combine(root, "tables"));
            File.WriteAllText(Path.Combine(root, "tables", "weights.jsonc"), """
                {
                  "$schema": "../schemas/table.schema.json",
                  "table_id": "weights", "key_type": "string", "value_type": "float64", "entries": { "a": 1.5 }
                }
                """);
            var with = Load(root);
            foreach (var file in WorkspaceFiles(root, requestsAreRunFiles: true))
            {
                var path = Path.Combine(root, file);
                var text = File.ReadAllText(path);
                var without = Regex.Replace(text, "\\n *\"\\$schema\": \"[^\"]*\",", string.Empty);
                Assert.NotEqual(text, without);
                File.WriteAllText(path, without);
            }

            var stripped = Load(root);
            var ids = ContentIds(with);
            Assert.Equal(9, ids.Count); // policy, catalog, bindings, pipeline, 3 experiments, release, table
            Assert.Equal(ids, ContentIds(stripped));
            Assert.DoesNotContain("$schema", with.Pipelines["home_feed"].Document.ToJsonString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("\"$schema\": \"../schemas/pipeline.schema.json\",", "\"$schema\": 3,", "/$schema", "ARTIFACT_SCHEMA")]
    [InlineData("\"$schema\": \"../schemas/pipeline.schema.json\",", "\"$schema\": null,", "/$schema", "ARTIFACT_SCHEMA")]
    [InlineData("\"order\": 350,", "\"order\": 350, \"$schema\": \"x\",", "/nodes/coarse_top_k/$schema", "NODE_BAD_SPEC")]
    public void ASchemaMemberThatIsNotTheEditors_IsAnError(string find, string replace, string pointer, string code)
    {
        var root = CopyOfHomeFeed();
        try
        {
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", find, replace);
            var output = new StringWriter();
            Assert.Equal(1, ValidateCommand.Run(Tool.Environment, root, "local", null, "json", warningsAsErrors: false, output));
            using var report = JsonDocument.Parse(output.ToString());
            var errors = report.RootElement.GetProperty("diagnostics").EnumerateArray().Where(d => d.GetProperty("severity").GetString() == "error").ToList();
            Assert.True(errors.Any(d => d.GetProperty("pointer").GetString() == pointer), output.ToString());
            var error = errors.First(d => d.GetProperty("pointer").GetString() == pointer);
            if (pointer == "/$schema")
            {
                Assert.Equal(code, error.GetProperty("code").GetString());
                Assert.Contains("must be a string", error.GetProperty("message").GetString(), StringComparison.Ordinal);
                Assert.Contains("\"$schema\": \"../schemas/pipeline.schema.json\"", error.GetProperty("hint").GetString(), StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ARequestFile_MayNameItsSchema_AsAString()
    {
        var root = HomeFeedTestHost.SampleRoot();
        var request = Path.Combine(root, "requests", "user_1001.json");
        Assert.Contains("\"$schema\": \"../schemas/request.schema.json\"", File.ReadAllText(request), StringComparison.Ordinal);
        Assert.Equal(0, await RunCommand.RunAsync(Tool.Environment, root, "local", request, Array.Empty<string>(), false, TextWriter.Null,
            CancellationToken.None, deadlineMs: HomeFeedTestHost.UntimedMs));

        var bad = Path.Combine(Directory.CreateTempSubdirectory("stratarec-editor-request-").FullName, "bad.json");
        try
        {
            File.WriteAllText(bad, File.ReadAllText(request).Replace("\"../schemas/request.schema.json\"", "[]", StringComparison.Ordinal));
            var errors = new StringWriter();
            Assert.Equal(2, await RunCommand.RunAsync(Tool.Environment, root, "local", bad, Array.Empty<string>(), false, TextWriter.Null,
                CancellationToken.None, errors: errors));
            Assert.Contains("REQUEST_INVALID /$schema: '$schema' must be a string", errors.ToString(), StringComparison.Ordinal);
            var expected = Path.GetRelativePath(Path.GetDirectoryName(bad)!, Path.Combine(root, "schemas", "request.schema.json")).Replace('\\', '/');
            Assert.Contains($"\"$schema\": \"{expected}\"", errors.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(bad)!, recursive: true);
        }
    }

    /// <summary>The same binary writes the same bytes: what --out writes is what 'schema &lt;kind&gt;' prints, and writing again changes nothing.</summary>
    [Fact]
    public async Task SchemaFiles_AreByteStable_AndWhatSchemaPrints()
    {
        var first = SchemaFiles.Generate(Tool.Environment);
        var second = SchemaFiles.Generate(Tool.Environment);
        Assert.Equal(first, second);
        Assert.Equal(
            new[] { "pipeline", "catalog", "bindings", "experiment", "release", "policy", "table", "request" }.Select(ArtifactSchemas.FileName),
            first.Select(f => f.Name));
        Assert.All(first, f =>
        {
            Assert.DoesNotContain("\r", f.Text, StringComparison.Ordinal);
            Assert.EndsWith("}\n", f.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("$ref", f.Text, StringComparison.Ordinal); // self-contained, so an editor resolves nothing else
            Assert.Contains("\"allowComments\": true", f.Text, StringComparison.Ordinal);
        });

        var directory = Directory.CreateTempSubdirectory("stratarec-editor-out-").FullName;
        try
        {
            var schemas = Path.Combine(directory, "schemas");
            var output = new StringWriter();
            Assert.Equal(0, await StrataRecCommandLine.InvokeAsync(new[] { "schema", "--out", schemas }, Tool.Environment, output, TextWriter.Null));
            Assert.Contains($"wrote 8 of 8 schemas to {schemas}", output.ToString(), StringComparison.Ordinal);
            var written = Directory.GetFiles(schemas).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes);
            output = new StringWriter();
            Assert.Equal(0, await StrataRecCommandLine.InvokeAsync(new[] { "schema", "-o", schemas }, Tool.Environment, output, TextWriter.Null));
            Assert.Contains("the 8 schemas are current", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(written, Directory.GetFiles(schemas).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes));

            foreach (var file in first)
            {
                var printed = new StringWriter();
                Assert.Equal(0, SchemaCommand.Run(Tool.Environment, file.Name[..^".schema.json".Length], null, printed, TextWriter.Null));
                Assert.Equal(file.Text, printed.ToString());
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(new string[0], "Give a kind")]
    [InlineData(new[] { "pipeline", "--out", "x" }, "not both")]
    [InlineData(new[] { "pipelines" }, "Unknown kind 'pipelines'. Known: pipeline, catalog, bindings, experiment, release, policy, table, request, manifest, replay_bundle, operator")]
    public async Task Schema_WithoutAKindOrWithBoth_IsAUsageError(string[] arguments, string message)
    {
        var error = new StringWriter();
        Assert.Equal(2, await StrataRecCommandLine.InvokeAsync(new[] { "schema" }.Concat(arguments).ToArray(), Tool.Environment, TextWriter.Null, error));
        Assert.Contains(message, error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 'validate' warns when schemas/ is not what this binary writes (a file that differs or is missing), with the
    /// command that writes them as the hint; --warnings-as-errors makes CI fail on it. Without schemas/, or with only line
    /// ends changed (a checkout's), there is nothing to say.
    /// </summary>
    [Fact]
    public void Validate_WarnsWhenTheWorkspaceSchemasAreNotThisBinarys()
    {
        var root = Directory.CreateTempSubdirectory("stratarec-editor-stale-").FullName;
        try
        {
            Assert.Equal(0, InitCommand.Run(Tool.Environment, root, "feed", force: false, TextWriter.Null, TextWriter.Null));
            Assert.Equal(0, Validate(root, strict: true, out var clean));
            Assert.DoesNotContain("SCHEMAS_STALE", clean, StringComparison.Ordinal);

            var pipeline = Path.Combine(root, "schemas", "pipeline.schema.json");
            File.WriteAllText(pipeline, File.ReadAllText(pipeline).Replace("\n", "\r\n", StringComparison.Ordinal));
            Assert.Equal(0, Validate(root, strict: true, out _)); // line ends only

            File.WriteAllText(pipeline, File.ReadAllText(pipeline).Replace("TopK@1", "TopK@0", StringComparison.Ordinal));
            File.Delete(Path.Combine(root, "schemas", "table.schema.json"));
            Assert.Equal(0, Validate(root, strict: false, out var stale));
            Assert.Contains("warning SCHEMAS_STALE schemas/: ", stale, StringComparison.Ordinal);
            Assert.Contains("'pipeline.schema.json' differs; 'table.schema.json' is missing.", stale, StringComparison.Ordinal);
            Assert.Contains($"hint: write them again with the binary that validates the workspace, and commit them: stratarec schema --out {Path.Combine(root, "schemas")}", stale, StringComparison.Ordinal);
            Assert.Equal(1, Validate(root, strict: true, out _));

            Assert.Equal(0, SchemaCommand.Write(Tool.Environment, Path.Combine(root, "schemas"), TextWriter.Null, TextWriter.Null));
            Assert.Equal(0, Validate(root, strict: true, out _));

            Directory.Delete(Path.Combine(root, "schemas"), recursive: true);
            Assert.Equal(0, Validate(root, strict: true, out _)); // a workspace without schemas/ is not checked
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A binary with other operators writes other schemas: a host's own operator is in its pipeline schema.</summary>
    [Fact]
    public void ThePipelineSchema_ListsTheOperatorsOfTheBinary()
    {
        var withLlm = Assert.IsType<JsonObject>(JsonNode.Parse(SchemaFiles.Generate(Tool.Environment)[0].Text));
        var core = Assert.IsType<JsonObject>(JsonNode.Parse(SchemaFiles.Generate(new StrataRec.Pipeline.Planning.PlanEnvironment())[0].Text));
        var ops = (JsonObject s) => s["properties"]!["nodes"]!["additionalProperties"]!["properties"]!["op"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("LlmReranker@1", ops(withLlm));
        Assert.DoesNotContain("LlmReranker@1", ops(core));
        Assert.Contains("TopK@1", ops(core));
    }

    private static readonly Lazy<JsonSchema> PipelineSchema = new(() => SchemaCheck.Load(SchemaFiles.Generate(Tool.Environment).Single(f => f.Name == "pipeline.schema.json").Text));

    private static readonly Lazy<JsonSchema> ExperimentSchema = new(() => SchemaCheck.Load(SchemaFiles.Generate(Tool.Environment).Single(f => f.Name == "experiment.schema.json").Text));

    private static JsonObject SamplePipeline() =>
        Assert.IsType<JsonObject>(Jsonc.Parse(File.ReadAllText(Path.Combine(Samples, "home_feed", "pipelines", "home_feed.jsonc"))));

    private static JsonObject Experiment(string arm) => new()
    {
        ["experiment_id"] = "e",
        ["pipeline_id"] = "home_feed",
        ["owner"] = "o",
        ["description"] = "d",
        ["expires_at"] = "2099-12-31",
        ["arms"] = new JsonObject { ["a"] = new JsonObject(), ["b"] = JsonNode.Parse(arm) },
    };

    /// <summary>The files of a workspace that name a schema: policy, every file of the artifact directories, and the requests.</summary>
    private static List<string> WorkspaceFiles(string root, bool requestsAreRunFiles)
    {
        var files = new List<string>();
        if (File.Exists(Path.Combine(root, "policy.jsonc")))
        {
            files.Add("policy.jsonc");
        }

        foreach (var directory in Directories)
        {
            if ((directory == "requests" && !requestsAreRunFiles) || !Directory.Exists(Path.Combine(root, directory)))
            {
                continue;
            }

            files.AddRange(Directory.GetFiles(Path.Combine(root, directory))
                .Where(f => f.EndsWith(".json", StringComparison.Ordinal) || f.EndsWith(".jsonc", StringComparison.Ordinal))
                .Select(f => directory + "/" + Path.GetFileName(f))
                .Order(StringComparer.Ordinal));
        }

        return files;
    }

    private static ConfigWorkspace Load(string root)
    {
        var diagnostics = new DiagnosticBag();
        var workspace = ConfigWorkspace.Load(root, diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        return workspace;
    }

    private static SortedDictionary<string, string> ContentIds(ConfigWorkspace workspace)
    {
        var ids = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (workspace.Policy is { } policy)
        {
            ids[policy.Path] = policy.ContentId;
        }

        foreach (var artifact in workspace.Catalogs.Values.Select(a => (a.Path, a.ContentId))
                     .Concat(workspace.Bindings.Values.Select(a => (a.Path, a.ContentId)))
                     .Concat(workspace.Pipelines.Values.Select(a => (a.Path, a.ContentId)))
                     .Concat(workspace.Experiments.Values.Select(a => (a.Path, a.ContentId)))
                     .Concat(workspace.Releases.Values.Select(a => (a.Path, a.ContentId)))
                     .Concat(workspace.Tables.Values.Select(a => (a.Path, a.ContentId))))
        {
            ids[artifact.Path] = artifact.ContentId;
        }

        return ids;
    }

    private static int Validate(string root, bool strict, out string text)
    {
        var output = new StringWriter();
        var code = ValidateCommand.Run(Tool.Environment, root, "local", null, "text", strict, output);
        text = output.ToString();
        return code;
    }

    /// <summary>A copy of the home_feed sample as written, which the test deletes.</summary>
    private static string CopyOfHomeFeed()
    {
        var from = Path.Combine(Samples, "home_feed");
        var to = Directory.CreateTempSubdirectory("stratarec-editor-home-feed-").FullName;
        foreach (var directory in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        }

        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
        }

        return to;
    }
}

/// <summary>JsonSchema.Net, as the tests use it: a schema of its own registry, and the errors of a document at their places.</summary>
internal static class SchemaCheck
{
    public static JsonSchema Load(string text) =>
        JsonSchema.FromText(text, new global::Json.Schema.BuildOptions { SchemaRegistry = new global::Json.Schema.SchemaRegistry() });

    /// <summary>
    /// Each error as (instance location, keyword, message); none when the document is valid. The errors of an <c>if</c>
    /// that did not hold are not errors of the document.
    /// </summary>
    public static List<(string At, string Keyword, string Message)> Errors(JsonSchema schema, JsonNode document)
    {
        var result = schema.Evaluate(JsonSerializer.SerializeToElement(document), new global::Json.Schema.EvaluationOptions { OutputFormat = global::Json.Schema.OutputFormat.List });
        var errors = new List<(string, string, string)>();
        if (result.IsValid)
        {
            return errors;
        }

        foreach (var detail in result.Details ?? new List<global::Json.Schema.EvaluationResults>())
        {
            if (detail.Errors is not { Count: > 0 } found || detail.EvaluationPath.ToString().Contains("/if/", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var (keyword, message) in found)
            {
                errors.Add((detail.InstanceLocation.ToString(), keyword, message));
            }
        }

        return errors;
    }
}
