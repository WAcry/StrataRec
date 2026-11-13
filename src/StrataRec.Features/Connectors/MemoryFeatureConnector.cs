using System.Text.Json;
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
/// </remarks>
public sealed class MemoryFeatureConnector : IFeatureSourceConnector
{
    private readonly Dictionary<string, JsonElement> _rows;
    private readonly int _latencyMs;
    private readonly bool _fail;

    public MemoryFeatureConnector(JsonElement data, int latencyMs = 0, bool fail = false)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("memory@1 data must be a JSON object keyed by entity key.");
        }

        _rows = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in data.EnumerateObject())
        {
            _rows[property.Name] = property.Value.Clone();
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
                if (_rows.TryGetValue(keys.ContextKey.ToString(), out var single))
                {
                    WriteRecord(single, 0, fields.Span, writer);
                }

                break;
            case KeyShape.Many:
                var many = keys.CandidateKeys.Span;
                for (var i = 0; i < many.Length; i++)
                {
                    if (_rows.TryGetValue(many[i].ToString(), out var record))
                    {
                        WriteRecord(record, i, fields.Span, writer);
                    }
                }

                break;
            case KeyShape.Cross:
                if (_rows.TryGetValue(keys.ContextKey.ToString(), out var byCandidate) && byCandidate.ValueKind == JsonValueKind.Object)
                {
                    var cand = keys.CandidateKeys.Span;
                    for (var i = 0; i < cand.Length; i++)
                    {
                        if (byCandidate.TryGetProperty(cand[i].ToString(), out var record))
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
                    writer.GetValues<int>(field.FieldIndex).Span[keyIndex] = v.GetInt32();
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
}

/// <summary>Factory for <c>memory@1</c>.</summary>
public sealed class MemoryFeatureConnectorFactory : IFeatureConnectorFactory
{
    public const string Type = "memory@1";

    public string ConnectorType => Type;

    public IFeatureSourceConnector Create(string sourceName, IReadOnlyDictionary<string, JsonElement> settings, string? baseDirectory)
    {
        JsonElement data;
        if (settings.TryGetValue("data", out var inline))
        {
            data = inline;
        }
        else if (settings.TryGetValue("data_file", out var file))
        {
            var path = file.GetString() ?? throw new FormatException($"Source '{sourceName}': data_file must be a string.");
            if (!Path.IsPathRooted(path) && baseDirectory is not null)
            {
                path = Path.Combine(baseDirectory, path);
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            data = doc.RootElement.Clone();
        }
        else
        {
            data = JsonDocument.Parse("{}").RootElement;
        }

        var latency = settings.TryGetValue("latency_ms", out var l) ? l.GetInt32() : 0;
        var fail = settings.TryGetValue("fail", out var f) && f.GetBoolean();
        return new MemoryFeatureConnector(data, latency, fail);
    }
}
