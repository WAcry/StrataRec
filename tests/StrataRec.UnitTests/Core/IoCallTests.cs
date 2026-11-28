using System.Diagnostics;
using Microsoft.Extensions.Logging;
using StrataRec.Core.Diagnostics;
using StrataRec.UnitTests.Features.Cache;

namespace StrataRec.UnitTests.Core;

public sealed class IoCallSpanTests
{
    [Theory]
    [InlineData(ListenerStage.Sample)]
    [InlineData(ListenerStage.Start)]
    [InlineData(ListenerStage.Stop)]
    public void AListenerThatThrowsOnACall_LeavesTheFlowsCurrentActivity_AndTheNextCallUnderTheRequest(ListenerStage stage)
    {
        using var flow = new TracedFlow(stage, "recall first");

        var first = new IoTarget(IoRoles.Recall, "first").Start();
        var during = Activity.Current;
        first.Complete(IoOutcome.Ok);
        var after = Activity.Current;
        var next = new IoTarget(IoRoles.Recall, "next").Start();
        next.Complete(IoOutcome.Ok);

        Assert.Equal(1, flow.Thrown);
        Assert.Same(flow.Request, after);
        Assert.Same(flow.Request, Assert.Single(flow.Named("recall next")).Parent);
        if (stage == ListenerStage.Stop)
        {
            Assert.Same(Assert.Single(flow.Named("recall first")), during);
            Assert.True(during!.IsStopped);
        }
        else
        {
            // No span: the sampler threw before one was created, or the start callback after it was made current.
            Assert.Same(flow.Request, during);
        }

        if (stage == ListenerStage.Start)
        {
            Assert.False(Assert.Single(flow.Named("recall first")).IsStopped); // not stopped: stopping would report it
        }
    }

    /// <summary>
    /// <see cref="Activity.Stop"/> makes the activity a span started over current, whatever the flow's current one.
    /// </summary>
    [Theory]
    [InlineData(ListenerStage.None)]
    [InlineData(ListenerStage.Stop)]
    public async Task ACallCompletedOnAFlowWithAnotherActivity_LeavesThatActivityCurrent(ListenerStage stage)
    {
        using var flow = new TracedFlow(stage, "fetch detached");
        var call = new IoTarget(IoRoles.Fetch, "detached").Start();
        call.Sent();
        Assert.Same(flow.Request, Activity.Current);

        var (current, foreign) = await Task.Run(() =>
        {
            var other = new Activity("another request").SetParentId(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom()).Start();
            call.Complete(IoOutcome.Ok, 1);
            var after = Activity.Current;
            other.Stop();
            return (after, other);
        }, TestContext.Current.CancellationToken);

        Assert.Same(foreign, current);
        Assert.True(Assert.Single(flow.Named("fetch detached")).IsStopped);
        Assert.Same(flow.Request, Activity.Current);
    }

    [Fact]
    public async Task ACallCompletedOnAFlowWhoseActivityStopped_LeavesIt_AndTheFlowIsNeverMovedToTheRequest()
    {
        using var flow = new TracedFlow();
        var call = new IoTarget(IoRoles.Recall, "late").Start();
        call.Sent();
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Activity foreign;
        Task<(Activity? Before, Activity? After, bool Moved, ActivityTraceId Next)> completing;
        using (foreign = new Activity("another request").SetParentId(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom()).Start())
        {
            completing = Task.Run(async () =>
            {
                await resume.Task;
                var before = Activity.Current;
                var moved = false;
                void Changed(object? sender, ActivityChangedEventArgs args) =>
                    moved |= ReferenceEquals(args.Previous, before) && ReferenceEquals(args.Current, flow.Request);
                Activity.CurrentChanged += Changed;
                try
                {
                    call.Complete(IoOutcome.Ok);
                }
                finally
                {
                    Activity.CurrentChanged -= Changed;
                }

                var after = Activity.Current;
                using var next = new Activity("the flow goes on").Start();
                return (before, after, moved, next.TraceId);
            }, TestContext.Current.CancellationToken);
        }

        resume.SetResult();
        var (before, after, moved, nextTrace) = await completing;

        Assert.True(foreign.IsStopped);
        Assert.Same(foreign, before);
        Assert.Same(foreign, after);
        Assert.False(moved);
        Assert.Equal(foreign.TraceId, nextTrace);
        Assert.True(Assert.Single(flow.Named("recall late")).IsStopped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACallStartedWithoutFlow_GivesTheFlowCompletingIt_ItsActivityBack(bool running)
    {
        using var flow = new TracedFlow();
        IoCall call;
        using (ExecutionContext.SuppressFlow())
        {
            call = new IoTarget(IoRoles.Recall, "unflowed").Start();
            call.Sent();
        }

        var (before, after, nextTrace) = await Task.Run(() =>
        {
            Activity.Current = null;
            using var own = running ? new Activity("another request").Start() : null;
            var before = Activity.Current;
            call.Complete(IoOutcome.Ok);
            var after = Activity.Current;
            using var next = new Activity("the flow goes on").Start();
            return (before, after, next.TraceId);
        }, TestContext.Current.CancellationToken);

        Assert.Same(before, after);
        Assert.NotEqual(flow.Request.TraceId, nextTrace);
        Assert.True(Assert.Single(flow.Named("recall unflowed")).IsStopped);
    }
}

[Collection(MetricMeasurement.Name)]
public sealed class IoCallMetricTests
{
    [Fact]
    public void AListenerThatThrowsOnTheDuration_LosesOnlyThatMeasurement()
    {
        const string source = "metric_boundaries";
        using var keys = new MeasurementHook(StrataRecMetrics.Names.IoKeys, static () => { }, (StrataRecTags.IoTarget, source));
        using var errors = new MeasurementHook(StrataRecMetrics.Names.IoErrors, static () => { }, (StrataRecTags.IoTarget, source));
        using var duration = MeasurementHook.Throwing(StrataRecMetrics.Names.IoDuration, (StrataRecTags.IoTarget, source));
        var logger = new EventIds();
        using var events = new RequestEventLog(logger, autoFlush: false);

        new IoTarget(IoRoles.Fetch, source).Start().Complete(IoOutcome.Error, 3, "req", "origin failed", events);
        events.Flush();

        Assert.Equal((1, 1, 1), (duration.Calls, keys.Calls, errors.Calls));
        Assert.Equal([2001, 2002], logger.Ids); // the source started failing, and the window's summary
    }

    private sealed class EventIds : ILogger
    {
        public List<int> Ids { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Ids.Add(eventId.Id);
    }
}
