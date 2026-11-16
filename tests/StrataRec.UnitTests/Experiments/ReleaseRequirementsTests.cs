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

        public ICandidateSource Create(BindingContext context) => new MemoryListCandidateSourceFactory().Create(context);
    }
}
