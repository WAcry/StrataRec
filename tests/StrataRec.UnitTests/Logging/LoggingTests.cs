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
    internal static readonly LogSchema TestSchema = new("sha256:schema", "test", [], []);

    internal static LogBatch Batch(string tap, LogDelivery delivery, int rows = 2) => new()
    {
        TapId = tap,
        RequestId = "req-1",
        PipelineId = "feed",
        ManifestId = "sha256:test",
        Schema = TestSchema,
        TimeMs = 1_767_225_600_000,
        Delivery = delivery,
        RequestProbability = 0.01,
        Int64EntityIds = rows == 2 ? new long[] { 10042, 20089 } : Array.Empty<long>(),
        Ranks = rows == 2 ? new[] { 1, 2 } : Array.Empty<int>(),
        RowProbability = 0.5,
        Columns = rows == 2
            ? new[]
            {
                LogColumn.FromValues("final", ColumnType.Float64, new[] { ScalarValue.FromFloat64(0.9), ScalarValue.Missing }),
                LogColumn.FromValues("category", ColumnType.String, new[] { ScalarValue.FromString("music"), ScalarValue.FromString("tech") }),
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
            await pipeline.FlushAsync(TestContext.Current.CancellationToken);
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
            await pipeline.FlushAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
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
        await pipeline.FlushAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Contains(pipeline.Stats(), s => s is { TapId: "debug", Delivered: 0, Dropped: 1 });
    }

    [Fact]
    public async Task FullBestEffortQueueDropsInsteadOfBlocking()
    {
        var sink = new BlockingSink();
        await using var pipeline = new LogPipeline(sink, new LogPipelineOptions { BestEffortMaxBytes = 2 * Batch("debug", LogDelivery.BestEffort).ApproximateBytes, MaxBatchesPerWrite = 1, DrainTimeout = TimeSpan.FromMilliseconds(100) });
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
        await using (var plain = new LogPipeline(sink, new LogPipelineOptions { DurableMaxBytes = OneBatch, MaxBatchesPerWrite = 1, DrainTimeout = TimeSpan.FromMilliseconds(50) }))
        {
            var admissions = Enumerable.Range(0, 5).Select(_ => plain.Submit(Batch("served", LogDelivery.Durable))).ToList();
            Assert.Contains(LogAdmission.Dropped, admissions);
            Assert.Equal(admissions.Count(a => a == LogAdmission.Dropped), plain.DurableOverflow);
        }

        await using (var spilling = new LogPipeline(sink, new LogPipelineOptions
        {
            DurableMaxBytes = OneBatch, MaxBatchesPerWrite = 1, DrainTimeout = TimeSpan.FromMilliseconds(50),
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

    private static long OneBatch => Batch("served", LogDelivery.Durable).ApproximateBytes;

    /// <summary>
    /// The queues are bounded by the memory their batches hold, not by their number: behind a stuck sink, wide candidate
    /// taps (here about 1 MiB each) stop at the default 256 MiB, where a bound of 65,536 batches let them take 64 GiB.
    /// </summary>
    [Fact]
    public async Task AStuckSink_FillsTheQueuesByteBudget_NotMemory()
    {
        var sink = new BlockingSink();
        var wide = Wide(); // shared by the batches, so the test itself stays small
        await using var pipeline = new LogPipeline(sink, new LogPipelineOptions { MaxBatchesPerWrite = 1, DrainTimeout = TimeSpan.FromMilliseconds(50) });
        var admissions = Enumerable.Range(0, 400).Select(_ => pipeline.Submit(WideBatch(wide, LogDelivery.Durable))).ToList();

        var size = WideBatch(wide, LogDelivery.Durable).ApproximateBytes;
        Assert.InRange(size, 1_000_000, 2L << 20);
        Assert.InRange(admissions.Count(a => a == LogAdmission.Queued), (256L << 20) / size - 1, (256L << 20) / size);
        Assert.Equal(admissions.Count(a => a == LogAdmission.Dropped), pipeline.DurableOverflow);
        Assert.InRange(pipeline.DurableQueuedBytes, 0, 256L << 20);

        // Written batches give their bytes back; a batch larger than the whole budget still goes into an empty queue.
        sink.Release();
        await pipeline.FlushAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(0, pipeline.DurableQueuedBytes);
    }

    [Fact]
    public async Task ABatchLargerThanTheBudget_IsQueuedWhenTheQueueIsEmpty()
    {
        var sink = new BlockingSink();
        var wide = Wide();
        await using var pipeline = new LogPipeline(sink, new LogPipelineOptions { BestEffortMaxBytes = 1024, MaxBatchesPerWrite = 1, DrainTimeout = TimeSpan.FromMilliseconds(50) });

        Assert.Equal(LogAdmission.Queued, pipeline.Submit(WideBatch(wide, LogDelivery.BestEffort)));
        Assert.Equal(LogAdmission.Dropped, pipeline.Submit(Batch("debug", LogDelivery.BestEffort)));
        sink.Release();
    }

    /// <summary>
    /// Review 65: a string many values point to (a dictionary-encoded column, a constant) is counted once per batch, not
    /// per row, which overstated 1,024 rows sharing a 4 KB string 158 times and dropped durable batches that fit; and the
    /// envelope's maps count per entry, where a fixed 256 bytes let zero-row batches of 100 node outcomes overrun the
    /// budget 13 times.
    /// </summary>
    [Fact]
    public void TheEstimate_CountsASharedStringOnce_AndTheEnvelopesMapsPerEntry()
    {
        const int rows = 1024;
        var shared = new string('s', 4096);
        var empty = Titled(rows, _ => "").ApproximateBytes; // "" is one object too

        Assert.Equal(empty + 4096 * 2, Titled(rows, _ => shared).ApproximateBytes);
        Assert.Equal(empty - 24 + rows * (24 + 4096 * 2L), Titled(rows, _ => new string('s', 4096)).ApproximateBytes);
        // Two strings alternating are two strings; two that share a slot of the table evict each other and count on every
        // row, an overstatement the estimate accepts (review 66: the test must pick them, not assume new objects differ).
        var (apart, together) = TwoPairs();
        Assert.Equal(empty - 24 + 2 * (24 + 4096 * 2L), Titled(rows, i => apart[i % 2]).ApproximateBytes);
        Assert.Equal(empty - 24 + rows * (24 + 4096 * 2L), Titled(rows, i => together[i % 2]).ApproximateBytes);

        var bare = Batch("$experiments", LogDelivery.Durable, rows: 0);
        var outcomes = Batch("$experiments", LogDelivery.Durable, rows: 0);
        outcomes.NodeOutcomes = Enumerable.Range(0, 100).ToDictionary(i => "node_" + i, _ => "succeeded");
        Assert.True(outcomes.ApproximateBytes >= bare.ApproximateBytes + 100 * 32, $"{outcomes.ApproximateBytes} for 100 outcomes");
    }

    /// <summary>Two 4 KB strings in different slots of the estimate's 256-slot table, and two in the same slot.</summary>
    private static (string[] Apart, string[] Together) TwoPairs()
    {
        var bySlot = new Dictionary<int, string>();
        string[]? apart = null, together = null;
        for (var i = 0; apart is null || together is null; i++)
        {
            var s = new string((char)('a' + i % 26), 4096);
            var slot = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(s) & 255;
            if (bySlot.TryGetValue(slot, out var other))
            {
                together ??= new[] { other, s };
            }
            else if (bySlot.Count > 0)
            {
                apart ??= new[] { bySlot.Values.First(), s };
            }

            bySlot.TryAdd(slot, s);
        }

        return (apart, together);
    }

    private static LogBatch Titled(int rows, Func<int, string> title) => new()
    {
        TapId = "titles", RequestId = "req-1", PipelineId = "feed", ManifestId = "sha256:test", Schema = TestSchema,
        TimeMs = 1_767_225_600_000, Delivery = LogDelivery.Durable, RequestProbability = 1.0,
        Int64EntityIds = Enumerable.Range(1, rows).Select(i => (long)i).ToArray(), Ranks = Enumerable.Range(1, rows).ToArray(),
        RowProbability = 1.0,
        Columns = new[] { LogColumn.FromValues("title", ColumnType.String, Enumerable.Range(0, rows).Select(i => ScalarValue.FromString(title(i))).ToArray()) },
        Context = Array.Empty<(string, ScalarValue)>(),
    };

    private static string[] Wide() => Enumerable.Range(0, 1000).Select(i => new string('x', 500)).ToArray();

    private static LogBatch WideBatch(string[] strings, LogDelivery delivery) => new()
    {
        TapId = "wide", RequestId = "req-1", PipelineId = "feed", ManifestId = "sha256:test", Schema = TestSchema,
        TimeMs = 1_767_225_600_000, Delivery = delivery, RequestProbability = 1.0,
        Int64EntityIds = new long[] { 1 }, Ranks = new[] { 1 }, RowProbability = 1.0,
        Columns = new[] { LogColumn.FromValues("titles", ColumnType.List(StorageKind.String), new[] { ScalarValue.FromStringList(strings) }) },
        Context = Array.Empty<(string, ScalarValue)>(),
    };

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
            await pipeline.FlushAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

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
            TapId = batch.TapId, RequestId = batch.RequestId, PipelineId = batch.PipelineId, ManifestId = batch.ManifestId, Schema = batch.Schema,
            TimeMs = batch.TimeMs, Delivery = batch.Delivery, RequestProbability = 1, Int64EntityIds = batch.Int64EntityIds, Ranks = batch.Ranks,
            RowProbability = batch.RowProbability, Context = batch.Context,
            Columns = new[] { LogColumn.FromValues("emb", ColumnType.Vector(2), new[] { ScalarValue.FromVector(new[] { 1f, float.NaN }), ScalarValue.FromVector(new[] { 1f, 2f }) }) },
        };

        using var document = JsonDocument.Parse(LogBatchJson.Encode(batch));
        var emb = document.RootElement.GetProperty("nested_cols").GetProperty("emb");
        Assert.Equal(JsonValueKind.Null, emb[0].ValueKind);
        Assert.Equal(2f, emb[1][1].GetSingle());
    }

    [Fact]
    public void ListInt32ColumnIsEncodedWithTheNestedColumns()
    {
        var batch = Batch("served", LogDelivery.Durable);
        batch = new LogBatch
        {
            TapId = batch.TapId, RequestId = batch.RequestId, PipelineId = batch.PipelineId, ManifestId = batch.ManifestId, Schema = batch.Schema,
            TimeMs = batch.TimeMs, Delivery = batch.Delivery, RequestProbability = 1, Int64EntityIds = batch.Int64EntityIds, Ranks = batch.Ranks,
            RowProbability = batch.RowProbability, Context = batch.Context,
            Columns = new[]
            {
                // Values are held promoted (int64 lists), like any int32 value; the column keeps its storage type.
                LogColumn.FromValues("tags", ColumnType.List(StorageKind.Int32).AsNullable(), new[] { ScalarValue.FromInt64List(new long[] { 1, int.MinValue, int.MaxValue }), ScalarValue.Missing }),
            },
        };

        using var document = JsonDocument.Parse(LogBatchJson.Encode(batch));
        var root = document.RootElement;
        var tags = root.GetProperty("nested_cols").GetProperty("tags");
        Assert.Equal(new long[] { 1, int.MinValue, int.MaxValue }, tags[0].EnumerateArray().Select(e => e.GetInt64()).ToArray());
        Assert.Equal(JsonValueKind.Null, tags[1].ValueKind);
        Assert.Equal(1, root.GetProperty("missing").GetProperty("tags")[0].GetInt32());
        Assert.False(root.GetProperty("int_cols").TryGetProperty("tags", out _));
    }

    /// <summary>
    /// Review 70: a bool is 0/1 wherever a log writes it (decisions: bool columns are int_cols), in a column, a list and the
    /// context alike. The typed column encoder wrote true/false for columns and lists while the context kept 0/1.
    /// </summary>
    [Fact]
    public void Bools_AreZeroOrOne_InColumnsListsAndContext()
    {
        var batch = Batch("served", LogDelivery.Durable);
        batch = new LogBatch
        {
            TapId = batch.TapId, RequestId = batch.RequestId, PipelineId = batch.PipelineId, ManifestId = batch.ManifestId, Schema = batch.Schema,
            TimeMs = batch.TimeMs, Delivery = batch.Delivery, RequestProbability = 1, Int64EntityIds = batch.Int64EntityIds, Ranks = batch.Ranks,
            RowProbability = batch.RowProbability, Context = [("fresh", ScalarValue.FromBool(true))],
            Columns =
            [
                LogColumn.FromValues("flag", ColumnType.Bool, [ScalarValue.FromBool(true), ScalarValue.FromBool(false)]),
                LogColumn.FromValues("flags", ColumnType.List(StorageKind.Bool), [ScalarValue.FromBoolList([true, false]), ScalarValue.FromBoolList([])]),
            ],
        };

        using var document = JsonDocument.Parse(LogBatchJson.Encode(batch, indented: false));
        var root = document.RootElement;
        Assert.Equal("[1,0]", root.GetProperty("int_cols").GetProperty("flag").GetRawText());
        Assert.Equal("[[1,0],[]]", root.GetProperty("nested_cols").GetProperty("flags").GetRawText());
        Assert.Equal("1", root.GetProperty("context").GetProperty("fresh").GetRawText());
    }

    /// <summary>
    /// Review 70: a schema id names one record. A context field whose type changed (the request field it reads did) got the
    /// same id, so a reader holding the record by id read it with the old type; the id now hashes the record itself.
    /// </summary>
    [Fact]
    public void ASchemaId_ChangesWithAnythingTheRecordSays_AndOnlyWithThat()
    {
        LogSchema Of(string tap, ColumnType contextType) =>
            LogSchema.Create(tap, [new LogField("final", ColumnType.Float64, "col.score.final")], [new LogField("x", contextType, "ctx.request.foo")]);

        Assert.Equal(Of("a", ColumnType.String).Id, Of("a", ColumnType.String).Id);
        Assert.NotEqual(Of("a", ColumnType.String).Id, Of("a", ColumnType.Int64).Id);
        Assert.NotEqual(Of("a", ColumnType.String).Id, Of("b", ColumnType.String).Id);
        Assert.StartsWith("sha256:", Of("a", ColumnType.String).Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonLinesSinkRejectsOnlyTheUnencodableBatch()
    {
        var writer = new StringWriter();
        using var sink = new JsonLinesLogSink(writer);
        var bad = Batch("bad", LogDelivery.Durable);
        bad.Columns[0] = LogColumn.FromValues(null!, ColumnType.Float64, new[] { ScalarValue.FromFloat64(1), ScalarValue.Missing }); // malformed: no name

        var thrown = await Assert.ThrowsAsync<LogBatchRejectedException>(async () =>
            await sink.WriteAsync(new[] { Batch("a", LogDelivery.Durable), bad, Batch("b", LogDelivery.Durable) }, CancellationToken.None));

        Assert.Same(bad, Assert.Single(thrown.Batches));
        var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length); // the schema's record, then the two batches
        Assert.StartsWith("{\"log_schema\":", lines[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// Design 8.7: the id a batch records names a schema record. The sink writes each schema's record once, before the
    /// first batch naming it, with every column's type and expression; it was hashed and never published.
    /// </summary>
    [Fact]
    public async Task JsonLinesSink_WritesEachSchemaRecordOnce_BeforeItsFirstBatch()
    {
        var writer = new StringWriter();
        using var sink = new JsonLinesLogSink(writer);
        var schema = new LogSchema("sha256:served", "served",
            [new LogField("final", ColumnType.Float64.AsNullable(), "col.score.final"), new LogField("tags", ColumnType.List(StorageKind.Int64), "col.item.tags")],
            [new LogField("surface", ColumnType.String, "ctx.request.surface")]);
        LogBatch Of(LogBatch b) => new()
        {
            TapId = b.TapId, RequestId = b.RequestId, PipelineId = b.PipelineId, ManifestId = b.ManifestId, Schema = schema, TimeMs = b.TimeMs,
            Delivery = b.Delivery, RequestProbability = 1, Int64EntityIds = b.Int64EntityIds, Ranks = b.Ranks, RowProbability = 1, Context = b.Context,
            Columns = b.Columns,
        };

        await sink.WriteAsync([Of(Batch("served", LogDelivery.Durable)), Of(Batch("served", LogDelivery.Durable))], CancellationToken.None);
        await sink.WriteAsync([Of(Batch("served", LogDelivery.Durable))], CancellationToken.None);

        var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        using var record = JsonDocument.Parse(lines[0]);
        var body = record.RootElement.GetProperty("log_schema");
        Assert.Equal("sha256:served", body.GetProperty("id").GetString());
        Assert.Equal(["final:float64?:col.score.final", "tags:list<int64>:col.item.tags"],
            body.GetProperty("columns").EnumerateArray().Select(c => $"{c.GetProperty("name")}:{c.GetProperty("type")}:{c.GetProperty("expr")}"));
        Assert.Equal("ctx.request.surface", body.GetProperty("context")[0].GetProperty("expr").GetString());
        Assert.All(lines[1..], line => Assert.Contains("\"schema\":\"sha256:served\"", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisposeIsBoundedEvenWhenTheSinkIgnoresCancellation()
    {
        // Review 5 finding 5: DrainTimeout must bound shutdown even if a write never completes.
        var stuck = new TaskCompletionSource();
        var sink = new StuckSink(stuck.Task);
        var pipeline = new LogPipeline(sink, new LogPipelineOptions { DrainTimeout = TimeSpan.FromMilliseconds(20) });
        pipeline.Submit(Batch("served", LogDelivery.Durable));
        await sink.Started.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The write never completes until the end of the test, so returning at all is the bound at work; the 30 s only
        // keeps a regression from hanging the run (a busy runner has stalled for seconds).
        await pipeline.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        stuck.SetResult();
    }

    [Fact]
    public async Task DrainTimeout_SettlesUnreadBatchesAsDropped()
    {
        // Review 6 #4: batches still queued when the drain timeout cancels the loop were never settled, so pending (and
        // the queue-depth series) stayed up and FlushAsync waited forever.
        var sink = new BlockingSink(); // honors cancellation
        var pipeline = new LogPipeline(sink, new LogPipelineOptions { MaxBatchesPerWrite = 1, DrainTimeout = TimeSpan.FromMilliseconds(20) });
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(LogAdmission.Queued, pipeline.Submit(Batch("served", LogDelivery.Durable)));
        }

        await pipeline.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        await pipeline.FlushAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Contains(pipeline.Stats(), s => s is { TapId: "served", Delivered: 0, Dropped: 3 });
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
