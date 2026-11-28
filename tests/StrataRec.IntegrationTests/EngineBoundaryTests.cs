using System.Diagnostics;
using System.Text.Json.Nodes;
using StrataRec.Core.Diagnostics;
using StrataRec.Experiments.Assignment;
using StrataRec.Hosting;

namespace StrataRec.IntegrationTests;

[Collection(ProcessWideListenersCollection.Name)]
public sealed class EngineBoundaryTests
{
    [Fact]
    public async Task AProviderIsNotCalled_WhenTheHostsTracingCancelsTheRequestAsItsCallStarts()
    {
        using var caller = new CancellationTokenSource();
        var provider = new CountingProvider();
        var host = HomeFeedTestHost.Load();
        await using var engine = new RecEngine(host.Environment, provider);
        Assert.True(engine.Load(host.Request).Applied);
        // This test's trace only: the listener is process-wide, and other tests' requests run meanwhile.
        using var root = new Activity("engine-boundary-test").Start();
        var trace = root.TraceId;
        var sampled = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == StrataRecTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
            {
                if (options.Parent.TraceId != trace)
                {
                    return ActivitySamplingResult.None;
                }

                if (options.Name == "assignment provider")
                {
                    Interlocked.Increment(ref sampled);
                    caller.Cancel();
                }

                return ActivitySamplingResult.AllDataAndRecorded;
            },
        };
        ActivitySource.AddActivityListener(listener);

        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await engine.ExecuteAsync(Request(HomeFeedTestHost.Deadline), caller.Token));

        Assert.Equal(1, sampled);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(caller.Token, cancelled.CancellationToken);
    }

    /// <summary>
    /// The message of what the provider threw is not read, so one whose message throws is still a cancellation.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheCallersCancellation_IsThrownWithTheCallersToken_WhateverTokenTheProviderThrew(bool unreadableMessage)
    {
        using var caller = new CancellationTokenSource();
        var host = HomeFeedTestHost.Load();
        await using var engine = new RecEngine(host.Environment, new CancelsTheCaller(caller, unreadableMessage));
        Assert.True(engine.Load(host.Request).Applied);

        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await engine.ExecuteAsync(Request(HomeFeedTestHost.Deadline), caller.Token));

        Assert.Equal(caller.Token, cancelled.CancellationToken);
        Assert.IsAssignableFrom<OperationCanceledException>(cancelled.InnerException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheLongestDeadline_AlsoRunsARequestWithAConfigurationOverride(bool withProvider)
    {
        var host = HomeFeedTestHost.Load();
        var provider = new CountingProvider();
        await using var engine = new RecEngine(host.Environment, withProvider ? provider : null);
        Assert.True(engine.Load(host.Request).Applied);

        var result = await engine.ExecuteAsync(Request(TimeSpan.MaxValue, new JsonObject()), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(withProvider ? 1 : 0, provider.Calls);
    }

    private static RecRequest Request(TimeSpan deadline, JsonObject? configOverride = null) => new()
    {
        PipelineId = "home_feed",
        RequestId = "engine-boundary",
        Inputs = HomeFeedTestHost.Inputs(1001, "home"),
        Deadline = deadline,
        Debug = configOverride is null ? null : new DebugOptions { ConfigOverride = configOverride },
    };

    private sealed class CountingProvider : IAssignmentProvider
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<IReadOnlyList<string>> AssignAsync(AssignmentContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return ValueTask.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }
    }

    private sealed class CancelsTheCaller(CancellationTokenSource caller, bool unreadableMessage) : IAssignmentProvider
    {
        public ValueTask<IReadOnlyList<string>> AssignAsync(AssignmentContext context, CancellationToken cancellationToken)
        {
            caller.Cancel();
            if (unreadableMessage && cancellationToken.IsCancellationRequested)
            {
                throw new UnreadableCancellation(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The token the provider got was not cancelled with the caller's.");
        }
    }

    private sealed class UnreadableCancellation(CancellationToken token) : OperationCanceledException(token)
    {
        public override string Message => throw new InvalidOperationException("The cancellation's message cannot be read.");
    }
}
