using StrataRec.Experiments.Compilation;

namespace StrataRec.IntegrationTests;

/// <summary>What a binding's selector names: one error at the selector, whatever the kind of binding and whatever reads it.</summary>
public sealed class BindingTypeTests
{
    [Theory]
    [InlineData("models", "ctr_model", """{ "client": "lineer@1" }""",
        "/models/ctr_model/client", "Model 'ctr_model' names type 'lineer@1', which this binary does not register.", "did you mean 'linear@1'?")]
    [InlineData("candidate_sources", "popular", """{ "client": "memory_lst@1", "data_file": "fixtures/recall_popular.json" }""",
        "/candidate_sources/popular/client", "Candidate source 'popular' names type 'memory_lst@1', which this binary does not register.",
        "did you mean 'memory_list@1'?")]
    [InlineData("sources", "item_store", """{ "connector": "memry@1", "timeout_ms": 1000 }""",
        "/sources/item_store/connector", "Feature source 'item_store' names type 'memry@1', which this binary does not register.", "did you mean 'memory@1'?")]
    [InlineData("sources", "author_store", """{ "mode": "local", "max_bytes": "16MiB", "bootstrap": { "source": "snapshot_file@1", "uri": "fixtures/author_snapshot" } }""",
        "/sources/author_store/bootstrap/source", "Update source of feature source 'author_store' names type 'snapshot_file@1', which this binary does not register.",
        "did you mean 'snapshot_files@1'?")]
    // A type of another kind is not one of this kind: the hint lists those that are.
    [InlineData("models", "ctr_model", """{ "client": "memory_list@1" }""",
        "/models/ctr_model/client", "Model 'ctr_model' names type 'memory_list@1', which is not a model client.", "registered: fixed_text@1, linear@1, random@1")]
    public void ATypeThisBinaryDoesNotHave_IsOneError_AtTheSelector(string section, string name, string binding, string pointer, string message, string hint)
    {
        var host = HomeFeedTestHost.LoadModified(root => Rebind(root, section, name, binding));

        var compilation = PlanCompiler.Compile(host.Request, host.Environment);

        // Not again at each node or tap that reads it: the binding is what to fix.
        var error = Assert.Single(compilation.Diagnostics, d => d.IsError);
        Assert.Equal(("BINDING_TYPE_UNKNOWN", "bindings:local", pointer, message, hint), (error.Code, error.Artifact, error.Pointer, error.Message, error.Hint));
    }

    /// <summary>
    /// A load that fails is a failure however often it is tried: the diagnostics bag keeps one of each error, so the
    /// verdict is the load's own, not the bag's count (a second load of the same bad workspace into the same bag used to
    /// come back as loaded).
    /// </summary>
    [Fact]
    public async Task LoadingTheSameBadWorkspaceTwice_FailsTwice()
    {
        var host = HomeFeedTestHost.LoadModified(root => SampleBindings.RebindModel(root, "ctr_model", """{ "client": "lineer@1" }"""));
        var config = StrataRec.CommandLine.Commands.ConfigRef.Workspace(host.Root, host.Environment);
        var diagnostics = new StrataRec.Artifacts.Model.DiagnosticBag();
        await using var engine = config.CreateReplayEngine();

        Assert.Null(await config.LoadIntoAsync(engine, "local", diagnostics, TestContext.Current.CancellationToken));
        Assert.Null(await config.LoadIntoAsync(engine, "local", diagnostics, TestContext.Current.CancellationToken));
        Assert.Single(diagnostics.Items, d => d.IsError && d.Code == "BINDING_TYPE_UNKNOWN");
    }

    private static void Rebind(string root, string section, string name, string binding)
    {
        switch (section)
        {
            case "models":
                SampleBindings.RebindModel(root, name, binding);
                break;
            case "candidate_sources":
                SampleBindings.RebindCandidateSource(root, name, binding);
                break;
            default:
                SampleBindings.Rebind(root, name, binding);
                break;
        }
    }
}
