using System.Collections.Concurrent;
using System.Diagnostics;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;
using StrataRec.UnitTests.Core;

namespace StrataRec.UnitTests.Features;

/// <summary>
/// The chunks of one fetch share its staging area, its budget and its cancellation source (design 4.8, 6.5): each chunk
/// writes only its own region, the source lives until the last connector really completed, and the memory is held once
/// per call still running. Asynchronous connectors here complete on dedicated threads, released by the test.
/// </summary>
public class FetchChunkTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Chunks_ShareOneCancellation_CancelledAtTheBudget_AndDisposedAfterTheLastConnectorCompletes()
    {
        var tokens = new ConcurrentQueue<CancellationToken>();
        var late = new TaskCompletionSource();
        var observed = new[] { new TaskCompletionSource(), new TaskCompletionSource() };
        var connector = new ScriptedConnector(async (keys, fields, writer, token) =>
        {
            tokens.Enqueue(token);
            var key = keys.CandidateKeys.Span[0].Int64Value;
            if (key == 1)
            {
                await late.Task; // ignores cancellation and finishes after the budget
                return FetchStatus.Ok;
            }

            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                observed[key - 2].SetResult();
                throw;
            }

            return FetchStatus.Ok;
        });
        var rig = new FeatureTestRig().Source("item_store", connector, timeoutMs: 100, maxBatch: 1);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        var hook = new FeatureCallHookTests.ListHook();
        var request = rig.NewRequest(new long[] { 1, 2, 3 }, hook: hook);

        var result = await request.EnsureAsync(score).AsTask().WaitAsync(Patience);
        await Task.WhenAll(observed.Select(o => o.Task)).WaitAsync(Patience); // both cooperative connectors saw the budget

        Assert.True(result.Failed);
        Assert.Contains("timed out after 100 ms", result.Error);
        Assert.Equal(3, hook.Recorded.Count);
        Assert.All(hook.Recorded, call => Assert.Equal(IoOutcome.Timeout, call.Outcome));
        var token = Assert.Single(tokens.Distinct()); // one source for the fetch, not one per chunk
        Assert.True(token.IsCancellationRequested);
        _ = token.WaitHandle; // still alive: the first connector holds it

        late.SetResult();
        await request.Fetcher.WhenIoCompleteAsync().WaitAsync(Patience);

        DisposedTokens.AssertEventuallyDisposed(token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellation_IsClassifiedCancelled_NotATimeout(bool connectorIgnoresIt)
    {
        using var caller = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource();
        var calls = 0;
        var connector = new ScriptedConnector(async (_, _, _, token) =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                started.SetResult();
            }

            if (connectorIgnoresIt)
            {
                await release.Task;
            }
            else
            {
                await Task.Delay(Timeout.Infinite, token);
            }

            return FetchStatus.Ok;
        });
        var rig = new FeatureTestRig().Source("item_store", connector, timeoutMs: 600_000, maxBatch: 1);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        var hook = new FeatureCallHookTests.ListHook();
        var request = rig.NewRequest(new long[] { 1, 2 }, deadlineMs: 600_000, hook: hook);

        var ensure = request.Fetcher.EnsureAsync(new[] { score }, caller.Token);
        await started.Task.WaitAsync(Patience);
        caller.Cancel();
        var result = await ensure.AsTask().WaitAsync(Patience);

        Assert.True(result.Failed);
        Assert.Contains("was cancelled", result.Error);
        Assert.Equal(2, hook.Recorded.Count);
        Assert.All(hook.Recorded, call => Assert.Equal(IoOutcome.Cancelled, call.Outcome));
        release.SetResult();
        await request.Fetcher.WhenIoCompleteAsync().WaitAsync(Patience);
    }

    [Fact]
    public async Task TheWaitsEndAtTheBudget_WhileAConnectorCancellationCallbackBlocks()
    {
        // Both chunks answer later. The second registers a cancellation callback that blocks until the test releases it.
        // Cancelling the shared source runs its callbacks one after another, so a wait that hung on that source would stay
        // behind the blocked callback; the waits have a bound of their own and both chunks time out at the budget.
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        using var callbackReturned = new ManualResetEventSlim();
        var answers = new[]
        {
            new TaskCompletionSource<FetchStatus>(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource<FetchStatus>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var connector = new ScriptedConnector((keys, _, _, token) =>
        {
            var key = keys.CandidateKeys.Span[0].Int64Value;
            if (key == 2)
            {
                token.Register(() =>
                {
                    callbackEntered.Set();
                    releaseCallback.Wait(Patience * 2); // outlasts the test's own cap, which must fail first
                    callbackReturned.Set();
                });
            }

            return new ValueTask<FetchStatus>(answers[key - 1].Task);
        });
        var rig = new FeatureTestRig().Source("item_store", connector, timeoutMs: 100, maxBatch: 1);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        var hook = new FeatureCallHookTests.ListHook();
        var pool = new PoisoningPool();
        var request = rig.NewRequest(new long[] { 1, 2 }, pool: pool, deadlineMs: 600_000, hook: hook);

        try
        {
            var ensure = request.EnsureAsync(score).AsTask();
            Assert.True(callbackEntered.Wait(Patience)); // the budget ran out and the callback blocks
            var result = await ensure.WaitAsync(Patience);
            Assert.False(callbackReturned.IsSet); // the callback still blocks

            Assert.True(result.Failed);
            Assert.Contains("timed out after 100 ms", result.Error);
            Assert.Equal(2, hook.Recorded.Count);
            Assert.All(hook.Recorded, call => Assert.Equal(IoOutcome.Timeout, call.Outcome));

            Assert.Equal(2, request.Memory.Release()); // both connectors still run: one hold each
            Assert.Equal(0, pool.Returned);
        }
        finally
        {
            releaseCallback.Set();
        }

        Assert.True(callbackReturned.Wait(Patience));
        answers[0].SetResult(FetchStatus.Ok);
        answers[1].SetResult(FetchStatus.Ok);
        await request.Fetcher.WhenIoCompleteAsync().WaitAsync(Patience);
        Assert.True(SpinWait.SpinUntil(() => request.Memory.IsReturned, Patience));
    }

    [Fact]
    public async Task AChunkSentAfterTheBudgetRanOut_GetsNoFreshBudget()
    {
        // The first chunk answers synchronously, but only once the fetch's budget has run out; the second never answers.
        // The budget is the fetch's, counted from its start, so the second chunk's wait times out at once. The first is a
        // timeout too: its answer is observed after the deadline (FetchObservationTests).
        var never = new TaskCompletionSource<FetchStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new ScriptedConnector((keys, fields, writer, _) =>
        {
            if (keys.CandidateKeys.Span[0].Int64Value == 2)
            {
                return new ValueTask<FetchStatus>(never.Task);
            }

            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 150)
            {
                Thread.Sleep(10);
            }

            ScriptedConnector.WriteAll(keys, fields, writer, (key, _) => key * 2.0);
            return ValueTask.FromResult(FetchStatus.Ok);
        });
        var rig = new FeatureTestRig().Source("item_store", connector, timeoutMs: 100, maxBatch: 1);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        var hook = new FeatureCallHookTests.ListHook();
        var request = rig.NewRequest(new long[] { 1, 2 }, deadlineMs: 600_000, hook: hook);

        var ensure = request.EnsureAsync(score);

        Assert.True(ensure.IsCompleted); // no wait at all, rather than another 100 ms
        var result = await ensure;
        Assert.Equal(FeatureState.Failed, result.State); // neither chunk answered within the budget
        var calls = hook.Recorded.OrderBy(c => c.Keys[0]).ToArray();
        Assert.Equal(new[] { IoOutcome.Timeout, IoOutcome.Timeout }, calls.Select(c => c.Outcome));
        Assert.All(calls, c => Assert.Contains("timed out after 100 ms", c.Status.Error));
        never.SetResult(FetchStatus.Ok);
        await request.Fetcher.WhenIoCompleteAsync().WaitAsync(Patience);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbandonedIo_IsOneHoldPerCallStillRunning_NotPerFetch(bool record)
    {
        // Three chunks: the first answers synchronously and is finished; the other two are still running when the request
        // returns without awaiting the prefetch.
        using var hang = new ManualResetEventSlim();
        var connector = new ScriptedConnector((keys, fields, writer, _) =>
        {
            if (keys.CandidateKeys.Span[0].Int64Value == 1)
            {
                ScriptedConnector.WriteAll(keys, fields, writer, (key, _) => key * 2.0);
                return ValueTask.FromResult(FetchStatus.Ok);
            }

            return OnThread(hang, () =>
            {
                ScriptedConnector.WriteAll(keys, fields, writer, (key, _) => key * 2.0);
                return FetchStatus.Ok;
            });
        });
        var rig = new FeatureTestRig().Source("item_store", connector, timeoutMs: 600_000, maxBatch: 1);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        var hook = record ? new FeatureCallHookTests.ListHook() : null;
        var pool = new PoisoningPool();
        var request = rig.NewRequest(new long[] { 1, 2, 3 }, pool: pool, deadlineMs: 600_000, hook: hook);

        request.Fetcher.Prefetch(new[] { score }, CancellationToken.None);
        var abandoned = request.Memory.Release();

        Assert.Equal(2, abandoned);
        Assert.Equal(0, pool.Returned);
        hang.Set();
        await request.Fetcher.WhenIoCompleteAsync().WaitAsync(Patience);
        Assert.True(SpinWait.SpinUntil(() => request.Memory.IsReturned, Patience)); // the last call site lets go right after
        if (hook is not null)
        {
            Assert.Equal(new[] { 2.0, 4.0, 6.0 }, hook.Recorded.OrderBy(c => c.Keys[0]).Select(c => c.Values!.Single()!.Value));
        }
    }

    [Fact]
    public async Task SynchronousAndAsynchronousChunks_AnAsynchronousCallKeepsItsWriterUntilItCompletes()
    {
        // Chunks 1 and 4 answer later, on threads of their own; the others answer at once. A view handed to a call that is
        // still running is never handed to another chunk, so the late writes land in the right keys.
        using var gate = new ManualResetEventSlim();
        var writers = new ConcurrentDictionary<long, IFeatureBatchWriter>();
        static object? ValueOf(long key, string field) => field == "tag" ? "t" + key : key * 1.5;
        var connector = new ScriptedConnector((keys, fields, writer, _) =>
        {
            var key = keys.CandidateKeys.Span[0].Int64Value;
            writers[key] = writer;
            if (key is 1 or 4)
            {
                return OnThread(gate, () =>
                {
                    ScriptedConnector.WriteAll(keys, fields, writer, ValueOf);
                    return FetchStatus.Ok;
                });
            }

            ScriptedConnector.WriteAll(keys, fields, writer, ValueOf);
            return ValueTask.FromResult(FetchStatus.Ok);
        });
        var rig = new FeatureTestRig().Source("item_store", connector, timeoutMs: 600_000, maxBatch: 1);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        var tag = rig.Physical("item.tag", FeatureTestRig.Item, ColumnType.String, "item_store");
        rig.Build();
        var request = rig.NewRequest(new long[] { 1, 2, 3, 4, 5 }, deadlineMs: 600_000);

        var ensure = request.EnsureAsync(score, tag).AsTask();
        Assert.False(ensure.IsCompleted);
        gate.Set();
        var result = await ensure.WaitAsync(Patience);

        Assert.Equal(FeatureState.Ok, result.State);
        for (var row = 0; row < 5; row++)
        {
            Assert.Equal((row + 1) * 1.5, request.Value(score, row).AsFloat64());
            Assert.Equal("t" + (row + 1), request.Value(tag, row).AsString());
        }

        foreach (var running in new long[] { 1, 4 })
        {
            for (var later = running + 1; later <= 5; later++)
            {
                Assert.NotSame(writers[running], writers[later]);
            }
        }
    }

    [Fact]
    public async Task ConcurrentChunks_WriteNumbersStringsAndLists_WhileAnAbandonedChunkKeepsWriting_AndItsResultIsNeverRead()
    {
        // Sixteen chunks of four keys write at the same time on threads of their own: their numeric values, strings, lists
        // and "written" bits share fetch-wide buffers (several chunks per 64-bit word, were the bits dense). The first chunk
        // writes and marks all of its values, then hangs, and goes on writing after the request returned. The caller cuts
        // it off once the fetch has decided the other fifteen, so no budget decides anything here: a budget is the
        // fetch's, and on a busy machine it also cut off chunks still finishing their writes.
        const int Chunks = 16;
        const int MaxBatch = 4;
        using var start = new ManualResetEventSlim();
        using var late = new ManualResetEventSlim();
        using var abandonedWrote = new ManualResetEventSlim();
        static object? ValueOf(long key, string field) => field switch
        {
            "score" => key * 0.5,
            "tag" => "t" + key,
            _ => new[] { key, key + 1, key + 2 },
        };
        static object? LateValueOf(long key, string field) => field switch
        {
            "score" => -1.0,
            "tag" => "late",
            _ => new long[64],
        };
        var hook = new DecisionHook();
        var connector = new ScriptedConnector((keys, fields, writer, _) =>
        {
            var first = keys.CandidateKeys.Span[0].Int64Value;
            if (first == 1)
            {
                return OnThread(start, () =>
                {
                    ScriptedConnector.WriteAll(keys, fields, writer, ValueOf);
                    abandonedWrote.Set();
                    late.Wait(Patience);
                    for (var i = 0; i < 100; i++)
                    {
                        ScriptedConnector.WriteAll(keys, fields, writer, LateValueOf); // grows its list arena
                    }

                    return FetchStatus.Ok;
                });
            }

            return OnThread(start, () =>
            {
                ScriptedConnector.WriteAll(keys, fields, writer, ValueOf);
                for (var i = 0; i < 500; i++)
                {
                    for (var k = 0; k < writer.KeyCount; k++)
                    {
                        foreach (var field in fields)
                        {
                            writer.SetMissing(field.FieldIndex, k);
                            writer.SetValid(field.FieldIndex, k);
                        }
                    }
                }

                hook.Answered(first);
                return FetchStatus.Ok;
            });
        });
        var rig = new FeatureTestRig().Source("item_store", connector, maxBatch: MaxBatch);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        var tag = rig.Physical("item.tag", FeatureTestRig.Item, ColumnType.String, "item_store");
        var history = rig.Physical("item.history", FeatureTestRig.Item, ColumnType.ListInt64, "item_store");
        rig.Build();
        var pool = new PoisoningPool();
        var request = rig.NewRequest(Enumerable.Range(1, Chunks * MaxBatch).Select(i => (long)i).ToArray(), pool: pool, hook: hook);
        using var caller = new CancellationTokenSource();

        var ensure = request.Fetcher.EnsureAsync(new[] { score, tag, history }, caller.Token).AsTask();
        start.Set();
        await hook.WhenDecided(Chunks - 1, Patience); // every chunk but the hung one
        Assert.True(abandonedWrote.Wait(Patience));
        caller.Cancel();
        var result = await ensure.WaitAsync(Patience); // returns at once, the first chunk still running

        // The controls are the chunks the fetch accepted, not the ones that merely finished their work; every message
        // carries each chunk's outcome and timing, so a failure explains itself.
        Assert.True(result.State == FeatureState.Degraded && result.Error!.Contains("was cancelled", StringComparison.Ordinal), $"{result.State}, {result.Error}; {hook.Describe()}");
        Assert.True(hook.Decided == Chunks && hook.Outcomes.Count == Chunks, $"each chunk is decided once; {hook.Describe()}");
        Assert.True(hook.Outcomes.All(call => call.Value.Outcome == (call.Key == 1 ? IoOutcome.Cancelled : IoOutcome.Ok)), hook.Describe());
        for (var row = 0; row < Chunks * MaxBatch; row++)
        {
            long key = row + 1;
            if (key <= MaxBatch)
            {
                // Written and marked before the cut-off, but the call was cut off: never read.
                Assert.True(request.Value(score, row).IsMissing);
                Assert.True(request.Value(tag, row).IsMissing);
                Assert.True(request.Value(history, row).IsMissing);
                continue;
            }

            Assert.True(request.IsFilled(score, row) && !request.Value(score, row).IsMissing, $"key {key} has no value; {hook.Describe()}");
            Assert.Equal(key * 0.5, request.Value(score, row).AsFloat64());
            Assert.Equal("t" + key, request.Value(tag, row).AsString());
            Assert.Equal(new[] { key, key + 1, key + 2 }, request.Value(history, row).AsInt64List());
        }

        request.Memory.Release(); // the request returns; the abandoned chunk still holds the memory
        Assert.False(request.Memory.IsReturned);
        Assert.Equal(0, pool.Returned);
        late.Set();
        await request.Fetcher.WhenIoCompleteAsync().WaitAsync(Patience);
        Assert.True(request.Memory.IsReturned);
    }

    [Theory]
    [InlineData(100, 25)]
    [InlineData(100, 3)]
    [InlineData(7, 1000)]
    public void StagingArea_EachChunkOwnsWholeBitmapWords_AndADisjointKeyRange(int keys, int chunkSize)
    {
        CompiledFieldBinding[] fields = { new("a", 0, ColumnType.Float64), new("t", 1, ColumnType.String), new("l", 2, ColumnType.ListInt64) };
        var area = new StagingArea(fields, keys, chunkSize, new RequestMemory());
        var chunks = (keys + chunkSize - 1) / chunkSize;

        Assert.Equal(chunks, area.ChunkCount);
        var previousEnd = 0;
        for (var f = 0; f < fields.Length; f++)
        {
            for (var c = 0; c < chunks; c++)
            {
                var first = area.FirstBit(f, c);
                Assert.Equal(0, first % 64); // two chunks never write one bitmap word
                Assert.True(first >= previousEnd, $"field {f} chunk {c} starts at bit {first}, inside the previous region");
                previousEnd = first + area.ChunkKeyCount(c);
            }
        }

        // Neighbouring chunks write their edges through their own views; nothing leaks across.
        var views = Enumerable.Range(0, chunks).Select(c =>
        {
            var view = new StagingBuffer(area);
            view.Bind(c);
            return view;
        }).ToArray();
        foreach (var view in views)
        {
            view.GetValues<double>(0).Span[^1] = view.KeyCount;
            view.SetValid(0, view.KeyCount - 1);
            view.SetString(1, 0, "first");
        }

        for (var key = 0; key < keys; key++)
        {
            var chunk = key / chunkSize;
            var last = key == (chunk * chunkSize) + views[chunk].KeyCount - 1;
            Assert.Equal(last, area.HasValue(0, key));
            Assert.Equal(key % chunkSize == 0, area.HasValue(1, key));
            if (last)
            {
                Assert.Equal(views[chunk].KeyCount, area.Get(0, key).AsFloat64());
            }
        }
    }

    /// <summary>Completes the call on a dedicated thread once <paramref name="gate"/> is set.</summary>
    private static ValueTask<FetchStatus> OnThread(ManualResetEventSlim gate, Func<FetchStatus> work)
    {
        var done = new TaskCompletionSource<FetchStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                if (!gate.Wait(Patience))
                {
                    throw new TimeoutException("the test never opened the gate");
                }

                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        { IsBackground = true, Name = "test connector" };
        thread.Start();
        return new ValueTask<FetchStatus>(done.Task);
    }

    /// <summary>
    /// Records each call's outcome by its first key and when the fetch decided it, and tells when a number of calls has
    /// been decided: what a test waits for before it cuts off the calls still running, instead of hoping a budget runs out
    /// after the others. <see cref="Describe"/> puts it all in a failure message.
    /// </summary>
    private sealed class DecisionHook : IFeatureCallHook
    {
        private readonly long _created = Stopwatch.GetTimestamp();
        private readonly ConcurrentDictionary<long, long> _answered = new();
        private readonly List<(int Count, TaskCompletionSource Reached)> _awaited = new();
        private int _decided;

        public ConcurrentDictionary<long, (IoOutcome Outcome, long At)> Outcomes { get; } = new();

        /// <summary>Calls recorded, counting a call recorded twice twice.</summary>
        public int Decided => Volatile.Read(ref _decided);

        public bool Replays => false;

        /// <summary>Notes that the connector's call for <paramref name="key"/> has done its work and is about to return.</summary>
        public void Answered(long key) => _answered[key] = Stopwatch.GetTimestamp();

        public async Task WhenDecided(int count, TimeSpan patience)
        {
            Task reached;
            lock (_awaited)
            {
                if (_decided >= count)
                {
                    return;
                }

                var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _awaited.Add((count, source));
                reached = source.Task;
            }

            Assert.True(await Task.WhenAny(reached, Task.Delay(patience)) == reached, $"{Decided} of {count} calls decided after {patience}; {Describe()}");
        }

        public string Describe()
        {
            var keys = new SortedSet<long>(Outcomes.Keys);
            keys.UnionWith(_answered.Keys);
            return string.Join("; ", keys.Select(key =>
                $"key {key}: " + (_answered.TryGetValue(key, out var answered) ? $"answered at {Ms(answered)} ms, " : string.Empty) +
                (Outcomes.TryGetValue(key, out var decided) ? $"{decided.Outcome} at {Ms(decided.At)} ms" : "undecided")));
        }

        public int Begin(FetchGroup group, in EntityKeyBatch batch, CompiledFieldBinding[] fields) => 0;

        public void Record(in FeatureCallInfo call, IStagedFeatureValues? values)
        {
            lock (_awaited)
            {
                Outcomes[call.Batch.CandidateKeys.Span[0].Int64Value] = (call.Outcome, Stopwatch.GetTimestamp());
                _decided++;
                foreach (var (count, reached) in _awaited)
                {
                    if (count == _decided)
                    {
                        reached.SetResult();
                    }
                }
            }
        }

        public FetchStatus Replay(FetchGroup group, in EntityKeyBatch batch, CompiledFieldBinding[] fields, IFeatureBatchWriter writer) =>
            throw new NotSupportedException();

        private string Ms(long timestamp) => Stopwatch.GetElapsedTime(_created, timestamp).TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
    }
}
