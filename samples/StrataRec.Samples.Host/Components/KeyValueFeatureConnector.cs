using System.Buffers;
using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Features.Connectors;

namespace StrataRec.Samples.Host.Components;

/// <summary>
/// <c>kv_store@1</c>: an in-process key-value feature store standing in for a remote one (a Redis hash, a feature
/// service). Each line of a JSON Lines file is one record keyed by an int64, e.g. <c>{"id": 7, "ctr_7d": 0.04,
/// "category": "sports"}</c>.
/// </summary>
/// <remarks>
/// <para>Binding settings: <see cref="KeyValueSettings"/>, the factory's type argument; everything else in the binding
/// (<c>timeout_ms</c>, <c>max_batch</c>) is the framework's.</para>
/// <list type="bullet">
/// <item>The file is read in <see cref="LoadAsync"/>, once, off the request path; <c>validate</c> never loads
/// it.</item>
/// <item>One instance serves every request of every plan with this binding, concurrently: it is immutable once
/// loaded.</item>
/// <item>No per-row allocation: a fetch copies values from typed arrays into the writer's buffers.</item>
/// <item>Only values the store has are marked present; a value of the wrong kind is a failed fetch, never a silent
/// 0.</item>
/// <item>The token is passed to every await; the engine stops waiting at the timeout whether or not it is
/// honoured.</item>
/// </list>
/// </remarks>
public sealed class KeyValueFeatureConnector : IFeatureSourceConnector, ILoadable
{
    private readonly string _path;
    private readonly string _keyField;
    private readonly int _latencyMs;
    private Store? _store; // set once by LoadAsync; the engine calls nothing else before the load completed

    public KeyValueFeatureConnector(string path, string keyField, int latencyMs)
    {
        _path = path;
        _keyField = keyField;
        _latencyMs = latencyMs;
    }

    public string ConnectorType => KeyValueFeatureConnectorFactory.TypeName;

    /// <summary>
    /// Entities keyed by one int64: a context entity (a user, one key per call) or a candidate entity (an item, many). Asked
    /// once when a plan is compiled, so an entity keyed otherwise (a string, several parts) is a configuration error
    /// (<c>CONNECTOR_KEY</c>) rather than a failed fetch on every request.
    /// </summary>
    public bool Supports(EntityKeyLayout layout) => layout.PartCount == 1 && layout[0].Type == EntityKeyType.Int64;

    public async ValueTask LoadAsync(CancellationToken cancellationToken)
    {
        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(_path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BindingException("/data_file", $"Cannot read '{_path}': {ex.Message}", "check the path; a relative path resolves against the config directory", ex);
        }

        _store = Store.Parse(lines, _keyField);
    }

    public ValueTask<FetchStatus> FetchAsync(
        EntityKeyBatch keys, ReadOnlyMemory<CompiledFieldBinding> fields, IFeatureBatchWriter writer, CancellationToken cancellationToken)
    {
        if (_latencyMs > 0)
        {
            return FetchRemoteAsync(keys, fields, writer, cancellationToken);
        }

        return new ValueTask<FetchStatus>(Serve(keys, fields.Span, writer));
    }

    /// <summary>What a remote store's call looks like: the token goes to the I/O, and nothing is written once it is cancelled.</summary>
    private async ValueTask<FetchStatus> FetchRemoteAsync(
        EntityKeyBatch keys, ReadOnlyMemory<CompiledFieldBinding> fields, IFeatureBatchWriter writer, CancellationToken cancellationToken)
    {
        await Task.Delay(_latencyMs, cancellationToken).ConfigureAwait(false);
        return Serve(keys, fields.Span, writer);
    }

    private FetchStatus Serve(in EntityKeyBatch keys, ReadOnlySpan<CompiledFieldBinding> fields, IFeatureBatchWriter writer)
    {
        var store = _store ?? throw new InvalidOperationException("kv_store@1 was called before it was loaded.");
        var batch = keys.Keys.Span; // one int64 part (Supports): one key per staging row

        // The record of each key, looked up once and shared by every field.
        var records = ArrayPool<int>.Shared.Rent(Math.Max(1, batch.Length));
        try
        {
            for (var i = 0; i < batch.Length; i++)
            {
                records[i] = store.Records.TryGetValue(batch[i].Int64Value, out var record) ? record : -1;
            }

            foreach (var field in fields)
            {
                if (store.Fields.TryGetValue(field.Field, out var column)
                    && !Write(column, field, records.AsSpan(0, batch.Length), writer))
                {
                    return FetchStatus.Failed($"kv_store@1 field '{field.Field}' holds {column.Kind} values, but the catalog reads it as {field.Type}.");
                }
            }

            return FetchStatus.Ok;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(records);
        }
    }

    /// <summary>Copies one field for every key that has it; false when the stored kind cannot be the declared type.</summary>
    private static bool Write(Column column, in CompiledFieldBinding field, ReadOnlySpan<int> records, IFeatureBatchWriter writer)
    {
        var index = field.FieldIndex;
        switch (field.Type.Kind)
        {
            case StorageKind.Float32 when column.Numbers is { } numbers:
            {
                var values = writer.GetValues<float>(index).Span;
                for (var i = 0; i < records.Length; i++)
                {
                    if (records[i] >= 0 && column.Present[records[i]])
                    {
                        values[i] = (float)numbers[records[i]];
                        writer.SetValid(index, i); // after the value is written, so a half-written record is never visible
                    }
                }

                return true;
            }

            case StorageKind.Float64 when column.Numbers is { } numbers:
            {
                var values = writer.GetValues<double>(index).Span;
                for (var i = 0; i < records.Length; i++)
                {
                    if (records[i] >= 0 && column.Present[records[i]])
                    {
                        values[i] = numbers[records[i]];
                        writer.SetValid(index, i);
                    }
                }

                return true;
            }

            case StorageKind.Bool when column.Bools is { } bools:
            {
                var values = writer.GetValues<bool>(index).Span;
                for (var i = 0; i < records.Length; i++)
                {
                    if (records[i] >= 0 && column.Present[records[i]])
                    {
                        values[i] = bools[records[i]];
                        writer.SetValid(index, i);
                    }
                }

                return true;
            }

            case StorageKind.String when column.Strings is { } strings:
                for (var i = 0; i < records.Length; i++)
                {
                    if (records[i] >= 0 && column.Present[records[i]])
                    {
                        writer.SetString(index, i, strings[records[i]]);
                    }
                }

                return true;

            default:
                return false;
        }
    }

    private sealed class Store(Dictionary<long, int> records, Dictionary<string, Column> fields)
    {
        public Dictionary<long, int> Records { get; } = records;

        public Dictionary<string, Column> Fields { get; } = fields;

        public static Store Parse(string[] lines, string keyField)
        {
            var documents = new List<(JsonElement Record, int Line)>(lines.Length);
            for (var i = 0; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(lines[i]);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        throw Bad(i + 1, "a record is a JSON object");
                    }

                    documents.Add((document.RootElement.Clone(), i + 1));
                }
                catch (JsonException ex)
                {
                    throw Bad(i + 1, ex.Message);
                }
            }

            var records = new Dictionary<long, int>(documents.Count);
            var fields = new Dictionary<string, Column>(StringComparer.Ordinal);
            for (var row = 0; row < documents.Count; row++)
            {
                var (record, line) = documents[row];
                // TryGetInt64 throws on anything but a number, so the kind is checked first: a string key ("7") is a load
                // error at its line, like every other mistake in the file, not an exception without a location.
                if (!record.TryGetProperty(keyField, out var key) || key.ValueKind != JsonValueKind.Number || !key.TryGetInt64(out var id))
                {
                    throw Bad(line, $"the record has no int64 '{keyField}' (an integer such as {{\"{keyField}\": 7}})");
                }

                if (!records.TryAdd(id, row))
                {
                    throw Bad(line, $"key {id} occurs twice");
                }

                foreach (var property in record.EnumerateObject())
                {
                    if (property.Name == keyField || property.Value.ValueKind == JsonValueKind.Null)
                    {
                        continue;
                    }

                    if (!fields.TryGetValue(property.Name, out var column))
                    {
                        fields[property.Name] = column = new Column(documents.Count);
                    }

                    if (!column.TrySet(row, property.Value))
                    {
                        throw Bad(line, $"field '{property.Name}' is {property.Value.ValueKind} here but {column.Kind} in earlier records");
                    }
                }
            }

            return new Store(records, fields);
        }

        private static BindingException Bad(int line, string reason) =>
            new("/data_file", $"Line {line}: {reason}.", "each line is one JSON object with an int64 key and fields of one kind each (number, string or bool)");
    }

    private sealed class Column(int count)
    {
        public bool[] Present { get; } = new bool[count];

        public double[]? Numbers { get; private set; }

        public string[]? Strings { get; private set; }

        public bool[]? Bools { get; private set; }

        public string Kind => Numbers is not null ? "number" : Strings is not null ? "string" : "bool";

        public bool TrySet(int row, JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Number when Strings is null && Bools is null:
                    (Numbers ??= new double[Present.Length])[row] = value.GetDouble();
                    break;
                case JsonValueKind.String when Numbers is null && Bools is null:
                    (Strings ??= new string[Present.Length])[row] = value.GetString()!;
                    break;
                case JsonValueKind.True or JsonValueKind.False when Numbers is null && Strings is null:
                    (Bools ??= new bool[Present.Length])[row] = value.GetBoolean();
                    break;
                default:
                    return false;
            }

            Present[row] = true;
            return true;
        }
    }
}

/// <summary>
/// The settings of <c>kv_store@1</c>: the members of a source's binding besides the framework's own (<c>connector</c>,
/// <c>timeout_ms</c>, <c>max_batch</c>, ...). Bound strictly by the engine; the bindings schema is generated from this type.
/// </summary>
public sealed class KeyValueSettings
{
    /// <summary>
    /// The JSON Lines file of the records: a path relative to the configuration directory, an absolute path or a file:// URI.
    /// Read when the connector loads.
    /// </summary>
    public required LocalFile DataFile { get; init; }

    /// <summary>The member that holds each record's int64 key (default <c>id</c>).</summary>
    public string KeyField { get; init; } = "id";

    /// <summary>A simulated network round trip, in milliseconds (default 0).</summary>
    public int LatencyMs { get; init; }
}

/// <summary>Creates <c>kv_store@1</c> connectors; registered in <see cref="SampleEnvironment"/>.</summary>
public sealed class KeyValueFeatureConnectorFactory : BindingFactory<IFeatureSourceConnector, KeyValueSettings>
{
    public const string TypeName = "kv_store@1";

    public override string Type => TypeName;

    /// <summary>
    /// Checks the settings only: <c>Create</c> runs for every compile, including <c>validate</c>, and may run on several
    /// threads at once. A wrong setting is a <see cref="BindingException"/> at its pointer, with a hint.
    /// </summary>
    protected override IFeatureSourceConnector Create(KeyValueSettings settings, BindingContext context)
    {
        if (settings.KeyField.Length == 0)
        {
            throw new BindingException("/key_field", "'key_field' must not be empty.", "name the member that holds each record's int64 key, e.g. \"id\"");
        }

        if (settings.LatencyMs < 0)
        {
            throw new BindingException("/latency_ms", "'latency_ms' must not be negative.", "remove it, or give the simulated round trip in milliseconds");
        }

        return new KeyValueFeatureConnector(settings.DataFile.Path, settings.KeyField, settings.LatencyMs);
    }
}
