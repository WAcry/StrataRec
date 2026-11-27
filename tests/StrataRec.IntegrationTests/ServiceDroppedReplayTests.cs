using StrataRec.Core.Bindings;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Services;
using StrataRec.Core.Values;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;
using StrataRec.Pipeline.Replay;

namespace StrataRec.IntegrationTests;

/// <summary>
/// A node that reads how many items its service added past the limit (<see cref="ServiceResponse.Dropped"/>) replays as it
/// ran: the recording has the count. It replayed with none, and the node took another branch, with no
/// gap to say so.
/// </summary>
public sealed class ServiceDroppedReplayTests
{
    [Fact]
    public async Task ANodeReadingTheItemsItsServiceDropped_ReplaysAsItRan()
    {
        var compilation = MiniWorkspace.TryCompile(
            """ "s": { "client": "overflowing@1", "cache": { "ttl_ms": 60000, "memory": { "max_bytes": "1MiB" } } } """,
            """
            { "pipeline_id": "p", "entity_key_type": "int64",
              "param_schema": { "k": { "type": "int64" } },
              "params": { "k": 10 },
              "nodes": { "n": { "order": 1, "op": "reads_dropped@1", "config": {} } },
              "output": { "limit_param": "k", "columns": {} }
            }
            """,
            bindings: PlanEnvironment.DefaultBindings().Add(new Overflowing()),
            operators: OperatorRegistry.CreateDefault().Add(new ReadsDropped()));
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        var plan = compilation.Plan!;
        var executor = new PipelineExecutor();

        var live = await executor.ExecuteAsync(plan, Request(), TestContext.Current.CancellationToken);
        var replay = RunReplay.Create(live.Recording!, "/runs/0");
        var replayed = await executor.ExecuteAsync(plan, Request(replay), TestContext.Current.CancellationToken);

        Assert.True(live.Succeeded && replayed.Succeeded, live.Error ?? replayed.Error);
        Assert.Equal(11, Assert.Single(live.Items).Id.Int64Value);
        Assert.Empty(replay.Gaps);
        Assert.Equal(11, Assert.Single(replayed.Items).Id.Int64Value);
    }

    private static PipelineRequest Request(RunReplay? replay = null) => new()
    {
        TimeMs = 1720000000000,
        RequestId = "r",
        Inputs = new RequestInputs(),
        Deadline = replay is null ? TimeSpan.FromSeconds(20) : RunReplay.Deadline,
        Record = replay is null,
        Replay = replay,
    };

    /// <summary>Adds 11 when its service dropped one item past the limit of 2, 22 otherwise.</summary>
    private sealed class ReadsDropped : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } = new("reads_dropped", 1, Effects.RowSource, typeof(NoSettings), "Reads the items its service dropped.");

        public Operator? Compile(OperatorCompileContext context) => context.Service("s", "", IoRoles.Recall) is { } bound ? new Node(bound) : null;

        private sealed class Node(BoundService bound) : Operator
        {
            public override int GetRowLimit(ReadOnlySpan<ScalarValue> parameters) => 1;

            public override async ValueTask ExecuteAsync(OperatorContext context)
            {
                var response = new ServiceResponse(context.Memory, 2);
                var status = await context.CallAsync(bound, new ServiceCall { Service = "s", Limit = 2 }, response);
                if (status.Succeeded)
                {
                    context.AppendRows(new[] { response.Dropped == 1 ? 11L : 22L });
                }
                else
                {
                    context.Fail(status.Error!);
                }
            }
        }
    }

    /// <summary>Adds three items whatever the limit.</summary>
    private sealed class Overflowing : IBindingFactory<IServiceClient>, IServiceClient
    {
        public string Type => "overflowing@1";

        public Type SettingsType => typeof(NoSettings);

        public IServiceClient Create(BindingContext context) => this;

        public ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse response, CancellationToken cancellationToken)
        {
            response.AddItem(1, 1);
            response.AddItem(2, 1);
            response.AddItem(3, 1);
            return ValueTask.FromResult(ServiceStatus.Ok);
        }
    }

    private sealed class NoSettings;
}
