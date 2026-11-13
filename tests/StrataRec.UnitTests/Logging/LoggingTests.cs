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
