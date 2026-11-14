using System.Diagnostics;
using Microsoft.Extensions.Logging;
using StrataRec.Core.Bindings;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Threading;
using StrataRec.Core.Values;
using StrataRec.Experiments.Assignment;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;
using StrataRec.Hosting;
using StrataRec.Logging;
using StrataRec.Models;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;
using StrataRec.Testing;
using StrataRec.Pipeline.Sources;

namespace StrataRec.IntegrationTests;

/// <summary>
/// Review 20 #1: the framework cancels, at a deadline or a timeout, tokens it handed to code it does not own. A callback
/// that code registered and that throws used to escape on the timer's thread, which ends the process (exit 134). Each
/// site runs in a child process with the system's timers: the child must live, end the request (or the shutdown) within
/// its budget, and observe the callback's exception: counted under its site and logged, with no task left unobserved.
/// </summary>
public sealed class TimerFaultTests
{
    [Theory]
    [InlineData("call", "recall sources failed: popular: timed out after 300 ms")] // BoundedCall's own timeout
    [InlineData("node", "timed out after")] // the node's budget (AttemptBudget), no timeout of the call's own
    [InlineData("fetch", "timed out")] // a feature fetch's budget (FeatureFetcher's FetchCancellation)
    [InlineData("assignment", "deadline exceeded during assignment")] // the assignment provider's deadline
    [InlineData("log_drain", "disposed")] // the log pipeline's drain timeout
    [InlineData("caller", "cancelled by the caller")] // the caller's own timer, through the node token linked to it
    public void AThrowingCancellationCallback_IsObservedAtItsSite_AndTheProcessLives(string site, string ended)
    {
        var output = TimerFaultChild.Start(site, out var exitCode);

        Assert.True(exitCode == 0, $"the child exited with {exitCode}:\n{output}");
        Assert.DoesNotContain("UNHANDLED", output);
        Assert.Contains("ended=", output);
        Assert.Contains(ended, Value(output, "ended"));
        Assert.True(long.Parse(Value(output, "elapsed_ms")) < long.Parse(Value(output, "budget_ms")) + 1_000, output); // its budget, give or take scheduling
        Assert.True(long.Parse(Value(output, "counted")) >= 1, output);
        Assert.Equal("0", Value(output, "counted_elsewhere"));
        Assert.Equal("True", Value(output, "logged"));
        Assert.Equal("0", Value(output, "unobserved"));
    }

    /// <summary>
    /// Review 22 #1: the holder of a token can end once it sees the cancellation flag, before the pool runs the callbacks
    /// of that cancellation, and the framework then disposes the source. The base disposal clears the registrations, so
    /// the callbacks were dropped, silently. The child has one pool worker, held by a gate, so the callbacks are queued
    /// and not yet taken when the source is disposed.
    /// </summary>
    [Fact]
    public void CallbacksACancellationQueued_RunEvenIfTheSourceIsDisposedBeforeThePoolTakesThem()
    {
        var output = TimerFaultChild.Start("cancel-then-dispose", out var exitCode, singleWorker: true);

        Assert.True(exitCode == 0, $"the child exited with {exitCode}:\n{output}");
        Assert.Equal("True", Value(output, "single_worker"));
        Assert.Equal("True", Value(output, "queued")); // the order the test is about: queued, disposed, then run
        Assert.Equal("1", Value(output, "ran"));
        Assert.Equal("True", Value(output, "disposed")); // and the disposal completes once they have run
    }

    /// <summary>
    /// Review 23 #1 and #2: the cancellation and the disposal of a source may meet in any order. Two cancellations at once
    /// (the timer and the linked token), and a disposal repeated while a cancellation is still starting, must neither
    /// drop the queued callbacks nor dispose ahead of them. The pool's only worker is held while every round queues its
    /// callbacks, so none has run when its source is disposed; then every callback must run, once, and every source end
    /// disposed. The two threads of a round only start together: nothing stops one inside a window, so this is a stress
    /// test, not a schedule. With the old protocol the rounds hit the windows the review found within a few dozen (in a
    /// review's run, 60 of 2,000 two-cancellation rounds dropped callbacks), so it fails reliably, not by construction.
    /// </summary>
    [Fact]
    public void CancellationsAndDisposals_InAnyOrder_RunEveryQueuedCallbackOnce_AndDisposeAfterThem()
    {
        var output = TimerFaultChild.Start("cancel-races", out var exitCode, singleWorker: true);

        Assert.True(exitCode == 0, $"the child exited with {exitCode}:\n{output}");
        Assert.Equal("True", Value(output, "single_worker"));
        Assert.Equal(Value(output, "rounds"), Value(output, "two_cancellations_ran"));
        Assert.Equal(Value(output, "rounds"), Value(output, "two_cancellations_disposed"));
        Assert.Equal(Value(output, "rounds"), Value(output, "double_disposal_ran"));
        Assert.Equal(Value(output, "rounds"), Value(output, "double_disposal_disposed"));
    }

    private static string Value(string output, string key)
    {
        foreach (var line in output.Split('\n'))
        {
            if (line.StartsWith(key + "=", StringComparison.Ordinal))
            {
                return line[(key.Length + 1)..].TrimEnd('\r');
            }
        }

        throw new Xunit.Sdk.XunitException($"the child printed no {key}:\n{output}");
    }
}

/// <summary>
/// The child process of <see cref="TimerFaultTests"/>: one site, with a client that registers a throwing callback on
/// its token and answers only when the token is cancelled. Prints what the parent checks, one <c>key=value</c> a line.
/// </summary>
internal static class TimerFaultChild
{
    public const string CleanupFailed = "the extension's cleanup failed";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>Runs this assembly as a child process for <paramref name="site"/> and returns what it printed.</summary>
    /// <param name="singleWorker">The child runs with one processor, so it can hold the pool to a single worker.</param>
    public static string Start(string site, out int exitCode, bool singleWorker = false)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.ArgumentList.Add(typeof(TimerFaultChild).Assembly.Location);
        if (site is "cancel-then-dispose" or "cancel-races")
        {
            start.ArgumentList.Add(site);
        }
        else
        {
            start.ArgumentList.Add("timer-fault");
            start.ArgumentList.Add(site);
        }

        start.Environment["DOTNET_DbgEnableMiniDump"] = "0"; // a crash is the failure the test is about: no dump
        if (singleWorker)
        {
            start.Environment["DOTNET_PROCESSOR_COUNT"] = "1"; // the pool's maximum cannot go below the processor count
        }
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            child.Kill(entireProcessTree: true);
            child.WaitForExit();
        }

        exitCode = child.ExitCode;
        return stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Cancels a source while the pool's only worker is held, so the callbacks are queued and not yet taken; disposes
    /// it, as the framework does once the token's holder has ended; then lets the pool go, counts the callbacks and
    /// checks that the disposal completed after them.
    /// </summary>
    public static int CancelThenDispose()
    {
        ThreadPool.SetMinThreads(1, 1);
        var single = ThreadPool.SetMaxThreads(1, 1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        ThreadPool.UnsafeQueueUserWorkItem(_ => { entered.Set(); release.Wait(); }, null);
        entered.Wait();

        var source = new ObservedCancellationSource(CancellationSite.Call);
        var ran = 0;
        source.Token.Register(() => Interlocked.Increment(ref ran));
        var callbacks = source.CancelObservedAsync();
        var queued = !callbacks.IsCompleted;
        source.Dispose();
        release.Set();
        var done = callbacks.Wait(Patience);
        var disposed = DisposedTokens.WaitUntilDisposed(source.Token, Patience);

        Console.WriteLine($"single_worker={single}");
        Console.WriteLine($"queued={queued}");
        Console.WriteLine($"done={done}");
        Console.WriteLine($"ran={Volatile.Read(ref ran)}");
        Console.WriteLine($"disposed={disposed}");
        return 0;
    }

    /// <summary>
    /// The rounds of <see cref="TimerFaultTests.CancellationsAndDisposals_InAnyOrder_RunEveryQueuedCallbackOnce_AndDisposeAfterThem"/>,
    /// with the pool's only worker held until all are queued.
    /// </summary>
    public static int CancelRaces()
    {
        const int Rounds = 2000;
        ThreadPool.SetMinThreads(1, 1);
        var single = ThreadPool.SetMaxThreads(1, 1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        ThreadPool.UnsafeQueueUserWorkItem(_ => { entered.Set(); release.Wait(); }, null);
        entered.Wait();

        // Two cancellations at once, then the disposal once both have returned.
        var twoRan = 0;
        var two = new ObservedCancellationSource[Rounds];
        using (var start = new Barrier(3))
        {
            var current = default(ObservedCancellationSource);
            void Canceller()
            {
                for (var i = 0; i < Rounds; i++)
                {
                    start.SignalAndWait();
                    _ = current!.CancelObservedAsync();
                    start.SignalAndWait();
                }
            }

            var a = new Thread(Canceller);
            var b = new Thread(Canceller);
            a.Start();
            b.Start();
            for (var i = 0; i < Rounds; i++)
            {
                current = two[i] = new ObservedCancellationSource(CancellationSite.Call);
                current.Token.Register(() => Interlocked.Increment(ref twoRan));
                start.SignalAndWait();
                start.SignalAndWait();
                current.Dispose();
            }

            a.Join();
            b.Join();
        }

        // One cancellation, and the disposal twice as soon as the token is cancelled: while it may still be starting.
        var doubleRan = 0;
        var twice = new ObservedCancellationSource[Rounds];
        using (var go = new Barrier(2))
        {
            var current = default(ObservedCancellationSource);
            var canceller = new Thread(() =>
            {
                for (var i = 0; i < Rounds; i++)
                {
                    go.SignalAndWait();
                    _ = current!.CancelObservedAsync();
                    go.SignalAndWait();
                }
            });
            canceller.Start();
            for (var i = 0; i < Rounds; i++)
            {
                current = twice[i] = new ObservedCancellationSource(CancellationSite.Call);
                current.Token.Register(() => Interlocked.Increment(ref doubleRan));
                go.SignalAndWait();
                var token = current.Token;
                SpinWait.SpinUntil(() => token.IsCancellationRequested);
                current.Dispose();
                current.Dispose();
                go.SignalAndWait();
            }

            canceller.Join();
        }

        release.Set();
        SpinWait.SpinUntil(() => Volatile.Read(ref twoRan) == Rounds && Volatile.Read(ref doubleRan) == Rounds, Patience);
        SpinWait.SpinUntil(() => DisposedCount(two) == Rounds && DisposedCount(twice) == Rounds, Patience);
        Console.WriteLine($"single_worker={single}");
        Console.WriteLine($"rounds={Rounds}");
        Console.WriteLine($"two_cancellations_ran={Volatile.Read(ref twoRan)}");
        Console.WriteLine($"two_cancellations_disposed={DisposedCount(two)}");
        Console.WriteLine($"double_disposal_ran={Volatile.Read(ref doubleRan)}");
        Console.WriteLine($"double_disposal_disposed={DisposedCount(twice)}");
        return 0;
    }

    private static int DisposedCount(ObservedCancellationSource[] sources)
    {
        var disposed = 0;
        foreach (var source in sources)
        {
            try
            {
                _ = source.Token;
            }
            catch (ObjectDisposedException)
            {
                disposed++;
            }
        }

        return disposed;
    }

    public static int Run(string site)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Console.WriteLine($"UNHANDLED: {e.ExceptionObject.GetType().Name}: {(e.ExceptionObject as Exception)?.Message}");
            Console.Out.Flush();
        };
        var unobserved = 0;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Interlocked.Increment(ref unobserved);
            Console.WriteLine($"unobserved task: {e.Exception.InnerException?.Message}");
        };

        using var capture = new MetricCapture();
        var loggers = new ListLoggerFactory();
        var token = site == "caller" ? "node" : site; // the caller cancels the node token, the one the source holds
        var (ended, budgetMs, elapsed, events) = site switch
        {
            "call" => Pipeline(loggers, popularTimeoutMs: 300, deadlineMs: 5_000),
            "node" => Pipeline(loggers, popularTimeoutMs: null, deadlineMs: 400),
            "caller" => Pipeline(loggers, popularTimeoutMs: null, deadlineMs: 5_000, callerTimeoutMs: 300),
            "fetch" => Fetch(loggers, timeoutMs: 300, deadlineMs: 5_000),
            "assignment" => Assignment(loggers, deadlineMs: 300),
            "log_drain" => LogDrain(loggers, drainMs: 200),
            _ => throw new ArgumentException($"no site '{site}'"),
        };

        // The callbacks run on the pool: wait until the failure was counted, then for anything that comes after it.
        int Counted(bool here) => capture.Of(StrataRecMetrics.Names.CancellationCallbackErrors)
            .Count(m => (m.Tags.TryGetValue(StrataRecTags.Token, out var tag) && (string?)tag == token) == here);
        SpinWait.SpinUntil(() => Counted(here: true) > 0, Patience);
        Thread.Sleep(200);
        events?.Flush();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // The request event log's summary (2001) names the event and the site and samples the callback's message; the
        // engine's (1102) and the log pipeline's (3005) own events carry the exception.
        var logged = loggers.Entries.Any(e => e.EventId.Id == 2001
                && e.Message.Contains($"CancellationCallbacksFailed for '{token}'", StringComparison.Ordinal)
                && e.Message.Contains(CleanupFailed, StringComparison.Ordinal)
            || e.EventId.Id is 1102 or 3005 && e.Exception?.ToString().Contains(CleanupFailed, StringComparison.Ordinal) == true);
        Console.WriteLine($"ended={ended}");
        Console.WriteLine($"elapsed_ms={elapsed}");
        Console.WriteLine($"budget_ms={budgetMs}");
        Console.WriteLine($"counted={Counted(here: true)}");
        Console.WriteLine($"counted_elsewhere={Counted(here: false)}");
        Console.WriteLine($"logged={logged}");
        Console.WriteLine($"unobserved={Volatile.Read(ref unobserved)}");
        foreach (var entry in loggers.Entries)
        {
            Console.WriteLine($"log {entry.EventId.Id}: {entry.Message}{(entry.Exception is null ? string.Empty : " / " + entry.Exception.GetType().Name)}");
        }

        return 0;
    }

    /// <summary>
    /// The sample's recall, its <c>popular</c> source bound to a client that never answers on its own: cut off at its
    /// own timeout (<c>call</c>), or, without one, at the node's budget, here the request's deadline (<c>node</c>), or by
    /// the caller's token, cancelled by its own timer (<c>caller</c>).
    /// </summary>
    private static (string Ended, int BudgetMs, long ElapsedMs, RequestEventLog? Events) Pipeline(
        ListLoggerFactory loggers, int? popularTimeoutMs, int deadlineMs, int? callerTimeoutMs = null)
    {
        var timeout = popularTimeoutMs is { } ms ? $", \"timeout_ms\": {ms}" : string.Empty;
        var host = HomeFeedTestHost.LoadModified(root => SampleBindings.RebindCandidateSource(root, "popular",
            $$"""{ "client": "{{HangingSources.Type}}", "data_file": "fixtures/recall_popular.json"{{timeout}} }"""));
        var events = new RequestEventLog(loggers.CreateLogger("events"), autoFlush: false);
        var (result, elapsed) = Execute(host, events, deadlineMs, sources: new HangingSources(), callerTimeoutMs: callerTimeoutMs);
        if (result is null)
        {
            return ("cancelled by the caller", callerTimeoutMs!.Value, elapsed, events);
        }

        var recall = result.Trace?.FirstOrDefault(n => n.NodeId == "recall");
        return ($"{result.Error} | recall: {recall?.Detail}", deadlineMs, elapsed, events);
    }

    /// <summary>The sample's <c>item_store</c> bound to a connector that never answers on its own: cut off at the fetch's budget.</summary>
    private static (string Ended, int BudgetMs, long ElapsedMs, RequestEventLog? Events) Fetch(ListLoggerFactory loggers, int timeoutMs, int deadlineMs)
    {
        var host = HomeFeedTestHost.LoadModified(root => SampleBindings.Rebind(root, "item_store",
            $$"""{ "connector": "{{HangingConnectors.Type}}", "data_file": "fixtures/items.json", "timeout_ms": {{timeoutMs}}, "max_batch": 25 }"""));
        var events = new RequestEventLog(loggers.CreateLogger("events"), autoFlush: false);
        var (result, elapsed) = Execute(host, events, deadlineMs, connectors: new HangingConnectors());
        var details = string.Join(" | ", (result!.Trace ?? Array.Empty<NodeOutcome>()).Select(n => n.Detail).Where(d => d is not null));
        return ($"{result.Error} | {details}", deadlineMs, elapsed, events);
    }

    /// <returns>The result, or null when the caller's token (cancelled after <paramref name="callerTimeoutMs"/>) ended the request.</returns>
    private static (RecResult? Result, long ElapsedMs) Execute(HomeFeedTestHost host, RequestEventLog events, int deadlineMs,
        ICandidateSourceFactory? sources = null, IFeatureConnectorFactory? connectors = null, int? callerTimeoutMs = null)
    {
        var environment = new PlanEnvironment
        {
            BaseDirectory = host.Root,
            Models = ModelClientRegistry.CreateDefault(),
            Connectors = connectors is null ? FeatureConnectorRegistry.CreateDefault() : FeatureConnectorRegistry.CreateDefault().Add(connectors),
            CandidateSources = sources is null ? CandidateSourceRegistry.CreateDefault() : CandidateSourceRegistry.CreateDefault().Add(sources),
        };
        var engine = new RecEngine(environment, executor: new PipelineExecutor(null, null, events));
        var load = engine.Load(host.Request);
        if (!load.Applied)
        {
            throw new InvalidOperationException(string.Join("\n", load.Diagnostics));
        }

        using var caller = callerTimeoutMs is { } ms ? new CancellationTokenSource(ms) : new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        try
        {
            var result = engine.ExecuteAsync(Request("req-timer-fault", deadlineMs), caller.Token).AsTask().WaitAsync(Patience).GetAwaiter().GetResult();
            return (result, watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested)
        {
            return (null, watch.ElapsedMilliseconds);
        }
    }

    /// <summary>A provider that never answers on its own: the request's deadline cancels its token.</summary>
    private static (string Ended, int BudgetMs, long ElapsedMs, RequestEventLog? Events) Assignment(ListLoggerFactory loggers, int deadlineMs)
    {
        var host = HomeFeedTestHost.Load();
        var engine = new RecEngine(host.Environment, new HangingProvider(), loggerFactory: loggers);
        if (!engine.Load(host.Request).Applied)
        {
            throw new InvalidOperationException("the sample did not load");
        }

        var watch = Stopwatch.StartNew();
        var result = engine.ExecuteAsync(Request("req-timer-fault", deadlineMs, force: false)).AsTask().WaitAsync(Patience).GetAwaiter().GetResult();
        return (result.Error ?? "succeeded", deadlineMs, watch.ElapsedMilliseconds, null);
    }

    /// <summary>A log sink whose write never ends on its own: the drain timeout cancels its token when the pipeline is disposed.</summary>
    private static (string Ended, int BudgetMs, long ElapsedMs, RequestEventLog? Events) LogDrain(ListLoggerFactory loggers, int drainMs)
    {
        var pipeline = new LogPipeline(new HangingSink(), new LogPipelineOptions { DrainTimeout = TimeSpan.FromMilliseconds(drainMs) }, "child", loggers);
        pipeline.Submit(new LogBatch
        {
            TapId = "served",
            RequestId = "req-timer-fault",
            PipelineId = "home_feed",
            ManifestId = "sha256:test",
            SchemaId = "sha256:schema",
            TimeMs = 1_767_225_600_000,
            Delivery = LogDelivery.Durable,
            RequestProbability = 1,
            Int64EntityIds = Array.Empty<long>(),
            Ranks = Array.Empty<int>(),
            RowProbabilities = Array.Empty<double>(),
            Columns = Array.Empty<LogColumn>(),
            Context = Array.Empty<(string, ScalarValue)>(),
        });
        var watch = Stopwatch.StartNew();
        pipeline.DisposeAsync().AsTask().WaitAsync(Patience).GetAwaiter().GetResult();
        return ("disposed", drainMs, watch.ElapsedMilliseconds, null);
    }

    private static RecRequest Request(string requestId, int deadlineMs, bool force = true) => new()
    {
        PipelineId = "home_feed",
        RequestId = requestId,
        Inputs = HomeFeedTestHost.Inputs(1001, "home"),
        Deadline = TimeSpan.FromMilliseconds(deadlineMs),
        TimeMs = 1_767_225_600_000,
        Debug = new DebugOptions { Trace = true, ForceArms = force ? ReplayTests.ControlArms : null },
    };

    /// <summary>
    /// Registers a callback that throws on <paramref name="token"/>, and returns a task that ends only when the token is
    /// cancelled: what an extension that cleans up on cancellation, and fails at it, looks like.
    /// </summary>
    private static Task<T> Hang<T>(CancellationToken token)
    {
        var ended = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(static state => ((TaskCompletionSource<T>)state!).TrySetCanceled(), ended);
        token.Register(static () => throw new InvalidOperationException(CleanupFailed));
        return ended.Task;
    }

    private sealed class HangingSources : ICandidateSourceFactory
    {
        public const string Type = "hanging_list@1";

        private readonly MemoryListCandidateSourceFactory _inner = new();

        public string ClientType => Type;

        public ICandidateSource Create(BindingContext context) => new Source(_inner.Create(context));

        private sealed class Source(ICandidateSource inner) : ICandidateSource
        {
            public string ClientType => inner.ClientType;

            public ValueTask<CandidateSourceStatus> RetrieveAsync(CandidateQuery query, ICandidateSink sink, CancellationToken cancellationToken) =>
                new(Hang<CandidateSourceStatus>(cancellationToken));
        }
    }

    private sealed class HangingConnectors : IFeatureConnectorFactory
    {
        public const string Type = "hanging_memory@1";

        private readonly MemoryFeatureConnectorFactory _inner = new();

        public string ConnectorType => Type;

        public IFeatureSourceConnector Create(BindingContext context) => new Connector(_inner.Create(context));

        private sealed class Connector(IFeatureSourceConnector inner) : IFeatureSourceConnector
        {
            public string ConnectorType => Type;

            public bool Supports(KeyShape shape) => inner.Supports(shape);

            public ValueTask<FetchStatus> FetchAsync(EntityKeyBatch keys, ReadOnlyMemory<CompiledFieldBinding> fields, IFeatureBatchWriter writer,
                CancellationToken cancellationToken) => new(Hang<FetchStatus>(cancellationToken));
        }
    }

    private sealed class HangingProvider : IAssignmentProvider
    {
        public ValueTask<IReadOnlyList<string>> AssignAsync(AssignmentContext context, CancellationToken cancellationToken) =>
            new(Hang<IReadOnlyList<string>>(cancellationToken));
    }

    private sealed class HangingSink : ILogSink
    {
        public ValueTask WriteAsync(IReadOnlyList<LogBatch> batches, CancellationToken cancellationToken) => new(Hang<bool>(cancellationToken));
    }
}
