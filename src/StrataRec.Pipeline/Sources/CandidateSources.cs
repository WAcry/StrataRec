using System.Text.Json;
using StrataRec.Core.Bindings;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Values;
using StrataRec.Features.Connectors;

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
/// <remarks>
/// A sink is valid only until the <see cref="ICandidateSource.RetrieveAsync"/> call it was passed to completes: its
/// buffers belong to the request, and candidates added afterwards are ignored. Sources must not keep it.
/// </remarks>
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

/// <summary>
/// The sink an operator passes to <see cref="Operators.OperatorContext.RetrieveAsync"/>: one source's results in rank order,
/// capped at the quota (excess is dropped and counted, design 4.8). The buffers are rented from the request memory at the
/// quota, which the table's capacity already accounts for.
/// </summary>
/// <remarks>
/// <para>Closed by the framework when the source call completes, before the call releases the request memory: a source
/// that adds after completing (which it must not) changes nothing and never writes into returned buffers. Being a
/// framework type lets the call site record what a source returned, and refill it from a recording, for every operator.</para>
/// <para>A call the framework stopped waiting for (its timeout, the node's budget, the caller) abandons its buffer: the
/// source may still be in the middle of an <see cref="Add(long, double)"/>, since closing is only a flag checked before
/// each one, so from then on the buffer reads as empty and nobody reads the arrays the source may be writing. The arrays
/// stay rented until the source completes (the call holds the request memory).</para>
/// </remarks>
public sealed class CandidateBuffer : ICandidateSink
{
    private readonly EntityKey[] _keys;
    private readonly double[] _scores;
    private int _count;
    private int _truncated;
    private bool _closed;
    private bool _abandoned;

    public CandidateBuffer(int limit, RequestMemory memory)
    {
        Limit = limit;
        _keys = memory.Rent<EntityKey>(limit);
        _scores = memory.Rent<double>(limit);
    }

    /// <summary>The most candidates kept; also the <see cref="CandidateQuery.Limit"/> to ask for.</summary>
    public int Limit { get; }

    /// <summary>Candidates added; 0 once the call was abandoned.</summary>
    public int Count => Volatile.Read(ref _abandoned) ? 0 : _count;

    public ReadOnlySpan<EntityKey> Keys => _keys.AsSpan(0, Count);

    /// <summary>Scores of <see cref="Keys"/>; a non-finite score is stored as 0.</summary>
    public ReadOnlySpan<double> Scores => _scores.AsSpan(0, Count);

    /// <summary>Candidates dropped because the buffer was full; 0 once the call was abandoned.</summary>
    public int Truncated => Volatile.Read(ref _abandoned) ? 0 : _truncated;

    public void Add(long entityId, double score) => Add(EntityKey.FromInt64(entityId), score);

    public void Add(string entityId, double score) => Add(EntityKey.FromString(entityId), score);

    internal void Add(EntityKey key, double score)
    {
        if (Volatile.Read(ref _closed))
        {
            return;
        }

        if (_count >= Limit)
        {
            _truncated++;
            return;
        }

        _keys[_count] = key;
        _scores[_count] = double.IsFinite(score) ? score : 0.0;
        _count++;
    }

    internal void Close() => Volatile.Write(ref _closed, true);

    /// <summary>The call was abandoned with the source still running: closes the buffer and hides what it holds.</summary>
    internal void Abandon()
    {
        Volatile.Write(ref _abandoned, true);
        Close();
    }
}

/// <summary>A retrieval backend: ANN index, collaborative filtering, trending lists… (design 1.4). Thread-safe.</summary>
public interface ICandidateSource
{
    /// <summary>Client type and version, e.g. <c>grpc_ann@1</c>.</summary>
    string ClientType { get; }

    ValueTask<CandidateSourceStatus> RetrieveAsync(CandidateQuery query, ICandidateSink sink, CancellationToken cancellationToken);
}

/// <summary>Creates candidate sources for bindings of one client type.</summary>
/// <remarks>
/// The engine creates one source per binding identity and shares it across plans and their concurrent requests, so a
/// source lives as long as some plan uses it. <see cref="Create"/> only reads the settings; a source that must connect
/// first implements <see cref="ILoadable"/>, and one holding resources implements <see cref="IAsyncDisposable"/> or
/// <see cref="IDisposable"/>: the engine disposes it once nothing can call it any more. The whole lifetime contract is on
/// <see cref="BindingContext"/>.
/// </remarks>
public interface ICandidateSourceFactory
{
    string ClientType { get; }

    /// <param name="context">The source's logical name, the binding's own settings, the base directory relative files
    /// resolve against and the host's secret store.</param>
    /// <exception cref="BindingException">The settings are wrong: at the setting's pointer, with a hint.</exception>
    ICandidateSource Create(BindingContext context);
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

    internal static MemoryListCandidateSource Create(Entry[] items, string? byInput, Dictionary<string, Entry[]> lists, int latencyMs, bool fail) =>
        new(items, byInput, lists, latencyMs, fail);

    internal readonly record struct Entry(long Int64Id, string? StringId, double Score);
}

/// <summary>Factory for <c>memory_list@1</c>.</summary>
public sealed class MemoryListCandidateSourceFactory : ICandidateSourceFactory
{
    public const string Type = "memory_list@1";

    public string ClientType => Type;

    private const string Takes = "memory_list@1 takes 'items' (ids, or { \"id\", \"score\" } objects, best first), or 'by_input' with 'lists' " +
                                 "(a list per value of that input, '*' for the others), 'latency_ms' and 'fail', and 'data_file': a JSON file of the same members";

    private static readonly string[] Members = ["items", "by_input", "lists", "latency_ms", "fail"];
    private static readonly string[] Settings = ["data_file", "items", "by_input", "lists", "latency_ms", "fail"];

    /// <remarks>
    /// A test fixture: it reads its data file here, at create time, so it needs no load. Members are checked strictly, in
    /// the binding and in the file: a misspelled one would otherwise be ignored and the list served empty or unchanged.
    /// </remarks>
    /// <exception cref="BindingException">A setting or a member of the data file is unknown or invalid: at the setting, or
    /// at <c>/data_file</c> naming the member in the file, with a hint.</exception>
    public ICandidateSource Create(BindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var members = new Dictionary<string, (JsonElement Value, Where Where)>(StringComparer.Ordinal);
        foreach (var (name, _) in context.Settings)
        {
            if (Array.IndexOf(Settings, name) < 0)
            {
                throw BindingException.Unknown(string.Empty, name, Settings, Takes);
            }
        }

        if (context.Settings.TryGetValue("data_file", out var dataFile))
        {
            var (root, path) = DataFile.Read(dataFile, context.BaseDirectory);
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new BindingException("/data_file", $"'{path}' must hold a JSON object of memory_list@1's members.", Takes);
            }

            foreach (var member in root.EnumerateObject())
            {
                if (Array.IndexOf(Members, member.Name) < 0)
                {
                    var guess = Core.Diagnostics.Spelling.DidYouMean(member.Name, Members);
                    throw new BindingException("/data_file", $"'{path}' has a member '{member.Name}', which memory_list@1 does not take.",
                        guess is null ? Takes : guess + " " + Takes);
                }

                members[member.Name] = (member.Value, new Where(path, "/" + member.Name));
            }
        }

        foreach (var (name, value) in context.Settings)
        {
            if (name != "data_file")
            {
                members[name] = (value, new Where(null, "/" + name)); // the binding's own settings override the file's
            }
        }

        var items = members.TryGetValue("items", out var i) ? List(i.Value, i.Where) : Array.Empty<MemoryListCandidateSource.Entry>();
        string? byInput = null;
        if (members.TryGetValue("by_input", out var b))
        {
            byInput = b.Value.ValueKind == JsonValueKind.String && b.Value.GetString() is { Length: > 0 } input
                ? input
                : throw b.Where.Error(string.Empty, $"'by_input' must name a request input; got {b.Value.GetRawText()}.");
        }

        var lists = new Dictionary<string, MemoryListCandidateSource.Entry[]>(StringComparer.Ordinal);
        if (members.TryGetValue("lists", out var l))
        {
            if (l.Value.ValueKind != JsonValueKind.Object)
            {
                throw l.Where.Error(string.Empty, $"'lists' must be an object of input value to list; got {l.Value.GetRawText()}.");
            }

            foreach (var list in l.Value.EnumerateObject())
            {
                lists[list.Name] = List(list.Value, l.Where.In("/" + BindingContext.EscapePointerToken(list.Name)));
            }
        }

        var latency = 0;
        if (members.TryGetValue("latency_ms", out var t))
        {
            latency = t.Value.ValueKind == JsonValueKind.Number && t.Value.TryGetInt32(out var ms) && ms >= 0
                ? ms
                : throw t.Where.Error(string.Empty, $"'latency_ms' must be a whole number of milliseconds, 0 or more; got {t.Value.GetRawText()}.");
        }

        var fail = false;
        if (members.TryGetValue("fail", out var f))
        {
            fail = f.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? f.Value.GetBoolean()
                : throw f.Where.Error(string.Empty, $"'fail' must be true or false; got {f.Value.GetRawText()}.");
        }

        return MemoryListCandidateSource.Create(items, byInput, lists, latency, fail);
    }

    /// <summary>A list: ids or <c>{"id", "score"}</c> objects, best first; scores default to a descending ramp.</summary>
    private static MemoryListCandidateSource.Entry[] List(JsonElement array, Where where)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw where.Error(string.Empty, $"A list must be an array of ids or {{ \"id\", \"score\" }} objects; got {array.GetRawText()}.");
        }

        var result = new MemoryListCandidateSource.Entry[array.GetArrayLength()];
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            var at = "/" + index;
            var score = 1.0 - (index / (double)Math.Max(1, result.Length));
            var id = item;
            if (item.ValueKind == JsonValueKind.Object)
            {
                id = default;
                foreach (var member in item.EnumerateObject())
                {
                    switch (member.Name)
                    {
                        case "id":
                            id = member.Value;
                            break;
                        case "score":
                            score = member.Value.ValueKind == JsonValueKind.Number && member.Value.TryGetDouble(out var s) && double.IsFinite(s)
                                ? s
                                : throw where.Error(at + "/score", $"A score must be a finite number; got {member.Value.GetRawText()}.");
                            break;
                        default:
                            throw where.Error(at + "/" + BindingContext.EscapePointerToken(member.Name), $"An item has 'id' and 'score', not '{member.Name}'.",
                                Core.Diagnostics.Spelling.DidYouMean(member.Name, ["id", "score"]));
                    }
                }

                if (id.ValueKind == JsonValueKind.Undefined)
                {
                    throw where.Error(at, "An item object needs its 'id'.");
                }
            }

            result[index++] = id.ValueKind switch
            {
                JsonValueKind.String => new MemoryListCandidateSource.Entry(0, id.GetString(), score),
                JsonValueKind.Number when id.TryGetInt64(out var int64) => new MemoryListCandidateSource.Entry(int64, null, score),
                _ => throw where.Error(at, $"An id must be a string or an int64; got {id.GetRawText()}."),
            };
        }

        return result;
    }

    /// <summary>Where a member came from: the binding's setting at <c>Pointer</c>, or the data file (<c>File</c>) at it.</summary>
    private readonly record struct Where(string? File, string Pointer)
    {
        public Where In(string pointer) => this with { Pointer = Pointer + pointer };

        public BindingException Error(string pointer, string message, string? hint = null) => File is null
            ? new BindingException(Pointer + pointer, message, hint)
            : new BindingException("/data_file", $"'{File}' at {Pointer + pointer}: {message}", hint);
    }
}
