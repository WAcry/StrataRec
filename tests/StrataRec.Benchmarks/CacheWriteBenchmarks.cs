using BenchmarkDotNet.Attributes;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Features.Cache;
using StrataRec.Features.Local;

namespace StrataRec.Benchmarks;

/// <summary>Writes fresh vectors through the local cache writer until it must make room several times.
/// The encoded answers are prepared before measurement, as they would already exist on the request side.</summary>
[MemoryDiagnoser]
public class CacheWriteBenchmarks
{
    private const int Keys = 49_152;
    private const int BatchSize = 128;
    private readonly LocalTableSchema _schema = new("vectors", "item", EntityKeyType.Int64, LocalTableMode.Cache,
        [new("embedding", ColumnType.List(StorageKind.Float32, fixedLength: 64))]);
    private readonly LocalFeatureTableOptions _options = new() { MaxBytes = 4 << 20, Cache = new CacheTableOptions { TtlMs = 86_400_000 } };
    private readonly CacheWriteBatch[] _batches = new CacheWriteBatch[Keys / BatchSize];

    [Params(1, BatchSize)]
    public int KeysPerWriteTime { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var cache = new LocalFeatureTable(_schema, _options).Cache!;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var vector = new float[64];
        for (var b = 0; b < _batches.Length; b++)
        {
            var writer = new CacheRecordWriter(cache.Layout, BatchSize * 320);
            var keys = new EntityKey[BatchSize];
            var records = new ReadOnlyMemory<byte>[BatchSize];
            for (var i = 0; i < BatchSize; i++)
            {
                var key = (b * BatchSize) + i;
                keys[i] = EntityKey.FromInt64(key);
                writer.Begin(now - ((Keys - 1 - key) / KeysPerWriteTime), cache.Fingerprint, absent: false);
                vector[0] = key;
                writer.List<float>(0, vector);
                var (offset, length) = writer.End();
                records[i] = writer.Bytes.AsMemory(offset, length);
            }

            _batches[b] = new CacheWriteBatch(keys, records, BatchSize);
        }
    }

    [Benchmark]
    public int WriteUnderCapacityPressure()
    {
        var table = new LocalFeatureTable(_schema, _options);
        var cache = table.Cache!;
        foreach (var batch in _batches)
        {
            if (!cache.TryWriteBack(batch))
            {
                throw new InvalidOperationException("The empty write queue rejected the batch.");
            }

            cache.WriteQueued(table.Writer);
        }

        return table.Pin().RecordCount;
    }
}
