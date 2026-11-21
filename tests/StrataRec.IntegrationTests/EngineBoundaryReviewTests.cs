using System.Diagnostics;
using System.Text.Json.Nodes;
using StrataRec.Core.Diagnostics;
using StrataRec.Experiments.Assignment;
using StrataRec.Hosting;

namespace StrataRec.IntegrationTests;

/// <summary>
/// The review of IO step 4b (the assignment provider on the one bounded wait), at the engine's boundary: a call the host's
/// tracing cancels as it starts is not sent, the caller's cancellation is thrown as the caller's, and the longest
/// deadline is no deadline on every path.
/// </summary>
[Collection(ProcessWideListenersCollection.Name)]
public sealed class EngineBoundaryReviewTests
{
    /// <summary>
    /// The bound is looked at again after the call starts: a host's sampler that cancels the request as the provider's
    /// span starts leaves the provider uncalled (it was called with a cancelled token).
    /// </summary>
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
    /// A provider that honours the token the engine gave it after the caller cancelled throws that token: the engine
    /// throws the caller's, as a host matching its own token expects (it threw the provider's). The message of what the
    /// provider threw is not read, so one whose message throws is still a cancellation (review of IO step 4b).
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
        Assert.IsAssignableFrom<OperationCanceledException>(cancelled.InnerException); // what the provider threw
    }

    /// <summary>The longest deadline is no deadline when the request compiles a configuration override too: its timer threw.</summary>
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
