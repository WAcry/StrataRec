using System.Text.Json;
using StrataRec.Experiments.Assignment;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>What the project audit of f47a255 found and was fixed, each as the request a host would send.</summary>
public sealed class RequestRegressionTests
{
    private sealed class SlowProvider : IAssignmentProvider
    {
        public long EnteredAtMs { get; private set; }

        public async ValueTask<IReadOnlyList<string>> AssignAsync(AssignmentContext context, CancellationToken cancellationToken)
        {
            EnteredAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await Task.Delay(60, cancellationToken);
            return [];
        }
    }

    /// <summary>
    /// A request's time is the time it arrived, whether or not it is recorded: taken after the assignment, a request that
    /// waited for the platform ran at a later time than the same request recorded, and a filter on the time could serve
    /// the two different items.
    /// </summary>
    [Fact]
    public async Task ARequestsTime_IsWhenItArrived_NotWhenItsAssignmentCameBack()
    {
        var host = HomeFeedTestHost.Load();
        var provider = new SlowProvider();
        var engine = new RecEngine(host.Environment, provider);
        Assert.True(engine.Load(host.Request).Applied);

        var result = await engine.ExecuteAsync(new RecRequest
        {
            PipelineId = "home_feed", RequestId = "req-time", Inputs = HomeFeedTestHost.Inputs(1001, "home"), Deadline = HomeFeedTestHost.Deadline,
            Debug = new DebugOptions { CaptureLogs = true },
        }, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.NotEmpty(result.LogBatches!);
        Assert.All(result.LogBatches!, batch => Assert.InRange(batch.TimeMs, provider.EnteredAtMs - 5_000, provider.EnteredAtMs));
    }

    /// <summary><c>cancelling_memory@1</c>: <c>memory@1</c>, cancelling the caller when asked for <paramref name="field"/>.</summary>
    private sealed class CancellingConnectors(CancellationTokenSource caller, string field) : StrataRec.Core.Bindings.IBindingFactory<StrataRec.Features.Connectors.IFeatureSourceConnector>
    {
        public const string TypeName = "cancelling_memory@1";

        private readonly StrataRec.Features.Connectors.MemoryFeatureConnectorFactory _memory = new();

        public string Type => TypeName;

        public Type SettingsType => _memory.SettingsType;

        public StrataRec.Features.Connectors.IFeatureSourceConnector Create(StrataRec.Core.Bindings.BindingContext context) => new Connector(_memory.Create(context), caller, field);

        private sealed class Connector(StrataRec.Features.Connectors.IFeatureSourceConnector inner, CancellationTokenSource caller, string field)
            : StrataRec.Features.Connectors.IFeatureSourceConnector
        {
            public string ConnectorType => TypeName;

            public bool Supports(StrataRec.Core.Keys.EntityKeyLayout layout) => inner.Supports(layout);

            public async ValueTask<StrataRec.Features.Connectors.FetchStatus> FetchAsync(
                StrataRec.Features.Connectors.EntityKeyBatch keys, ReadOnlyMemory<StrataRec.Features.Connectors.CompiledFieldBinding> fields,
                StrataRec.Features.Connectors.IFeatureBatchWriter writer, CancellationToken cancellationToken)
            {
                var status = await inner.FetchAsync(keys, fields, writer, CancellationToken.None);
                foreach (var binding in fields.Span)
                {
                    if (binding.Field == field)
                    {
                        caller.Cancel(); // answered, and the caller is gone
                    }
                }

                return status;
            }
        }
    }

    /// <summary>
    /// A caller that cancels while the result is built (here while the output's own feature is fetched) gets no result,
    /// and its rows are not logged as returned (the last check was before the
    /// result was built, and the output's tap went to the log).
    /// </summary>
    [Fact]
    public async Task ACallerThatCancelsWhileTheResultIsBuilt_GetsNoResult_AndNoRowIsLoggedAsReturned()
    {
        using var caller = new CancellationTokenSource();
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"score\": \"col.score.final\",", "\"score\": \"col.score.final\",\n      \"age\": \"col.item.age_hours\",");
            ReplayTests.Edit(root, "bindings/local.jsonc", "\"item_store\":   { \"connector\": \"memory@1\"", "\"item_store\":   { \"connector\": \"" + CancellingConnectors.TypeName + "\"");
        });
        var sink = new StrataRec.Logging.InMemoryLogSink();
        await using var logs = new StrataRec.Logging.LogPipeline(sink);
        await using var engine = new RecEngine(
            host.Environment with { Bindings = PlanEnvironment.DefaultBindings().Add(new CancellingConnectors(caller, "i_age_h")) }, logs: logs);
        var load = engine.Load(host.Request);
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ExecuteAsync(new RecRequest
        {
            PipelineId = "home_feed", RequestId = "req-cancel-output", Inputs = HomeFeedTestHost.Inputs(1001, "home"), Assignment = [], Deadline = HomeFeedTestHost.Deadline,
        }, caller.Token).AsTask());

        await logs.FlushAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(sink.Snapshot(), batch => batch.TapId == "served"); // the tap of the rows returned
    }

    private sealed class CancelHereConfig;

    /// <summary><c>CancelHere@1</c>: a node that computes without I/O, during which the caller cancels.</summary>
    private sealed class CancelHereFactory(CancellationTokenSource caller) : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } = new("CancelHere", 1, Effects.None, typeof(CancelHereConfig), "Cancels the caller's token and returns.");

        public Operator? Compile(OperatorCompileContext context) => context.BindConfig<CancelHereConfig>() is null ? null : new CancelHere(caller);

        private sealed class CancelHere(CancellationTokenSource caller) : Operator
        {
            public override ValueTask ExecuteAsync(OperatorContext context)
            {
                caller.Cancel();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>
    /// A caller that cancelled gets its cancellation, not a result: a request whose remaining nodes compute without I/O
    /// never looked at the token, returned a result and logged its rows as served. Nor is a request served that arrives
    /// cancelled.
    /// </summary>
    [Fact]
    public async Task ACallerThatCancelled_GetsNoResult_ThoughTheRemainingNodesDoNoIo()
    {
        using var caller = new CancellationTokenSource();
        var host = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "pipelines/home_feed.jsonc", "\"nodes\": {",
            "\"nodes\": {\n    \"last\": { \"order\": 990, \"op\": \"CancelHere@1\", \"config\": {} },"));
        var engine = new RecEngine(host.Environment with { Operators = OperatorRegistry.CreateDefault().Add(new CancelHereFactory(caller)) });
        var load = engine.Load(host.Request);
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        var request = new RecRequest
        {
            PipelineId = "home_feed", RequestId = "req-cancel", Inputs = HomeFeedTestHost.Inputs(1001, "home"), Assignment = [], Deadline = HomeFeedTestHost.Deadline,
        };

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ExecuteAsync(request, caller.Token).AsTask());
        Assert.Equal(caller.Token, thrown.CancellationToken);

        // Cancelled before it arrives: nothing runs.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ExecuteAsync(request, caller.Token).AsTask());
    }

    /// <summary>
    /// A configuration as deep as a file may be loads: the engine keeps a copy of what a plan was compiled from, which
    /// nests each artifact a few levels deeper, and a workspace that validated and built failed there with a JsonException.
    /// </summary>
    [Fact]
    public void AConfigurationAsDeepAsAFileMayBe_Loads()
    {
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            object extra = 4;
            for (var i = 0; i < 64 - 5; i++)
            {
                extra = new Dictionary<string, object> { ["a"] = extra };
            }

            var record = JsonSerializer.Serialize(new Dictionary<string, object> { ["region"] = "na", ["extra"] = extra }, new JsonSerializerOptions { MaxDepth = 128 });
            ReplayTests.Edit(root, "bindings/local.jsonc", "\"DE\": { \"region\": \"eu\" }", "\"DE\": { \"region\": \"eu\" }, \"XX\": " + record);
        });
        var engine = new RecEngine(host.Environment);

        var load = engine.Load(host.Request);

        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
    }

    /// <summary>
    /// An output column that reads a node's version of a column (<c>col.x@node</c>) has that node as its producer in the
    /// result's lineage, as a tap's has: only the columns reading the current version were listed.
    /// </summary>
    [Fact]
    public async Task AnOutputColumnReadingANodesVersion_HasThatNodeAsItsProducer()
    {
        var host = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "pipelines/home_feed.jsonc",
            "\"score\": \"col.score.final\",", "\"score\": \"col.score.final\",\n      \"fused\": \"col.score.final@fusion\",\n      \"doubled\": \"col.score.final@fusion * 2.0\",\n      \"scaled\": \"col.score.final@fusion * param.quality_weight\",\n      \"weighted\": \"col.score.final * param.quality_weight\","));
        var engine = new RecEngine(host.Environment);
        Assert.True(engine.Load(host.Request).Applied);

        var result = await engine.ExecuteAsync(new RecRequest
        {
            PipelineId = "home_feed", RequestId = "req-lineage", Inputs = HomeFeedTestHost.Inputs(1001, "home"), Assignment = [], Deadline = HomeFeedTestHost.Deadline,
            Debug = new DebugOptions { Trace = true },
        }, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(("engagement_boost", "fusion", "fusion"), (result.ColumnProducers!["score"], result.ColumnProducers["fused"], result.ColumnProducers["doubled"]));
        Assert.Equal(("fusion", "engagement_boost"), (result.ColumnProducers["scaled"], result.ColumnProducers["weighted"])); // a parameter beside the column is no second producer
    }

    /// <summary>A pipeline whose output returns a column <paramref name="times"/> times, under as many names.</summary>
    internal static string ColumnOutputOf(string id, int times, string column = "col.clicks") => $$"""
        {
          "pipeline_id": "{{id}}",
          "entity_key_type": "int64",
          "request_schema": { "user_id": { "type": "int64?" }, "country": { "type": "string?" } },
          "columns": { "clicks": { "type": "float32", "default": 0.0 } },
          "param_schema": { "recall_quotas": { "type": "map<string,int64>", "min": 0, "max": 1000 }, "k": { "type": "int64", "min": 1, "max": 100 } },
          "params": { "recall_quotas": { "popular": 20 }, "k": 3 },
          "nodes": {
            "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "recall_quotas" } },
            "copy":   { "order": 200, "op": "ExpressionScorer@1", "config": { "output_col": "clicks", "expr": "col.item.clicks_7d" } }
          },
          "output": { "limit_param": "k", "columns": { {{string.Join(", ", Enumerable.Range(1, times).Select(i => $"\"c{i}\": \"{column}\""))}} } }
        }
        """;
}

/// <summary>Measured with the process's meter: not beside tests that run requests of their own.</summary>
[Collection(ProcessWideListenersCollection.Name)]
public sealed class OutputMemoryTests
{
    /// <summary>
    /// An output column that is a column is read from the column's storage, for the rows returned: evaluating it rented a
    /// buffer of the table's size for each (2 MB for a 128-float embedding over 4,096 rows). Returning a column under
    /// five names rents what returning it once does.
    /// </summary>
    [Theory]
    [InlineData("col.clicks")]
    [InlineData("col.clicks@copy")] // a node's version of the column: nullable where the column is not, the same storage
    public async Task AnOutputColumnThatIsAColumn_RentsNoBufferOfItsOwn(string column)
    {
        var host = HomeFeedTestHost.LoadModified(root =>
        {
            File.WriteAllText(Path.Combine(root, "pipelines", "once.jsonc"), RequestRegressionTests.ColumnOutputOf("once", 1, column));
            File.WriteAllText(Path.Combine(root, "pipelines", "five.jsonc"), RequestRegressionTests.ColumnOutputOf("five", 5, column));
        });
        var engine = new RecEngine(host.Environment);
        foreach (var id in new[] { "once", "five" })
        {
            var diagnostics = new StrataRec.Artifacts.Model.DiagnosticBag();
            var load = engine.Load(host.Workspace.CreateCompileRequest(id, "local", diagnostics)!);
            Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        }

        using var capture = new MetricCapture();
        foreach (var id in new[] { "once", "five" })
        {
            var result = await engine.ExecuteAsync(new RecRequest
            {
                PipelineId = id, RequestId = "req-" + id, Inputs = new StrataRec.Core.Values.RequestInputs().Set("user_id", 1001), Deadline = HomeFeedTestHost.Deadline,
            }, TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(3, result.Items.Count);
        }

        double Rented(string id) => Assert.Single(capture.Of(StrataRec.Core.Diagnostics.StrataRecMetrics.Names.RequestMemoryRented),
            m => (string?)m.Tags[StrataRec.Core.Diagnostics.StrataRecTags.Pipeline] == id).Value;
        Assert.Equal(Rented("once"), Rented("five"));
    }
}
