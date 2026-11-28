using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using StrataRec.Core.Diagnostics;

namespace StrataRec.UnitTests.Core;

[Collection(AllocationMeasurement.Name)]
public sealed class RequestEventLogTests
{
    [Fact]
    public void ConcurrentFailures_BecomeOneSummaryPerSubjectAndWindow()
    {
        var logger = new ListLogger();
        using var events = new RequestEventLog(logger, autoFlush: false);

        Parallel.For(0, 10_000, i => events.Report(RequestEvent.NodeFallback, i % 2 == 0 ? "feed/ranker" : "feed/filter", "req-" + i, "boom"));
        events.Flush();

        Assert.Equal(2, logger.Entries.Count);
        Assert.All(logger.Entries, e => Assert.Equal(5_000L, e.State["Count"]));
        Assert.All(logger.Entries, e => Assert.StartsWith("req-", (string)e.State["SampleRequestId"]!));
        Assert.All(logger.Entries, e => Assert.Equal(LogLevel.Warning, e.Level));

        logger.Entries.Clear();
        events.Flush();
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Dependency_IsLoggedWhenItStartsFailingAndWhenItRecovers()
    {
        var logger = new ListLogger();
        using var events = new RequestEventLog(logger, autoFlush: false);
        var source = new IoTarget(IoRoles.Recall, "ann");

        Calls(events, source, ok: 10, failed: 0);
        events.Flush();
        Assert.Empty(logger.Entries);
        Calls(events, source, ok: 10, failed: 1);
        events.Flush();
        Assert.Equal(2001, Assert.Single(logger.Entries).EventId.Id);

        logger.Entries.Clear();
        Calls(events, source, ok: 5, failed: 5);
        events.Flush();
        var failing = Assert.Single(logger.Entries, e => e.EventId.Id == 2002);
        Assert.Equal(5L, failing.State["Failures"]);
        Assert.Equal(10L, failing.State["Calls"]);
        Assert.Equal("recall ann", Assert.Single(logger.Entries, e => e.EventId.Id == 2001).State["Subject"]); // the timeout summary

        logger.Entries.Clear();
        Calls(events, source, ok: 0, failed: 3);
        events.Flush();
        Assert.DoesNotContain(logger.Entries, e => e.EventId.Id is 2002 or 2003);

        logger.Entries.Clear();
        events.Flush();
        Calls(events, source, ok: 4, failed: 1);
        events.Flush();
        var recovered = Assert.Single(logger.Entries, e => e.EventId.Id == 2003);
        Assert.Equal(LogLevel.Information, recovered.Level);
        Assert.Equal(("recall", "ann"), (recovered.State["Role"], recovered.State["Name"]));
    }

    [Fact]
    public void ARoleOfItsOwn_IsMeasuredAndSummarizedAsTheBuiltInOnes_AndTwoRolesOfOneName_AreTwoDependencies()
    {
        var logger = new ListLogger();
        using var events = new RequestEventLog(logger, autoFlush: false);
        var lookup = new IoTarget("lookup", "geo");
        var fetch = new IoTarget(IoRoles.Fetch, "geo");
        using var errors = new MeasurementHook(StrataRecMetrics.Names.IoErrors, static () => { }, (StrataRecTags.IoRole, "lookup"), (StrataRecTags.IoTarget, "geo"));
        using var keys = new MeasurementHook(StrataRecMetrics.Names.IoKeys, static () => { }, (StrataRecTags.IoRole, "lookup"), (StrataRecTags.IoTarget, "geo"));

        lookup.Start().Complete(IoOutcome.Error, keys: 2, requestId: "req-1", error: "down", events: events);
        lookup.Start().Complete(IoOutcome.Error, requestId: "req-2", error: "down", events: events);
        fetch.Start().Complete(IoOutcome.Ok, keys: 3, events: events);
        fetch.Start().Complete(IoOutcome.Ok, keys: 3, events: events);
        events.Flush();

        Assert.Equal((2, 1), (errors.Calls, keys.Calls));
        Assert.Equal("lookup geo", Assert.Single(logger.Entries, e => e.EventId.Id == 2001).State["Subject"]);
        var failing = Assert.Single(logger.Entries, e => e.EventId.Id == 2002);
        Assert.Equal(("lookup", "geo", 2L, 2L), (failing.State["Role"], failing.State["Name"], failing.State["Failures"], failing.State["Calls"]));
    }

    [Theory]
    [InlineData("fetch geo")] // would be one subject with ("fetch", "geo shard")
    [InlineData("Fetch")]
    [InlineData("1fetch")]
    [InlineData("fetch-geo")]
    public void ARoleIsALabel(string role)
    {
        Assert.Throws<ArgumentException>(() => new IoTarget(role, "shard"));
    }

    [Fact]
    public void ReportingAnExistingPair_AllocatesNothing()
    {
        using var events = new RequestEventLog(new ListLogger(), autoFlush: false);
        var source = new IoTarget(IoRoles.Fetch, "items");
        var first = Measure(events, source);
        var second = Measure(events, source);
        var third = Measure(events, source);

        Assert.True(third == 0, $"{first} B, {second} B, {third} B");
    }

    private static long Measure(RequestEventLog events, IoTarget target)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            events.Report(RequestEvent.NodeFailed, "feed/ranker", "req-1", "boom");
            events.RecordIo(target, IoOutcome.Timeout, "req-1", "slow");
            events.RecordIo(target, IoOutcome.Ok, "req-1", null);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void Instrumentation_WithoutListeners_AllocatesNothing()
    {
        var source = new IoTarget(IoRoles.Fetch, "items");
        var model = new IoTarget(IoRoles.Model, "ctr");
        KeyValuePair<string, object?>[] tags = [new(StrataRecTags.Pipeline, "feed"), new(StrataRecTags.Node, "ranker")];
        Exercise(source, model, tags);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            Exercise(source, model, tags);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static void Exercise(IoTarget source, IoTarget model, KeyValuePair<string, object?>[] tags)
    {
        Assert.Null(StrataRec.Hosting.RequestTelemetry.StartRequest("feed", "req-1"));
        Assert.False(StrataRecTracing.IsRecording);
        source.Start().Complete(IoOutcome.Error, 25, "req-1", "boom");
        model.Start().Complete(IoOutcome.Ok);
        StrataRecMetrics.NodeDuration.Record(0.002, tags);
        StrataRecMetrics.NodeRowsIn.Record(500, tags);
        StrataRecMetrics.NodeOutcomes.Add(1, tags);
    }

    [Fact]
    public void AllFailedCalls_RecordedWhileFlushing_NeverReadAsRecovery()
    {
        // Producers run on dedicated threads with a fixed amount of work, so a busy thread pool (CI) cannot leave the
        // windows empty.
        var logger = new ListLogger();
        using var events = new RequestEventLog(logger, autoFlush: false);
        var target = new IoTarget(IoRoles.Fetch, "store");

        var producers = new Thread[2];
        for (var p = 0; p < producers.Length; p++)
        {
            producers[p] = new Thread(() =>
            {
                for (var i = 0; i < 500_000; i++)
                {
                    events.RecordIo(target, IoOutcome.Error, "req", "down");
                }
            }) { IsBackground = true };
            producers[p].Start();
        }

        var flushes = 0;
        while (producers[0].IsAlive || producers[1].IsAlive)
        {
            events.Flush();
            flushes++;
        }

        foreach (var producer in producers)
        {
            producer.Join();
        }

        events.Flush();

        Assert.True(flushes > 1, "no flush overlapped the producers");
        Assert.DoesNotContain(logger.Entries, e => e.EventId.Id == 2003); // recovered
        var failing = Assert.Single(logger.Entries, e => e.EventId.Id == 2002);
        Assert.Equal(failing.State["Calls"], failing.State["Failures"]);
    }

    private static void Calls(RequestEventLog events, IoTarget target, int ok, int failed)
    {
        for (var i = 0; i < ok; i++)
        {
            events.RecordIo(target, IoOutcome.Ok, "req-ok", null);
        }

        for (var i = 0; i < failed; i++)
        {
            events.RecordIo(target, IoOutcome.Timeout, "req-slow", "timed out");
        }
    }

    private sealed record Entry(LogLevel Level, EventId EventId, IReadOnlyDictionary<string, object?> State);

    private sealed class ListLogger : ILogger
    {
        public ConcurrentBag<Entry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (key, value) in pairs)
                {
                    values[key] = value;
                }
            }

            Entries.Add(new Entry(logLevel, eventId, values));
        }
    }
}
