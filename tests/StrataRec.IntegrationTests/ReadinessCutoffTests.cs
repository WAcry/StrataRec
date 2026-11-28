using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using StrataRec.Core.Bindings;
using StrataRec.Core.Diagnostics;
using StrataRec.Hosting;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

public sealed class ReadinessCutoffTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReadWithNothingToWaitFor_IsNotSettled_AfterTheBudgetRanOutOrTheCallerGaveUp(bool callerCancels)
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var factory = new CutoffProbeFactory(empty: true, (context, i) =>
        {
            if (callerCancels)
            {
                caller.Cancel();
            }
            else
            {
                Assert.True(context.CancellationToken.WaitHandle.WaitOne(RecallQueryTests.Patience));
            }
        });
        await using var engine = Engine(factory);

        if (callerCancels)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ExecuteAsync(RecallQueryTests.Request("req-empty-caller"), caller.Token).AsTask());
        }
        else
        {
            var result = await engine.ExecuteAsync(RecallQueryTests.Request("req-empty-deadline"), caller.Token);
            Assert.Equal("timed out after 500 ms", result.Trace!.Single(n => n.NodeId == "cutoff").Detail);
        }

        Assert.Equal([0], factory.Settled);
        Assert.False(factory.Returned);
    }

    [Fact]
    public async Task TheOperatorsList_IsReadOncePerRead_BeforeAnyCallback()
    {
        var factory = new CutoffProbeFactory(empty: false, static (_, _) => { }, recordList: true);
        await using var engine = Engine(factory);

        var result = await engine.ExecuteAsync(RecallQueryTests.Request("req-list-once"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal([0, 1], factory.Settled);
        Assert.Equal([(0, 0), (1, 0)], factory.ListReads); // (index, reads settled when it was asked for)
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheBoundPassingWhileTheListIsRead_SettlesNothing(bool callerCancels)
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var factory = new CutoffProbeFactory(empty: false, static (_, _) => { }, recordList: true, whileReadingTheSecond: context =>
        {
            if (callerCancels)
            {
                caller.Cancel();
            }
            else
            {
                Assert.True(context.CancellationToken.WaitHandle.WaitOne(RecallQueryTests.Patience));
            }
        });
        await using var engine = Engine(factory);

        if (callerCancels)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ExecuteAsync(RecallQueryTests.Request("req-list-caller"), caller.Token).AsTask());
        }
        else
        {
            var result = await engine.ExecuteAsync(RecallQueryTests.Request("req-list-deadline"), caller.Token);
            Assert.Equal("timed out after 500 ms", result.Trace!.Single(n => n.NodeId == "cutoff").Detail);
        }

        Assert.Empty(factory.Settled);
        Assert.False(factory.Returned);
    }

    [Fact]
    public async Task ACallbacksException_IsTheNodesFailure_AndTheNextReadIsNotSettled()
    {
        var factory = new CutoffProbeFactory(empty: false, (_, _) => throw new InvalidOperationException("settled callback failure"));
        await using var engine = Engine(factory);

        var result = await engine.ExecuteAsync(RecallQueryTests.Request("req-settled-error"), TestContext.Current.CancellationToken);

        Assert.Equal("InvalidOperationException: settled callback failure", result.Trace!.Single(n => n.NodeId == "cutoff").Detail);
        Assert.Equal([0], factory.Settled);
    }

    [Fact]
    public async Task AnUntimedReplay_SettlesTheSameReads()
    {
        var factory = new CutoffProbeFactory(empty: false, static (_, _) => { });
        await using var engine = Engine(factory);
        var token = TestContext.Current.CancellationToken;
        var result = await engine.ExecuteAsync(RecallQueryTests.Request("req-settled-replay"), token);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal([0, 1], factory.Settled);
        factory.Settled.Clear();

        var replayed = await engine.ReplayAsync(result.Recording!, cancellationToken: token);

        Assert.True(replayed.Report.Diff.Identical);
        Assert.Equal([0, 1], factory.Settled);
    }

    internal static RecEngine Engine(CutoffProbeFactory factory)
    {
        var host = HomeFeedTestHost.LoadModified(root => ReplayTests.Edit(root, "pipelines/home_feed.jsonc",
            "\"recall\": {", "\"cutoff\": { \"order\": 50, \"op\": \"CutoffProbe@1\", \"timeout_ms\": 500, \"config\": {} }, \"recall\": {"));
        var engine = new RecEngine(new PlanEnvironment { BaseDirectory = host.Root, Operators = OperatorRegistry.CreateDefault().Add(factory) });
        var load = engine.Load(host.Request);
        Assert.True(load.Applied, string.Join("\n", load.Diagnostics));
        return engine;
    }

    internal sealed class CutoffProbeFactory(
        bool empty, Action<OperatorContext, int> onSettled, bool region = false, bool recordList = false,
        Action<OperatorContext>? whileReadingTheSecond = null) : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } = new("CutoffProbe", 1, Effects.ColumnWriter, typeof(NoSettings), "Settles two reads.");

        public ConcurrentQueue<int> Settled { get; } = new();

        public bool Returned { get; private set; }

        public Action<OperatorContext, int> OnSettled { get; } = onSettled;

        public ConcurrentQueue<(int Index, int Settled)> ListReads { get; } = new();

        public bool RecordList { get; } = recordList;

        public Action<OperatorContext>? WhileReadingTheSecond { get; } = whileReadingTheSecond;

        public Operator? Compile(OperatorCompileContext context)
        {
            if (empty)
            {
                return new Probe(this, [AwaitedReads.None, AwaitedReads.None]);
            }

            if (context.CompileAwaitedExpression("ctx.user.embedding", "/user") is not { } user)
            {
                return null;
            }

            var first = context.AwaitedReadsOf([user]);
            if (!region)
            {
                return new Probe(this, [first, first]);
            }

            return context.CompileAwaitedExpression("ctx.country.region", "/region") is { } second
                ? new Probe(this, [first, context.AwaitedReadsOf([second])])
                : null;
        }

        private sealed class Probe(CutoffProbeFactory owner, AwaitedReads[] reads) : Operator
        {
            public override async ValueTask ExecuteAsync(OperatorContext context)
            {
                IReadOnlyList<AwaitedReads> list = owner.RecordList ? new RecordingReads(reads, owner, context, owner.WhileReadingTheSecond) : reads;
                await context.WhenEachReadyAsync(list, (i, _) =>
                {
                    owner.Settled.Enqueue(i);
                    owner.OnSettled(context, i);
                });
                owner.Returned = true;
            }
        }

        private sealed class RecordingReads(AwaitedReads[] reads, CutoffProbeFactory owner, OperatorContext context, Action<OperatorContext>? hold)
            : IReadOnlyList<AwaitedReads>
        {
            public int Count => reads.Length;

            public AwaitedReads this[int index]
            {
                get
                {
                    owner.ListReads.Enqueue((index, owner.Settled.Count));
                    if (index == 1)
                    {
                        hold?.Invoke(context);
                    }

                    return reads[index];
                }
            }

            public IEnumerator<AwaitedReads> GetEnumerator() => ((IEnumerable<AwaitedReads>)reads).GetEnumerator();

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}

/// <summary>
/// With a prefetch fault: both reads are stored in the same round, the second's prefetch faulted (a listener of
/// geo_store's values throws, process-wide, hence the collection).
/// </summary>
[Collection(ProcessWideListenersCollection.Name)]
public sealed class ReadinessCutoffFaultTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFaultReadAfterTheBudgetRanOutOrTheCallerGaveUp_DoesNotStandForIt(bool callerCancels)
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var faulted = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Meter.Name == StrataRecTelemetry.Name && instrument.Name == StrataRecMetrics.Names.SourceValues)
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var (key, value) in tags)
            {
                if (key == StrataRecTags.Source && (string?)value == "geo_store")
                {
                    Interlocked.Increment(ref faulted);
                    throw new InvalidOperationException("injected prefetch fault");
                }
            }
        });
        listener.Start();
        var factory = new ReadinessCutoffTests.CutoffProbeFactory(empty: false, (context, i) =>
        {
            Assert.Equal(1, Volatile.Read(ref faulted)); // the second read's prefetch has faulted already
            if (callerCancels)
            {
                caller.Cancel();
            }
            else
            {
                Assert.True(context.CancellationToken.WaitHandle.WaitOne(RecallQueryTests.Patience));
            }
        }, region: true);
        await using var engine = ReadinessCutoffTests.Engine(factory);

        if (callerCancels)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ExecuteAsync(RecallQueryTests.WithCountry("req-fault-caller"), caller.Token).AsTask());
        }
        else
        {
            var result = await engine.ExecuteAsync(RecallQueryTests.WithCountry("req-fault-deadline"), caller.Token);
            Assert.Equal("timed out after 500 ms", result.Trace!.Single(n => n.NodeId == "cutoff").Detail);
        }

        Assert.Equal([0], factory.Settled);
        Assert.False(factory.Returned);
    }
}
