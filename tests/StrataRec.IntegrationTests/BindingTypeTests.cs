using StrataRec.Experiments.Compilation;

namespace StrataRec.IntegrationTests;

public sealed class BindingTypeTests
{
    [Theory]
    [InlineData("services", "ctr_model", """{ "client": "lineer@1" }""",
        "/services/ctr_model/client", "Service 'ctr_model' names type 'lineer@1', which this binary does not register.", "did you mean 'linear@1'?")]
    [InlineData("services", "popular", """{ "client": "memory_lst@1", "data_file": "fixtures/recall_popular.json" }""",
        "/services/popular/client", "Service 'popular' names type 'memory_lst@1', which this binary does not register.",
        "did you mean 'memory_list@1'?")]
    [InlineData("sources", "item_store", """{ "connector": "memry@1", "timeout_ms": 1000 }""",
        "/sources/item_store/connector", "Feature source 'item_store' names type 'memry@1', which this binary does not register.", "did you mean 'memory@1'?")]
    [InlineData("sources", "author_store", """{ "mode": "local", "max_bytes": "16MiB", "bootstrap": { "source": "snapshot_file@1", "uri": "fixtures/author_snapshot" } }""",
        "/sources/author_store/bootstrap/source", "Update source of feature source 'author_store' names type 'snapshot_file@1', which this binary does not register.",
        "did you mean 'snapshot_files@1'?")]
    [InlineData("services", "ctr_model", """{ "client": "memory@1" }""",
        "/services/ctr_model/client", "Service 'ctr_model' names type 'memory@1', which is not a service client.",
        "registered: fixed_text@1, linear@1, memory_list@1, memory_vectors@1, random@1")]
    public void ATypeThisBinaryDoesNotHave_IsOneError_AtTheSelector(string section, string name, string binding, string pointer, string message, string hint)
    {
        var host = HomeFeedTestHost.LoadModified(root => Rebind(root, section, name, binding));

        var compilation = PlanCompiler.Compile(host.Request, host.Environment);

        var error = Assert.Single(compilation.Diagnostics, d => d.IsError);
        Assert.Equal(("BINDING_TYPE_UNKNOWN", "bindings:local", pointer, message, hint), (error.Code, error.Artifact, error.Pointer, error.Message, error.Hint));
    }

    [Fact]
    public async Task LoadingTheSameBadWorkspaceTwice_FailsTwice()
    {
        var host = HomeFeedTestHost.LoadModified(root => SampleBindings.RebindService(root, "ctr_model", """{ "client": "lineer@1" }"""));
        var config = StrataRec.CommandLine.Commands.ConfigRef.Workspace(host.Root, host.Environment);
        var diagnostics = new StrataRec.Artifacts.Model.DiagnosticBag();
        await using var engine = config.CreateReplayEngine();

        Assert.Null(await config.LoadIntoAsync(engine, "local", diagnostics, TestContext.Current.CancellationToken));
        Assert.Null(await config.LoadIntoAsync(engine, "local", diagnostics, TestContext.Current.CancellationToken));
        Assert.Single(diagnostics.Items, d => d.IsError && d.Code == "BINDING_TYPE_UNKNOWN");
    }

    private static void Rebind(string root, string section, string name, string binding)
    {
        if (section == "services")
        {
            SampleBindings.RebindService(root, name, binding);
        }
        else
        {
            SampleBindings.Rebind(root, name, binding);
        }
    }
}
