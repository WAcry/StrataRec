using System.Buffers;
using System.Globalization;
using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;

namespace StrataRec.Features.Connectors;

/// <summary>
/// <c>memory@1</c>: serves features from an in-memory JSON document. For local runs, tests, replays and samples.
/// </summary>
/// <remarks>
/// Settings: either <c>"data"</c> (inline object) or <c>"data_file"</c> (path relative to the config root), plus optional
/// <c>"latency_ms"</c> and <c>"fail": true</c> for fault injection. Data layout: one object level per key part, in the
/// order the entity declares its key, then the fields:
/// <code>
/// { "10042": { "i_clk_7d": 12.0, "i_author_id": 7 } }            // one part: key → fields
/// { "10042": { "US": { "boost": 1.2 } } }                          // [col.entity_id, ctx.request.country]: item → country → fields
/// </code>
/// Key parts are JSON property names (int64 parts in decimal). Absent keys or fields are reported as missing. Every key
/// layout is supported.
/// </remarks>
public sealed class MemoryFeatureConnector : IFeatureSourceConnector
{
    private readonly Node _root;
    private readonly int _latencyMs;
    private readonly bool _fail;

    public MemoryFeatureConnector(JsonElement data, int latencyMs = 0, bool fail = false)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("memory@1 data must be a JSON object keyed by entity key.");
        }

        _root = new Node(data.Clone());
        _root.Index(); // the top level now, so a one-part lookup never builds anything
        _latencyMs = latencyMs;
        _fail = fail;
    }

    public string ConnectorType => MemoryFeatureConnectorFactory.TypeName;

    /// <summary>Every layout: the data nests one level per part.</summary>
    public bool Supports(EntityKeyLayout layout) => true;

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
        if (keys.PartCount == 1 && !keys.IsShared(0))
        {
            var one = keys.Part(0).Span;
            for (var i = 0; i < one.Length; i++)
            {
                if (_root.TryGet(one[i], out var record))
                {
                    WriteRecord(record.Value, i, fields.Span, writer);
                }
            }

            return FetchStatus.Ok;
        }

        for (var i = 0; i < keys.Count; i++)
        {
            var node = _root;
            var found = true;
            for (var p = 0; p < keys.PartCount && found; p++)
            {
                found = node.TryGet(keys.Key(i, p), out node);
            }

            if (found)
            {
                WriteRecord(node.Value, i, fields.Span, writer);
            }
        }

        return FetchStatus.Ok;
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

        // A value is marked present only after it decoded: a record that throws half-way exposes no field it did not write.
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
                case StorageKind.String: writer.SetString(field.FieldIndex, keyIndex, v.GetString()!); break;
                case StorageKind.List:
                    WriteList(v, keyIndex, field, writer);
                    break;
            }
        }
    }

    /// <summary>A list value, decoded straight to the field's element storage into a pooled buffer, which can go back
    /// right away: the writer copies.</summary>
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

    /// <summary>
    /// Indexed on first use, by name and, for canonical int64 names, by <c>long</c>; later duplicates win, as
    /// <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/> resolves them.
    /// </summary>
    private sealed class Node(JsonElement value)
    {
        private Levels? _levels;

        public JsonElement Value { get; } = value;

        public bool TryGet(in EntityKey key, out Node child)
        {
            var levels = Index();
            if (levels is null)
            {
                child = null!;
                return false;
            }

            return key.StringValue is { } name ? levels.ByName.TryGetValue(name, out child!) : levels.ByInt64.TryGetValue(key.Int64Value, out child!);
        }

        /// <summary>The node's properties, indexed once (concurrent first uses may both build; one index wins, the same).</summary>
        public Levels? Index()
        {
            if (Volatile.Read(ref _levels) is { } built || Value.ValueKind != JsonValueKind.Object)
            {
                return _levels;
            }

            var byName = new Dictionary<string, Node>(StringComparer.Ordinal);
            var byInt64 = new Dictionary<long, Node>();
            foreach (var property in Value.EnumerateObject())
            {
                var child = new Node(property.Value);
                byName[property.Name] = child;
                if (TryParseCanonicalInt64(property.Name, out var id))
                {
                    byInt64[id] = child;
                }
            }

            Interlocked.CompareExchange(ref _levels, new Levels(byName, byInt64), null);
            return _levels;
        }
    }

    private sealed record Levels(Dictionary<string, Node> ByName, Dictionary<long, Node> ByInt64);
}

/// <summary>Settings of <c>memory@1</c>.</summary>
public sealed class MemoryConnectorSettings
{
    /// <summary>
    /// The records, by entity key: <c>{ "10042": { "i_clk_7d": 12.0 } }</c>; for a key of several parts, one object level per
    /// part in the entity's key order, e.g. item, then country, then fields. Not with <c>data_file</c>; without either, there
    /// are none.
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
public sealed class MemoryFeatureConnectorFactory : BindingFactory<IFeatureSourceConnector, MemoryConnectorSettings>
{
    public const string TypeName = "memory@1";

    public override string Type => TypeName;

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
