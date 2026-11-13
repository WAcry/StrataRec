using System.Collections.Concurrent;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Keys;
using StrataRec.Experiments.Compilation;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;
using StrataRec.Hosting;
using StrataRec.Hosting.Bindings;
using StrataRec.Hosting.Replay;
using StrataRec.Hosting.Workspace;
using StrataRec.Models;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Sources;

namespace StrataRec.IntegrationTests;

/// <summary>
/// The lifetime of a plan's clients through the engine (design 6.4, 10.3): a client created for one plan is disposed only
/// once no served plan uses it and nothing of a replaced plan's requests can still call it; a plan is served only after its
/// clients loaded; and secrets are resolved at load without their values reaching a diagnostic, a log or a recording.
/// The sample's <c>ctr_model</c> is bound to <see cref="ScriptedModels"/>, whose calls, loads and disposals the tests drive;
/// its connectors and candidate sources to counting doubles of the in-memory ones where a test covers all three kinds; and
/// <see cref="RemoteSignatureModels"/> stands in for a client that has to ask a model server for its signatures.
/// </summary>
public sealed class BindingLifetimeTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ASwapDuringAnInFlightRequest_KeepsTheReplacedPlansClientUntilTheRequestEnds()
    {
        var models = new ScriptedModels();
        var root = Workspace(generation: 1);
        await using var engine = new RecEngine(Environment(root, models));
        Assert.True((await engine.LoadAsync(Compile(root))).Applied);
        var old = Assert.Single(models.Created);
        var instances = engine.Bindings.InstanceCount; // the model, and the sample's connectors and candidate sources

        models.HoldCalls();
        var running = engine.ExecuteAsync(Request("req-in-flight")).AsTask();
        await models.CallStarted.Task.WaitAsync(Wait);

        Rebind(root, generation: 2); // another identity: a new client
        Assert.True((await engine.LoadAsync(Compile(root))).Applied);
        var current = Assert.Single(models.Created, c => c != old);
        await Task.Delay(50);
        Assert.False(old.Disposed.Task.IsCompleted); // its request is still running on it

        models.ReleaseCalls();
        Assert.True((await running.WaitAsync(Wait)).Succeeded);
        await old.Disposed.Task.WaitAsync(Wait);
        Assert.False(current.Disposed.Task.IsCompleted);
        Assert.True((await engine.ExecuteAsync(Request("req-after"))).Succeeded);
        Assert.Equal(instances, engine.Bindings.InstanceCount); // the other instances were taken over, the old model went
    }

    [Fact]
    public async Task ACallAbandonedAtItsTimeout_KeepsTheReplacedPlansClientUntilItReturns()
    {
        var models = new ScriptedModels();
        var root = Workspace(generation: 1, modelTimeoutMs: 100);
        await using var engine = new RecEngine(Environment(root, models));
        Assert.True((await engine.LoadAsync(Compile(root))).Applied);
        var old = Assert.Single(models.Created);
        models.HoldCalls();

        var result = await engine.ExecuteAsync(Request("req-abandoning")).AsTask().WaitAsync(Wait);
        Assert.True(result.Succeeded); // served by ctr_backup; the model call runs on, abandoned
        Assert.Contains(result.Trace!, o => o is { NodeId: "ctr_ranker", Kind: NodeOutcomeKind.Degraded, ProducedBy: "ctr_backup" });

        Rebind(root, generation: 2);
        Assert.True((await engine.LoadAsync(Compile(root))).Applied);
        await Task.Delay(50);
        Assert.False(old.Disposed.Task.IsCompleted); // the request returned, its call did not

        models.ReleaseCalls();
        await old.Disposed.Task.WaitAsync(Wait);
        Assert.Equal(1, old.Calls);
    }

    [Fact]
    public async Task APlanWhoseClientFailsToLoad_IsRejectedAtTheBinding_AndReturnsItsLease()
    {
        var models = new ScriptedModels();
        var root = Workspace(generation: 1);
        await using var engine = new RecEngine(Environment(root, models));
        Assert.True((await engine.LoadAsync(Compile(root))).Applied);
        var served = Assert.Single(models.Created);
        var instances = engine.Bindings.InstanceCount;

        models.Load = (_, _) => throw new BindingException("/model_file", "The model file 'ctr.bin' does not exist.", "check the path");
        Rebind(root, generation: 2);
        var load = await engine.LoadAsync(Compile(root));

        Assert.False(load.Applied);
        var error = Assert.Single(load.Diagnostics, d => d.IsError);
        Assert.Equal(("BINDING_LOAD_FAILED", "bindings:local", "/models/ctr_model/model_file", "check the path"),
            (error.Code, error.Artifact, error.Pointer, error.Hint));
        var failed = Assert.Single(models.Created, c => c != served);
        await failed.Disposed.Task.WaitAsync(Wait); // the rejected plan's lease is returned
        Assert.False(served.Disposed.Task.IsCompleted); // the served plan keeps serving
        Assert.Equal(instances, engine.Bindings.InstanceCount); // the rejected plan held nothing more
        Assert.True((await engine.ExecuteAsync(Request("req-still-served"))).Succeeded);
    }

    [Fact]
    public async Task AnEngineDisposalDuringAClientsLoad_CancelsIt_AndDisposesTheClientOnceTheLoadReturns()
    {
        var models = new ScriptedModels();
        var loading = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        models.Load = async (_, token) =>
        {
            loading.SetResult(token);
            await finish.Task; // ignores the token until the test lets it return
        };
        var root = Workspace(generation: 1);
        var engine = new RecEngine(Environment(root, models), bindingOptions: new BindingHostOptions { ShutdownTimeout = TimeSpan.FromMilliseconds(100) });
        var load = engine.LoadAsync(Compile(root));
        var token = await loading.Task.WaitAsync(Wait);

        await engine.DisposeAsync().AsTask().WaitAsync(Wait); // bounded, although the load has not returned

        await Assert.ThrowsAsync<ObjectDisposedException>(() => load.WaitAsync(Wait));
        Assert.True(token.IsCancellationRequested);
        var client = Assert.Single(models.Created);
        Assert.False(client.Disposed.Task.IsCompleted); // never disposed under its load

        finish.SetResult();
        await client.Disposed.Task.WaitAsync(Wait);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.ExecuteAsync(Request("req-late")).AsTask());
    }

    [Fact]
    public async Task EngineDisposalIsBounded_AndARequestStillRunningKeepsItsClientUntilItEnds()
    {
        var models = new ScriptedModels();
        var root = Workspace(generation: 1);
        var engine = new RecEngine(Environment(root, models), bindingOptions: new BindingHostOptions { ShutdownTimeout = TimeSpan.FromMilliseconds(100) });
        Assert.True((await engine.LoadAsync(Compile(root))).Applied);
        var client = Assert.Single(models.Created);
        models.HoldCalls();
        var running = engine.ExecuteAsync(Request("req-at-shutdown")).AsTask();
        await models.CallStarted.Task.WaitAsync(Wait);

        await engine.DisposeAsync().AsTask().WaitAsync(Wait);

        Assert.False(client.Disposed.Task.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.ExecuteAsync(Request("req-after-shutdown")).AsTask());
        models.ReleaseCalls();
        Assert.True((await running.WaitAsync(Wait)).Succeeded); // the request in flight completes on its plan
        await client.Disposed.Task.WaitAsync(Wait);
    }

    [Fact]
    public async Task ASignatureTheClientKnowsOnlyOnceLoaded_IsCheckedBeforeThePlanIsServed()
    {
        var models = new ScriptedModels
        {
            Signature = (client, _, _) => client.Loads == 0
                ? throw new InvalidOperationException("asked before the load")
                : new ModelSignature(
                    [new TensorSpec("smoothed_ctr", TensorElementType.Float32, [-1])],
                    [new TensorSpec("p_click", TensorElementType.Float32, [-1])]),
        };
        var root = Workspace(generation: 1);
        await using var engine = new RecEngine(Environment(root, models));

        var load = await engine.LoadAsync(Compile(root));

        Assert.False(load.Applied);
        var error = Assert.Single(load.Diagnostics, d => d.Code == "MODEL_SIGNATURE"); // and the tap that reads the node's inputs
        Assert.Equal(("pipeline:home_feed", "/nodes/ctr_ranker/config/inputs/log_followers"), (error.Artifact, error.Pointer));
        Assert.Contains("has no input 'log_followers'", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(models.Created).Loads);

        // Static validation never loads a client: the signature is left to the engine, and nothing is created twice.
        var validated = PlanCompiler.Compile(Compile(root), Environment(root, models));
        Assert.True(validated.Succeeded, string.Join("\n", validated.Diagnostics));
    }

    [Fact]
    public async Task AMissingSecret_RejectsThePlanAtItsSetting()
    {
        var models = new ScriptedModels();
        var root = Workspace(generation: 1, credentials: "secret://ctr-token");
        await using var engine = new RecEngine(Environment(root, models), bindingOptions: new BindingHostOptions { Secrets = new Store() });

        var load = await engine.LoadAsync(Compile(root));

        Assert.False(load.Applied);
        var error = Assert.Single(load.Diagnostics, d => d.IsError);
        Assert.Equal(("BINDING_LOAD_FAILED", "bindings:local", "/models/ctr_model/credentials"), (error.Code, error.Artifact, error.Pointer));
        Assert.Contains("no secret 'ctr-token'", error.Message, StringComparison.Ordinal);
        Assert.Contains("add the secret 'ctr-token'", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecretsValue_ReachesItsClient_AndNoDiagnosticLogTraceOrRecording()
    {
        const string Value = "s3cr3t-value-7f2a";
        var logs = new ListLoggerFactory();
        var models = new ScriptedModels();
        var root = Workspace(generation: 1, credentials: "secret://ctr-token");
        await using var engine = new RecEngine(Environment(root, models), loggerFactory: logs,
            bindingOptions: new BindingHostOptions { Secrets = new Store(("ctr-token", Value)) });

        var load = await engine.LoadAsync(Compile(root));
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        ReplayBundle? bundle = null;
        var result = await engine.ExecuteAsync(Request("req-secret", onRecording: b => bundle = b));

        Assert.True(result.Succeeded);
        Assert.Equal(Value, Assert.Single(models.Created).Credential!.Reveal());
        var seen = string.Join("\n", load.Diagnostics) + string.Join("\n", logs.Entries.Select(e => e.Message + " " + string.Join(",", e.State.Values)))
                   + string.Join("\n", result.Trace!.Select(o => o.ToString())) + ReplayBundleFile.Serialize(bundle!);
        Assert.DoesNotContain(Value, seen, StringComparison.Ordinal);
        Assert.Contains("secret://ctr-token", ReplayBundleFile.Serialize(bundle!) + File.ReadAllText(Path.Combine(root, "bindings", "local.jsonc")),
            StringComparison.Ordinal); // the reference is configuration and may be shown
    }

    [Fact]
    public async Task ACredentialWrittenAsAValue_IsAConfigErrorThatQuotesItNowhere()
    {
        const string Pasted = "AKIA-pasted-by-mistake";
        var logs = new ListLoggerFactory();
        var models = new ScriptedModels();
        var root = Workspace(generation: 1, credentials: Pasted);
        await using var engine = new RecEngine(Environment(root, models), loggerFactory: logs);

        var load = await engine.LoadAsync(Compile(root));

        Assert.False(load.Applied);
        var error = Assert.Single(load.Diagnostics, d => d.Code == "MODEL_CONFIG"); // and the tap that reads the node's inputs
        Assert.Equal(("MODEL_CONFIG", "bindings:local", "/models/ctr_model/credentials"), (error.Code, error.Artifact, error.Pointer));
        Assert.DoesNotContain(Pasted, string.Join("\n", load.Diagnostics) + string.Join("\n", logs.Entries.Select(e => e.Message)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelsConnectorsAndCandidateSources_AreSharedWhileUnchanged_ReplacedWhenChanged_AndDisposedOnceUnused()
    {
        var models = new ScriptedModels();
        var connectors = new CountingConnectors();
        var sources = new CountingSources();
        var root = Workspace(generation: 1);
        BindCounting(root, latencyMs: null);
        var engine = new RecEngine(Environment(root, models, connectors, sources));
        Assert.True((await engine.LoadAsync(Compile(root))).Applied);
        Assert.Equal((1, 2, 2), (models.Created.Count, connectors.Created.Count, sources.Created.Count));
        var model = Assert.Single(models.Created);
        var items = connectors.Last("item_store");
        var popular = sources.Last("popular");

        // A swap during a request: the connector and the source whose settings changed are replaced, the others are
        // taken over loaded, and the replaced ones live until the request that runs on them ends.
        models.HoldCalls();
        var running = engine.ExecuteAsync(Request("req-across-the-swap")).AsTask();
        await models.CallStarted.Task.WaitAsync(Wait);
        BindCounting(root, latencyMs: 0);
        Assert.True((await engine.LoadAsync(Compile(root))).Applied);
        Assert.Equal((1, 3, 3), (models.Created.Count, connectors.Created.Count, sources.Created.Count));
        Assert.Single(connectors.Created, c => c.Name == "user_store");
        Assert.Single(sources.Created, s => s.Name == "two_tower");
        await Task.Delay(50);
        Assert.False(items.Disposed.Task.IsCompleted || popular.Disposed.Task.IsCompleted);

        models.ReleaseCalls();
        Assert.True((await running.WaitAsync(Wait)).Succeeded);
        await Task.WhenAll(items.Disposed.Task, popular.Disposed.Task).WaitAsync(Wait);
        Assert.False(model.Disposed.Task.IsCompleted); // shared with the current plan
        Assert.True((await engine.ExecuteAsync(Request("req-on-the-new-plan"))).Succeeded);

        // A new model: the old one goes as soon as no request runs on it.
        Rebind(root, generation: 2);
        Assert.True((await engine.LoadAsync(Compile(root))).Applied);
        await model.Disposed.Task.WaitAsync(Wait);
        Assert.True((await engine.ExecuteAsync(Request("req-on-the-new-model"))).Succeeded);

        // The engine's disposal takes the rest: every instance was disposed exactly once, and none was called after.
        await engine.DisposeAsync().AsTask().WaitAsync(Wait);
        var all = models.Created.Cast<Counted>().Concat(connectors.Created).Concat(sources.Created).ToList();
        Assert.Equal(8, all.Count); // two models, three connectors (one replaced), three sources (one replaced)
        await Task.WhenAll(all.Select(i => i.Disposed.Task)).WaitAsync(Wait);
        Assert.All(all, i => Assert.Equal((1, 0), (i.Disposals, i.LateCalls)));
        Assert.All(all.Where(i => i is not CountingConnectors.Connector { Name: "user_store" }), // the baseline reads no user feature
            i => Assert.True(i.Calls > 0, "every instance served a request"));
        Assert.Equal(1, models.Created[0].Loads);
    }

    [Fact]
    public async Task ASignatureOnlyTheModelServerKnows_IsAwaitedWhenTheEngineLoads_AndLeftToTheEngineByValidate()
    {
        var root = Workspace(generation: 1);
        SampleBindings.RebindModel(root, "ctr_model", $$"""
            {
              "client": "{{RemoteSignatureModels.Type}}",
              "outputs": { "p_click": { "bias": -1.5, "weights": { "smoothed_ctr": 12.0, "log_followers": 0.05 }, "activation": "sigmoid" } }
            }
            """);

        var served = new RemoteSignatureModels();
        await using (var engine = new RecEngine(Environment(root, served)))
        {
            var load = await engine.LoadAsync(Compile(root));
            Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
            Assert.True(served.Lookups > 0);
            Assert.True((await engine.ExecuteAsync(Request("req-remote-signature"))).Succeeded);
        }

        // The server's model has no input the node maps: the plan is rejected at the node, before it is served...
        var renamed = new RemoteSignatureModels
        {
            Answer = new ModelSignature(
                [new TensorSpec("smoothed_ctr", TensorElementType.Float32, [-1])],
                [new TensorSpec("p_click", TensorElementType.Float32, [-1])]),
        };
        await using var rejecting = new RecEngine(Environment(root, renamed));
        var rejected = await rejecting.LoadAsync(Compile(root));
        Assert.False(rejected.Applied);
        var error = Assert.Single(rejected.Diagnostics, d => d.Code == "MODEL_SIGNATURE");
        Assert.Equal(("pipeline:home_feed", "/nodes/ctr_ranker/config/inputs/log_followers"), (error.Artifact, error.Pointer));

        // ...while static validation does not wait for a model server: the check is the engine's.
        var validated = PlanCompiler.Compile(Compile(root), Environment(root, renamed));
        Assert.True(validated.Succeeded, string.Join("\n", validated.Diagnostics));
    }

    private static string Workspace(int generation, int? modelTimeoutMs = null, string? credentials = null) =>
        HomeFeedTestHost.LoadModified(root =>
        {
            Rebind(root, generation, credentials);
            if (modelTimeoutMs is { } timeout)
            {
                var path = Path.Combine(root, "pipelines", "home_feed.jsonc");
                var text = File.ReadAllText(path);
                var edited = text.Replace($"\"fallback:ctr_backup\", \"timeout_ms\": {HomeFeedTestHost.UntimedMs}", $"\"fallback:ctr_backup\", \"timeout_ms\": {timeout}",
                    StringComparison.Ordinal);
                Assert.NotEqual(text, edited);
                File.WriteAllText(path, edited);
            }
        }).Root;

    /// <summary>Binds <c>ctr_model</c> to <see cref="ScriptedModels"/> with the sample's linear weights; the generation makes the identity.</summary>
    private static void Rebind(string root, int generation, string? credentials = null) =>
        SampleBindings.RebindModel(root, "ctr_model", $$"""
            {
              "client": "{{ScriptedModels.Type}}", "generation": {{generation}},
              {{(credentials is null ? string.Empty : $"\"credentials\": \"{credentials}\",")}}
              "outputs": { "p_click": { "bias": -1.5, "weights": { "smoothed_ctr": 12.0, "log_followers": 0.05 }, "activation": "sigmoid" } }
            }
            """);

    /// <summary>
    /// Binds the sample's feature sources and candidate sources to the counting doubles; a latency setting (any value)
    /// changes the identity of <c>item_store</c> and <c>popular</c> only.
    /// </summary>
    private static void BindCounting(string root, int? latencyMs)
    {
        var latency = latencyMs is { } ms ? $", \"latency_ms\": {ms}" : string.Empty;
        SampleBindings.Rebind(root, "user_store",
            $$"""{ "connector": "{{CountingConnectors.Type}}", "data_file": "fixtures/users.json", "timeout_ms": {{HomeFeedTestHost.UntimedMs}} }""");
        SampleBindings.Rebind(root, "item_store",
            $$"""{ "connector": "{{CountingConnectors.Type}}", "data_file": "fixtures/items.json", "timeout_ms": {{HomeFeedTestHost.UntimedMs}}, "max_batch": 25{{latency}} }""");
        SampleBindings.RebindCandidateSource(root, "two_tower", $$"""{ "client": "{{CountingSources.Type}}", "data_file": "fixtures/recall_two_tower.json" }""");
        SampleBindings.RebindCandidateSource(root, "popular", $$"""{ "client": "{{CountingSources.Type}}", "data_file": "fixtures/recall_popular.json"{{latency}} }""");
    }

    private static PlanEnvironment Environment(string root, IModelClientFactory models, IFeatureConnectorFactory? connectors = null, ICandidateSourceFactory? sources = null) => new()
    {
        BaseDirectory = root,
        Models = ModelClientRegistry.CreateDefault().Add(models),
        Connectors = connectors is null ? FeatureConnectorRegistry.CreateDefault() : FeatureConnectorRegistry.CreateDefault().Add(connectors),
        CandidateSources = sources is null ? CandidateSourceRegistry.CreateDefault() : CandidateSourceRegistry.CreateDefault().Add(sources),
    };

    private static PlanCompileRequest Compile(string root)
    {
        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("home_feed", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        return request!;
    }

    private static RecRequest Request(string requestId, Action<ReplayBundle>? onRecording = null) => new()
    {
        PipelineId = "home_feed",
        RequestId = requestId,
        Inputs = HomeFeedTestHost.Inputs(1001, "home"),
        Assignment = Array.Empty<string>(),
        Deadline = HomeFeedTestHost.Deadline,
        TimeMs = 1_767_225_600_000,
        Debug = new DebugOptions { Trace = true, OnRecording = onRecording },
    };

    /// <summary>
    /// <c>scripted@1</c>: linear@1 models the test drives. Each client loads with <see cref="Load"/> (and resolves its
    /// <c>credentials</c> reference), answers <see cref="Signature"/> when set, and holds its calls while
    /// <see cref="HoldCalls"/> is in effect, ignoring their cancellation like a client stuck in I/O.
    /// </summary>
    private sealed class ScriptedModels : IModelClientFactory
    {
        public const string Type = "scripted@1";

        private readonly LinearModelClientFactory _linear = new();
        private readonly ConcurrentQueue<Client> _created = new();
        private TaskCompletionSource? _gate;

        public string ClientType => Type;

        public List<Client> Created => _created.ToList();

        public Func<Client, CancellationToken, Task>? Load { get; set; }

        public Func<Client, string, string?, ModelSignature>? Signature { get; init; }

        public TaskCompletionSource CallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void HoldCalls() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseCalls() => _gate!.TrySetResult();

        public IModelClient Create(BindingContext context)
        {
            var client = new Client(this, _linear.Create(context), context.SecretReference("credentials"));
            _created.Enqueue(client);
            return client;
        }

        public sealed class Client(ScriptedModels owner, IModelClient inner, SecretReference? credentials) : Counted, IModelClient, ILoadable
        {
            private int _loads;

            public string ClientType => Type;

            public int Loads => Volatile.Read(ref _loads);

            public Secret? Credential { get; private set; }

            public async ValueTask LoadAsync(CancellationToken cancellationToken)
            {
                if (owner.Load is { } load)
                {
                    await load(this, cancellationToken);
                }

                if (credentials is not null)
                {
                    Credential = await credentials.ResolveAsync(cancellationToken);
                }

                Interlocked.Increment(ref _loads);
            }

            public ValueTask<ModelSignature> GetSignatureAsync(string model, string? version, CancellationToken cancellationToken) =>
                owner.Signature is { } signature ? ValueTask.FromResult(signature(this, model, version)) : inner.GetSignatureAsync(model, version, cancellationToken);

            public async ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken)
            {
                BeginCall();
                try
                {
                    if (owner._gate is { } gate)
                    {
                        owner.CallStarted.TrySetResult();
                        await gate.Task.ConfigureAwait(false); // ignores cancellation
                    }

                    return await inner.InvokeAsync(call, outputs, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    EndCall();
                }
            }
        }
    }

    /// <summary>
    /// What the engine did with one instance: its request calls, its disposals, and the calls that were running when a
    /// disposal began or started after one, which the lifetime contract says never happen.
    /// </summary>
    private abstract class Counted : IDisposable
    {
        private int _calls;
        private int _inFlight;
        private int _disposals;
        private int _lateCalls;

        public int Calls => Volatile.Read(ref _calls);

        public int Disposals => Volatile.Read(ref _disposals);

        public int LateCalls => Volatile.Read(ref _lateCalls);

        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
            if (Volatile.Read(ref _inFlight) > 0)
            {
                Interlocked.Increment(ref _lateCalls);
            }

            Interlocked.Increment(ref _disposals);
            Disposed.TrySetResult();
        }

        protected void BeginCall()
        {
            Interlocked.Increment(ref _inFlight);
            Interlocked.Increment(ref _calls);
            if (Disposals > 0)
            {
                Interlocked.Increment(ref _lateCalls);
            }
        }

        protected void EndCall() => Interlocked.Decrement(ref _inFlight);
    }

    /// <summary><c>counting_memory@1</c>: <c>memory@1</c> connectors that count what the engine does with them.</summary>
    private sealed class CountingConnectors : IFeatureConnectorFactory
    {
        public const string Type = "counting_memory@1";

        private readonly MemoryFeatureConnectorFactory _memory = new();
        private readonly ConcurrentQueue<Connector> _created = new();

        public string ConnectorType => Type;

        public List<Connector> Created => _created.ToList();

        public Connector Last(string name) => Created.Last(c => c.Name == name);

        public IFeatureSourceConnector Create(BindingContext context)
        {
            var connector = new Connector(context.Name, _memory.Create(context));
            _created.Enqueue(connector);
            return connector;
        }

        public sealed class Connector(string name, IFeatureSourceConnector inner) : Counted, IFeatureSourceConnector
        {
            public string Name { get; } = name;

            public string ConnectorType => Type;

            public bool Supports(KeyShape shape) => inner.Supports(shape);

            public async ValueTask<FetchStatus> FetchAsync(EntityKeyBatch keys, ReadOnlyMemory<CompiledFieldBinding> fields, IFeatureBatchWriter writer,
                CancellationToken cancellationToken)
            {
                BeginCall();
                try
                {
                    return await inner.FetchAsync(keys, fields, writer, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    EndCall();
                }
            }
        }
    }

    /// <summary><c>counting_list@1</c>: <c>memory_list@1</c> candidate sources that count what the engine does with them.</summary>
    private sealed class CountingSources : ICandidateSourceFactory
    {
        public const string Type = "counting_list@1";

        private readonly MemoryListCandidateSourceFactory _list = new();
        private readonly ConcurrentQueue<Source> _created = new();

        public string ClientType => Type;

        public List<Source> Created => _created.ToList();

        public Source Last(string name) => Created.Last(s => s.Name == name);

        public ICandidateSource Create(BindingContext context)
        {
            var source = new Source(context.Name, _list.Create(context));
            _created.Enqueue(source);
            return source;
        }

        public sealed class Source(string name, ICandidateSource inner) : Counted, ICandidateSource
        {
            public string Name { get; } = name;

            public string ClientType => Type;

            public async ValueTask<CandidateSourceStatus> RetrieveAsync(CandidateQuery query, ICandidateSink sink, CancellationToken cancellationToken)
            {
                BeginCall();
                try
                {
                    return await inner.RetrieveAsync(query, sink, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    EndCall();
                }
            }
        }
    }

    /// <summary>
    /// <c>remote_signature@1</c>: linear@1 models whose signature only a model server knows. The client needs no load, and
    /// every answer takes a round trip; <see cref="Answer"/> stands in for a server whose model differs from the binding.
    /// </summary>
    private sealed class RemoteSignatureModels : IModelClientFactory
    {
        public const string Type = "remote_signature@1";

        private readonly LinearModelClientFactory _linear = new();
        private int _lookups;

        public string ClientType => Type;

        public ModelSignature? Answer { get; init; }

        public int Lookups => Volatile.Read(ref _lookups);

        public IModelClient Create(BindingContext context) => new Client(this, _linear.Create(context));

        private sealed class Client(RemoteSignatureModels owner, IModelClient inner) : IModelClient
        {
            public string ClientType => Type;

            public async ValueTask<ModelSignature> GetSignatureAsync(string model, string? version, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner._lookups);
                await Task.Delay(20, cancellationToken).ConfigureAwait(false); // the round trip
                return owner.Answer ?? await inner.GetSignatureAsync(model, version, cancellationToken).ConfigureAwait(false);
            }

            public ValueTask<ModelStatus> InvokeAsync(ModelCall call, IModelOutputWriter outputs, CancellationToken cancellationToken) =>
                inner.InvokeAsync(call, outputs, cancellationToken);
        }
    }

    private sealed class Store(params (string Name, string Value)[] secrets) : ISecretResolver
    {
        public ValueTask<string?> ResolveAsync(string name, CancellationToken cancellationToken)
        {
            foreach (var (n, v) in secrets)
            {
                if (n == name)
                {
                    return ValueTask.FromResult<string?>(v);
                }
            }

            return ValueTask.FromResult<string?>(null);
        }
    }
}
