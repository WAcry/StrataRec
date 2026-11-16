using System.Text.Json;
using StrataRec.Artifacts.Model;
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

    /// <summary>
    /// The C# type the binding's own settings bind to (<see cref="BindingSettings"/>): the single source of their JSON
    /// Schema (design 9.1). <see cref="CandidateSourceFactory{TSettings}"/> binds them to it before its <c>Create</c> sees
    /// them; <see cref="NoSettings"/> for a source that takes none.
    /// </summary>
    Type SettingsType { get; }

    /// <param name="context">The source's logical name, the binding's own settings, the base directory relative files
    /// resolve against and the host's secret store.</param>
    /// <exception cref="BindingException">The settings are wrong: at the setting's pointer, with a hint.</exception>
    ICandidateSource Create(BindingContext context);
}

/// <summary>
/// The base of a candidate source factory: it declares the type of its settings once, as its type argument, and gets
/// them bound to it, so the type the schema describes is the type that binds, and the factory parses no JSON.
/// </summary>
/// <typeparam name="TSettings">The settings type: snake_case members, documented for the schema, required ones
/// <c>required</c>; <see cref="LocalFile"/> and <see cref="SecretReference"/> for files and credentials.</typeparam>
public abstract class CandidateSourceFactory<TSettings> : ICandidateSourceFactory
    where TSettings : class
{
    public abstract string ClientType { get; }

    public Type SettingsType => typeof(TSettings);

    /// <summary>Binds the settings strictly (<see cref="BindingSettings"/>) and creates the source from them.</summary>
    /// <exception cref="BindingException">The settings are wrong: at the setting's pointer, with a hint.</exception>
    public ICandidateSource Create(BindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Create(BindingSettings.Bind<TSettings>(context, ClientType), context);
    }

    /// <summary>Creates the source: checks what the settings type cannot say, and reads nothing else (see <see cref="BindingContext"/>).</summary>
    /// <exception cref="BindingException">A setting is wrong: at its pointer, with a hint.</exception>
    protected abstract ICandidateSource Create(TSettings settings, BindingContext context);
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

/// <summary>The members of <c>memory_list@1</c>'s settings that its data file may hold too.</summary>
public class MemoryListData
{
    /// <summary>
    /// The list, best first: ids (strings or int64s) or <c>{ "id": …, "score": … }</c> objects; scores default to a
    /// descending ramp.
    /// </summary>
    public List<JsonElement>? Items { get; init; }

    /// <summary>The request input whose value picks the list of <c>lists</c> a request gets.</summary>
    public string? ByInput { get; init; }

    /// <summary>A list per value of <c>by_input</c>, each like <c>items</c>; <c>"*"</c> is the list of every other value.</summary>
    public Dictionary<string, List<JsonElement>>? Lists { get; init; }

    /// <summary>Simulated latency of every retrieval, in milliseconds (default 0).</summary>
    public int? LatencyMs { get; init; }

    /// <summary>Fail every retrieval (fault injection).</summary>
    public bool? Fail { get; init; }
}

/// <summary>Settings of <c>memory_list@1</c>.</summary>
public sealed class MemoryListSettings : MemoryListData
{
    /// <summary>
    /// A JSON file of the other members: a path relative to the configuration directory, an absolute path or a file:// URI.
    /// A member the binding has too is the binding's.
    /// </summary>
    public LocalFile? DataFile { get; init; }
}

/// <summary>An item of a <c>memory_list@1</c> list written as an object.</summary>
public sealed class MemoryListItem
{
    /// <summary>The candidate's id: a string or an int64.</summary>
    public required JsonElement Id { get; init; }

    /// <summary>Its score; without one, its place on the list's descending ramp.</summary>
    public double? Score { get; init; }
}

/// <summary>Factory for <c>memory_list@1</c>.</summary>
public sealed class MemoryListCandidateSourceFactory : CandidateSourceFactory<MemoryListSettings>
{
    public const string Type = "memory_list@1";

    public override string ClientType => Type;

    /// <remarks>A test fixture: it reads its data file here, at create time, so it needs no load.</remarks>
    /// <exception cref="BindingException">A setting or a member of the data file is invalid: at the setting, or at
    /// <c>/data_file</c> naming the place in the file, with a hint.</exception>
    protected override ICandidateSource Create(MemoryListSettings settings, BindingContext context)
    {
        var file = settings.DataFile;
        var data = file is null ? null : InFile(file, () => BindingSettings.Bind<MemoryListData>(context, DataFile.Read(file), string.Empty, "the file"));

        // A member the binding has is the binding's; else the file's.
        var items = settings.Items is { } own
            ? Entries(context, own, "/items")
            : data?.Items is { } fromFile ? InFile(file!, () => Entries(context, fromFile, "/items")) : [];
        var byInput = settings.ByInput ?? data?.ByInput;
        if (byInput is { Length: 0 })
        {
            throw In(settings.ByInput is null ? file : null, new BindingException("/by_input", "'by_input' must name a request input.",
                "give the request input whose value picks the list, e.g. \"by_input\": \"user_id\""));
        }

        var lists = new Dictionary<string, MemoryListCandidateSource.Entry[]>(StringComparer.Ordinal);
        var listsFrom = settings.Lists is null ? file : null;
        foreach (var (value, list) in settings.Lists ?? data?.Lists ?? [])
        {
            lists[value] = InFile(listsFrom, () => Entries(context, list, "/lists/" + BindingContext.EscapePointerToken(value)));
        }

        var latency = settings.LatencyMs ?? data?.LatencyMs ?? 0;
        if (latency < 0)
        {
            throw In(settings.LatencyMs is null ? file : null, new BindingException("/latency_ms", $"'latency_ms' must be 0 or more, got {latency}.",
                "give the simulated latency of every retrieval in milliseconds, or remove it for none"));
        }

        return MemoryListCandidateSource.Create(items, byInput, lists, latency, settings.Fail ?? data?.Fail ?? false);
    }

    /// <summary>A list: ids or <c>{"id", "score"}</c> objects, best first; scores default to a descending ramp.</summary>
    private static MemoryListCandidateSource.Entry[] Entries(BindingContext context, List<JsonElement> list, string pointer)
    {
        var result = new MemoryListCandidateSource.Entry[list.Count];
        for (var i = 0; i < list.Count; i++)
        {
            var at = pointer + "/" + i;
            var score = 1.0 - (i / (double)Math.Max(1, result.Length));
            var id = list[i];
            if (id.ValueKind == JsonValueKind.Object)
            {
                var item = BindingSettings.Bind<MemoryListItem>(context, id, at, "an item of " + Type);
                (id, score) = (item.Id, item.Score ?? score);
            }

            result[i] = id.ValueKind switch
            {
                JsonValueKind.String => new MemoryListCandidateSource.Entry(0, id.GetString(), score),
                JsonValueKind.Number when id.TryGetInt64(out var int64) => new MemoryListCandidateSource.Entry(int64, null, score),
                _ => throw new BindingException(at, $"An id must be a string or an int64; got {id.GetRawText()}."),
            };
        }

        return result;
    }

    /// <summary>What <paramref name="read"/> finds wrong in the data file, reported at its setting, naming the place in the file.</summary>
    private static T InFile<T>(LocalFile? file, Func<T> read)
    {
        try
        {
            return read();
        }
        catch (BindingException ex) when (file is not null)
        {
            throw In(file, ex);
        }
    }

    /// <summary>An error in the data file <paramref name="file"/> (at a pointer within it), as an error of its setting; as it is when null.</summary>
    private static BindingException In(LocalFile? file, BindingException error) => file is null
        ? error
        : new BindingException(file.Pointer, $"'{file.Path}' at {(error.Pointer.Length == 0 ? "its root" : error.Pointer)}: {error.Reason}", error.Hint);
}
