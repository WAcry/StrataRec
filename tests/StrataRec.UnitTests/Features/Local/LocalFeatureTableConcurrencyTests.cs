using System.IO.Hashing;
using System.Runtime.InteropServices;
using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Local;
using Xunit.Abstractions;

namespace StrataRec.UnitTests.Features.Local;

/// <summary>
/// One writer thread updates, partially updates, deletes and compacts while reader threads look records up and fill
/// columns, as requests do (design 6.6 item 3). Every value of a record is derived from a stamp, so a reader can check
/// that what it read is one complete record of the key it asked for: a torn record, a record of another key, a stale
/// directory or a half-built index would break an invariant or throw.
/// </summary>
/// <remarks>
/// <para>Fields come in two groups. A full update writes both from one stamp; a partial update writes only group P, so a
/// record may combine a Q stamp with a newer P stamp, but within a group all fields share one stamp, which is never
/// newer than the record's version.</para>
/// <para>Dedicated threads and a fixed amount of writer work: readers start first and keep reading until the writer has
/// finished, so the two overlap however the scheduler runs them (CI has two cores; also run under <c>taskset -c 0,1</c>).
/// Small pages, a small compaction threshold, a growing key range and thousands of distinct strings make the index, the
/// page directory and the dictionary grow many times, and the writer compacts many times.</para>
/// </remarks>
public class LocalFeatureTableConcurrencyTests(ITestOutputHelper output)
{
    private const int StableKeys = 200;      // written before the readers start, never deleted: always found

    // Stable keys the writer rewrites in a fifth of its operations, and every reader batch ends with: their records are the
    // newest, so a batch that overlaps a page-directory or index growth probes records published after the growth.
    private const int HotKeys = 8;
    private const int MaxKeys = 12_000;
    private const int Operations = 60_000;
    private const int ReaderCount = 3;
    private const int Batch = 160;
    private const int MinReaderIterations = 200;
    private const int DistinctStrings = 1_000_000; // effectively a new string per write: published dictionaries keep growing

    private static readonly LocalTableField[] Fields =
    {
        new("q", ColumnType.Int64),         // key << 32 | stamp
        new("q_f64", ColumnType.Float64),
        new("q_i32", ColumnType.Int32),
        new("q_text", ColumnType.Text),
        new("q_vec", ColumnType.Vector(8)),
        new("q_li64", ColumnType.ListInt64),
        new("q_ls", ColumnType.ListString),
        new("p", ColumnType.Int64),         // key << 32 | stamp
        new("p_f32", ColumnType.Float32),
        new("p_b", ColumnType.Bool),
        new("p_s", ColumnType.String),
        new("p_li32", ColumnType.ListInt32),
    };

    private const int Q = 0, QF64 = 1, QI32 = 2, QText = 3, QVec = 4, QLi64 = 5, QLs = 6, P = 7, PF32 = 8, PB = 9, PS = 10, PLi32 = 11;

    [Theory]
    [InlineData(EntityKeyType.Int64)]
    [InlineData(EntityKeyType.String)]
    public void ReadersSeeOnlyCompleteRecords_WhileTheWriterUpdatesGrowsAndCompacts(EntityKeyType keyType)
    {
        var table = new LocalFeatureTable(
            new LocalTableSchema("s", "e", keyType, LocalTableMode.Local, Fields),
            new LocalFeatureTableOptions
            {
                PageSize = 1024, // a few records per page: the page directory grows often
                CompactionMinBytes = 64 * 1024,

                // 2,048 hash values for 12,000 string keys: collisions everywhere, on top of the probe chains.
                StringKeyHasher = keyType == EntityKeyType.String ? static key => XxHash3.HashToUInt64(MemoryMarshal.AsBytes(key)) % 2048 : null,
            });
        var writer = table.Writer;
        var model = new Dictionary<long, (long Version, long QStamp, long PStamp, bool Deleted)>();
        for (var key = 1L; key <= StableKeys; key++)
        {
            WriteFull(writer, keyType, key, key);
            model[key] = (key, key, key, false);
        }

        var failures = new List<Exception>();
        var writerDone = 0;
        var ready = new CountdownEvent(ReaderCount);
        var found = new long[ReaderCount];
        var iterations = new long[ReaderCount];
        var threads = new List<Thread>();
        for (var r = 0; r < ReaderCount; r++)
        {
            var id = r;
            threads.Add(new Thread(() => Guard(failures, () =>
            {
                ready.Signal();
                Read(table, keyType, id, () => Volatile.Read(ref writerDone) == 1, out found[id], out iterations[id]);
            })) { IsBackground = true, Name = "local-table-reader-" + id });
        }

        var writerThread = new Thread(() => Guard(failures, () =>
        {
            try
            {
                ready.Wait();
                Write(writer, keyType, model);
            }
            finally
            {
                Volatile.Write(ref writerDone, 1);
            }
        })) { IsBackground = true, Name = "local-table-writer" };
        threads.Add(writerThread);
        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        Assert.True(failures.Count == 0, string.Join("\n\n", failures));
        var stats = table.Pin();
        output.WriteLine($"{table.Compactions} compactions, generation {stats.Generation}, {stats.StringCount} strings, {stats.PageCount} pages, " +
            $"{stats.Index.SlotCount} index slots; reader batches {string.Join("/", iterations)}, found {string.Join("/", found)}");

        // The work really exercised growth and compaction, and the readers really read.
        Assert.True(table.Compactions >= 8, $"{table.Compactions} compactions");
        var final = table.Pin();
        Assert.True(final.Generation >= 8);
        Assert.True(final.StringCount > 1000, $"{final.StringCount} strings");
        Assert.True(final.PageCount > 64, $"{final.PageCount} pages");
        Assert.True(final.Index.SlotCount >= 16_384, $"{final.Index.SlotCount} index slots");
        for (var r = 0; r < ReaderCount; r++)
        {
            Assert.True(iterations[r] >= MinReaderIterations && found[r] > iterations[r] * Batch / 4, $"reader {r}: {iterations[r]} batches, {found[r]} found");
        }

        // The final table is exactly what the writer meant to write.
        foreach (var (key, expected) in model)
        {
            var record = final.Find(KeyOf(keyType, key));
            Assert.Equal(!expected.Deleted, record.Found);
            if (expected.Deleted)
            {
                continue;
            }

            Assert.Equal(expected.Version, final.VersionOf(record));
            Assert.Equal(expected.QStamp < 0 ? ScalarValue.Missing : ScalarValue.FromInt64((key << 32) | expected.QStamp), final.Read(Q, record));
            Assert.Equal(ScalarValue.FromInt64((key << 32) | expected.PStamp), final.Read(P, record));
        }
    }

    private static void Write(LocalTableWriter writer, EntityKeyType keyType, Dictionary<long, (long Version, long QStamp, long PStamp, bool Deleted)> model)
    {
        var random = new Random(12345);
        for (var op = 1; op <= Operations; op++)
        {
            var version = StableKeys + op;
            var active = Math.Min(MaxKeys, StableKeys + (op / 4));
            var key = random.Next(5) == 0 ? 1L + random.Next(HotKeys) : 1L + random.Next(active);
            var action = random.Next(100);
            model.TryGetValue(key, out var current);
            var known = model.ContainsKey(key);
            if (action < 4 && key > StableKeys)
            {
                Assert.True(writer.Delete(KeyOf(keyType, key), version));
                model[key] = (version, -1, -1, true);
            }
            else if (action < 34)
            {
                // Group P only: group Q keeps its stamp, or stays missing on a new or deleted key.
                var builder = writer.Begin(KeyOf(keyType, key), version);
                WriteP(builder, key, version);
                Assert.True(builder.Commit());
                model[key] = (version, known && !current.Deleted ? current.QStamp : -1, version, false);
            }
            else if (action < 37 && known)
            {
                // A stale or duplicate delivery: ignored, and the record does not change.
                var builder = writer.Begin(KeyOf(keyType, key), current.Version - random.Next(2));
                Assert.True(builder.IsIgnored);
                WriteP(builder, key, version);
                Assert.False(builder.Commit());
            }
            else
            {
                WriteFull(writer, keyType, key, version);
                model[key] = (version, version, version, false);
            }

            if (op % 5000 == 0)
            {
                writer.Compact();
            }
        }
    }

    private static void WriteFull(LocalTableWriter writer, EntityKeyType keyType, long key, long stamp)
    {
        var builder = writer.Begin(KeyOf(keyType, key), stamp);
        builder.SetInt64(Q, (key << 32) | stamp);
        builder.SetFloat64(QF64, stamp + 0.5);
        builder.SetInt32(QI32, (int)stamp);
        builder.SetString(QText, Text(key, stamp));
        var vector = builder.VectorBuffer(QVec);
        for (var j = 0; j < vector.Length; j++)
        {
            vector[j] = (stamp % 100_000) + j;
        }

        builder.StartList(QLi64);
        for (var j = 0; j < 1 + (stamp % 5); j++)
        {
            builder.AddInt64Item(QLi64, (key << 32) | (stamp + j));
        }

        builder.StartList(QLs);
        for (var j = 0; j < stamp % 3; j++)
        {
            builder.AddStringItem(QLs, Word(stamp + j));
        }

        WriteP(builder, key, stamp);
        Assert.True(builder.Commit());
    }

    private static void WriteP(RecordBuilder builder, long key, long stamp)
    {
        builder.SetInt64(P, (key << 32) | stamp);
        builder.SetFloat32(PF32, stamp % 1_000_000);
        builder.SetBool(PB, (stamp & 1) == 1);
        builder.SetString(PS, Word(stamp));
        builder.StartList(PLi32);
        for (var j = 0; j < stamp % 4; j++)
        {
            builder.AddInt32Item(PLi32, (int)stamp + j);
        }
    }

    private static void Read(LocalFeatureTable table, EntityKeyType keyType, int id, Func<bool> writerDone, out long found, out long iterations)
    {
        var random = new Random(1000 + id);
        var int64Keys = new long[Batch];
        var stringKeys = new string?[Batch];
        var records = new LocalRecordRef[Batch];
        var rows = new int[Batch];
        for (var i = 0; i < Batch; i++)
        {
            rows[i] = Batch - 1 - i;
        }

        found = 0;
        iterations = 0;
        TableVersion? held = null;
        while (!writerDone() || iterations < MinReaderIterations)
        {
            iterations++;

            // Mostly the current version; every few batches, one pinned many batches ago (compactions may have replaced it).
            var version = held is not null && iterations % 7 == 0 ? held : table.Pin();
            if (iterations % 97 == 1)
            {
                held = version;
            }

            for (var i = 0; i < Batch; i++)
            {
                var key = i >= Batch - HotKeys ? 1L + i - (Batch - HotKeys)
                    : i % 3 == 0 ? 1L + random.Next(StableKeys)
                    : 1L + random.Next(MaxKeys + 500);
                int64Keys[i] = key;
                stringKeys[i] = "k" + key;
            }

            found += keyType == EntityKeyType.Int64 ? version.Lookup(int64Keys, records) : version.Lookup(stringKeys, records);
            var memory = new RequestMemory();
            try
            {
                // Both read paths: one fill per field, and all fields in one blocked fill.
                var columns = new ColumnBuffer[Fields.Length];
                var reads = new LocalFieldRead[Fields.Length];
                for (var f = 0; f < Fields.Length; f++)
                {
                    columns[f] = ColumnBuffer.Create(Fields[f].Type, Batch, memory);
                    reads[f] = new LocalFieldRead(f, columns[f], ScalarValue.Missing);
                    if (iterations % 2 == 0)
                    {
                        version.Fill(f, records, rows, columns[f], ScalarValue.Missing);
                    }
                }

                if (iterations % 2 != 0)
                {
                    version.Fill(records, rows, reads);
                }

                for (var i = 0; i < Batch; i++)
                {
                    var key = int64Keys[i];
                    if (key <= StableKeys && !records[i].Found)
                    {
                        throw new InvalidOperationException($"Stable key {key} was not found in version {version.Generation}.");
                    }

                    if (records[i].Found)
                    {
                        Check(key, version.VersionOf(records[i]), columns, rows[i], key <= StableKeys);
                    }
                    else if (columns[Q].IsValid(rows[i]) || columns[P].IsValid(rows[i]))
                    {
                        throw new InvalidOperationException($"Key {key} was not found but its row has values.");
                    }
                }
            }
            finally
            {
                memory.Release();
            }
        }
    }

    /// <summary>The invariants of one record read through the columns.</summary>
    private static void Check(long key, long version, ColumnBuffer[] columns, int row, bool stable)
    {
        var q = Group(columns, row, key, Q, QF64, QI32, QText, QVec, QLi64, QLs);
        var p = Group(columns, row, key, P, PF32, PB, PS, PLi32);
        if (q < 0 && p < 0)
        {
            throw new InvalidOperationException($"Key {key}: a found record with no values (a torn or unwritten record).");
        }

        if (stable && q < 0)
        {
            throw new InvalidOperationException($"Stable key {key} lost its group Q values.");
        }

        if (q > version || p > version)
        {
            throw new InvalidOperationException($"Key {key}: stamps Q {q} / P {p} are newer than the record's version {version}.");
        }

        if (q >= 0)
        {
            Expect(key, "q_f64", ((ScalarColumn<double>)columns[QF64])[row] == q + 0.5);
            Expect(key, "q_i32", ((ScalarColumn<int>)columns[QI32])[row] == (int)q);
            Expect(key, "q_text", ((TextColumn)columns[QText]).Get(row) == Text(key, q));
            var vector = ((VectorColumn)columns[QVec]).Row(row);
            for (var j = 0; j < vector.Length; j++)
            {
                Expect(key, "q_vec", vector[j] == (q % 100_000) + j);
            }

            var longs = ((ListColumn<long>)columns[QLi64]).Get(row);
            Expect(key, "q_li64 length", longs.Length == 1 + (q % 5));
            for (var j = 0; j < longs.Length; j++)
            {
                Expect(key, "q_li64", longs[j] == ((key << 32) | (q + j)));
            }

            var words = ((ListColumn<string>)columns[QLs]).Get(row);
            Expect(key, "q_ls length", words.Length == q % 3);
            for (var j = 0; j < words.Length; j++)
            {
                Expect(key, "q_ls", words[j] == Word(q + j));
            }
        }

        if (p >= 0)
        {
            Expect(key, "p_f32", ((ScalarColumn<float>)columns[PF32])[row] == p % 1_000_000);
            Expect(key, "p_b", ((ScalarColumn<bool>)columns[PB])[row] == ((p & 1) == 1));
            Expect(key, "p_s", ((DictStringColumn)columns[PS]).Get(row) == Word(p));
            var ints = ((ListColumn<int>)columns[PLi32]).Get(row);
            Expect(key, "p_li32 length", ints.Length == p % 4);
            for (var j = 0; j < ints.Length; j++)
            {
                Expect(key, "p_li32", ints[j] == (int)p + j);
            }
        }
    }

    /// <summary>The group's stamp (from its int64 field, which must name the key), or -1 when the whole group is missing.
    /// A group with some fields present and others missing is a torn record.</summary>
    private static long Group(ColumnBuffer[] columns, int row, long key, int stampField, params int[] others)
    {
        var present = columns[stampField].IsValid(row);
        foreach (var f in others)
        {
            if (columns[f].IsValid(row) != present)
            {
                throw new InvalidOperationException($"Key {key}: field {Fields[f].Name} is {(present ? "missing" : "present")} while {Fields[stampField].Name} is not.");
            }
        }

        if (!present)
        {
            return -1;
        }

        var value = ((ScalarColumn<long>)columns[stampField])[row];
        if (value >> 32 != key)
        {
            throw new InvalidOperationException($"Key {key}: read the record of key {value >> 32}.");
        }

        return value & 0xFFFF_FFFF;
    }

    private static void Expect(long key, string what, bool ok)
    {
        if (!ok)
        {
            throw new InvalidOperationException($"Key {key}: {what} does not match its stamp.");
        }
    }

    private static EntityKey KeyOf(EntityKeyType keyType, long key) =>
        keyType == EntityKeyType.Int64 ? EntityKey.FromInt64(key) : EntityKey.FromString("k" + key);

    private static string Text(long key, long stamp) => string.Concat(Enumerable.Repeat($"{key}:{stamp};", 1 + (int)(stamp % 4)));

    private static string Word(long stamp) => "w" + (stamp % DistinctStrings);

    private static void Guard(List<Exception> failures, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            lock (failures)
            {
                failures.Add(ex);
            }
        }
    }
}
