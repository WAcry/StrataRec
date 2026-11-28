using StrataRec.Core.Diagnostics;

namespace StrataRec.Features.Local;

/// <summary>Limits and tuning of one <see cref="LocalFeatureTable"/>. A served table takes its options from its binding
/// (<c>max_bytes</c>) and the engine's defaults. A <c>local</c> table's identity covers the binding, so its options never
/// change; a cache table is kept when only its limits change, and takes the new ones (<see cref="LocalFeatureTable.Retune"/>).</summary>
public sealed class LocalFeatureTableOptions
{
    public static LocalFeatureTableOptions Default { get; } = new();

    /// <summary>The binding's <c>max_bytes</c>: pages, index and dictionary of the current version together. While a
    /// compaction or reload builds a new version, the old one is still held, so the process briefly needs more.</summary>
    public long MaxBytes { get; init; } = long.MaxValue;

    /// <summary>Largest page. Pages start small and double up to this; a record larger than it gets a page of its own.</summary>
    public int PageSize { get; init; } = 1 << 20;

    /// <summary>Superseded bytes below this never trigger a compaction, so a small table is not rebuilt every few updates.</summary>
    public long CompactionMinBytes { get; init; } = 1 << 20;

    /// <summary>Keys the index holds before it first grows (a snapshot's record count, when known).</summary>
    public int InitialCapacity { get; init; }

    /// <summary>The hash of a hashed key (<see cref="TableKeys"/>); tests inject a weak one to force
    /// collisions.</summary>
    internal KeyHasher? KeyHasher { get; init; }

    /// <summary>The cache policy of a table in mode <see cref="LocalTableMode.Cache"/> (required there, and only there).</summary>
    public Cache.CacheTableOptions? Cache { get; init; }

    internal void Validate()
    {
        if (MaxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBytes), MaxBytes, "max_bytes must be positive.");
        }

        if (PageSize is < 64 or > 1 << 30 || PageSize % 8 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PageSize), PageSize, "The page size is a multiple of 8 between 64 B and 1 GiB.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(CompactionMinBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(InitialCapacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(InitialCapacity, IndexTable.MaxSlots / 2);
    }
}

/// <summary>
/// The in-process feature table behind the <c>cache</c> and <c>local</c> modes: one per (source, entity), one
/// fixed-size record of native values per entity (<see cref="RecordLayout"/>), written by one background writer and
/// read by requests without locks.
/// </summary>
/// <remarks>
/// <para>The publication rules readers rely on:</para>
/// <list type="number">
/// <item>A record is built privately, copied past the used end of a page, and only then published by a release write of
/// its position into the index slot; published values never change (a cache reader sets only a hit flag).</item>
/// <item>A new slot gets its key by a plain write, then its position by the release write; a key never changes, and
/// entries are never removed (deletes are tombstones).</item>
/// <item>The index, the page directory and the string dictionary grow by filling a larger copy and publishing its
/// reference; the old array is never written again.</item>
/// <item>A reader reads a position (acquire) before its page or string code, and loads the directory or dictionary
/// again when the number is past the end of the one it holds.</item>
/// <item>A request reads one pinned <see cref="TableVersion"/>; compaction and reload publish a new one whole.</item>
/// </list>
/// </remarks>
public sealed class LocalFeatureTable
{
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly KeyValuePair<string, object?>[] _tags;
    private readonly long _emptyBytes;
    private TableVersion _current;
    private long _maxBytes;
    private UpdatePosition _position = UpdatePosition.Empty;
    private long _compactions;
    private long _completions;

    public LocalFeatureTable(LocalTableSchema schema, LocalFeatureTableOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        Options = options ?? LocalFeatureTableOptions.Default;
        Options.Validate();
        Schema = schema;
        var layout = new RecordLayout(schema);
        if (layout.FixedSize > int.MaxValue / 4)
        {
            throw new ArgumentException($"A record of {layout.FixedSize} fixed bytes is too large for a local table.", nameof(schema));
        }

        _current = new TableVersion(
            layout, Options.KeyHasher ?? KeyHashes.Default, Options.PageSize, generation: 0, Options.InitialCapacity);
        _emptyBytes = _current.Bytes;
        _maxBytes = CheckedMaxBytes(Options.MaxBytes);

        Writer = new LocalTableWriter(this, layout, Options, _current);
        _tags = [new(StrataRecTags.Source, schema.Source), new(StrataRecTags.Entity, schema.Entity)];
        HitTags = [_tags[0], _tags[1], new(StrataRecTags.Outcome, "hit")];
        MissTags = [_tags[0], _tags[1], new(StrataRecTags.Outcome, "miss")];
        if ((schema.Mode == LocalTableMode.Cache) != (Options.Cache is not null))
        {
            throw new ArgumentException(
                $"Local table {schema.Source}/{schema.Entity} is in mode {schema.Mode}: cache options belong to cache tables, and every cache table has them.",
                nameof(options));
        }

        Cache = Options.Cache is { } cache ? new Cache.LocalTableCache(this, cache) : null;
    }

    public LocalTableSchema Schema { get; }

    /// <summary>What the table was created with. Its limit now is <see cref="MaxBytes"/>.</summary>
    public LocalFeatureTableOptions Options { get; }

    /// <summary>The table's <c>max_bytes</c> now: what it was created with, or what the plan that took it last asked for.</summary>
    public long MaxBytes => Volatile.Read(ref _maxBytes);

    /// <summary>
    /// A plan that holds the table, created for an earlier one, now serves with other limits: the table keeps its
    /// records and takes the plan's <c>max_bytes</c> and, for a cache, its times. A cache's writer meets a lower limit
    /// at once, evicting down to it. Plans that share a table share one limit, that of the one served last.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The options are not ones the table can take
    /// (<see cref="CheckRetune"/>); nothing of the table has changed.</exception>
    public void Retune(LocalFeatureTableOptions options)
    {
        var (maxBytes, policy) = Checked(options);
        var lowered = maxBytes < Interlocked.Exchange(ref _maxBytes, maxBytes);
        if (policy is not null)
        {
            Cache!.Adopt(policy);
        }

        if (lowered)
        {
            Cache?.Wake();
        }
    }

    /// <summary>Checks that the table could take <paramref name="options"/> (<see cref="Retune"/>), and changes nothing.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><c>max_bytes</c> cannot hold even the empty table, or a cache's times are not ones a policy takes.</exception>
    public void CheckRetune(LocalFeatureTableOptions options) => Checked(options);

    private (long MaxBytes, Cache.CachePolicy? Policy) Checked(LocalFeatureTableOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var maxBytes = CheckedMaxBytes(options.MaxBytes);
        var policy = Cache is { } cache && options.Cache is { } times && (times.TtlMs != cache.Policy.TtlMs || times.StaleIfErrorMs != cache.Policy.StaleIfErrorMs)
            ? new Cache.CachePolicy(times.TtlMs, times.StaleIfErrorMs)
            : null;
        return (maxBytes, policy);
    }

    private long CheckedMaxBytes(long maxBytes)
    {
        if (maxBytes <= 0 || _emptyBytes > maxBytes)
        {
            throw new ArgumentOutOfRangeException(
                "options", maxBytes,
                $"max_bytes of {Schema.Source}/{Schema.Entity} is {maxBytes} B, but the empty table already takes {_emptyBytes} B " +
                $"(an index for {Options.InitialCapacity} keys, the page directory and the string dictionary): raise max_bytes, or lower the initial capacity.");
        }

        return maxBytes;
    }

    /// <summary>The cache policy and the write-back queue of a table in mode <see cref="LocalTableMode.Cache"/>; null otherwise.</summary>
    public Cache.LocalTableCache? Cache { get; }

    /// <summary>The table's single writer. Hand it to exactly one update source (or the cache writer) at a time.</summary>
    public LocalTableWriter Writer { get; }

    /// <summary>The position of the last checkpoint or completed reload: every update before it is in the table.</summary>
    public UpdatePosition Position => Volatile.Read(ref _position);

    /// <summary>
    /// Checkpoints and completed reloads so far: grows whenever the source makes progress, even when the position stays
    /// the same (a snapshot source without a stream completes every reload at <see cref="UpdatePosition.Empty"/>).
    /// </summary>
    public long Completions => Volatile.Read(ref _completions);

    /// <summary>Compactions so far (including those of versions a reload was building).</summary>
    public long Compactions => Volatile.Read(ref _compactions);

    /// <summary>
    /// True once the table holds a complete load: its first reload completed or its first checkpoint was written.
    /// Before that it is empty, and a request that reads it fails the features it needs instead of serving them as
    /// missing (an empty table would answer "no value" for every key).
    /// </summary>
    public bool IsLoaded => _loaded.Task.IsCompleted;

    /// <summary>Completes when <see cref="IsLoaded"/> becomes true (never faults; a failed load is the source's error).</summary>
    public Task WhenLoaded => _loaded.Task;

    /// <summary>Metric tags of the table: <c>stratarec.source</c>, <c>stratarec.entity</c>; recording a measurement
    /// allocates nothing. Also for whoever runs the table's source (its failures).</summary>
    public ReadOnlySpan<KeyValuePair<string, object?>> MetricTags => _tags;

    internal KeyValuePair<string, object?>[] HitTags { get; }

    internal KeyValuePair<string, object?>[] MissTags { get; }

    /// <summary>
    /// The current version. A request pins it once per table and does all its lookups against it, so the arrays it
    /// reads stay alive and consistent for the request even if the writer compacts or reloads meanwhile.
    /// </summary>
    public TableVersion Pin() => Volatile.Read(ref _current);

    /// <summary>Makes a fully built version current (release: its arrays are complete before a reader can pin it).</summary>
    internal void Publish(TableVersion version) => Volatile.Write(ref _current, version);

    internal void SetPosition(UpdatePosition position)
    {
        Volatile.Write(ref _position, position);
        Volatile.Write(ref _completions, _completions + 1);
        RecordSize();
        if (Reports)
        {
            StrataRecMetrics.LocalLoadLastSuccess.Record(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, _tags);
        }

        _loaded.TrySetResult();
    }

    /// <summary>
    /// Whether the table reports its size and its loads. Not a cache's without an in-process tier: its zeros and the
    /// time its writer started would be recorded over those of the table another plan's bindings keep of the same
    /// source and entity, whose instruments have the same tags.
    /// </summary>
    private bool Reports => Cache is not { Memory: false };

    internal void RecordSize()
    {
        if (!Reports)
        {
            return;
        }

        var version = Pin();
        StrataRecMetrics.LocalBytes.Record(version.Bytes, _tags);
        StrataRecMetrics.LocalRecords.Record(version.RecordCount, _tags);
    }

    internal void CountLoad(TimeSpan duration) => StrataRecMetrics.LocalLoadDuration.Record(duration.TotalSeconds, _tags);

    internal void CountCompaction()
    {
        Volatile.Write(ref _compactions, _compactions + 1);
        StrataRecMetrics.LocalCompactions.Add(1, _tags);
    }
}
