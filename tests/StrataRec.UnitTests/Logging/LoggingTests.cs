using System.Text.Json;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Logging;

namespace StrataRec.UnitTests.Logging;

public sealed class CoordinatedSamplingTests
{
    [Fact]
    public void RequestSamplingIsStableAndNearTheRate()
    {
        var seed = CoordinatedSampling.GroupSeed("feed", "deep_trace");
        var hits = 0;
        for (var i = 0; i < 100_000; i++)
        {
            var key = ScalarValue.FromString("req-" + i);
            var sampled = CoordinatedSampling.SampleRequest(seed, 0, key, 0.05);
            Assert.Equal(sampled, CoordinatedSampling.SampleRequest(seed, 0, key, 0.05));
            hits += sampled ? 1 : 0;
        }

        Assert.InRange(hits, 4_700, 5_300);
    }

    [Fact]
    public void KeyIndexSeparatesUserAndSessionKeys()
    {
        var seed = CoordinatedSampling.GroupSeed("feed", "panel");
        var differ = 0;
        for (var i = 0; i < 1_000; i++)
        {
            var key = ScalarValue.FromInt64(i);
            differ += CoordinatedSampling.SampleRequest(seed, 0, key, 0.5) != CoordinatedSampling.SampleRequest(seed, 1, key, 0.5) ? 1 : 0;
        }

        Assert.InRange(differ, 400, 600);
    }

    [Fact]
    public void BottomKKeepsSmallestPrioritiesWithThresholdProbability()
    {
        var rows = new[] { 10, 11, 12, 13, 14 };
        var priorities = new[] { 0.9, 0.1, 0.5, 0.3, 0.7 };

        var kept = CoordinatedSampling.Select(rows, priorities, rate: 1.0, maxRows: 2, out var probability);

        Assert.Equal(2, kept);
        Assert.Equal(new[] { 11, 13 }, rows[..2]); // original order of the two smallest
        Assert.Equal(0.5, probability);              // τ = third smallest priority
    }

    [Fact]
    public void RateThenBottomKTakesTheSmallerProbability()
    {
        var rows = new[] { 1, 2, 3, 4 };
        var priorities = new[] { 0.05, 0.15, 0.3, 0.6 };

        var kept = CoordinatedSampling.Select(rows, priorities, rate: 0.5, maxRows: 10, out var probability);

        Assert.Equal(3, kept);
        Assert.Equal(0.5, probability);

        rows = new[] { 1, 2, 3, 4 };
        priorities = new[] { 0.05, 0.15, 0.3, 0.6 };
        kept = CoordinatedSampling.Select(rows, priorities, rate: 0.5, maxRows: 1, out probability);
        Assert.Equal(1, kept);
        Assert.Equal(1, rows[0]);
        Assert.Equal(0.15, probability);
    }

    [Fact]
    public void RowPrioritiesAreCoordinatedAcrossTaps()
    {
        var seed = CoordinatedSampling.GroupSeed("feed", "deep_trace");
        var request = CoordinatedSampling.RequestHash("req-1");
        Assert.Equal(CoordinatedSampling.RowPriority(seed, request, 42), CoordinatedSampling.RowPriority(seed, request, 42));
        Assert.NotEqual(CoordinatedSampling.RowPriority(seed, request, 42), CoordinatedSampling.RowPriority(seed, request, 43));
    }
}

public sealed class LogPipelineTests
{
    internal static LogBatch Batch(string tap, LogDelivery delivery, int rows = 2) => new()
    {
        TapId = tap,
        RequestId = "req-1",
        PipelineId = "feed",
        ManifestId = "sha256:test",
        SchemaId = "sha256:schema",
        TimeMs = 1_767_225_600_000,
        Delivery = delivery,
        RequestProbability = 0.01,
        Int64EntityIds = rows == 2 ? new long[] { 10042, 20089 } : Array.Empty<long>(),
        Ranks = rows == 2 ? new[] { 1, 2 } : Array.Empty<int>(),
        RowProbabilities = rows == 2 ? new[] { 1.0, 0.5 } : Array.Empty<double>(),
        Columns = rows == 2
            ? new[]
            {
                new LogColumn("final", ColumnType.Float64, new[] { ScalarValue.FromFloat64(0.9), ScalarValue.Missing }),
                new LogColumn("category", ColumnType.String, new[] { ScalarValue.FromString("music"), ScalarValue.FromString("tech") }),
            }
            : Array.Empty<LogColumn>(),
        Context = new[] { ("user_id", ScalarValue.Missing) },
    };

    [Fact]
    public async Task DurableBatchesGetConsecutiveSequenceNumbers()
    {
        var sink = new InMemoryLogSink();
        await using (var pipeline = new LogPipeline(sink, producerId: "pod-1/1"))
        {
            for (var i = 0; i < 5; i++)
            {
                pipeline.Submit(Batch("served", LogDelivery.Durable));
            }

            pipeline.Submit(Batch("debug", LogDelivery.BestEffort));
            await pipeline.FlushAsync();
            Assert.Contains(pipeline.Stats(), s => s is { TapId: "served", Delivered: 5, Dropped: 0 });
        }

        var batches = sink.Snapshot();
        var seqs = batches.Where(b => b.Delivery == LogDelivery.Durable).Select(b => b.Seq).Order().ToArray();
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, seqs);
        Assert.All(batches, b => Assert.Equal("pod-1/1", b.ProducerId));
        Assert.Equal(0, batches.Single(b => b.TapId == "debug").Seq);
    }

    [Fact]
    public async Task DurableBatchesAreRetriedUntilTheSinkAccepts()
    {
        var sink = new FlakySink(failures: 3);
        await using (var pipeline = new LogPipeline(sink))
        {
            pipeline.Submit(Batch("served", LogDelivery.Durable));
            await pipeline.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(1, sink.Written);
        Assert.Equal(4, sink.Attempts);
    }

    [Fact]
    public async Task BestEffortBatchesAreDroppedAndCountedWhenTheSinkFails()
    {
        var sink = new FlakySink(failures: int.MaxValue);
        await using var pipeline = new LogPipeline(sink);
        pipeline.Submit(Batch("debug", LogDelivery.BestEffort));
        await pipeline.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(pipeline.Stats(), s => s is { TapId: "debug", Delivered: 0, Dropped: 1 });
    }

    [Fact]
    public async Task FullBestEffortQueueDropsInsteadOfBlocking()
    {
        var sink = new BlockingSink();
        await using var pipeline = new LogPipeline(sink, new LogPipelineOptions { BestEffortCapacity = 2, MaxBatchesPerWrite = 1, DrainTimeout = TimeSpan.FromMilliseconds(100) });
        for (var i = 0; i < 20; i++)
        {
            pipeline.Submit(Batch("debug", LogDelivery.BestEffort));
        }

        Assert.True(pipeline.Stats().Single().Dropped >= 17);
        sink.Release();
    }

    [Fact]
    public void JsonEncodingKeepsMissingValuesMissing()
    {
        var json = LogBatchJson.Encode(Batch("served", LogDelivery.Durable));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("served", root.GetProperty("tap_id").GetString());
        Assert.Equal(2, root.GetProperty("row_count").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("float_cols").GetProperty("final")[1].ValueKind);
        Assert.Equal(1, root.GetProperty("missing").GetProperty("final")[0].GetInt32());
        Assert.Equal("tech", root.GetProperty("string_cols").GetProperty("category")[1].GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("context").GetProperty("user_id").ValueKind);
    }

    // ---- Review 4 ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FullDurableQueueReportsTheLossOrSpills()
    {
        // Finding 3: a full durable queue must not drop silently.
        var sink = new BlockingSink();
        var spilled = new List<LogBatch>();
        await using (var plain = new LogPipeline(sink, new LogPipelineOptions { DurableCapacity = 1, MaxBatchesPerWrite = 1, DrainTimeout = TimeSpan.FromMilliseconds(50) }))
        {
            var admissions = Enumerable.Range(0, 5).Select(_ => plain.Submit(Batch("served", LogDelivery.Durable))).ToList();
            Assert.Contains(LogAdmission.Dropped, admissions);
            Assert.Equal(admissions.Count(a => a == LogAdmission.Dropped), plain.DurableOverflow);
        }

        await using (var spilling = new LogPipeline(sink, new LogPipelineOptions
        {
            DurableCapacity = 1, MaxBatchesPerWrite = 1, DrainTimeout = TimeSpan.FromMilliseconds(50),
            DurableOverflow = batch => { lock (spilled) { spilled.Add(batch); } return true; },
        }))
        {
            var admissions = Enumerable.Range(0, 5).Select(_ => spilling.Submit(Batch("served", LogDelivery.Durable))).ToList();
            Assert.DoesNotContain(LogAdmission.Dropped, admissions);
            Assert.Contains(LogAdmission.Spilled, admissions);
            Assert.Equal(0, spilling.DurableOverflow);
            Assert.All(spilled, b => Assert.True(b.Seq > 0)); // spilled batches keep their sequence number
        }

        sink.Release();
    }

    [Fact]
    public async Task PermanentlyRejectedBatchDoesNotBlockTheDurableStream()
    {
        // Finding 4: one poison batch used to be retried forever, blocking everything behind it.
        var sink = new RejectingSink(poisonTap: "poison");
        var rejected = new List<LogBatch>();
        await using (var pipeline = new LogPipeline(sink, new LogPipelineOptions { MaxBatchesPerWrite = 1, Rejected = (b, _) => rejected.Add(b) }))
        {
            pipeline.Submit(Batch("poison", LogDelivery.Durable));
            pipeline.Submit(Batch("served", LogDelivery.Durable));
            pipeline.Submit(Batch("served", LogDelivery.Durable));
            await pipeline.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, pipeline.Rejected);
            Assert.Contains(pipeline.Stats(), s => s is { TapId: "poison", Delivered: 0, Dropped: 1 });
            Assert.Contains(pipeline.Stats(), s => s is { TapId: "served", Delivered: 2, Dropped: 0 });
        }

        Assert.Equal(2, sink.Written);
        Assert.Single(rejected);
    }

    [Fact]
    public void NonFiniteVectorIsEncodedAsMissingInsteadOfThrowing()
    {
        var batch = Batch("served", LogDelivery.Durable);
        batch = new LogBatch
        {
            TapId = batch.TapId, RequestId = batch.RequestId, PipelineId = batch.PipelineId, ManifestId = batch.ManifestId, SchemaId = batch.SchemaId,
            TimeMs = batch.TimeMs, Delivery = batch.Delivery, RequestProbability = 1, Int64EntityIds = batch.Int64EntityIds, Ranks = batch.Ranks,
            RowProbabilities = batch.RowProbabilities, Context = batch.Context,
            Columns = new[] { new LogColumn("emb", ColumnType.Vector(2), new[] { ScalarValue.FromVector(new[] { 1f, float.NaN }), ScalarValue.FromVector(new[] { 1f, 2f }) }) },
        };

        using var document = JsonDocument.Parse(LogBatchJson.Encode(batch));
        var emb = document.RootElement.GetProperty("nested_cols").GetProperty("emb");
        Assert.Equal(JsonValueKind.Null, emb[0].ValueKind);
        Assert.Equal(2f, emb[1][1].GetSingle());
    }

    [Fact]
    public async Task JsonLinesSinkRejectsOnlyTheUnencodableBatch()
    {
        var writer = new StringWriter();
        using var sink = new JsonLinesLogSink(writer);
        var bad = Batch("bad", LogDelivery.Durable);
        bad.Columns[0] = new LogColumn(null!, ColumnType.Float64, new[] { ScalarValue.FromFloat64(1), ScalarValue.Missing }); // malformed: no name

        var thrown = await Assert.ThrowsAsync<LogBatchRejectedException>(async () =>
            await sink.WriteAsync(new[] { Batch("a", LogDelivery.Durable), bad, Batch("b", LogDelivery.Durable) }, CancellationToken.None));

        Assert.Same(bad, Assert.Single(thrown.Batches));
        Assert.Equal(2, writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task DisposeIsBoundedEvenWhenTheSinkIgnoresCancellation()
    {
        // Review 5 finding 5: DrainTimeout must bound shutdown even if a write never completes.
        var stuck = new TaskCompletionSource();
        var sink = new StuckSink(stuck.Task);
        var pipeline = new LogPipeline(sink, new LogPipelineOptions { DrainTimeout = TimeSpan.FromMilliseconds(20) });
        pipeline.Submit(Batch("served", LogDelivery.Durable));
        await sink.Started.WaitAsync(TimeSpan.FromSeconds(5));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await pipeline.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.InRange(watch.ElapsedMilliseconds, 0, 3_000);
        stuck.SetResult();
    }

    private sealed class StuckSink(Task release) : ILogSink
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public async ValueTask WriteAsync(IReadOnlyList<LogBatch> batches, CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await release; // ignores cancellation on purpose
        }
    }

    private sealed class RejectingSink(string poisonTap) : ILogSink
    {
        public int Written;

        public ValueTask WriteAsync(IReadOnlyList<LogBatch> batches, CancellationToken cancellationToken)
        {
            var poison = batches.Where(b => b.TapId == poisonTap).ToList();
            Interlocked.Add(ref Written, batches.Count - poison.Count);
            return poison.Count > 0 ? throw new LogBatchRejectedException(poison) : ValueTask.CompletedTask;
        }
    }

    private sealed class FlakySink(int failures) : ILogSink
    {
        public int Attempts;
        public int Written;

        public ValueTask WriteAsync(IReadOnlyList<LogBatch> batches, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Attempts) <= failures)
            {
                throw new IOException("sink unavailable");
            }

            Interlocked.Add(ref Written, batches.Count);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingSink : ILogSink
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public async ValueTask WriteAsync(IReadOnlyList<LogBatch> batches, CancellationToken cancellationToken) =>
            await _release.Task.WaitAsync(cancellationToken);
    }
}
