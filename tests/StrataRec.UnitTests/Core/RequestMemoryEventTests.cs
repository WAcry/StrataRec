using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Memory;

namespace StrataRec.UnitTests.Core;

/// <summary>
/// The two memory events of design 13.2: a buffer pool that will not take a request's buffers back, and buffers still
/// rented long after their request returned because calls it abandoned have not finished.
/// </summary>
public sealed class RequestMemoryEventTests
{
    [Fact]
    public void APoolThatThrows_KeepsThoseBuffers_TakesTheOthers_AndIsReportedOncePerRelease()
    {
        var logger = new ListLogger();
        using var events = new RequestEventLog(logger, autoFlush: false);
        var pool = new RefusingPool();
        var ended = new Ended();
        var memory = new RequestMemory(pool, ended, events, "home_feed", "req-1");
        _ = memory.Rent<float>(16);
        _ = memory.Rent<int>(16);
        _ = memory.Rent<float>(16);

        Assert.Equal(0, memory.Release()); // never throws for the pool: the last release may run on a client's thread
        Assert.Equal((1, true), (pool.Taken, memory.IsReturned)); // the int buffer went back; the floats are the GC's
        Assert.Equal(1, ended.Count); // and what the request borrowed is no longer borrowed

        events.Flush();
        var entry = Assert.Single(logger.Entries);
        Assert.Equal((RequestEvent.BufferReturnFailed, "home_feed", 1L, "req-1"),
            ((RequestEvent)entry.State["Event"]!, (string)entry.State["Subject"]!, (long)entry.State["Count"]!, (string)entry.State["SampleRequestId"]!));
        Assert.Equal("the buffer pool did not take back 2 of 3 buffer(s), left to the GC: refused", entry.State["SampleError"]);
    }

    [Fact]
    public void ARequestWhoseBuffersAreStillHeld_IsReportedOnce_WhenTheWatchTimeHasPassed()
    {
        var logger = new ListLogger();
        using var events = new RequestEventLog(logger, autoFlush: false, heldAfter: TimeSpan.Zero);
        var memory = Abandoned();

        events.WatchHeld(memory, "home_feed", "req-hung");
        events.Flush();
        var entry = Assert.Single(logger.Entries);
        Assert.Equal((RequestEvent.BuffersHeld, "home_feed", 1L, "req-hung"),
            ((RequestEvent)entry.State["Event"]!, (string)entry.State["Subject"]!, (long)entry.State["Count"]!, (string)entry.State["SampleRequestId"]!));
        Assert.EndsWith("s after the request returned: calls it abandoned at a timeout have not finished", (string)entry.State["SampleError"]!);

        logger.Entries.Clear();
        events.Flush();
        Assert.Empty(logger.Entries); // once per request: the watch ends with the report

        Assert.Equal(0, memory.Release());
    }

    [Fact]
    public void ARequestWhoseCallsFinished_IsNotReported()
    {
        var logger = new ListLogger();
        using var events = new RequestEventLog(logger, autoFlush: false, heldAfter: TimeSpan.Zero);
        var memory = Abandoned();
        events.WatchHeld(memory, "home_feed", "req-late");

        Assert.Equal(0, memory.Release()); // the abandoned call ended before the flush
        events.Flush();
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void BeforeTheWatchTime_NothingIsReported_AndTheRequestStaysWatched()
    {
        var logger = new ListLogger();
        using var events = new RequestEventLog(logger, autoFlush: false);
        var memory = Abandoned();
        events.WatchHeld(memory, "home_feed", "req-slow");

        events.Flush();
        events.Flush();
        Assert.Empty(logger.Entries);
        Assert.Equal(RequestEventLog.DefaultHeldAfter, events.HeldAfter);
        Assert.Equal(0, memory.Release());
    }

    /// <summary>A request's memory after the request returned, held by one call it abandoned.</summary>
    private static RequestMemory Abandoned()
    {
        var memory = new RequestMemory();
        _ = memory.Rent<float>(16);
        memory.Hold();
        Assert.Equal(1, memory.Release());
        return memory;
    }

    /// <summary>Takes back int buffers only.</summary>
    private sealed class RefusingPool : IBufferPool
    {
        public int Taken { get; private set; }

        public T[] Rent<T>(int minimumLength) => new T[minimumLength];

        public void Return<T>(T[] array, bool clearArray)
        {
            if (typeof(T) != typeof(int))
            {
                throw new InvalidOperationException("refused");
            }

            Taken++;
        }
    }

    private sealed class Ended : IRequestEndObserver
    {
        public int Count { get; private set; }

        public void RequestEnded() => Count++;
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
