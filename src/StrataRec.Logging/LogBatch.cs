using System.Runtime.CompilerServices;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.Logging;

/// <summary>Delivery class of a tap. The two classes use separate queues.</summary>
public enum LogDelivery : byte
{
    /// <summary>Dropped (and counted) when its queue is full; for funnel analysis and debugging.</summary>
    BestEffort = 1,

    /// <summary>At-least-once with gap detection: every batch gets a per-process sequence number.</summary>
    Durable,
}

/// <summary>
/// One tap firing: a fixed envelope plus a columnar payload. Batches own their data, so the request's
/// candidate table can be returned to the pool while the batch waits in a queue.
/// </summary>
public sealed class LogBatch
{
    public required string TapId { get; init; }

    public required string RequestId { get; init; }

    public required string PipelineId { get; init; }

    /// <summary>
    /// For a run a node of another pipeline called: that pipeline, the node and the call's place among the node's calls in
    /// its run, as <c>feed/promoted#0</c>; for a run a called run called, the calls from the request's pipeline down,
    /// as <c>home/blend#1 &gt; inner/blend#0</c>. Null for a run of the pipeline the request named. Two runs of one
    /// pipeline in a request log under the same request and pipeline; this tells them apart.
    /// </summary>
    public string? CalledBy { get; init; }

    /// <summary>Content id of the manifest the plan was built from.</summary>
    public required string ManifestId { get; init; }

    /// <summary>
    /// For the record of a run that did not happen because a cache answered its call (<c>$experiments</c> of a called
    /// pipeline, written so that exposure counts do not depend on the hit rate): the request whose run computed the
    /// result, that request's time, and the call that ran it there, which is the <see cref="CalledBy"/> of what that
    /// run logged. The called pipeline's other taps wrote under that request and that call. Null for a run.
    /// </summary>
    public string? CachedFromRequestId { get; init; }

    /// <inheritdoc cref="CachedFromRequestId"/>
    public long CachedFromTimeMs { get; init; }

    /// <inheritdoc cref="CachedFromRequestId"/>
    public string? CachedFromCalledBy { get; init; }

    /// <summary>
    /// The tap variant's schema (columns, types, expressions), shared by its batches; its <see cref="LogSchema.Id"/> is what
    /// a batch records, and a sink that needs the record publishes it once per id.
    /// </summary>
    public required LogSchema Schema { get; init; }

    public required long TimeMs { get; init; }

    public required LogDelivery Delivery { get; init; }

    /// <summary>Request sampling rate of the tap's funnel group (Horvitz–Thompson weight 1/req_prob).</summary>
    public required double RequestProbability { get; init; }

    /// <summary>Entity ids of the rows (int64 pipelines); null for string keys.</summary>
    public long[]? Int64EntityIds { get; init; }

    /// <summary>Entity ids of the rows (string pipelines); null for int64 keys.</summary>
    public string[]? StringEntityIds { get; init; }

    /// <summary>1-based rank of each row in the order at the attach point (the served position for output taps).</summary>
    public required int[] Ranks { get; init; }

    /// <summary>For <c>dropped</c> taps, the rank each row had before the node removed it.</summary>
    public int[]? RanksBefore { get; init; }

    /// <summary>
    /// Inclusion probability of every row given the request was sampled: coordinated sampling keeps each row
    /// with the same probability, so the batch holds one number, not one per row.
    /// </summary>
    public required double RowProbability { get; init; }

    /// <summary>The logged columns, in the order of <see cref="LogSchema.Columns"/>.</summary>
    public required LogColumn[] Columns { get; init; }

    /// <summary>
    /// Per-request context fields (<c>context</c> of the tap), in the order of <see cref="LogSchema.Context"/>: field i's
    /// type is <c>Schema.Context[i].Type</c>, by which a sink narrows the value (held promoted, as every value is).
    /// </summary>
    public required (string Name, ScalarValue Value)[] Context { get; init; }

    public int RowCount => Ranks.Length;

    // Filled when the request finishes, so every batch of a request carries the same final record.

    public IReadOnlyList<string> AssignedArms { get; set; } = Array.Empty<string>();

    public IReadOnlyList<string> TriggeredExperiments { get; set; } = Array.Empty<string>();

    /// <summary>Outcome per executed node, e.g. <c>ctr_ranker: Failed</c>, <c>ctr_backup: Succeeded</c>.</summary>
    public IReadOnlyDictionary<string, string> NodeOutcomes { get; set; } = EmptyMap;

    /// <summary>Log column → node that actually produced the underlying column (lineage).</summary>
    public IReadOnlyDictionary<string, string> ColumnProducers { get; set; } = EmptyMap;

    /// <summary><c>process/start-time</c> of the producing process.</summary>
    public string ProducerId { get; set; } = string.Empty;

    /// <summary>Per-process sequence number of durable batches (0 for best-effort).</summary>
    public long Seq { get; set; }

    /// <summary>
    /// About how much memory the batch holds: arrays, column values and what they point to (strings, lists), and
    /// the envelope's lists and maps. What the log queues are bounded by (<see cref="LogPipelineOptions.DurableMaxBytes"/>):
    /// batches differ in size by orders of magnitude (a request-level tap is one row, a candidate tap thousands of rows of
    /// lists), so a count of batches bounds nothing. Computed once, when the batch is first queued; the batch does not
    /// change afterwards.
    /// </summary>
    /// <remarks>
    /// <para>A string is counted once per batch however many values point to it: a local table's values, a category a
    /// connector interned or a constant give many rows the same string object, and counting it per row overstated a batch
    /// of 1,024 rows sharing a 4 KB string 158 times. Sharing is found by identity in a small direct-mapped table of recent strings, so a
    /// string evicted by another that maps to the same slot is counted again: an overstatement, never an understatement.
    /// A column's values are one array (<see cref="LogColumn"/>), counted by its length.</para>
    /// <para>Strings of the plan (tap, pipeline, manifest and schema ids, node, arm, column and outcome names) are shared by every
    /// request and not counted; the envelope's lists and maps are, per entry. The maps of a request are shared by its
    /// batches and counted in each, a bounded overstatement (taps per request).</para>
    /// </remarks>
    public long ApproximateBytes => _bytes >= 0 ? _bytes : _bytes = Estimate();

    private long _bytes = -1;

    private const int ObjectBytes = 24; // header, method table, and an array's length, rounded
    private const int ValueBytes = 24; // a ScalarValue: kind, bits, reference
    private const int MapEntryBytes = 32; // a dictionary entry (hash, next, key, value) and its bucket, rounded
    private const int SeenSlots = 256;

    /// <summary>Strings already counted in the batch being estimated; cleared after each estimate, so it holds none.</summary>
    [ThreadStatic]
    private static string?[]? _seen;

    private long Estimate()
    {
        var seen = _seen ??= new string?[SeenSlots];
        try
        {
            long bytes = 256 + StringBytes(RequestId); // the object and its fields; the other envelope strings are the plan's
            bytes += Ranks.LongLength * 4 + (RanksBefore?.LongLength ?? 0) * 4;
            bytes += (Int64EntityIds?.LongLength ?? 0) * 8;
            bytes += ListBytes(AssignedArms) + ListBytes(TriggeredExperiments) + MapBytes(NodeOutcomes) + MapBytes(ColumnProducers);
            if (StringEntityIds is { } ids)
            {
                bytes += ObjectBytes + ids.LongLength * 8;
                foreach (var id in ids)
                {
                    bytes += Once(seen, id);
                }
            }

            foreach (var column in Columns)
            {
                bytes += 2 * ObjectBytes + (column.Offsets?.LongLength ?? 0) * 4 + (column.Validity?.LongLength ?? 0) * 8;
                switch (column.Values)
                {
                    case string?[] strings:
                        bytes += strings.LongLength * 8;
                        foreach (var s in strings)
                        {
                            bytes += Once(seen, s);
                        }

                        break;
                    case bool[] flags:
                        bytes += flags.LongLength;
                        break;
                    case float[] or int[]:
                        bytes += column.Values.LongLength * 4;
                        break;
                    default:
                        bytes += column.Values.LongLength * 8; // long[] or double[]
                        break;
                }
            }

            foreach (var (_, value) in Context)
            {
                bytes += ValueBytes + PayloadBytes(seen, value);
            }

            return bytes;
        }
        finally
        {
            Array.Clear(seen);
        }
    }

    /// <summary>The heap memory a value points to, beyond the value itself; strings already counted in the batch are free.</summary>
    private static long PayloadBytes(string?[] seen, in ScalarValue value)
    {
        switch (value.Kind)
        {
            case StorageKind.String:
                return Once(seen, value.AsString());
            case StorageKind.List:
                var length = value.ListLength;
                switch (value.ListElement)
                {
                    case StorageKind.String:
                        long strings = ObjectBytes + length * 8L;
                        foreach (var s in value.AsStringList())
                        {
                            strings += Once(seen, s);
                        }

                        return strings;
                    case StorageKind.Bool:
                        return ObjectBytes + length;
                    default:
                        return ObjectBytes + length * 8L; // numbers are held promoted: long[] or double[]
                }

            default:
                return 0;
        }
    }

    /// <summary>A string's bytes the first time the batch's estimate meets it, 0 when it was the last one in its slot.</summary>
    private static long Once(string?[] seen, string? s)
    {
        if (s is null)
        {
            return 0;
        }

        var slot = RuntimeHelpers.GetHashCode(s) & (SeenSlots - 1);
        if (ReferenceEquals(seen[slot], s))
        {
            return 0;
        }

        seen[slot] = s;
        return StringBytes(s);
    }

    private static long ListBytes(IReadOnlyList<string> list) => list.Count == 0 ? 0 : ObjectBytes + list.Count * 8L;

    private static long MapBytes(IReadOnlyDictionary<string, string> map) => map.Count == 0 ? 0 : 3 * ObjectBytes + map.Count * (long)MapEntryBytes;

    private static long StringBytes(string? s) => s is null ? 0 : ObjectBytes + s.Length * 2L;

    private static readonly IReadOnlyDictionary<string, string> EmptyMap = new Dictionary<string, string>();
}
