namespace StrataRec.Features.Local;

/// <summary>Limits and tuning of one <see cref="LocalFeatureTable"/>.</summary>
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

    /// <summary>String-key hash; tests inject a weak one to force collisions. Defaults to XXH3-64 of the UTF-16 key.</summary>
    internal StringKeyHasher? StringKeyHasher { get; init; }

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
/// The in-process feature table behind the <c>cache</c> and <c>local</c> modes (design 6.6): one per (source, entity),
/// holding one fixed-size record of native values per entity, written by one background writer and read by requests
/// without locks.
/// </summary>
/// <remarks>
/// <para><b>Layout</b> (<see cref="RecordLayout"/>). Records are entity-major so that reading 30 features of a candidate
/// touches two or three cache lines. Pages are large <c>byte[]</c>s and records are appended at their end; the index is
/// one <c>long[]</c>; strings are codes into one dictionary. Millions of records are a few dozen arrays to the garbage
/// collector, which does not scan their contents.</para>
/// <para><b>Concurrency protocol.</b> One writer (<see cref="LocalTableWriter"/>), any number of readers, no locks.
/// Everything a reader can reach is either immutable once published or replaced as a whole:</para>
/// <list type="number">
/// <item><b>Records are never modified.</b> The writer builds a record in private buffers, copies it into free space past
/// the end of a page's used part, and only then publishes its position into the index slot with
/// <c>Volatile.Write</c> (release). An update writes a new record and repoints the slot.</item>
/// <item><b>Index slots.</b> A slot is a (key, position) pair; position 0 means empty. A new key's slot gets its key with
/// a plain write and then its position with the release write, so a reader that sees the position (read with
/// <c>Volatile.Read</c>, acquire) also sees the key, and the record bytes behind it. A slot's key never
/// changes once set; entries are never removed (deletes are tombstone records).</item>
/// <item><b>Growth publishes a complete copy.</b> The index, the page directory and the string dictionary each grow by
/// copying into a larger array that is filled completely and then published with one release write of the reference.
/// The old array is never written again, so a reader holding it sees a consistent, frozen snapshot. Within an array that
/// has room, a new page or string is stored into the next element before any record using it is published.</item>
/// <item><b>Readers re-load on out-of-range.</b> A reader loads the index reference once per batch, reads each position
/// (acquire), and only afterwards resolves its page through the directory. A directory loaded earlier may be older than
/// the position: if the page number is past its end, the page was added in a grown directory published before the
/// position, so loading the directory again (acquire) finds it. If the page number is inside it, the element was stored
/// before the position was published. String codes are resolved the same way. A reader therefore never sees a torn
/// record, an unpublished page or an out-of-range position.</item>
/// <item><b>Versions.</b> Pages, index and dictionary belong to a <see cref="TableVersion"/>. A request pins the current
/// version (<see cref="Pin"/>, acquire) and reads only from it. Compaction and reload build a new version privately and
/// publish it with one release write of <c>_current</c>; the writer never touches the old one again, and the garbage
/// collector frees it after the last request that pinned it. Pinning is for memory safety only: keys read in one request
/// may reflect updates from different moments, as with a remote fetch.</item>
/// </list>
/// <para>On x64 every plain store is a release and every plain load an acquire, so the volatile accesses only stop the JIT
/// from reordering; on ARM64 they are the barriers the protocol needs.</para>
/// </remarks>
public sealed class LocalFeatureTable
{
    private TableVersion _current;
    private UpdatePosition _position = UpdatePosition.Empty;
    private long _compactions;

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
            layout, Options.StringKeyHasher ?? StringKeyHashes.Default, Options.PageSize, generation: 0, Options.InitialCapacity, expectedBytes: 0);
        Writer = new LocalTableWriter(this, layout, Options, _current);
    }

    public LocalTableSchema Schema { get; }

    public LocalFeatureTableOptions Options { get; }

    /// <summary>The table's single writer. Hand it to exactly one update source (or the cache writer) at a time.</summary>
    public LocalTableWriter Writer { get; }

    /// <summary>The position of the last checkpoint or completed reload: every update before it is in the table.</summary>
    public UpdatePosition Position => Volatile.Read(ref _position);

    /// <summary>Compactions so far (including those of versions a reload was building).</summary>
    public long Compactions => Volatile.Read(ref _compactions);

    /// <summary>
    /// The current version. A request pins it once per table and does all its lookups against it, so the arrays it
    /// reads stay alive and consistent for the request even if the writer compacts or reloads meanwhile.
    /// </summary>
    public TableVersion Pin() => Volatile.Read(ref _current);

    /// <summary>Makes a fully built version current (release: its arrays are complete before a reader can pin it).</summary>
    internal void Publish(TableVersion version) => Volatile.Write(ref _current, version);

    internal void SetPosition(UpdatePosition position) => Volatile.Write(ref _position, position);

    internal void CountCompaction() => Volatile.Write(ref _compactions, _compactions + 1);
}
