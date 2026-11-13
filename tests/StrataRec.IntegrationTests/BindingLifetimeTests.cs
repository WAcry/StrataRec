using System.Collections.Concurrent;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting;
using StrataRec.Hosting.Bindings;
using StrataRec.Hosting.Replay;
using StrataRec.Hosting.Workspace;
using StrataRec.Models;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// The lifetime of a plan's clients through the engine (design 6.4, 10.3): a client created for one plan is disposed only
/// once no served plan uses it and nothing of a replaced plan's requests can still call it; a plan is served only after its
/// clients loaded; and secrets are resolved at load without their values reaching a diagnostic, a log or a recording.
/// The sample's <c>ctr_model</c> is bound to <see cref="ScriptedModels"/>, whose calls, loads and disposals the tests drive.
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

    private static PlanEnvironment Environment(string root, ScriptedModels models) => new()
    {
        BaseDirectory = root,
        Models = ModelClientRegistry.CreateDefault().Add(models),
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

        public sealed class Client(ScriptedModels owner, IModelClient inner, SecretReference? credentials) : IModelClient, ILoadable, IDisposable
        {
            private int _loads;
            private int _calls;

            public string ClientType => Type;

            public int Loads => Volatile.Read(ref _loads);

            public int Calls => Volatile.Read(ref _calls);

            public Secret? Credential { get; private set; }

            public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
                Assert.False(Disposed.Task.IsCompleted, "called after it was disposed");
                Interlocked.Increment(ref _calls);
                if (owner._gate is { } gate)
                {
                    owner.CallStarted.TrySetResult();
                    await gate.Task.ConfigureAwait(false); // ignores cancellation
                }

                return await inner.InvokeAsync(call, outputs, CancellationToken.None).ConfigureAwait(false);
            }

            public void Dispose() => Disposed.TrySetResult();
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
