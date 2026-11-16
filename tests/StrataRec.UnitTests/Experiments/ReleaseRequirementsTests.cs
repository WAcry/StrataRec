using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Sources;

namespace StrataRec.UnitTests.Experiments;

/// <summary>What a release needs of a binary, the baseline's and each experiment's (design 10.4).</summary>
public sealed class ReleaseRequirementsTests
{
    /// <summary>
    /// Review 69: a combination can need what no experiment needs alone. exp_switch points recall at quota map q2;
    /// exp_add (another layer) adds a source of client type future@1 to q2. Alone, neither binds it; together they do. It
    /// goes to both, so removing either removes it, and the union of a release's requires covers what its full compile
    /// needs.
    /// </summary>
    [Fact]
    public void ACapabilityOnlyACombinationNeeds_GoesToEveryExperimentItNeeds()
    {
        var root = Directory.CreateTempSubdirectory("stratarec-requirements-").FullName;
        Write(root, "catalog/c.jsonc", """{ "catalog_id": "c" }""");
        Write(root, "bindings/local.jsonc", """
            { "environment": "local",
              "candidate_sources": { "r": { "client": "memory_list@1", "items": [1] }, "future": { "client": "future@1", "items": [99] } } }
            """);
        Write(root, "pipelines/p.jsonc", """
            { "pipeline_id": "p", "entity_key_type": "int64",
              "param_schema": { "q1": { "type": "map<string,int64>" }, "q2": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "q1": { "r": 1 }, "q2": { "r": 1 }, "k": 10 },
              "nodes": { "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "q1" } } },
              "output": { "limit_param": "k", "columns": { } } }
            """);
        Write(root, "experiments/exp_switch.jsonc", Experiment("exp_switch", """{ "nodes": { "recall": { "config": { "quotas_param": "q2" } } } }"""));
        Write(root, "experiments/exp_add.jsonc", Experiment("exp_add", """{ "params": { "q2": { "future": 1 } } }"""));
        Write(root, "releases/p.local.jsonc", """
            { "pipeline_id": "p", "environment": "local", "catalog": "c", "bindings": "local",
              "experiments": { "exp_switch": { "layer": "a" }, "exp_add": { "layer": "b" } } }
            """);
        var environment = new PlanEnvironment
        {
            BaseDirectory = root,
            CandidateSources = CandidateSourceRegistry.CreateDefault().Add(new AliasFactory()),
        };
        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("p", "local", diagnostics)!;
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));

        var full = PlanCompiler.Compile(request, environment);
        var requirements = PlanCompiler.Requirements(request, environment, diagnostics);

        Assert.True(full.Succeeded, string.Join("\n", full.Diagnostics));
        Assert.Contains("future@1", full.Requires);
        Assert.Equal(["MultiSourceRecall@1", "memory_list@1"], requirements!.Baseline);
        Assert.Equal(["future@1"], requirements.Experiments["exp_switch"]);
        Assert.Equal(["future@1"], requirements.Experiments["exp_add"]);
    }

    /// <summary>
    /// Review 70: a needs future@1 alone, and b and c (other layers) need it together. It goes to all three: revoking a
    /// leaves b and c requiring what they need. Before, only a listed it, and the release without a required nothing a
    /// binary without future@1 lacks, though its compile fails there.
    /// </summary>
    [Fact]
    public void ACapabilityOneExperimentNeedsAlone_AndAPairOfOthersNeedsToo_GoesToAllThree()
    {
        var requirements = Requirements(new()
        {
            ["a"] = """{ "nodes": { "one": { "config": { "quotas_param": "qf" } } } }""",
            ["b"] = """{ "nodes": { "two": { "config": { "quotas_param": "q3" } } } }""",
            ["c"] = """{ "params": { "q3": { "future": 1 } } }""",
        });

        Assert.DoesNotContain("future@1", requirements.Baseline);
        Assert.Equal(["future@1"], requirements.Experiments["a"]);
        Assert.Equal(["future@1"], requirements.Experiments["b"]);
        Assert.Equal(["future@1"], requirements.Experiments["c"]);
    }

    /// <summary>Two experiments that each need it alone list it, and the baseline does not: revoking both frees it.</summary>
    [Fact]
    public void ACapabilityTwoExperimentsEachNeedAlone_GoesToBoth_NotToTheBaseline()
    {
        var requirements = Requirements(new()
        {
            ["a"] = """{ "nodes": { "one": { "config": { "quotas_param": "qf" } } } }""",
            ["b"] = """{ "nodes": { "two": { "config": { "quotas_param": "qf" } } } }""",
            ["d"] = """{ "params": { "k": 20 } }""",
        });

        Assert.DoesNotContain("future@1", requirements.Baseline);
        Assert.Equal(["future@1"], requirements.Experiments["a"]);
        Assert.Equal(["future@1"], requirements.Experiments["b"]);
        Assert.Empty(requirements.Experiments["d"]);
    }

    /// <summary>
    /// Every set of experiments a revocation can leave requires what its compile needs: the property the attribution
    /// promises, checked over all subsets of a release with a capability one experiment needs alone and a pair needs too.
    /// </summary>
    [Fact]
    public void EverySetOfExperimentsLeft_RequiresWhatItNeeds()
    {
        var patches = new Dictionary<string, string>
        {
            ["a"] = """{ "nodes": { "one": { "config": { "quotas_param": "qf" } } } }""",
            ["b"] = """{ "nodes": { "two": { "config": { "quotas_param": "q3" } } } }""",
            ["c"] = """{ "params": { "q3": { "future": 1 } } }""",
            ["d"] = """{ "params": { "k": 20 } }""",
        };
        var (request, environment) = Workspace(patches);
        var requirements = PlanCompiler.Requirements(request, environment, new DiagnosticBag())!;
        var ids = patches.Keys.ToArray();
        for (var mask = 0; mask < 1 << ids.Length; mask++)
        {
            var left = request.Experiments.Where((_, i) => (mask & (1 << i)) != 0).ToList();
            var needs = PlanCompiler.Compile(new PlanCompileRequest
            {
                Pipeline = request.Pipeline, Catalog = request.Catalog, Bindings = request.Bindings, Tables = request.Tables, Experiments = left,
            }, environment).Requires;
            var required = new HashSet<string>(requirements.Baseline);
            foreach (var placement in left)
            {
                required.UnionWith(requirements.Experiments[placement.Experiment.ExperimentId]);
            }

            Assert.True(required.IsSupersetOf(needs), $"{string.Join(",", left.Select(p => p.Experiment.ExperimentId))}: needs {string.Join(",", needs)}");
        }
    }

    private static ReleaseRequirements Requirements(Dictionary<string, string> patches)
    {
        var (request, environment) = Workspace(patches);
        var diagnostics = new DiagnosticBag();
        var requirements = PlanCompiler.Requirements(request, environment, diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        return requirements!;
    }

    /// <summary>
    /// Two recalls reading quota maps q1 and q2; qf holds a source of client type future@1, q3 does not until an arm adds it.
    /// Each experiment is in a layer of its own.
    /// </summary>
    private static (PlanCompileRequest Request, PlanEnvironment Environment) Workspace(Dictionary<string, string> patches)
    {
        var root = Directory.CreateTempSubdirectory("stratarec-requirements-").FullName;
        Write(root, "catalog/c.jsonc", """{ "catalog_id": "c" }""");
        Write(root, "bindings/local.jsonc", """
            { "environment": "local",
              "candidate_sources": { "r": { "client": "memory_list@1", "items": [1] }, "future": { "client": "future@1", "items": [99] } } }
            """);
        Write(root, "pipelines/p.jsonc", """
            { "pipeline_id": "p", "entity_key_type": "int64",
              "param_schema": { "q1": { "type": "map<string,int64>" }, "q2": { "type": "map<string,int64>" }, "q3": { "type": "map<string,int64>" },
                                "qf": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "q1": { "r": 1 }, "q2": { "r": 1 }, "q3": { "r": 1 }, "qf": { "r": 1, "future": 1 }, "k": 10 },
              "nodes": { "one": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "q1" } },
                         "two": { "order": 200, "op": "MultiSourceRecall@1", "config": { "quotas_param": "q2" } } },
              "output": { "limit_param": "k", "columns": { } } }
            """);
        var release = new List<string>();
        foreach (var (id, patch) in patches)
        {
            Write(root, $"experiments/{id}.jsonc", Experiment(id, patch));
            release.Add($"\"{id}\": {{ \"layer\": \"{id}\" }}");
        }

        Write(root, "releases/p.local.jsonc", $$"""
            { "pipeline_id": "p", "environment": "local", "catalog": "c", "bindings": "local", "experiments": { {{string.Join(", ", release)}} } }
            """);
        var environment = new PlanEnvironment
        {
            BaseDirectory = root,
            CandidateSources = CandidateSourceRegistry.CreateDefault().Add(new AliasFactory()),
        };
        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("p", "local", diagnostics)!;
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        return (request, environment);
    }

    private static string Experiment(string id, string patch) => $$"""
        { "experiment_id": "{{id}}", "pipeline_id": "p", "owner": "t", "description": "d", "expires_at": "2099-01-01",
          "arms": { "control": {}, "on": {{patch}} } }
        """;

    private static void Write(string root, string path, string content)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>A client type this binary would not have: the memory list under another name.</summary>
    private sealed class AliasFactory : ICandidateSourceFactory
    {
        public string ClientType => "future@1";

        public Type SettingsType => typeof(MemoryListSettings);

        public ICandidateSource Create(BindingContext context) => new MemoryListCandidateSourceFactory().Create(context);
    }
}
