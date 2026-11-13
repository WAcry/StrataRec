using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Local;
using StrataRec.Hosting.LocalTables;

namespace StrataRec.UnitTests.Hosting;

/// <summary>
/// The engine's table lifecycle (design 6.6): sharing by identity, readiness, first-load failures, restarts with backoff,
/// release and shutdown. Sources are scripted run by run and signal through task completion sources, so nothing here
/// depends on timing.
/// </summary>
public class LocalTableHostTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task SpecsWithTheSameIdentity_ShareOneTable_LoadedOnce()
    {
        using var host = new LocalTableHost();
        var first = new ScriptedSource(Load(1));
        var second = new ScriptedSource(Load(2));
        using var a = host.OpenLease();
        using var b = host.OpenLease();

        var tableA = a.Acquire(Spec(first));
        var tableB = b.Acquire(Spec(second));
        var other = b.Acquire(Spec(new ScriptedSource(Load(3)), maxBytes: "2MiB"));

        Assert.Same(tableA, tableB);
        Assert.NotSame(tableA, other);
        Assert.Equal(2, host.TableCount);
        Assert.Empty(await a.WaitLoadedAsync());
        Assert.Empty(await b.WaitLoadedAsync());
        Assert.Equal(1, first.Runs);
        Assert.Equal(0, second.Runs); // the shared table keeps the source that loaded it
        Assert.Equal(1.0, Read(tableB, 1));
    }

    [Fact]
    public async Task ReleasingTheLastLease_StopsTheSource()
    {
        using var host = new LocalTableHost();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ScriptedSource(async (_, writer, token) =>
        {
            Load(1)(UpdatePosition.Empty, writer, token).Wait(token);
            await WaitForCancellation(token, stopped);
        });
        var a = host.OpenLease();
        var b = host.OpenLease();
        a.Acquire(Spec(source));
        b.Acquire(Spec(source));
        Assert.Empty(await a.WaitLoadedAsync());

        a.Dispose();
        Assert.False(stopped.Task.IsCompleted);
        Assert.Equal(1, host.TableCount);
        b.Dispose();

        await stopped.Task.WaitAsync(Wait);
        Assert.Equal(0, host.TableCount);
    }

    [Fact]
    public async Task AFirstLoadFailure_NamesTheBindingAndTheFile_AndTheNextLeaseRetriesWithANewTable()
    {
        var logs = new ListLoggerFactory();
        using var host = new LocalTableHost(loggerFactory: logs);
        var source = new ScriptedSource(
            (_, writer, _) =>
            {
                writer.BeginReload();
                throw new FeatureUpdateException("/data/items/part-0000.jsonl:3", "/fields/score", "expected a number, got \"x\"", "write the field as a JSON number");
            },
            Load(7));
        using var failed = host.OpenLease();
        var failedTable = failed.Acquire(Spec(source));

        var failure = Assert.Single(await failed.WaitLoadedAsync());

        Assert.Equal(("item_store/item", "bindings:test", "/sources/item_store"), (failure.Table, failure.Artifact, failure.Pointer));
        Assert.Contains("/data/items/part-0000.jsonl:3 /fields/score: expected a number", failure.Message);
        Assert.Equal("write the field as a JSON number", failure.Hint);
        Assert.False(failedTable.IsLoaded);
        Assert.False(failedTable.Writer.IsReloading); // the open reload was aborted
        Assert.Contains(logs.Entries, e => e.EventId == 1202 && e.Message.Contains("part-0000.jsonl:3", StringComparison.Ordinal));

        // Still held by the failed lease, but never handed out again: a new plan loads a new table.
        using var retry = host.OpenLease();
        var retried = retry.Acquire(Spec(source));
        Assert.NotSame(failedTable, retried);
        Assert.Empty(await retry.WaitLoadedAsync());
        Assert.Equal(7.0, Read(retried, 1));
        Assert.Equal(2, source.Runs);
    }

    [Fact]
    public async Task ASourceThatReturnsWithoutLoading_FailsTheFirstLoad()
    {
        using var host = new LocalTableHost();
        using var lease = host.OpenLease();
        lease.Acquire(Spec(new ScriptedSource((_, _, _) => Task.CompletedTask)));

        var failure = Assert.Single(await lease.WaitLoadedAsync());

        Assert.Contains("returned without loading the table", failure.Message);
    }

    [Fact]
    public async Task ASourceThatFailsAfterLoading_RestartsFromThePosition_WithADelayThatDoublesUntilItMakesProgress()
    {
        var logs = new ListLoggerFactory();
        var host = new LocalTableHost(new LocalTableHostOptions { RestartDelay = TimeSpan.FromMilliseconds(1), MaxRestartDelay = TimeSpan.FromMilliseconds(3) }, logs);
        var lastRun = new TaskCompletionSource<UpdatePosition>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = new ConcurrentQueue<UpdatePosition>();
        var source = new ScriptedSource(
            (from, writer, _) =>
            {
                starts.Enqueue(from);
                writer.BeginReload();
                Write(writer, 1, 1.0);
                writer.CompleteReload(UpdatePosition.Single(5));
                throw new IOException("connection reset");
            },
            Fail(starts, "still down"),
            Fail(starts, "still down"),
            Fail(starts, "still down"),
            (from, writer, _) =>
            {
                starts.Enqueue(from);
                Write(writer, 1, 2.0);
                writer.Checkpoint(UpdatePosition.Single(6));
                throw new IOException("connection reset again");
            },
            async (from, _, token) =>
            {
                lastRun.SetResult(from);
                await WaitForCancellation(token, stopped);
            });
        using var lease = host.OpenLease();
        var table = lease.Acquire(Spec(source));

        Assert.Empty(await lease.WaitLoadedAsync());
        Assert.Equal(UpdatePosition.Single(6), await lastRun.Task.WaitAsync(Wait));

        Assert.Equal(new[] { UpdatePosition.Empty, UpdatePosition.Single(5), UpdatePosition.Single(5), UpdatePosition.Single(5), UpdatePosition.Single(5) }, starts);
        var restarts = logs.Entries.Where(e => e.EventId == 1203).ToList();
        Assert.Equal(new[] { 1.0, 2, 3, 3, 1 }, restarts.Select(e => (double)e.Values["DelayMs"]!));
        Assert.Equal(new[] { 1, 2, 3, 4, 1 }, restarts.Select(e => (int)e.Values["Failures"]!));
        Assert.Equal(2.0, Read(table, 1)); // served throughout, with what each run wrote

        await host.DisposeAsync();
        await stopped.Task.WaitAsync(Wait);
    }

    [Fact]
    public async Task DisposingTheHost_CancelsEverySource_AndWaitsForThem()
    {
        var host = new LocalTableHost();
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ScriptedSource(async (_, _, token) =>
        {
            loading.SetResult();
            await WaitForCancellation(token, returned); // never completes its first load
        });
        using var lease = host.OpenLease();
        lease.Acquire(Spec(source));
        var waiting = lease.WaitLoadedAsync();
        await loading.Task.WaitAsync(Wait);

        await host.DisposeAsync();

        Assert.True(returned.Task.IsCompleted); // the host waited for the source to return
        var failure = Assert.Single(await waiting.WaitAsync(Wait));
        Assert.Contains("stopped before the load completed", failure.Message);
        Assert.Throws<ObjectDisposedException>(() => host.OpenLease());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACancellationCallbackThatBlocks_HoldsUpNeitherTheRelease_NorShutdownPastItsTimeout(bool releasedFirst)
    {
        var logs = new ListLoggerFactory();
        var host = new LocalTableHost(new LocalTableHostOptions { ShutdownTimeout = TimeSpan.FromMilliseconds(50) }, logs);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var gate = new ManualResetEventSlim();
        var source = new ScriptedSource(async (_, writer, token) =>
        {
            await Load(1)(UpdatePosition.Empty, writer, token);
            token.Register(() =>
            {
                entered.TrySetResult();
                gate.Wait(); // a source's cleanup that hangs
            });
            await WaitForCancellation(token, returned);
        });
        var lease = host.OpenLease();
        lease.Acquire(Spec(source));
        Assert.Empty(await lease.WaitLoadedAsync());
        try
        {
            // Called off the test thread, so a call that blocks fails the test instead of hanging it.
            if (releasedFirst)
            {
                // What a plan swap does with the tables only the replaced plan read: it returns, the callback runs elsewhere.
                await Task.Run(lease.Dispose).WaitAsync(Wait);
                await entered.Task.WaitAsync(Wait);
            }

            // Shutdown waits for the released table too, and gives up after its timeout.
            await Task.Run(() => host.DisposeAsync().AsTask()).WaitAsync(Wait);

            await entered.Task.WaitAsync(Wait);
            Assert.False(gate.IsSet);
            Assert.Equal(1, Assert.Single(logs.Entries, e => e.EventId == 1205).Values["Running"]);
        }
        finally
        {
            gate.Set();
        }

        await returned.Task.WaitAsync(Wait); // the source still ends once its callback does
        lease.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACancellationCallbackThatThrows_IsLogged_AndFailsNeitherTheReleaseNorShutdown(bool releasedFirst)
    {
        var logs = new ListLoggerFactory();
        var host = new LocalTableHost(loggerFactory: logs);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ScriptedSource(async (_, writer, token) =>
        {
            await Load(1)(UpdatePosition.Empty, writer, token);
            token.Register(() => throw new InvalidOperationException("cleanup failed"));
            await WaitForCancellation(token, returned);
        });
        var lease = host.OpenLease();
        lease.Acquire(Spec(source));
        Assert.Empty(await lease.WaitLoadedAsync());

        if (releasedFirst)
        {
            lease.Dispose();
            await returned.Task.WaitAsync(Wait);
        }

        await host.DisposeAsync().AsTask().WaitAsync(Wait);

        await returned.Task.WaitAsync(Wait);
        var failed = Assert.Single(logs.Entries, e => e.EventId == 1206);
        Assert.Contains("cleanup failed", failed.Exception!.ToString());
        Assert.DoesNotContain(logs.Entries, e => e.EventId == 1205);
        lease.Dispose();
    }

    [Fact]
    public async Task WaitLoaded_HonoursItsToken_AlsoWhenThereIsNothingToWaitFor()
    {
        using var host = new LocalTableHost();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var empty = host.OpenLease();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => empty.WaitLoadedAsync(cancelled.Token));

        using var lease = host.OpenLease();
        var table = lease.Acquire(Spec(new ScriptedSource(Load(1))));
        Assert.Empty(await lease.WaitLoadedAsync());
        Assert.True(table.IsLoaded);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease.WaitLoadedAsync(cancelled.Token)); // ready already

        using var unloaded = new LocalTableHost(new LocalTableHostOptions { LoadTables = false });
        using var never = unloaded.OpenLease();
        never.Acquire(Spec(new ScriptedSource(Load(1))));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => never.WaitLoadedAsync(cancelled.Token));
    }

    [Fact]
    public async Task AHostThatDoesNotLoadTables_NeverRunsASource()
    {
        using var host = new LocalTableHost(new LocalTableHostOptions { LoadTables = false });
        var source = new ScriptedSource(Load(1));
        using var lease = host.OpenLease();
        var table = lease.Acquire(Spec(source));

        Assert.Empty(await lease.WaitLoadedAsync());
        Assert.Equal(0, source.Runs);
        Assert.False(table.IsLoaded);
    }

    private static LocalTableSpec Spec(IFeatureUpdateSource source, string maxBytes = "1MiB")
    {
        var entity = new StrataRec.Features.Catalog.EntityDefinition
        {
            Name = "item", Kind = StrataRec.Features.Catalog.EntityKind.Candidate, CandidateKeyColumn = "entity_id", CandidateKeyType = EntityKeyType.Int64,
        };
        var schema = new LocalTableSchema("item_store", entity.Name, EntityKeyType.Int64, LocalTableMode.Local, new[] { new LocalTableField("score", ColumnType.Float64) });
        var binding = ArtifactSerializer.Deserialize<FeatureSourceBinding>(
            $$"""{ "mode": "local", "max_bytes": "{{maxBytes}}", "bootstrap": { "source": "scripted@1" } }""");
        ByteSize.TryParse(maxBytes, out var bytes);
        return new LocalTableSpec(schema, new LocalFeatureTableOptions { MaxBytes = bytes }, binding, source, "bindings:test", "/sources/item_store", null);
    }

    /// <summary>A run that loads item 1 with <paramref name="score"/> in one reload and returns.</summary>
    private static Func<UpdatePosition, IFeatureTableWriter, CancellationToken, Task> Load(double score) => (_, writer, _) =>
    {
        writer.BeginReload();
        Write(writer, 1, score);
        writer.CompleteReload(UpdatePosition.Empty);
        return Task.CompletedTask;
    };

    private static Func<UpdatePosition, IFeatureTableWriter, CancellationToken, Task> Fail(ConcurrentQueue<UpdatePosition> starts, string message) =>
        (from, _, _) =>
        {
            starts.Enqueue(from);
            throw new IOException(message);
        };

    private static void Write(IFeatureTableWriter writer, long key, double score)
    {
        var builder = writer.Begin(key, (long)(score * 10));
        builder.Set(0, ScalarValue.FromFloat64(score));
        builder.Commit();
    }

    private static double Read(LocalFeatureTable table, long key)
    {
        var version = table.Pin();
        return version.Read(0, version.Find(EntityKey.FromInt64(key))).AsFloat64();
    }

    private static async Task WaitForCancellation(CancellationToken token, TaskCompletionSource returned)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, token);
        }
        finally
        {
            returned.TrySetResult();
        }
    }

    /// <summary>Runs its scripted runs in order (the last one again if it is run more often); counts runs.</summary>
    private sealed class ScriptedSource(params Func<UpdatePosition, IFeatureTableWriter, CancellationToken, Task>[] runs) : IFeatureUpdateSource
    {
        private int _runs;

        public string SourceType => "scripted@1";

        public int Runs => Volatile.Read(ref _runs);

        public Task RunAsync(UpdatePosition from, IFeatureTableWriter writer, CancellationToken cancellationToken)
        {
            var run = Interlocked.Increment(ref _runs) - 1;
            return runs[Math.Min(run, runs.Length - 1)](from, writer, cancellationToken);
        }
    }

    private sealed class ListLoggerFactory : ILoggerFactory
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new ListLogger(Entries);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class ListLogger(ConcurrentQueue<LogEntry> entries) : ILogger
        {
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

                entries.Enqueue(new LogEntry(eventId.Id, formatter(state, exception), values, exception));
            }
        }
    }

    private sealed record LogEntry(int EventId, string Message, Dictionary<string, object?> Values, Exception? Exception);
}
