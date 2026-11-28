using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Features.Cache;
using StrataRec.Features.Local;

namespace StrataRec.UnitTests.Features.Cache;

/// <summary>
/// The table keeps no list: its order is the position of the records in its pages, which every rebuild has to preserve.
/// </summary>
public class CacheEvictionModelTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void TheTableEvictsTheKeysSieveEvicts(int seed, bool vectors)
    {
        // Scalars: records of one width, which the table orders without a sort. Vectors of four widths on pages of
        // 16 KiB: records of mixed size, some pages shared between versions and the runs between them copied.
        var schema = new LocalTableSchema("s", "item", EntityKeyType.Int64, LocalTableMode.Cache,
            [new("v", vectors ? ColumnType.List(StorageKind.Float32) : ColumnType.Float64)]);
        var clock = new ManualClock();
        var table = new LocalFeatureTable(schema, new LocalFeatureTableOptions
        {
            MaxBytes = vectors ? 256 << 10 : 64 << 10,
            PageSize = vectors ? 16 << 10 : 1 << 20,
            Cache = new CacheTableOptions { TtlMs = 3_600_000, Clock = clock },
        });
        var cache = table.Cache!;
        var record = new CacheRecordWriter(cache.Layout, 512);
        float[][] widths = [new float[1], new float[4], new float[16], new float[64]];

        var order = new LinkedList<long>();
        var nodes = new Dictionary<long, LinkedListNode<long>>();
        var values = new Dictionary<long, int>();
        var visited = new HashSet<long>();
        LinkedListNode<long>? hand = null; // null: the oldest
        var random = new Random(seed);
        long nextKey = 0;
        var evictions = 0;
        for (var step = 1; step <= 6_000; step++)
        {
            var version = table.Pin();
            for (var r = 0; r < 16; r++)
            {
                var key = random.NextDouble() < 0.8 ? random.Next(150) : random.NextInt64(Math.Max(1, nextKey - 1000), Math.Max(2, nextKey + 1));
                var found = version.Find(key);
                Assert.Equal(values.ContainsKey(key), found.Found);
                if (found.Found)
                {
                    Assert.Equal(values[key], Value(version, found));
                    found.MarkCacheHit();
                    visited.Add(key);
                }
            }

            var written = step % 7 == 0 ? random.NextInt64(Math.Max(1, nextKey))
                : step % 5 == 0 && order.Last is { } newest ? newest.Value
                : nextKey++;
            Write(written, step);
            version = table.Pin();
            Assert.Equal(step, Value(version, version.Find(written)));

            if (nodes.Remove(written, out var replaced))
            {
                hand = replaced == hand ? replaced.Next : hand;
                order.Remove(replaced);
                values.Remove(written);
                visited.Remove(written);
            }

            // How many the table evicted is its own business (it frees bytes, a quarter at a time); which ones is SIEVE's.
            var toEvict = order.Count + 1 - version.RecordCount;
            var evicted = toEvict > 0;
            while (toEvict > 0)
            {
                var at = hand ?? order.First!;
                hand = at.Next;
                if (visited.Remove(at.Value))
                {
                    continue;
                }

                order.Remove(at);
                nodes.Remove(at.Value);
                values.Remove(at.Value);
                toEvict--;
                evictions++;
            }

            nodes.Add(written, order.AddLast(written));
            values[written] = step;
            Assert.Equal(values.Count, version.RecordCount);
            if (evicted)
            {
                foreach (var (key, value) in values)
                {
                    Assert.Equal(value, Value(version, version.Find(key)));
                }
            }

            if (step % 667 == 0)
            {
                table.Writer.Compact();
            }

            clock.AdvanceMs(1);
        }

        Assert.True(evictions > 1_000, $"{evictions} evictions");

        void Write(long key, int value)
        {
            record.Begin(clock.NowMs, cache.Fingerprint, absent: false);
            if (vectors)
            {
                var vector = widths[(int)(unchecked((uint)key * 2654435761u) >> 30)];
                vector[0] = value;
                record.List<float>(0, vector);
            }
            else
            {
                record.Float64(0, value);
            }

            var (offset, length) = record.End();
            Assert.True(cache.TryWriteBack(new CacheWriteBatch([EntityKey.FromInt64(key)], [record.Bytes.AsMemory(offset, length)], 1)));
            cache.WriteQueued(table.Writer);
        }

        double Value(TableVersion version, LocalRecordRef found)
        {
            Assert.True(found.Found);
            return vectors ? version.Read(0, found).AsFloat64List()[0] : version.Read(0, found).AsFloat64();
        }
    }
}
