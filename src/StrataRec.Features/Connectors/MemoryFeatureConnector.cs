using System.Buffers;
using System.Globalization;
using System.Text.Json;
using StrataRec.Core.Bindings;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;

namespace StrataRec.Features.Connectors;

/// <summary>
/// <c>memory@1</c>: serves features from an in-memory JSON document. For local runs, tests, replays and samples.
/// </summary>
/// <remarks>
/// Settings: either <c>"data"</c> (inline object) or <c>"data_file"</c> (path relative to the config root), plus optional
/// <c>"latency_ms"</c> and <c>"fail": true</c> for fault injection. Data layout:
/// <code>
/// { "10042": { "i_clk_7d": 12.0, "i_author_id": 7 } }            // Single / Many: key → fields
/// { "123":   { "10042": { "ui_eng_30d": 3.0 } } }                  // Cross: context key → candidate key → fields
/// </code>
/// Keys are JSON property names (int64 keys in decimal). Absent keys or fields are reported as missing.
/// <para>Names that are canonical int64 decimals are also indexed by <c>long</c> when the data is loaded (top-level
/// keys, and the candidate keys of a <c>Cross</c> record), so int64 lookups format no strings per key.</para>
/// </remarks>
public sealed class MemoryFeatureConnector : IFeatureSourceConnector
{
    private readonly Dictionary<string, Record> _rows;
    private readonly Dictionary<long, Record> _int64Rows;
    private readonly int _latencyMs;
    private readonly bool _fail;

    public MemoryFeatureConnector(JsonElement data, int latencyMs = 0, bool fail = false)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("memory@1 data must be a JSON object keyed by entity key.");
        }

        // Later duplicates win, as JsonElement.TryGetProperty resolves them (the nested Cross index does the same).
        _rows = new Dictionary<string, Record>(StringComparer.Ordinal);
        _int64Rows = new Dictionary<long, Record>();
        foreach (var property in data.EnumerateObject())
        {
            var value = property.Value.Clone();
            var record = new Record(value, IndexInt64Properties(value));
            _rows[property.Name] = record;
            if (TryParseCanonicalInt64(property.Name, out var id))
            {
                _int64Rows[id] = record;
            }
        }

        _latencyMs = latencyMs;
        _fail = fail;
    }

    public string ConnectorType => MemoryFeatureConnectorFactory.Type;

    public bool Supports(KeyShape shape) => shape is KeyShape.Single or KeyShape.Many or KeyShape.Cross;

    public async ValueTask<FetchStatus> FetchAsync(
        EntityKeyBatch keys, ReadOnlyMemory<CompiledFieldBinding> fields, IFeatureBatchWriter writer, CancellationToken cancellationToken)
    {
        if (_latencyMs > 0)
        {
            await Task.Delay(_latencyMs, cancellationToken).ConfigureAwait(false);
        }

        if (_fail)
        {
            return FetchStatus.Failed("memory@1 source is configured to fail.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Serve(keys, fields, writer);
    }

    private FetchStatus Serve(EntityKeyBatch keys, ReadOnlyMemory<CompiledFieldBinding> fields, IFeatureBatchWriter writer)
    {
        switch (keys.Shape)
        {
            case KeyShape.Single:
                if (TryGetRecord(keys.ContextKey, out var single))
                {
                    WriteRecord(single.Value, 0, fields.Span, writer);
                }

                break;
            case KeyShape.Many:
                var many = keys.CandidateKeys.Span;
                for (var i = 0; i < many.Length; i++)
                {
                    if (TryGetRecord(many[i], out var record))
                    {
                        WriteRecord(record.Value, i, fields.Span, writer);
                    }
                }

                break;
            case KeyShape.Cross:
                if (TryGetRecord(keys.ContextKey, out var byCandidate) && byCandidate.Value.ValueKind == JsonValueKind.Object)
                {
                    var cand = keys.CandidateKeys.Span;
                    for (var i = 0; i < cand.Length; i++)
                    {
                        var found = cand[i].StringValue is { } name
                            ? byCandidate.Value.TryGetProperty(name, out var record)
                            : TryGetNested(byCandidate, cand[i].Int64Value, out record);
                        if (found)
                        {
                            WriteRecord(record, i, fields.Span, writer);
                        }
                    }
                }

                break;
            default:
                return FetchStatus.Failed($"memory@1 does not support {keys.Shape}.");
        }

        return FetchStatus.Ok;
    }

    private bool TryGetRecord(in EntityKey key, out Record record) => key.StringValue is { } name
        ? _rows.TryGetValue(name, out record)
        : _int64Rows.TryGetValue(key.Int64Value, out record);

    private static bool TryGetNested(in Record record, long key, out JsonElement value)
    {
        if (record.NestedByInt64 is { } nested)
        {
            return nested.TryGetValue(key, out value);
        }

        value = default;
        return false;
    }

    /// <summary>The properties of an object whose names are canonical int64 decimals (the candidate keys of Cross data).</summary>
    private static Dictionary<long, JsonElement>? IndexInt64Properties(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        Dictionary<long, JsonElement>? index = null;
        foreach (var property in value.EnumerateObject())
        {
            if (TryParseCanonicalInt64(property.Name, out var id))
            {
                (index ??= new Dictionary<long, JsonElement>())[id] = property.Value;
            }
        }

        return index;
    }

    /// <summary>
    /// True when <paramref name="name"/> is exactly how an int64 key formats (no '+', leading zeros, "-0" or
    /// whitespace), so a long lookup finds exactly the records a lookup by the key's decimal string would.
    /// </summary>
    private static bool TryParseCanonicalInt64(string name, out long value)
    {
        if (name.Length is 0 or > 20 || !long.TryParse(name, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
        {
            value = 0;
            return false;
        }

        Span<char> formatted = stackalloc char[20];
        return value.TryFormat(formatted, out var written, default, CultureInfo.InvariantCulture)
            && formatted[..written].SequenceEqual(name);
    }

    private static void WriteRecord(JsonElement record, int keyIndex, ReadOnlySpan<CompiledFieldBinding> fields, IFeatureBatchWriter writer)
    {
        if (record.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // Fields start missing; each value is marked present only after it decoded, so a record that throws half-way
        // never exposes a field it did not write.
        foreach (var field in fields)
        {
            if (!record.TryGetProperty(field.Field, out var v) || v.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            switch (field.Type.Kind)
            {
                case StorageKind.Bool:
                    writer.GetValues<bool>(field.FieldIndex).Span[keyIndex] = v.GetBoolean();
                    writer.SetValid(field.FieldIndex, keyIndex);
                    break;
                case StorageKind.Int32:
                    writer.GetValues<int>(field.FieldIndex).Span[keyIndex] = ReadInt32(v, field);
                    writer.SetValid(field.FieldIndex, keyIndex);
                    break;
                case StorageKind.Int64:
                    writer.GetValues<long>(field.FieldIndex).Span[keyIndex] = v.GetInt64();
                    writer.SetValid(field.FieldIndex, keyIndex);
                    break;
                case StorageKind.Float32:
                    writer.GetValues<float>(field.FieldIndex).Span[keyIndex] = v.GetSingle();
                    writer.SetValid(field.FieldIndex, keyIndex);
                    break;
                case StorageKind.Float64:
                    writer.GetValues<double>(field.FieldIndex).Span[keyIndex] = v.GetDouble();
                    writer.SetValid(field.FieldIndex, keyIndex);
                    break;
                case StorageKind.String or StorageKind.Text: writer.SetString(field.FieldIndex, keyIndex, v.GetString()!); break;
                case StorageKind.Vector:
                    var dim = field.Type.Dimension;
                    var dest = writer.GetValues<float>(field.FieldIndex).Span.Slice(keyIndex * dim, dim);
                    var n = 0;
                    foreach (var e in v.EnumerateArray())
                    {
                        if (n < dim)
                        {
                            dest[n] = e.GetSingle();
                        }

                        n++;
                    }

                    if (n == dim)
                    {
                        writer.SetValid(field.FieldIndex, keyIndex);
                    }

                    break;
                case StorageKind.ListInt64:
                    var longs = new long[v.GetArrayLength()];
                    var li = 0;
                    foreach (var e in v.EnumerateArray())
                    {
                        longs[li++] = e.GetInt64();
                    }

                    writer.SetInt64List(field.FieldIndex, keyIndex, longs);
                    break;
                case StorageKind.ListInt32:
                {
                    // Decoded straight to int32 (the writer copies, so the pooled buffer can go back right away).
                    var count = v.GetArrayLength();
                    var ints = ArrayPool<int>.Shared.Rent(Math.Max(1, count));
                    try
                    {
                        var ii = 0;
                        foreach (var e in v.EnumerateArray())
                        {
                            ints[ii++] = ReadInt32(e, field);
                        }

                        writer.SetInt32List(field.FieldIndex, keyIndex, ints.AsSpan(0, count));
                    }
                    finally
                    {
                        ArrayPool<int>.Shared.Return(ints);
                    }

                    break;
                }
                case StorageKind.ListString:
                    var strings = new string[v.GetArrayLength()];
                    var si = 0;
                    foreach (var e in v.EnumerateArray())
                    {
                        strings[si++] = e.GetString()!;
                    }

                    writer.SetStringList(field.FieldIndex, keyIndex, strings);
                    break;
            }
        }
    }

    /// <summary>
    /// An <c>int32</c> value or <c>list&lt;int32&gt;</c> element. Data that does not fit is a broken fixture, not a value to
    /// wrap: the call fails (the source is reported failed) with the field and value named.
    /// </summary>
    private static int ReadInt32(JsonElement value, in CompiledFieldBinding field)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result))
        {
            return result;
        }

        throw new FormatException(
            $"memory@1 field '{field.Field}' ({field.Type}) holds {value.GetRawText()}, which is not an int32 in [{int.MinValue}, {int.MaxValue}]; " +
            "fix the data or declare the field with an int64 type.");
    }

    /// <summary>A top-level entry; for Cross data its properties are candidate keys, indexed by int64 when canonical.</summary>
    private readonly record struct Record(JsonElement Value, Dictionary<long, JsonElement>? NestedByInt64);
}

/// <summary>Factory for <c>memory@1</c>.</summary>
public sealed class MemoryFeatureConnectorFactory : IFeatureConnectorFactory
{
    public const string Type = "memory@1";

    public string ConnectorType => Type;

    private const string Takes = "memory@1 takes 'data' (the records, by entity key) or 'data_file' (a JSON file of them), and optionally 'latency_ms' and 'fail'";
    private static readonly string[] Settings = ["data", "data_file", "latency_ms", "fail"];

    /// <remarks>
    /// A test fixture: it reads its data file here, at create time, so it needs no load. Settings are checked strictly: a
    /// misspelled one would otherwise be ignored, and every feature read missing.
    /// </remarks>
    /// <exception cref="BindingException">A setting is unknown or invalid, or the data file cannot be read: at the setting,
    /// with a hint.</exception>
    public IFeatureSourceConnector Create(BindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = context.Settings;
        foreach (var (name, _) in settings)
        {
            if (Array.IndexOf(Settings, name) < 0)
            {
                throw BindingException.Unknown(string.Empty, name, Settings, Takes);
            }
        }

        JsonElement data;
        if (settings.TryGetValue("data", out var inline))
        {
            if (settings.ContainsKey("data_file"))
            {
                throw new BindingException("/data_file", "memory@1 takes 'data' or 'data_file', not both.", "keep the one that holds the records");
            }

            data = inline;
        }
        else if (settings.TryGetValue("data_file", out var file))
        {
            data = Read(file, context.BaseDirectory);
        }
        else
        {
            data = JsonDocument.Parse("{}").RootElement;
        }

        if (data.ValueKind != JsonValueKind.Object)
        {
            throw new BindingException(settings.ContainsKey("data") ? "/data" : "/data_file", "The records must be a JSON object, by entity key.", Takes);
        }

        var latency = 0;
        if (settings.TryGetValue("latency_ms", out var l))
        {
            latency = l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var ms) && ms >= 0
                ? ms
                : throw new BindingException("/latency_ms", $"'latency_ms' must be a whole number of milliseconds, 0 or more; got {l.GetRawText()}.");
        }

        var fail = false;
        if (settings.TryGetValue("fail", out var f))
        {
            fail = f.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? f.GetBoolean()
                : throw new BindingException("/fail", $"'fail' must be true or false; got {f.GetRawText()}.");
        }

        return new MemoryFeatureConnector(data, latency, fail);
    }

    private static JsonElement Read(JsonElement setting, string baseDirectory)
    {
        if (setting.ValueKind != JsonValueKind.String || setting.GetString() is not { Length: > 0 } relative)
        {
            throw new BindingException("/data_file", $"'data_file' must be the path of a JSON file; got {setting.GetRawText()}.");
        }

        var path = Path.Combine(baseDirectory, relative); // a rooted path stays as it is
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BindingException("/data_file", $"'{path}' cannot be read: {ex.Message}", $"a relative path resolves against {baseDirectory}");
        }
        catch (JsonException ex)
        {
            throw new BindingException("/data_file", $"'{path}' is not JSON: {ex.Message}");
        }
    }
}
