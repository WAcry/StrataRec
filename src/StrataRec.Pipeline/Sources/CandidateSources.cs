using System.Text.Json;
using StrataRec.Core.Values;

namespace StrataRec.Pipeline.Sources;

/// <summary>One retrieval call: which source, how many candidates, and the request it serves.</summary>
public sealed class CandidateQuery
{
    public required string SourceName { get; init; }

    /// <summary>Maximum candidates to return (the source's quota); extra results are truncated and counted.</summary>
    public required int Limit { get; init; }

    public required RequestInputs Inputs { get; init; }

    public required string RequestId { get; init; }

    public long Seed { get; init; }
}

/// <summary>Receives retrieved candidates in rank order.</summary>
public interface ICandidateSink
{
    void Add(long entityId, double score);

    void Add(string entityId, double score);
}

public readonly record struct CandidateSourceStatus(bool Succeeded, string? Error = null)
{
    public static CandidateSourceStatus Ok => new(true);

    public static CandidateSourceStatus Failed(string error) => new(false, error);
}

/// <summary>A retrieval backend: ANN index, collaborative filtering, trending lists… (design 1.4). Thread-safe.</summary>
public interface ICandidateSource
{
    /// <summary>Client type and version, e.g. <c>grpc_ann@1</c>.</summary>
    string ClientType { get; }

    ValueTask<CandidateSourceStatus> RetrieveAsync(CandidateQuery query, ICandidateSink sink, CancellationToken cancellationToken);
}

/// <summary>Creates candidate sources for bindings of one client type.</summary>
public interface ICandidateSourceFactory
{
    string ClientType { get; }

    ICandidateSource Create(string sourceName, IReadOnlyDictionary<string, JsonElement> settings, string? baseDirectory);
}

/// <summary>
/// <c>memory_list@1</c>: a fixed candidate list, optionally per value of a request input. For local runs and tests.
/// </summary>
/// <remarks>
/// Settings: <c>"items"</c> — array of ids or <c>{"id": …, "score": …}</c> objects (scores default to a descending
/// ramp); or <c>"by_input": "user_id"</c> with <c>"lists": {"&lt;input value&gt;": [...], "*": [...]}</c>
/// (<c>"*"</c> is the fallback). Also <c>"data_file"</c> to load the same object from a file (settings in the binding
/// override the file's), plus <c>"latency_ms"</c> and <c>"fail"</c> for fault injection.
/// </remarks>
public sealed class MemoryListCandidateSource : ICandidateSource
{
    private readonly Entry[] _items;
    private readonly string? _byInput;
    private readonly Dictionary<string, Entry[]> _lists;
    private readonly int _latencyMs;
    private readonly bool _fail;

    private MemoryListCandidateSource(Entry[] items, string? byInput, Dictionary<string, Entry[]> lists, int latencyMs, bool fail)
    {
        _items = items;
        _byInput = byInput;
        _lists = lists;
        _latencyMs = latencyMs;
        _fail = fail;
    }

    public string ClientType => MemoryListCandidateSourceFactory.Type;

    public async ValueTask<CandidateSourceStatus> RetrieveAsync(CandidateQuery query, ICandidateSink sink, CancellationToken cancellationToken)
    {
        if (_latencyMs > 0)
        {
            await Task.Delay(_latencyMs, cancellationToken).ConfigureAwait(false);
        }

        if (_fail)
        {
            return CandidateSourceStatus.Failed("memory_list@1 source is configured to fail.");
        }

        var list = _items;
        if (_byInput is not null)
        {
            var key = query.Inputs[_byInput];
            var keyText = key.IsMissing ? null : key.Kind == Core.Types.StorageKind.String ? key.AsString() : key.ToString();
            if (keyText is null || !_lists.TryGetValue(keyText, out list!))
            {
                list = _lists.TryGetValue("*", out var fallback) ? fallback : Array.Empty<Entry>();
            }
        }

        var n = Math.Min(query.Limit, list.Length);
        for (var i = 0; i < n; i++)
        {
            if (list[i].StringId is { } s)
            {
                sink.Add(s, list[i].Score);
            }
            else
            {
                sink.Add(list[i].Int64Id, list[i].Score);
            }
        }

        return CandidateSourceStatus.Ok;
    }

    internal static MemoryListCandidateSource Create(JsonElement settings)
    {
        var items = settings.TryGetProperty("items", out var i) ? ParseList(i) : Array.Empty<Entry>();
        string? byInput = settings.TryGetProperty("by_input", out var b) ? b.GetString() : null;
        var lists = new Dictionary<string, Entry[]>(StringComparer.Ordinal);
        if (settings.TryGetProperty("lists", out var l))
        {
            foreach (var p in l.EnumerateObject())
            {
                lists[p.Name] = ParseList(p.Value);
            }
        }

        var latency = settings.TryGetProperty("latency_ms", out var lat) ? lat.GetInt32() : 0;
        var fail = settings.TryGetProperty("fail", out var f) && f.GetBoolean();
        return new MemoryListCandidateSource(items, byInput, lists, latency, fail);
    }

    private static Entry[] ParseList(JsonElement array)
    {
        var result = new Entry[array.GetArrayLength()];
        var index = 0;
        foreach (var e in array.EnumerateArray())
        {
            var score = 1.0 - (index / (double)Math.Max(1, result.Length));
            var id = e;
            if (e.ValueKind == JsonValueKind.Object)
            {
                id = e.GetProperty("id");
                if (e.TryGetProperty("score", out var s))
                {
                    score = s.GetDouble();
                }
            }

            result[index++] = id.ValueKind == JsonValueKind.String
                ? new Entry(0, id.GetString(), score)
                : new Entry(id.GetInt64(), null, score);
        }

        return result;
    }

    private readonly record struct Entry(long Int64Id, string? StringId, double Score);
}

/// <summary>Factory for <c>memory_list@1</c>.</summary>
public sealed class MemoryListCandidateSourceFactory : ICandidateSourceFactory
{
    public const string Type = "memory_list@1";

    public string ClientType => Type;

    public ICandidateSource Create(string sourceName, IReadOnlyDictionary<string, JsonElement> settings, string? baseDirectory)
    {
        // The data file supplies the list object; settings in the binding itself (latency_ms, fail, ...) override it,
        // so fault injection works the same with or without a data file.
        JsonDocument? fileDoc = null;
        try
        {
            if (settings.TryGetValue("data_file", out var file))
            {
                var path = file.GetString() ?? throw new FormatException($"Candidate source '{sourceName}': data_file must be a string.");
                if (!Path.IsPathRooted(path) && baseDirectory is not null)
                {
                    path = Path.Combine(baseDirectory, path);
                }

                fileDoc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (fileDoc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new FormatException($"Candidate source '{sourceName}': data_file '{path}' must contain a JSON object.");
                }
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                if (fileDoc is not null)
                {
                    foreach (var property in fileDoc.RootElement.EnumerateObject())
                    {
                        if (!settings.ContainsKey(property.Name))
                        {
                            property.WriteTo(writer);
                        }
                    }
                }

                foreach (var (key, value) in settings)
                {
                    if (key != "data_file")
                    {
                        writer.WritePropertyName(key);
                        value.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            using var settingsDoc = JsonDocument.Parse(buffer.ToArray());
            return MemoryListCandidateSource.Create(settingsDoc.RootElement.Clone());
        }
        finally
        {
            fileDoc?.Dispose();
        }
    }
}
