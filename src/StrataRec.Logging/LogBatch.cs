using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.Logging;

/// <summary>Delivery class of a tap (design 8.1). The two classes use separate queues.</summary>
public enum LogDelivery : byte
{
    /// <summary>Dropped (and counted) when its queue is full; for funnel analysis and debugging.</summary>
    BestEffort = 1,

    /// <summary>At-least-once with gap detection: every batch gets a per-process sequence number (design 8.6).</summary>
    Durable,
}

/// <summary>One logged column of a batch: native type plus one value per row; missing values stay missing (design 8.7).</summary>
/// <param name="Name">Log column name.</param>
/// <param name="Type">Declared type of the column expression.</param>
/// <param name="Values">One value per batch row; <see cref="ScalarValue.Missing"/> where the value is missing.</param>
public sealed record LogColumn(string Name, ColumnType Type, ScalarValue[] Values);

/// <summary>
/// One tap firing: a fixed envelope plus a columnar payload (design 8.7). Batches own their data, so the request's
/// candidate table can be returned to the pool while the batch waits in a queue.
/// </summary>
public sealed class LogBatch
{
    public required string TapId { get; init; }

    public required string RequestId { get; init; }

    public required string PipelineId { get; init; }

    /// <summary>Content id of the manifest the plan was built from.</summary>
    public required string ManifestId { get; init; }

    /// <summary>Content id of this tap variant's schema (columns, types, expressions, defining artifact).</summary>
    public required string SchemaId { get; init; }

    public required long TimeMs { get; init; }

    public required LogDelivery Delivery { get; init; }

    /// <summary>Request sampling rate of the tap's funnel group (Horvitz–Thompson weight 1/req_prob, design 8.4).</summary>
    public required double RequestProbability { get; init; }

    /// <summary>Entity ids of the rows (int64 pipelines); null for string keys.</summary>
    public long[]? Int64EntityIds { get; init; }

    /// <summary>Entity ids of the rows (string pipelines); null for int64 keys.</summary>
    public string[]? StringEntityIds { get; init; }

    /// <summary>1-based rank of each row in the order at the attach point (the served position for output taps).</summary>
    public required int[] Ranks { get; init; }

    /// <summary>For <c>dropped</c> taps, the rank each row had before the node removed it.</summary>
    public int[]? RanksBefore { get; init; }

    /// <summary>Inclusion probability of each row given the request was sampled (design 8.4).</summary>
    public required double[] RowProbabilities { get; init; }

    public required LogColumn[] Columns { get; init; }

    /// <summary>Per-request context fields (<c>context</c> of the tap).</summary>
    public required (string Name, ScalarValue Value)[] Context { get; init; }

    public int RowCount => Ranks.Length;

    // Filled when the request finishes, so every batch of a request carries the same final record.

    public IReadOnlyList<string> AssignedArms { get; set; } = Array.Empty<string>();

    public IReadOnlyList<string> TriggeredExperiments { get; set; } = Array.Empty<string>();

    /// <summary>Outcome per executed node, e.g. <c>ctr_ranker: Failed</c>, <c>ctr_backup: Succeeded</c>.</summary>
    public IReadOnlyDictionary<string, string> NodeOutcomes { get; set; } = EmptyMap;

    /// <summary>Log column → node that actually produced the underlying column (lineage, design 8.7).</summary>
    public IReadOnlyDictionary<string, string> ColumnProducers { get; set; } = EmptyMap;

    /// <summary><c>process/start-time</c> of the producing process.</summary>
    public string ProducerId { get; set; } = string.Empty;

    /// <summary>Per-process sequence number of durable batches (0 for best-effort).</summary>
    public long Seq { get; set; }

    private static readonly IReadOnlyDictionary<string, string> EmptyMap = new Dictionary<string, string>();
}
