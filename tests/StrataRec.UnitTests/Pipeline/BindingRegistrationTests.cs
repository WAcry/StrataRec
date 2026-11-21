using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Features.Bindings;
using StrataRec.Models;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Sources;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>What a binary's registered binding types are to the schema, the capabilities and a compile.</summary>
public sealed class BindingRegistrationTests
{
    [Fact]
    public void AFactoryOfTwoContracts_IsDescribedForEachKind_WithTheSettingsItBindsForIt()
    {
        var environment = new PlanEnvironment { Bindings = new BindingRegistry().Add((IBindingFactory<IModelClient>)new Dual()) };

        var sources = environment.BindingSchemaSources();

        Assert.Equal(
            new[] { new BindingSchemaSource(BindingKind.Model, "dual@1", typeof(ModelSettings)), new BindingSchemaSource(BindingKind.CandidateSource, "dual@1", typeof(RecallSettings)) },
            sources);
        Assert.Equal(["dual@1"], environment.Capabilities().FindAll(c => c == "dual@1")); // one capability
    }

    [Fact]
    public void AFactoryNoBindingCanName_IsNeitherDescribedNorACapability()
    {
        var environment = new PlanEnvironment { Bindings = PlanEnvironment.DefaultBindings().Add(new Unusable()) };

        Assert.DoesNotContain(environment.BindingSchemaSources(), s => s.Type == "unusable@1");
        Assert.DoesNotContain("unusable@1", environment.Capabilities());
        Assert.Contains("linear@1", environment.Capabilities());
    }

    [Fact]
    public void Capabilities_AskNoFactoryForItsSettingsType()
    {
        // A manifest's capability check needs the types, not their schemas: a settings type in an assembly the host loads
        // lazily must not fail a load that does not use it.
        var environment = new PlanEnvironment { Bindings = PlanEnvironment.DefaultBindings().Add(new LazySettings()) };

        Assert.Contains("lazy@1", environment.Capabilities());
    }

    [Fact]
    public void ABindingTypeOfAnOperatorsId_IsOneCapability()
    {
        var environment = new PlanEnvironment { Bindings = PlanEnvironment.DefaultBindings().Add(new Named("TopK@1")) };

        Assert.Single(environment.Capabilities(), c => c == "TopK@1");
    }

    [Fact]
    public void ABindingTheFactoryRejects_IsCreatedOnceAndReportedOnce_HoweverManyNodesReadIt()
    {
        var factory = new Rejecting();
        var diagnostics = new DiagnosticBag();
        var bindings = new SourceBindingArtifact
        {
            Environment = "test",
            Models = new() { ["ctr_model"] = new ClientBinding { Client = Rejecting.TypeName } },
        };
        var instances = new UnloadedBindings();
        var resolver = new ClientResolver(bindings, new BindingResolver(new BindingRegistry().Add(factory), instances, null, "bindings:test", diagnostics), instances);

        for (var node = 0; node < 3; node++)
        {
            Assert.False(resolver.TryGetModel("ctr_model", out _, out _, out var error, out _));
            Assert.Null(error); // reported at the binding, not again at the node
        }

        Assert.Equal(1, factory.Created);
        var reported = Assert.Single(diagnostics.Items);
        Assert.Equal(("BINDING_SETTINGS", "/models/ctr_model/uri"), (reported.Code, reported.Pointer));
        Assert.Empty(resolver.BoundModels);
    }

    [Fact]
    public void MergingAnotherCompilationsDiagnostics_KeepsOneOfEach()
    {
        var bag = new DiagnosticBag();
        var shared = new ConfigDiagnostic(DiagnosticSeverity.Error, "BINDING_TYPE_UNKNOWN", "bindings:local", "/models/ctr_model/client", "Model 'ctr_model' names type 'x@1'.");
        var own = new ConfigDiagnostic(DiagnosticSeverity.Error, "EXPR_UNKNOWN_NAME", "pipeline:b", "/nodes/n/config/expr", "Unknown name.");

        bag.AddRange([shared]);
        bag.AddRange([shared with { }, own]); // a second pipeline reading the same binding

        Assert.Equal(new[] { shared, own }, bag.Items);
    }

    private sealed class ModelSettings
    {
        public required string ModelEndpoint { get; init; }
    }

    private sealed class RecallSettings
    {
        public required string RecallEndpoint { get; init; }
    }

    /// <summary>One type of two kinds, each bound with settings of its own.</summary>
    private sealed class Dual : IBindingFactory<IModelClient>, IBindingFactory<ICandidateSource>
    {
        public string Type => "dual@1";

        Type IBindingFactory<IModelClient>.SettingsType => typeof(ModelSettings);

        Type IBindingFactory<ICandidateSource>.SettingsType => typeof(RecallSettings);

        IModelClient IBindingFactory<IModelClient>.Create(BindingContext context) => throw new NotSupportedException();

        ICandidateSource IBindingFactory<ICandidateSource>.Create(BindingContext context) => throw new NotSupportedException();
    }

    private sealed class Unusable : IBindingFactory<object>
    {
        public string Type => "unusable@1";

        public Type SettingsType => typeof(NoSettings);

        public object Create(BindingContext context) => new();
    }

    private sealed class Named(string type) : IBindingFactory<IModelClient>
    {
        public string Type => type;

        public Type SettingsType => typeof(NoSettings);

        public IModelClient Create(BindingContext context) => throw new NotSupportedException();
    }

    private sealed class LazySettings : IBindingFactory<IModelClient>
    {
        public string Type => "lazy@1";

        public Type SettingsType => throw new FileNotFoundException("the optional settings assembly is not deployed");

        public IModelClient Create(BindingContext context) => throw new NotSupportedException();
    }

    private sealed class Rejecting : IBindingFactory<IModelClient>
    {
        public const string TypeName = "rejecting@1";

        public int Created { get; private set; }

        public string Type => TypeName;

        public Type SettingsType => typeof(NoSettings);

        public IModelClient Create(BindingContext context)
        {
            Created++;
            throw new BindingException("/uri", "'uri' cannot be read.", "give a readable file");
        }
    }
}
