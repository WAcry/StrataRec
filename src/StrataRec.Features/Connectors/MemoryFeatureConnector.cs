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
                case StorageKind.List:
                    WriteList(v, keyIndex, field, writer);
                    break;
            }
        }
    }

    /// <summary>A list value, decoded straight to the field's element storage into a pooled buffer, which can go back
    /// right away: the writer copies. Written as it is, whatever its length: the engine checks a fixed-length field's
    /// length where it stores the value, as for any connector.</summary>
    private static void WriteList(JsonElement array, int keyIndex, in CompiledFieldBinding field, IFeatureBatchWriter writer)
    {
        var count = array.GetArrayLength();
        var (f, k) = (field.FieldIndex, keyIndex);
        switch (field.Type.Element)
        {
            case StorageKind.Bool:
                var bools = ArrayPool<bool>.Shared.Rent(Math.Max(1, count));
                writer.SetBoolList(f, k, Fill(array, field, bools));
                ArrayPool<bool>.Shared.Return(bools);
                break;
            case StorageKind.Int32:
                var ints = ArrayPool<int>.Shared.Rent(Math.Max(1, count));
                writer.SetInt32List(f, k, Fill(array, field, ints));
                ArrayPool<int>.Shared.Return(ints);
                break;
            case StorageKind.Int64:
                var longs = ArrayPool<long>.Shared.Rent(Math.Max(1, count));
                writer.SetInt64List(f, k, Fill(array, field, longs));
                ArrayPool<long>.Shared.Return(longs);
                break;
            case StorageKind.Float32:
                var floats = ArrayPool<float>.Shared.Rent(Math.Max(1, count));
                writer.SetFloat32List(f, k, Fill(array, field, floats));
                ArrayPool<float>.Shared.Return(floats);
                break;
            case StorageKind.Float64:
                var doubles = ArrayPool<double>.Shared.Rent(Math.Max(1, count));
                writer.SetFloat64List(f, k, Fill(array, field, doubles));
                ArrayPool<double>.Shared.Return(doubles);
                break;
            default:
                var strings = ArrayPool<string>.Shared.Rent(Math.Max(1, count));
                writer.SetStringList(f, k, Fill(array, field, strings));
                ArrayPool<string>.Shared.Return(strings, clearArray: true);
                break;
        }
    }

    /// <summary>Decodes the array's items into <paramref name="buffer"/>; a buffer a decoding error leaves behind is
    /// simply not returned to the pool.</summary>
    private static ReadOnlySpan<T> Fill<T>(JsonElement array, in CompiledFieldBinding field, T[] buffer)
    {
        var i = 0;
        foreach (var e in array.EnumerateArray())
        {
            // typeof(T) tests are constants once the method is compiled for T: no boxing remains.
            buffer[i++] = typeof(T) == typeof(bool) ? (T)(object)e.GetBoolean()
                : typeof(T) == typeof(int) ? (T)(object)ReadInt32(e, field)
                : typeof(T) == typeof(long) ? (T)(object)e.GetInt64()
                : typeof(T) == typeof(float) ? (T)(object)e.GetSingle()
                : typeof(T) == typeof(double) ? (T)(object)e.GetDouble()
                : (T)(object)e.GetString()!;
        }

        return buffer.AsSpan(0, i);
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

/// <summary>Settings of <c>memory@1</c>.</summary>
public sealed class MemoryConnectorSettings
{
    /// <summary>
    /// The records, by entity key: <c>{ "10042": { "i_clk_7d": 12.0 } }</c>, or for a cross entity, context key to candidate
    /// key to fields. Not with <c>data_file</c>; without either, there are none.
    /// </summary>
    public JsonElement? Data { get; init; }

    /// <summary>A JSON file of the records, as <c>data</c> holds them: a path relative to the configuration directory, an absolute path or a file:// URI.</summary>
    public LocalFile? DataFile { get; init; }

    /// <summary>Simulated latency of every fetch, in milliseconds (default 0).</summary>
    public int LatencyMs { get; init; }

    /// <summary>Fail every fetch (fault injection).</summary>
    public bool Fail { get; init; }
}

/// <summary>Factory for <c>memory@1</c>.</summary>
public sealed class MemoryFeatureConnectorFactory : FeatureConnectorFactory<MemoryConnectorSettings>
{
    public const string Type = "memory@1";

    public override string ConnectorType => Type;

    /// <remarks>A test fixture: it reads its data file here, at create time, so it needs no load.</remarks>
    /// <exception cref="BindingException">A setting is invalid, or the data file cannot be read: at the setting, with a hint.</exception>
    protected override IFeatureSourceConnector Create(MemoryConnectorSettings settings, BindingContext context)
    {
        const string Records = "the records are a JSON object by entity key, e.g. { \"10042\": { \"i_clk_7d\": 12.0 } }";
        if (settings.Data is not null && settings.DataFile is not null)
        {
            throw new BindingException(settings.DataFile.Pointer, "memory@1 takes 'data' or 'data_file', not both.", "keep the one that holds the records");
        }

        var data = settings.Data ?? (settings.DataFile is { } file ? DataFile.Read(file) : JsonDocument.Parse("{}").RootElement);
        if (data.ValueKind != JsonValueKind.Object)
        {
            throw new BindingException(settings.DataFile?.Pointer ?? "/data", "The records must be a JSON object, by entity key.", Records);
        }

        if (settings.LatencyMs < 0)
        {
            throw new BindingException("/latency_ms", $"'latency_ms' must be 0 or more, got {settings.LatencyMs}.",
                "give the simulated latency of every fetch in milliseconds, or remove it for none");
        }

        return new MemoryFeatureConnector(data, settings.LatencyMs, settings.Fail);
    }
}
