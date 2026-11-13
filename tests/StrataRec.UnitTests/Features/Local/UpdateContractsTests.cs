using System.Text.Json;
using StrataRec.Core.Keys;
using StrataRec.Core.Values;
using StrataRec.Features.Local;
using static StrataRec.UnitTests.Features.Local.LocalTableTestSupport;

namespace StrataRec.UnitTests.Features.Local;

/// <summary><see cref="UpdatePosition"/> and the in-memory update source.</summary>
public class UpdateContractsTests
{
    [Fact]
    public void UpdatePosition_EveryWayOfMakingOne_RejectsNegativeOffsets()
    {
        // Codex review 9, item 8: Single bypassed the checks and made {"0":-1}, which FromJson then rejected.
        Assert.Throws<ArgumentOutOfRangeException>(() => UpdatePosition.Single(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UpdatePosition(new[] { new PartitionOffset(0, -1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UpdatePosition(new[] { new PartitionOffset(-1, 0) }));
        Assert.Throws<ArgumentException>(() => new UpdatePosition(new[] { new PartitionOffset(1, 0), new PartitionOffset(1, 2) }));
        using (var negative = JsonDocument.Parse("""{"0": -1}"""))
        {
            Assert.Throws<FormatException>(() => UpdatePosition.FromJson(negative.RootElement));
        }

        // What can be made reads back as itself.
        foreach (var position in new[] { UpdatePosition.Empty, UpdatePosition.Single(0), UpdatePosition.Single(long.MaxValue) })
        {
            using var json = JsonDocument.Parse(position.ToString());
            Assert.Equal(position, UpdatePosition.FromJson(json.RootElement));
        }
    }

    [Fact]
    public async Task MemorySource_AppliesUpdatesInOrder_AndCheckpointsEachBatch()
    {
        var source = new MemoryFeatureUpdateSource();
        source.Upsert(EntityKey.FromInt64(1), 1, new Dictionary<string, ScalarValue> { ["i64"] = ScalarValue.FromInt64(10) });
        source.Upsert(EntityKey.FromInt64(1), 2, new Dictionary<string, ScalarValue> { ["i64"] = ScalarValue.FromInt64(20) });
        source.Delete(EntityKey.FromInt64(2), 1);
        source.Complete();

        var table = Table();
        await source.RunAsync(UpdatePosition.Empty, table.Writer, CancellationToken.None);
        Assert.Equal(UpdatePosition.Single(3), table.Position);
        Assert.Equal(20L, table.Pin().Read(table.Schema.FieldIndex("i64"), table.Pin().Find(1L)).AsInt64());
        Assert.Equal(1, table.Pin().TombstoneCount);

        // Resuming from the table's position applies nothing again.
        var resumed = Table();
        await source.RunAsync(UpdatePosition.Single(3), resumed.Writer, CancellationToken.None);
        Assert.Equal(0, resumed.Pin().RecordCount);
    }

    [Fact]
    public async Task MemorySource_CancelledBeforeItsFirstBatch_WritesNothing()
    {
        // Codex review 9, item 7 (third case): a completed source with an update waiting, run with a cancelled token.
        var source = new MemoryFeatureUpdateSource();
        source.Upsert(EntityKey.FromInt64(1), 1, new Dictionary<string, ScalarValue> { ["i64"] = ScalarValue.FromInt64(1) });
        source.Complete();
        var table = Table();
        var empty = table.Pin();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.RunAsync(UpdatePosition.Empty, table.Writer, cancel.Token));
        Assert.Same(empty, table.Pin());
        Assert.Equal(0, table.Pin().RecordCount);
        Assert.Equal(UpdatePosition.Empty, table.Position);
    }

    [Theory]
    [InlineData(1, 1)] // Codex review 10, item 3: cancelled while the first of three updates is applied
    [InlineData(3, 3)] // cancelled while the last one is applied: the checkpoint and the successful return are next
    public async Task MemorySource_CancelledMidBatch_ThrowsAndKeepsThePosition(int cancelAtBegin, int applied)
    {
        var source = new MemoryFeatureUpdateSource();
        for (var key = 1L; key <= 3; key++)
        {
            source.Upsert(EntityKey.FromInt64(key), 1, new Dictionary<string, ScalarValue> { ["i64"] = ScalarValue.FromInt64(key) });
        }

        source.Complete();
        var table = Table();
        using var cancel = new CancellationTokenSource();
        var writer = new CancellingWriter(table.Writer, cancel, cancelAtBegin);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.RunAsync(UpdatePosition.Empty, writer, cancel.Token));
        Assert.Equal(cancelAtBegin, writer.Begins);
        Assert.Equal(applied, table.Pin().RecordCount); // what was applied before the cancellation stays...
        Assert.Equal(UpdatePosition.Empty, table.Position); // ...but the position does not move past it

        // A rerun from the table's position applies everything; the updates applied twice change nothing.
        await source.RunAsync(table.Position, table.Writer, CancellationToken.None);
        Assert.Equal(3, table.Pin().RecordCount);
        Assert.Equal(UpdatePosition.Single(3), table.Position);
    }

    /// <summary>Passes everything to a table's writer and cancels a token on the n-th <c>Begin</c>.</summary>
    private sealed class CancellingWriter(IFeatureTableWriter inner, CancellationTokenSource cancel, int cancelAtBegin) : IFeatureTableWriter
    {
        public int Begins { get; private set; }

        public LocalTableSchema Schema => inner.Schema;

        public RecordBuilder Begin(long key, long version) => Counted(inner.Begin(key, version));

        public RecordBuilder Begin(ReadOnlySpan<char> key, long version) => Counted(inner.Begin(key, version));

        public RecordBuilder Begin(in EntityKey key, long version) => Counted(inner.Begin(key, version));

        public bool Delete(long key, long version) => inner.Delete(key, version);

        public bool Delete(ReadOnlySpan<char> key, long version) => inner.Delete(key, version);

        public bool Delete(in EntityKey key, long version) => inner.Delete(key, version);

        public void Checkpoint(UpdatePosition position) => inner.Checkpoint(position);

        public void BeginReload() => inner.BeginReload();

        public void CompleteReload(UpdatePosition position) => inner.CompleteReload(position);

        public void AbortReload() => inner.AbortReload();

        private RecordBuilder Counted(RecordBuilder builder)
        {
            if (++Begins == cancelAtBegin)
            {
                cancel.Cancel();
            }

            return builder;
        }
    }
}
