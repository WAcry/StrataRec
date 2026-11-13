using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;

namespace StrataRec.Features.Local;

/// <summary>
/// <c>snapshot_files@1</c>: loads a table snapshot from local files (design 6.6 local items 1 and 5), and with
/// <c>reload</c> loads it again periodically, swapping each complete load in whole.
/// </summary>
/// <remarks>
/// <para><b>Layout.</b> The snapshot is a manifest, <c>snapshot.json</c>, next to (or naming) its data files:</para>
/// <code>
/// { "format": "jsonl", "position": { "0": 1200, "1": 980 }, "files": ["part-0000.jsonl", "part-0001.jsonl"] }
/// </code>
/// <para><c>position</c> is the update-stream position the data covers (<see cref="UpdatePosition"/>; omit it for a
/// source without a stream). <c>files</c> are paths relative to the manifest. A job publishes a new snapshot by writing
/// new data files and then replacing the manifest (write and rename), so a reader never sees half a snapshot.</para>
/// <para><b>Data files</b> are JSON Lines (one JSON object per line, UTF-8):</para>
/// <code>
/// {"key": 10042, "version": 17, "fields": {"i_clk_7d": 12.0, "i_title": "…", "i_emb_v3": [0.1, …], "i_age_h": null}}
/// {"key": 10043, "version": 18, "deleted": true}
/// </code>
/// <para><c>key</c> is a JSON number for int64-keyed tables and a string for string-keyed ones; <c>version</c> is an int64.
/// <c>fields</c> uses the store's physical field names; fields the table does not hold are skipped, <c>null</c> is
/// missing, and an absent field is not written (a later line of the same key keeps the earlier value). Values have the
/// field's type; floats and vector components also accept <c>"NaN"</c>, <c>"Infinity"</c> and <c>"-Infinity"</c>, which
/// are stored as such and replaced on read like any non-finite value. A line with <c>"deleted": true</c> is a tombstone.</para>
/// <para><b>Settings.</b> <c>uri</c>: the snapshot directory (holding <c>snapshot.json</c>) or the manifest itself, as a
/// path (relative to the config root) or a <c>file://</c> URI. <c>reload</c>: an ISO 8601 duration such as <c>PT1H</c>.</para>
/// <para>Each load is a reload of the table (<see cref="IFeatureTableWriter.BeginReload"/>): readers keep the previous
/// version until the new one is complete; a load that fails leaves it in place and the error is thrown. A manifest that
/// has not changed since the last load is not loaded again.</para>
/// </remarks>
public sealed class SnapshotFilesUpdateSource : IFeatureUpdateSource
{
    public const string Type = "snapshot_files@1";

    /// <summary>The manifest's name inside a snapshot directory.</summary>
    public const string ManifestFileName = "snapshot.json";

    public SnapshotFilesUpdateSource(string manifestPath, TimeSpan? reload = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(manifestPath);
        if (reload is { } period && period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(reload), reload, "The reload period must be positive.");
        }

        ManifestPath = manifestPath;
        Reload = reload;
    }

    public string SourceType => Type;

    public string ManifestPath { get; }

    /// <summary>How often to look for a new snapshot; null to load once.</summary>
    public TimeSpan? Reload { get; }

    public async Task RunAsync(UpdatePosition from, IFeatureTableWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(writer);
        byte[]? loaded = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = SnapshotManifest.Read(ManifestPath);

            // Unchanged since the last load, or (first round) already covered by the table the engine resumes.
            var unchanged = loaded is not null
                ? manifest.Digest.AsSpan().SequenceEqual(loaded)
                : !from.IsEmpty && manifest.Position.Equals(from);
            if (!unchanged)
            {
                Load(manifest, writer, cancellationToken);
            }

            loaded = manifest.Digest;
            if (Reload is not { } period)
            {
                return;
            }

            await Task.Delay(period, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Loads the current snapshot once, as one reload of the table, and returns the position it covers.</summary>
    public UpdatePosition Load(IFeatureTableWriter writer, CancellationToken cancellationToken = default)
    {
        var manifest = SnapshotManifest.Read(ManifestPath);
        Load(manifest, writer, cancellationToken);
        return manifest.Position;
    }

    private static void Load(SnapshotManifest manifest, IFeatureTableWriter writer, CancellationToken cancellationToken)
    {
        var reader = new JsonLinesSnapshotReader(writer);
        writer.BeginReload();
        try
        {
            for (var i = 0; i < manifest.Files.Length; i++)
            {
                reader.Load(manifest.Files[i], cancellationToken);
            }

            writer.CompleteReload(manifest.Position);
        }
        catch
        {
            writer.AbortReload();
            throw;
        }
        finally
        {
            reader.Dispose();
        }
    }
}

/// <summary>Factory for <c>snapshot_files@1</c>.</summary>
public sealed class SnapshotFilesUpdateSourceFactory : IFeatureUpdateSourceFactory
{
    public string SourceType => SnapshotFilesUpdateSource.Type;

    public IFeatureUpdateSource Create(string sourceName, IReadOnlyDictionary<string, JsonElement> settings, string? baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var location = $"source '{sourceName}' ({SnapshotFilesUpdateSource.Type})";
        string? uri = null;
        TimeSpan? reload = null;
        foreach (var (name, value) in settings)
        {
            switch (name)
            {
                case "source":
                    break; // the binding's own selector of this factory
                case "uri":
                    uri = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                    if (string.IsNullOrEmpty(uri))
                    {
                        throw new FeatureUpdateException(location, "/uri", "uri must be a non-empty string.",
                            "give the snapshot directory or its snapshot.json, as a path or a file:// URI");
                    }

                    break;
                case "reload":
                    reload = ParseDuration(value, location);
                    break;
                default:
                    throw new FeatureUpdateException(location, "/" + name, $"Unknown setting '{name}'.",
                        $"{SnapshotFilesUpdateSource.Type} takes 'uri' and optionally 'reload'");
            }
        }

        if (uri is null)
        {
            throw new FeatureUpdateException(location, string.Empty, "The 'uri' setting is required.",
                "add \"uri\": the snapshot directory (holding snapshot.json) or the manifest file");
        }

        return new SnapshotFilesUpdateSource(ResolveManifest(uri, baseDirectory, location), reload);
    }

    /// <summary>ISO 8601 duration (the JSON Schema <c>duration</c> format), e.g. <c>PT1H</c> or <c>PT30M</c>.</summary>
    private static TimeSpan ParseDuration(JsonElement value, string location)
    {
        const string Hint = "write an ISO 8601 duration such as \"PT1H\" (1 hour), \"PT15M\" or \"P1D\"";
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new FeatureUpdateException(location, "/reload", $"reload must be a string, got {value.GetRawText()}.", Hint);
        }

        TimeSpan period;
        try
        {
            period = XmlConvert.ToTimeSpan(value.GetString()!);
        }
        catch (FormatException)
        {
            throw new FeatureUpdateException(location, "/reload", $"'{value.GetString()}' is not an ISO 8601 duration.", Hint);
        }

        if (period <= TimeSpan.Zero)
        {
            throw new FeatureUpdateException(location, "/reload", $"reload must be positive, got '{value.GetString()}'.", Hint);
        }

        return period;
    }

    private static string ResolveManifest(string uri, string? baseDirectory, string location)
    {
        string path;
        if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            path = new Uri(uri).LocalPath;
        }
        else if (uri.Contains("://", StringComparison.Ordinal))
        {
            throw new FeatureUpdateException(location, "/uri", $"'{uri}' is not a local path; {SnapshotFilesUpdateSource.Type} reads local files.",
                "sync the snapshot to a local directory first, or register an update source for the object store");
        }
        else
        {
            path = Path.IsPathRooted(uri) || baseDirectory is null ? uri : Path.Combine(baseDirectory, uri);
        }

        return path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? path : Path.Combine(path, SnapshotFilesUpdateSource.ManifestFileName);
    }
}

/// <summary>A parsed <c>snapshot.json</c>: data files (resolved), the position they cover, and a digest to detect change.</summary>
internal sealed class SnapshotManifest
{
    private SnapshotManifest(UpdatePosition position, string[] files, byte[] digest)
    {
        Position = position;
        Files = files;
        Digest = digest;
    }

    public UpdatePosition Position { get; }

    public string[] Files { get; }

    public byte[] Digest { get; }

    public static SnapshotManifest Read(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FeatureUpdateException(path, string.Empty, $"Cannot read the snapshot manifest: {ex.Message}",
                "the snapshot directory must contain snapshot.json; a snapshot job writes it after the data files", ex);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            throw new FeatureUpdateException(path, string.Empty, $"The manifest is not valid JSON: {ex.Message}", null, ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FeatureUpdateException(path, string.Empty, "The manifest must be a JSON object.",
                    "write {\"files\": [\"part-0000.jsonl\"], \"position\": {\"0\": 1200}}");
            }

            var position = UpdatePosition.Empty;
            string[]? files = null;
            var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            foreach (var member in root.EnumerateObject())
            {
                switch (member.Name)
                {
                    case "format":
                        if (member.Value.ValueKind != JsonValueKind.String || member.Value.GetString() != "jsonl")
                        {
                            throw new FeatureUpdateException(path, "/format", $"Unsupported snapshot format {member.Value.GetRawText()}.",
                                "snapshot_files@1 reads \"jsonl\" (JSON Lines) snapshots");
                        }

                        break;
                    case "position":
                        try
                        {
                            position = UpdatePosition.FromJson(member.Value, "/position");
                        }
                        catch (FormatException ex)
                        {
                            var colon = ex.Message.IndexOf(": ", StringComparison.Ordinal);
                            throw new FeatureUpdateException(path, ex.Message[..colon], ex.Message[(colon + 2)..],
                                "position maps each partition number to the offset of the first update the snapshot does not include");
                        }

                        break;
                    case "files":
                        files = ReadFiles(member.Value, path, directory);
                        break;
                    default:
                        throw new FeatureUpdateException(path, "/" + member.Name, $"Unknown manifest member '{member.Name}'.",
                            "a snapshot manifest has 'files', and optionally 'position' and 'format'");
                }
            }

            if (files is null)
            {
                throw new FeatureUpdateException(path, string.Empty, "The manifest has no 'files'.",
                    "list the data files, relative to the manifest: \"files\": [\"part-0000.jsonl\"]");
            }

            return new SnapshotManifest(position, files, SHA256.HashData(bytes));
        }
    }

    private static string[] ReadFiles(JsonElement value, string path, string directory)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new FeatureUpdateException(path, "/files", "files must be an array of paths.", "\"files\": [\"part-0000.jsonl\"]");
        }

        var files = new string[value.GetArrayLength()];
        var i = 0;
        foreach (var item in value.EnumerateArray())
        {
            var name = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (string.IsNullOrEmpty(name))
            {
                throw new FeatureUpdateException(path, "/files/" + i.ToString(CultureInfo.InvariantCulture),
                    "Each file is a non-empty path string.", "paths are relative to the manifest's directory");
            }

            files[i++] = Path.IsPathRooted(name) ? name : Path.GetFullPath(Path.Combine(directory, name));
        }

        return files;
    }
}

/// <summary>
/// Decodes JSON Lines snapshot files straight into a table writer: one <see cref="Utf8JsonReader"/> pass per line over a
/// reused byte buffer, strings copied into a reused char buffer, so a line allocates nothing (only a string value the
/// table's dictionary does not hold yet becomes a string).
/// </summary>
internal sealed class JsonLinesSnapshotReader : IDisposable
{
    private const int CancellationCheckLines = 4096;
    private static readonly JsonReaderOptions Strict = new() { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false };

    private readonly IFeatureTableWriter _writer;
    private readonly LocalTableSchema _schema;
    private readonly byte[][] _names;
    private readonly bool[] _seen;
    private byte[] _buffer;
    private char[] _chars = new char[256];
    private char[] _key = new char[64];
    private string _path = string.Empty;
    private long _line;
    private int _cursor;

    public JsonLinesSnapshotReader(IFeatureTableWriter writer)
    {
        _writer = writer;
        _schema = writer.Schema;
        _names = new byte[_schema.FieldCount][];
        for (var i = 0; i < _names.Length; i++)
        {
            _names[i] = System.Text.Encoding.UTF8.GetBytes(_schema.Fields[i].Name);
        }

        _seen = new bool[_schema.FieldCount];
        _buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
    }

    /// <summary>Loads every line of one file. Returns the number of lines that held a record.</summary>
    public long Load(string path, CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FeatureUpdateException(path, string.Empty, $"Cannot read the data file: {ex.Message}",
                "the manifest names a file that is missing; publish the data files before the manifest", ex);
        }

        using (stream)
        {
            long line = 0;
            long records = 0;
            var start = 0;
            var end = 0;
            var eof = false;
            while (true)
            {
                var newline = _buffer.AsSpan(start, end - start).IndexOf((byte)'\n');
                if (newline < 0)
                {
                    if (eof)
                    {
                        if (end > start)
                        {
                            records += Line(_buffer.AsSpan(start, end - start), path, ++line);
                        }

                        return records;
                    }

                    if (start > 0)
                    {
                        _buffer.AsSpan(start, end - start).CopyTo(_buffer);
                        end -= start;
                        start = 0;
                    }

                    if (end == _buffer.Length)
                    {
                        var grown = ArrayPool<byte>.Shared.Rent(_buffer.Length * 2);
                        _buffer.AsSpan(0, end).CopyTo(grown);
                        ArrayPool<byte>.Shared.Return(_buffer);
                        _buffer = grown;
                    }

                    var read = stream.Read(_buffer, end, _buffer.Length - end);
                    eof = read == 0;
                    end += read;
                    continue;
                }

                records += Line(_buffer.AsSpan(start, newline), path, ++line);
                start += newline + 1;
                if (line % CancellationCheckLines == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }
    }

    public void Dispose()
    {
        if (_buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = Array.Empty<byte>();
        }
    }

    private int Line(ReadOnlySpan<byte> line, string path, long number)
    {
        if (number == 1 && line.StartsWith("\uFEFF"u8))
        {
            line = line[3..]; // UTF-8 byte order mark
        }

        if (!line.IsEmpty && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        if (line.Trim(" \t"u8).IsEmpty)
        {
            return 0;
        }

        _path = path;
        _line = number;
        try
        {
            Record(line);
            return 1;
        }
        catch (JsonException ex)
        {
            throw Error(string.Empty, $"Invalid JSON at byte {ex.BytePositionInLine?.ToString(CultureInfo.InvariantCulture) ?? "?"}: {ex.Message}",
                "each line of a snapshot file is one JSON object: {\"key\": …, \"version\": …, \"fields\": {…}}");
        }
    }

    private void Record(ReadOnlySpan<byte> line)
    {
        var reader = new Utf8JsonReader(line, Strict);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw Error(string.Empty, "A snapshot line must be a JSON object.", "write {\"key\": …, \"version\": …, \"fields\": {…}}");
        }

        var hasKey = false;
        long intKey = 0;
        var keyLength = 0;
        long? version = null;
        bool? deleted = null;
        var fieldsStart = -1;
        var fieldsEnd = -1;
        var builder = default(RecordBuilder);
        var begun = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("key"u8))
            {
                Once(hasKey, "/key");
                reader.Read();
                hasKey = true;
                if (_schema.KeyType == EntityKeyType.Int64)
                {
                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out intKey))
                    {
                        throw Error("/key", $"The key must be an int64 number, got {Raw(ref reader)}.",
                            $"table {_schema.Source}/{_schema.Entity} is keyed by int64: write the key as a JSON number");
                    }
                }
                else
                {
                    if (reader.TokenType != JsonTokenType.String)
                    {
                        throw Error("/key", $"The key must be a string, got {Raw(ref reader)}.",
                            $"table {_schema.Source}/{_schema.Entity} is keyed by string: write the key as a JSON string");
                    }

                    keyLength = CopyString(ref reader, ref _key);
                }
            }
            else if (reader.ValueTextEquals("version"u8))
            {
                Once(version.HasValue, "/version");
                reader.Read();
                if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var v))
                {
                    throw Error("/version", $"The version must be an int64 number, got {Raw(ref reader)}.",
                        "use the update's event version (e.g. its source timestamp or sequence number)");
                }

                version = v;
            }
            else if (reader.ValueTextEquals("deleted"u8))
            {
                Once(deleted.HasValue, "/deleted");
                reader.Read();
                if (reader.TokenType is not (JsonTokenType.True or JsonTokenType.False))
                {
                    throw Error("/deleted", $"deleted must be true or false, got {Raw(ref reader)}.",
                        "write \"deleted\": true for a tombstone, or leave it out");
                }

                deleted = reader.TokenType == JsonTokenType.True;
            }
            else if (reader.ValueTextEquals("fields"u8))
            {
                Once(fieldsStart >= 0, "/fields");
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    throw Error("/fields", $"fields must be an object of field values, got {Raw(ref reader)}.",
                        "write \"fields\": {\"<field>\": <value>, …} with the store's field names");
                }

                fieldsStart = (int)reader.TokenStartIndex;
                if (hasKey && version is { } known && deleted != true)
                {
                    // The usual order (key and version first): decode the fields in this pass instead of tokenizing them twice.
                    builder = Begin(intKey, keyLength, known);
                    begun = true;
                    Fields(ref reader, builder);
                }
                else
                {
                    reader.Skip();
                    fieldsEnd = (int)reader.BytesConsumed;
                }
            }
            else
            {
                throw Error("/" + Escape(reader.GetString()!), $"Unknown member '{reader.GetString()}'.",
                    "a snapshot line has 'key', 'version', 'fields' and, for a tombstone, \"deleted\": true");
            }
        }

        // The loop ends at the object's end; anything else is a line cut short. Data after the object makes the reader throw.
        if (reader.TokenType != JsonTokenType.EndObject || reader.CurrentDepth != 0 || reader.Read())
        {
            throw Error(string.Empty, "The line is not one complete JSON object.", "write one JSON object per line");
        }

        if (!hasKey)
        {
            throw Error(string.Empty, "The line has no 'key'.", "every record names its entity: \"key\": 10042");
        }

        if (version is not { } recordVersion)
        {
            throw Error(string.Empty, "The line has no 'version'.", "every record carries its event version: \"version\": 17");
        }

        if (deleted == true)
        {
            if (fieldsStart >= 0)
            {
                throw Error("/fields", "A tombstone (\"deleted\": true) has no fields.", "drop 'fields', or drop 'deleted' to write values");
            }

            _ = _schema.KeyType == EntityKeyType.Int64 ? _writer.Delete(intKey, recordVersion) : _writer.Delete(_key.AsSpan(0, keyLength), recordVersion);
            return;
        }

        if (!begun)
        {
            builder = Begin(intKey, keyLength, recordVersion);
            if (fieldsStart >= 0)
            {
                var fields = new Utf8JsonReader(line[fieldsStart..fieldsEnd], Strict);
                fields.Read(); // the object's start; the first pass checked the shape
                Fields(ref fields, builder);
            }
        }

        builder.Commit();
    }

    private RecordBuilder Begin(long intKey, int keyLength, long version) => _schema.KeyType == EntityKeyType.Int64
        ? _writer.Begin(intKey, version)
        : _writer.Begin(_key.AsSpan(0, keyLength), version);

    /// <summary>Decodes a fields object; <paramref name="reader"/> is on its start and is left on its end.</summary>
    private void Fields(ref Utf8JsonReader reader, RecordBuilder builder)
    {
        Array.Clear(_seen);
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var field = Match(ref reader);
            reader.Read();
            if (field < 0)
            {
                reader.Skip(); // a field of the store that no feature of this table reads
                continue;
            }

            if (_seen[field])
            {
                throw Error(Pointer(field), $"Field '{_schema.Fields[field].Name}' appears twice.", "write each field once per line");
            }

            _seen[field] = true;
            if (reader.TokenType == JsonTokenType.Null)
            {
                builder.SetMissing(field);
                continue;
            }

            Value(ref reader, field, builder);
        }
    }

    /// <summary>The field a property names, or -1. Lines usually list fields in the same order, so the field after the last
    /// match is tried first.</summary>
    private int Match(ref Utf8JsonReader reader)
    {
        var count = _names.Length;
        for (var i = 0; i < count; i++)
        {
            var candidate = _cursor + i < count ? _cursor + i : _cursor + i - count;
            if (reader.ValueTextEquals(_names[candidate]))
            {
                _cursor = candidate + 1 < count ? candidate + 1 : 0;
                return candidate;
            }
        }

        return -1;
    }

    /// <summary>Decodes one field value into the builder. Pointers for errors are built only when an error is thrown.</summary>
    private void Value(ref Utf8JsonReader reader, int field, RecordBuilder builder)
    {
        var type = _schema.Fields[field].Type;
        switch (type.Kind)
        {
            case StorageKind.Bool:
                if (reader.TokenType is not (JsonTokenType.True or JsonTokenType.False))
                {
                    throw Mismatch(ref reader, field, -1, type);
                }

                builder.SetBool(field, reader.TokenType == JsonTokenType.True);
                break;
            case StorageKind.Int32:
                builder.SetInt32(field, Int32(ref reader, field, -1, type));
                break;
            case StorageKind.Int64:
                builder.SetInt64(field, Int64(ref reader, field, -1, type));
                break;
            case StorageKind.Float32:
                builder.SetFloat32(field, Float32(ref reader, field, -1, type));
                break;
            case StorageKind.Float64:
                builder.SetFloat64(field, Float64(ref reader, field, -1, type));
                break;
            case StorageKind.String or StorageKind.Text:
                if (reader.TokenType != JsonTokenType.String)
                {
                    throw Mismatch(ref reader, field, -1, type);
                }

                builder.SetString(field, _chars.AsSpan(0, CopyString(ref reader, ref _chars)));
                break;
            case StorageKind.Vector:
            {
                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    throw Mismatch(ref reader, field, -1, type);
                }

                var target = builder.VectorBuffer(field);
                var n = 0;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (n == target.Length)
                    {
                        throw Error(Pointer(field), $"Expected {target.Length} elements for {type}, got more.", $"write exactly {target.Length} numbers");
                    }

                    target[n] = Float32(ref reader, field, n, ColumnType.Float32);
                    n++;
                }

                if (n != target.Length)
                {
                    throw Error(Pointer(field), $"Expected {target.Length} elements for {type}, got {n}.", $"write exactly {target.Length} numbers");
                }

                break;
            }

            case StorageKind.ListInt32 or StorageKind.ListInt64 or StorageKind.ListString:
            {
                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    throw Mismatch(ref reader, field, -1, type);
                }

                builder.StartList(field);
                var n = 0;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    switch (type.Kind)
                    {
                        case StorageKind.ListInt32: builder.AddInt32Item(field, Int32(ref reader, field, n, ColumnType.Int32)); break;
                        case StorageKind.ListInt64: builder.AddInt64Item(field, Int64(ref reader, field, n, ColumnType.Int64)); break;
                        default:
                            if (reader.TokenType != JsonTokenType.String)
                            {
                                throw Error(Pointer(field, n), $"A list<string> item must be a string, got {Raw(ref reader)}.", "list items cannot be null");
                            }

                            builder.AddStringItem(field, _chars.AsSpan(0, CopyString(ref reader, ref _chars)));
                            break;
                    }

                    n++;
                }

                break;
            }

            default:
                throw new InvalidOperationException($"Unsupported field type {type}.");
        }
    }

    private int Int32(ref Utf8JsonReader reader, int field, int element, ColumnType type)
    {
        if (reader.TokenType != JsonTokenType.Number)
        {
            throw Mismatch(ref reader, field, element, type);
        }

        if (!reader.TryGetInt32(out var value))
        {
            throw Error(Pointer(field, element), $"{Raw(ref reader)} is not an int32 in [{int.MinValue}, {int.MaxValue}].",
                "fix the data, or declare the feature with an int64 type");
        }

        return value;
    }

    private long Int64(ref Utf8JsonReader reader, int field, int element, ColumnType type)
    {
        if (reader.TokenType != JsonTokenType.Number)
        {
            throw Mismatch(ref reader, field, element, type);
        }

        if (!reader.TryGetInt64(out var value))
        {
            throw Error(Pointer(field, element), $"{Raw(ref reader)} is not an int64.", "integers are written without a fraction or exponent, within the int64 range");
        }

        return value;
    }

    private float Float32(ref Utf8JsonReader reader, int field, int element, ColumnType type)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return (float)NonFinite(ref reader, field, element, type);
        }

        if (reader.TokenType != JsonTokenType.Number)
        {
            throw Mismatch(ref reader, field, element, type);
        }

        // A number beyond the float32 range reads as ±∞, as it does for memory@1 (both use System.Text.Json); the read path
        // then replaces it like any non-finite value.
        if (!reader.TryGetSingle(out var value))
        {
            throw Error(Pointer(field, element), $"{Raw(ref reader)} is not a float32 number.", "write a JSON number");
        }

        return value;
    }

    private double Float64(ref Utf8JsonReader reader, int field, int element, ColumnType type)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return NonFinite(ref reader, field, element, type);
        }

        if (reader.TokenType != JsonTokenType.Number)
        {
            throw Mismatch(ref reader, field, element, type);
        }

        if (!reader.TryGetDouble(out var value))
        {
            throw Error(Pointer(field, element), $"{Raw(ref reader)} is not a float64 number.", "write a JSON number");
        }

        return value;
    }

    /// <summary>The spellings of non-finite values, as replay bundles write them (design 13.4).</summary>
    private double NonFinite(ref Utf8JsonReader reader, int field, int element, ColumnType type)
    {
        if (reader.ValueTextEquals("NaN"u8))
        {
            return double.NaN;
        }

        if (reader.ValueTextEquals("Infinity"u8))
        {
            return double.PositiveInfinity;
        }

        if (reader.ValueTextEquals("-Infinity"u8))
        {
            return double.NegativeInfinity;
        }

        throw Error(Pointer(field, element), $"Expected a {type} number, got {Raw(ref reader)}.",
            "the only strings a float field accepts are \"NaN\", \"Infinity\" and \"-Infinity\"");
    }

    private static int CopyString(ref Utf8JsonReader reader, ref char[] buffer)
    {
        // The unescaped value has at most as many chars as its escaped UTF-8 form has bytes.
        var bound = reader.HasValueSequence ? checked((int)reader.ValueSequence.Length) : reader.ValueSpan.Length;
        if (bound > buffer.Length)
        {
            buffer = new char[Math.Max(bound, buffer.Length * 2)];
        }

        return reader.CopyString(buffer);
    }

    private void Once(bool seen, string pointer)
    {
        if (seen)
        {
            throw Error(pointer, $"'{pointer[1..]}' appears twice.", "write each member once per line");
        }
    }

    private FeatureUpdateException Mismatch(ref Utf8JsonReader reader, int field, int element, ColumnType type) =>
        Error(Pointer(field, element), $"Expected a value of type {type}, got {Raw(ref reader)}.",
            element < 0 ? $"write a {type} value, or null for no value" : $"write a {type} value");

    private FeatureUpdateException Error(string pointer, string message, string? hint = null) =>
        new(_path + ":" + _line.ToString(CultureInfo.InvariantCulture), pointer, message, hint);

    /// <summary><c>/fields/&lt;name&gt;</c>, or <c>/fields/&lt;name&gt;/&lt;element&gt;</c> for a vector or list element.</summary>
    private string Pointer(int field, int element = -1) =>
        "/fields/" + Escape(_schema.Fields[field].Name) + (element < 0 ? string.Empty : "/" + element.ToString(CultureInfo.InvariantCulture));

    /// <summary>The current token as written, shortened, for messages.</summary>
    private static string Raw(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            return reader.TokenType == JsonTokenType.StartObject ? "an object" : "an array";
        }

        var span = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan.ToArray();
        var text = System.Text.Encoding.UTF8.GetString(span, 0, Math.Min(span.Length, 64));
        return reader.TokenType == JsonTokenType.String ? "\"" + text + "\"" : text;
    }

    private static string Escape(string token) => token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
