using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Caching.Memory;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Features.Cache;
using StrataRec.Features.Local;

namespace StrataRec.Benchmarks;

/// <summary>Paired, single-producer cache-layer benchmark. Batch percentiles cover the foreground call.
/// In Queue the caller returns after submission. Other local writes include the single writer applying the batch.
/// The whole-run CPU, allocations and throughput include pending work on both stores.</summary>
internal static class CacheComparison
{
    internal const int Batch = 128;
    internal const int Keys = 65_536;
    internal const long Budget = 1 << 20;

    internal enum Work { Hit, Miss, Fill, Churn, HotScan, Uniform, Queue, Admit }

    public static int Run(string[] args)
    {
        int rounds = 3;
        Work selected = default;
        double compact = new MemoryCacheOptions().CompactionPercentage;
        if (args.Length > 3 ||
            (args.Length > 0 && (!int.TryParse(args[0], CultureInfo.InvariantCulture, out rounds) || rounds < 1)) ||
            (args.Length > 1 && (!Enum.TryParse(args[1], true, out selected) || !Enum.IsDefined(selected))) ||
            (args.Length > 2 && (!double.TryParse(args[2], CultureInfo.InvariantCulture, out compact) || !(compact > 0 && compact <= 1))))
        {
            Console.Error.WriteLine("Usage: --cache-compare [rounds=3] [Hit|Miss|Fill|Churn|HotScan|Uniform|Queue|Admit] [MemoryCache compaction fraction]");
            return 1;
        }

        var cases = args.Length > 1 ? [selected] : Enum.GetValues<Work>();
        var version = typeof(MemoryCache).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        Console.Error.WriteLine($"Runtime {Environment.Version}, MemoryCache {version?.InformationalVersion}, compaction {compact:P0}, {Batch} int64 keys/batch.");
        Console.WriteLine("round,width,work,store,local_limit_B,memory_limit_entries,keys,reads,hits,origin_batches,write_attempts,admitted,not_admitted,queue_peak_observed_B,resident,front_ms,wall_ms,Mkeys_s,cpu_ns_key,alloc_B_key,p50_us,p95_us,p99_us,max_us,checksum");
        foreach (int width in new[] { 1, 64 })
        {
            var data = new CacheComparisonData(width);
            int capacity = Calibrate(data);
            Console.Error.WriteLine($"{width} float(s)/entry: sequential steady-state peak {capacity} keys in {Budget} B.");
            foreach (var work in cases)
            {
                if (work == Work.Admit && (long)(capacity * compact) == 0)
                {
                    Console.Error.WriteLine("This compaction fraction reclaims fewer than one entry. Increase it to run Admit.");
                    return 1;
                }

                var trace = Trace(work, capacity);
                for (var round = 0; round <= rounds; round++)
                {
                    foreach (var local in (round % 2 == 0 ? new[] { true, false } : [false, true]))
                    {
                        RunCase(round, local, work, data, capacity, trace, compact);
                    }
                }
            }
        }

        return 0;
    }

    // MemoryCache units are supplied by the caller. Match the peak resident entries during steady sequential
    // pressure for this width. The two implementations have different heap footprints and steady-state occupancy.
    private static int Calibrate(CacheComparisonData data)
    {
        using var store = new ComparisonLocal(data, Budget);
        long[] key = [0];
        int peak = 0;
        for (var i = 0; i < Keys * 2; i++)
        {
            key[0] = i % Keys;
            store.Write(key);
            if (i >= Keys) peak = Math.Max(peak, store.Count);
        }

        return store.Table.Compactions > 0 ? peak : throw new InvalidOperationException("Calibration did not reach the byte limit.");
    }

    private static long[] Trace(Work work, int capacity)
    {
        int batches = work switch { Work.Hit or Work.Miss => 16_384, Work.Fill or Work.Queue or Work.Admit => Keys / Batch, _ => 4_096 };
        int fit = capacity / 2;
        var trace = new long[batches * Batch];
        var random = new Random(14731);
        var seen = new int[Keys];
        int scan = fit;
        for (var b = 0; b < batches; b++)
        {
            for (var k = 0; k < Batch; k++)
            {
                int p = (b * Batch) + k;
                int id;
                do
                {
                    id = work switch
                    {
                        Work.Hit => random.Next(fit),
                        Work.Miss => fit + random.Next(fit),
                        Work.Fill or Work.Churn or Work.Queue or Work.Admit => p % Keys,
                        Work.HotScan when b % 10 < 8 => random.Next(fit / 2),
                        Work.HotScan => fit + ((scan++ - fit) % (Keys - fit)),
                        _ => random.Next(Math.Min(Keys, capacity * 3)),
                    };
                }
                while (seen[id] == b + 1);

                trace[p] = id;
                seen[id] = b + 1;
            }
        }

        return trace;
    }

    private static void RunCase(int round, bool local, Work work, CacheComparisonData data, int capacity, long[] trace, double compact)
    {
        bool fill = work == Work.Fill;
        int limit = fill ? Keys * 2 : capacity;
        long localLimit = fill ? 64L << 20 : Budget;
        using ComparisonStore store = local ? new ComparisonLocal(data, localLimit) : new ComparisonMemory(data, limit, work == Work.Admit, compact);
        bool reading = work is Work.Hit or Work.Miss or Work.HotScan or Work.Uniform;
        bool through = work is Work.HotScan or Work.Uniform;
        if (reading || work == Work.Churn)
        {
            var seed = new long[Batch];
            int total = work is Work.Hit or Work.Miss ? capacity / 2 : capacity;
            for (int i = 0; i < total; i += Batch)
            {
                int count = Math.Min(Batch, total - i);
                for (var k = 0; k < count; k++) seed[k] = i + k;
                store.Write(seed.AsSpan(0, count));
            }
        }

        var misses = new long[Batch];
        var rows = new int[Batch];
        var output = new float[Batch * data.Width];
        int batches = trace.Length / Batch;
        var ticks = new long[batches];
        long hits = 0, originBatches = 0;
        double check = 0;
        if (through)
        {
            for (var b = 0; b < 128; b++)
            {
                var warm = trace.AsSpan(b * Batch, Batch);
                int n = store.Read(warm, misses, rows, output);
                if (n > 0) store.Write(misses.AsSpan(0, n));
            }
        }

        // The calling thread owns the workload. Wait for the MemoryCache worker left by the seed,
        // then include that worker's time and allocations when the timed workload queues more work.
        store.Flush();
        if (work == Work.Queue && store is ComparisonLocal queued) queued.StartWriter();
        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        using var process = Process.GetCurrentProcess();
        var admittedBefore = store.Admitted;
        var attemptedBefore = store.Attempts;
        var cpu = process.TotalProcessorTime;
        long allocated = GC.GetTotalAllocatedBytes(true);
        var begin = Stopwatch.GetTimestamp();
        for (var b = 0; b < batches; b++)
        {
            var keys = trace.AsSpan(b * Batch, Batch);
            long t = Stopwatch.GetTimestamp();
            if (reading)
            {
                int n = store.Read(keys, misses, rows, output);
                hits += Batch - n;
                if (through && n > 0)
                {
                    // Every read-through request delivers exactly the same values, including source answers on misses.
                    for (var i = 0; i < n; i++) data.Copy(misses[i], output.AsSpan(rows[i] * data.Width, data.Width));
                    store.Write(misses.AsSpan(0, n));
                    originBatches++;
                }

                check += output[(b * 31) % output.Length];
            }
            else
            {
                store.Write(keys);
            }

            ticks[b] = Stopwatch.GetTimestamp() - t;
        }

        var front = Stopwatch.GetElapsedTime(begin);
        store.Flush();
        var elapsed = Stopwatch.GetElapsedTime(begin);
        allocated = GC.GetTotalAllocatedBytes(true) - allocated;
        cpu = process.TotalProcessorTime - cpu;
        long admitted = store.Admitted - admittedBefore;
        long attempts = store.Attempts - attemptedBefore;
        int reads = reading ? trace.Length : 0;
        if ((work == Work.Hit && hits != reads) || (work == Work.Miss && hits != 0) ||
            (fill && (store.Count != trace.Length || admitted != attempts)) ||
            (work == Work.Admit && admitted != trace.Length) || admitted > attempts)
        {
            throw new InvalidOperationException($"Unexpected {work}: {hits} hits, {admitted}/{attempts} admitted, {store.Count} resident.");
        }

        if (reading)
        {
            double expected = 0;
            for (var b = 0; b < batches && work != Work.Miss; b++)
            {
                int p = (b * 31) % output.Length;
                expected += trace[(b * Batch) + (p / data.Width)] + (p % data.Width) + 1;
            }

            if (check != expected) throw new InvalidOperationException($"Delivered checksum {check}, expected {expected}.");
        }

        if (round == 0) return;
        Array.Sort(ticks);
        double Us(double q) => ticks[(int)Math.Ceiling(q * batches) - 1] * (1_000_000d / Stopwatch.Frequency);
        string result = FormattableString.Invariant($"{round},{data.Width},{work},{(local ? "local" : "memory")},{(local ? localLimit : 0)},{(local ? 0 : limit)},{trace.Length},{reads},{hits},{originBatches},{attempts},{admitted},{attempts - admitted},{store.QueuePeak},{store.Count},{front.TotalMilliseconds:F2},{elapsed.TotalMilliseconds:F2},{trace.Length / elapsed.TotalSeconds / 1e6:F3},{cpu.TotalNanoseconds / trace.Length:F2},{(double)allocated / trace.Length:F2},{Us(.5):F2},{Us(.95):F2},{Us(.99):F2},{Us(1):F2},{check:F0}");
        Console.WriteLine(result);
    }
}

internal sealed class CacheComparisonData
{
    public readonly LocalTableSchema Schema;
    public readonly int Width;
    public readonly object[] BoxedKeys = new object[CacheComparison.Keys];
    public readonly object[] Values = new object[CacheComparison.Keys];
    public readonly Memory<byte>[] Records = new Memory<byte>[CacheComparison.Keys];
    public readonly long[] Lifetime = new long[CacheComparison.Keys];
    public readonly ulong Fingerprint;

    public CacheComparisonData(int width)
    {
        Width = width;
        Schema = new("comparison", "item", EntityKeyType.Int64, LocalTableMode.Cache,
            [new("value", width == 1 ? ColumnType.Float32 : ColumnType.List(StorageKind.Float32, fixedLength: width))]);
        var cache = MakeTable(CacheComparison.Budget).Cache!;
        Fingerprint = cache.Fingerprint;
        // Materialize independently owned, cache-ready source values for both APIs before measurement.
        // Keys are preboxed for MemoryCache so repeated calls do not pay for boxing an int64.
        var writer = new CacheRecordWriter(cache.Layout, CacheComparison.Keys * (40 + (width * 4)));
        for (var i = 0; i < Values.Length; i++)
        {
            writer.Begin(0, Fingerprint, false);
            BoxedKeys[i] = (long)i;
            Lifetime[i] = cache.LifetimeMs(EntityKey.FromInt64(i));
            if (width == 1)
            {
                Values[i] = (float)(i + 1);
                writer.Float32(0, i + 1);
            }
            else
            {
                var vector = new float[width];
                for (var j = 0; j < width; j++) vector[j] = i + j + 1;
                Values[i] = vector;
                writer.List<float>(0, vector);
            }

            var (offset, count) = writer.End();
            Records[i] = writer.Bytes.AsMemory(offset, count);
        }
    }

    public LocalFeatureTable MakeTable(long limit) => new(Schema, new() { MaxBytes = limit, Cache = new() { TtlMs = 86_400_000 } });

    public void Copy(long id, Span<float> destination) => CopyValue(Values[id], destination);

    public static void CopyValue(object value, Span<float> destination)
    {
        if (destination.Length == 1) destination[0] = (float)value;
        else ((float[])value).AsSpan().CopyTo(destination);
    }
}
