using System.Globalization;
using System.Text;
using System.Text.Json;
using StrataRec.Core.Keys;

namespace StrataRec.Features.Local;

/// <summary>
/// A background feed of a local feature table: a snapshot loader, an update stream consumer, or both (design 6.6).
/// Like a connector, it decodes bytes straight into the writer (<see cref="IFeatureTableWriter.Begin(long, long)"/> and
/// <see cref="RecordBuilder"/>), with no object per message.
/// </summary>
public interface IFeatureUpdateSource
{
    /// <summary>Source type and version, e.g. <c>snapshot_files@1</c> or <c>kafka@1</c>.</summary>
    string SourceType { get; }

    /// <summary>
    /// Feeds <paramref name="writer"/> from <paramref name="from"/> on, until the source has nothing more to do (a snapshot
    /// without reload) or <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <remarks>
    /// <para>The writer has a single writer: the source must not call it from two threads at once (one call after
    /// another from different threads, as an async method does, is fine). Loading may run synchronously on the calling
    /// thread before the first await; the engine runs sources off the request path.</para>
    /// <para>A failure is thrown, preferably as <see cref="FeatureUpdateException"/> with where the bad data is and how
    /// to fix it. The table keeps every update written before the failure and its last complete reload; the engine may
    /// run the source again from the table's <see cref="LocalFeatureTable.Position"/>.</para>
    /// </remarks>
    Task RunAsync(UpdatePosition from, IFeatureTableWriter writer, CancellationToken cancellationToken);
}

/// <summary>Creates update sources for bindings of one source type (the update-source counterpart of
/// <see cref="Connectors.IFeatureConnectorFactory"/>).</summary>
public interface IFeatureUpdateSourceFactory
{
    string SourceType { get; }

    /// <param name="sourceName">Logical source name, e.g. <c>item_store</c>.</param>
    /// <param name="settings">Source-specific settings (<c>bootstrap</c> or <c>updates</c> of the binding, without <c>source</c>).</param>
    /// <param name="baseDirectory">Directory relative paths resolve against (the config root), if any.</param>
    /// <exception cref="FeatureUpdateException">A setting is missing or invalid; the pointer names it.</exception>
    IFeatureUpdateSource Create(string sourceName, IReadOnlyDictionary<string, JsonElement> settings, string? baseDirectory);
}

/// <summary>
/// The write side of a local feature table, as seen by update sources (design 6.6). Implemented by the engine
/// (<see cref="LocalTableWriter"/>); there is one per table and it has a single writer.
/// </summary>
/// <remarks>
/// <para><b>Versions.</b> Every update carries a version: the event version for <c>local</c> tables, the write time for
/// <c>cache</c>. An update or delete whose version is not greater than the version of the key's current record (or
/// tombstone) is ignored, so out-of-order and duplicate delivery do not change the result.</para>
/// <para><b>Partial updates.</b> A record built with <see cref="Begin(long, long)"/> starts from the key's current
/// record: fields the update does not write keep their old values. <see cref="RecordBuilder.SetMissing"/> clears one.</para>
/// <para><b>One builder at a time.</b> A builder that is not committed before the next <c>Begin</c>, <c>Delete</c>,
/// <c>Checkpoint</c> or reload call is discarded, which is also how a source drops a message it failed to decode.</para>
/// </remarks>
public interface IFeatureTableWriter
{
    LocalTableSchema Schema { get; }

    /// <summary>Starts an update of an int64-keyed table.</summary>
    RecordBuilder Begin(long key, long version);

    /// <summary>Starts an update of a string-keyed table, from the key's chars (no string needed).</summary>
    RecordBuilder Begin(ReadOnlySpan<char> key, long version);

    RecordBuilder Begin(in EntityKey key, long version);

    /// <summary>Writes a tombstone: the key has no record from this version on. Returns false when ignored as stale.</summary>
    bool Delete(long key, long version);

    bool Delete(ReadOnlySpan<char> key, long version);

    bool Delete(in EntityKey key, long version);

    /// <summary>Every update before <paramref name="position"/> has been written (see <see cref="LocalFeatureTable.Position"/>).</summary>
    void Checkpoint(UpdatePosition position);

    /// <summary>
    /// Starts building a replacement of the whole table (a snapshot load or reload, design 6.6 local item 5). Writes go to
    /// an empty version that readers do not see until <see cref="CompleteReload"/> swaps it in.
    /// </summary>
    void BeginReload();

    /// <summary>Swaps the version built since <see cref="BeginReload"/> in and records the position its data covers.</summary>
    void CompleteReload(UpdatePosition position);

    /// <summary>Discards the version built since <see cref="BeginReload"/>; readers keep the current one.</summary>
    void AbortReload();
}

/// <summary>One partition's offset in an <see cref="UpdatePosition"/>.</summary>
public readonly record struct PartitionOffset(int Partition, long Offset);

/// <summary>
/// Where an update stream stands (design 6.6 local item 1): for each partition, the offset of the next update not yet
/// applied (the convention of Kafka's committed offsets). A snapshot records the position its data covers, and the stream
/// is consumed from there. A partition the position does not mention is at its start.
/// </summary>
/// <remarks>Immutable. JSON form: an object from partition number to offset, e.g. <c>{"0": 1200, "1": 980}</c>.</remarks>
public sealed class UpdatePosition : IEquatable<UpdatePosition>
{
    private readonly PartitionOffset[] _offsets;

    /// <summary>
    /// The only constructor, so every way of making a position (<see cref="Empty"/>, <see cref="Single"/>,
    /// <see cref="FromJson"/>) passes the same checks: partitions and offsets non-negative, each partition once.
    /// </summary>
    public UpdatePosition(IEnumerable<PartitionOffset> offsets)
    {
        ArgumentNullException.ThrowIfNull(offsets);
        var list = new List<PartitionOffset>(offsets);
        list.Sort(static (a, b) => a.Partition.CompareTo(b.Partition));
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Partition < 0 || list[i].Offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offsets), list[i], "Partitions and offsets are non-negative.");
            }

            if (i > 0 && list[i].Partition == list[i - 1].Partition)
            {
                throw new ArgumentException($"Partition {list[i].Partition} appears twice.", nameof(offsets));
            }
        }

        _offsets = list.ToArray();
    }

    /// <summary>Nothing applied: every partition at its start (also the position of a source without a stream).</summary>
    public static UpdatePosition Empty { get; } = new(Array.Empty<PartitionOffset>());

    /// <summary>A position of a single-partition stream.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative.</exception>
    public static UpdatePosition Single(long offset) => new(new[] { new PartitionOffset(0, offset) });

    /// <summary>Offsets ordered by partition.</summary>
    public ReadOnlySpan<PartitionOffset> Offsets => _offsets;

    public bool IsEmpty => _offsets.Length == 0;

    /// <summary>The partition's offset, or 0 (its start) when the position does not mention it.</summary>
    public long OffsetOf(int partition)
    {
        foreach (var o in _offsets)
        {
            if (o.Partition == partition)
            {
                return o.Offset;
            }
        }

        return 0;
    }

    /// <summary>Reads the JSON form. Errors name the bad member relative to <paramref name="pointer"/>.</summary>
    /// <exception cref="FormatException">Not an object of non-negative integer partition names to non-negative int64 offsets.</exception>
    public static UpdatePosition FromJson(JsonElement json, string pointer = "")
    {
        if (json.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"{pointer}: an update position is an object from partition number to offset, e.g. {{\"0\": 1200}}.");
        }

        var offsets = new List<PartitionOffset>();
        foreach (var property in json.EnumerateObject())
        {
            var at = pointer + "/" + property.Name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
            if (!int.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var partition))
            {
                throw new FormatException($"{at}: '{property.Name}' is not a partition number (a non-negative integer).");
            }

            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt64(out var offset) || offset < 0)
            {
                throw new FormatException($"{at}: the offset must be a non-negative int64, got {property.Value.GetRawText()}.");
            }

            foreach (var o in offsets)
            {
                if (o.Partition == partition)
                {
                    throw new FormatException($"{at}: partition {partition} appears twice.");
                }
            }

            offsets.Add(new PartitionOffset(partition, offset));
        }

        return offsets.Count == 0 ? Empty : new UpdatePosition(offsets);
    }

    public bool Equals(UpdatePosition? other)
    {
        if (other is null || other._offsets.Length != _offsets.Length)
        {
            return false;
        }

        for (var i = 0; i < _offsets.Length; i++)
        {
            if (_offsets[i] != other._offsets[i])
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is UpdatePosition other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var o in _offsets)
        {
            hash.Add(o);
        }

        return hash.ToHashCode();
    }

    /// <summary>The JSON form, e.g. <c>{"0":1200,"1":980}</c>.</summary>
    public override string ToString()
    {
        var text = new StringBuilder("{");
        for (var i = 0; i < _offsets.Length; i++)
        {
            text.Append(i == 0 ? "\"" : ",\"").Append(_offsets[i].Partition.ToString(CultureInfo.InvariantCulture)).Append("\":")
                .Append(_offsets[i].Offset.ToString(CultureInfo.InvariantCulture));
        }

        return text.Append('}').ToString();
    }
}

/// <summary>
/// Update data or settings an update source cannot use: where the problem is (a file and line, or the source's name),
/// the RFC 6901 pointer of the bad value within that line or document, and how to fix it.
/// </summary>
public sealed class FeatureUpdateException : Exception
{
    public FeatureUpdateException(string location, string pointer, string message, string? hint = null, Exception? innerException = null)
        : base(Format(location, pointer, message, hint), innerException)
    {
        Location = location;
        Pointer = pointer;
        Reason = message;
        Hint = hint;
    }

    /// <summary>E.g. <c>/data/items/part-0000.jsonl:12</c>, or <c>source 'item_store'</c> for a setting.</summary>
    public string Location { get; }

    /// <summary>RFC 6901 pointer within the line or document, e.g. <c>/fields/i_clk_7d</c>; empty for the whole of it.</summary>
    public string Pointer { get; }

    public string Reason { get; }

    public string? Hint { get; }

    private static string Format(string location, string pointer, string message, string? hint) =>
        $"{location}{(pointer.Length == 0 ? string.Empty : " " + pointer)}: {message}{(hint is null ? string.Empty : "\n    hint: " + hint)}";
}

/// <summary>
/// A write would take a local table past its <c>max_bytes</c> even after compaction. For a <c>local</c> table the load
/// fails and the source is unavailable (design 6.6 local item 6); a <c>cache</c> table will evict instead (phase 2).
/// </summary>
public sealed class LocalTableFullException : InvalidOperationException
{
    public LocalTableFullException(LocalTableSchema schema, long maxBytes, long requiredBytes)
        : base($"Local table {schema.Source}/{schema.Entity} needs {requiredBytes} bytes, more than its max_bytes ({maxBytes}).\n" +
               "    hint: raise max_bytes in the source's binding, or bind fewer or smaller features to the source.")
    {
        MaxBytes = maxBytes;
        RequiredBytes = requiredBytes;
    }

    public long MaxBytes { get; }

    /// <summary>The table's size had the write gone ahead.</summary>
    public long RequiredBytes { get; }
}
