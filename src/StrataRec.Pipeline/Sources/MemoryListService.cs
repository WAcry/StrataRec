using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Services;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Connectors;

namespace StrataRec.Pipeline.Sources;

/// <summary>
/// <c>memory_list@1</c>: a fixed candidate list, optionally per value of a request input. For local runs and tests.
/// </summary>
/// <remarks>
/// Settings: <c>"items"</c> — array of ids or <c>{"id": …, "score": …}</c> objects (scores default to a descending
/// ramp); or <c>"by_input": "user_id"</c> with <c>"lists": {"&lt;input value&gt;": [...], "*": [...]}</c>
/// (<c>"*"</c> is the fallback). Also <c>"data_file"</c> to load the same object from a file (settings in the binding
/// override the file's), plus <c>"latency_ms"</c> and <c>"fail"</c> for fault injection.
/// </remarks>
public sealed class MemoryListService : IServiceClient
{
    private readonly Entry[] _items;
    private readonly string? _byInput;
    private readonly Dictionary<string, Entry[]> _lists;
    private readonly int _latencyMs;
    private readonly bool _fail;

    private MemoryListService(Entry[] items, string? byInput, Dictionary<string, Entry[]> lists, int latencyMs, bool fail)
    {
        _items = items;
        _byInput = byInput;
        _lists = lists;
        _latencyMs = latencyMs;
        _fail = fail;
    }

    public async ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse response, CancellationToken cancellationToken)
    {
        if (_latencyMs > 0)
        {
            await Task.Delay(_latencyMs, cancellationToken).ConfigureAwait(false);
        }

        if (_fail)
        {
            return ServiceStatus.Failed("memory_list@1 source is configured to fail.");
        }

        var list = _items;
        if (_byInput is not null)
        {
            var key = call.Request[_byInput];
            var keyText = key.IsMissing ? null : key.Kind == Core.Types.StorageKind.String ? key.AsString() : key.ToString();
            if (keyText is null || !_lists.TryGetValue(keyText, out list!))
            {
                list = _lists.TryGetValue("*", out var fallback) ? fallback : Array.Empty<Entry>();
            }
        }

        var n = Math.Min(call.Limit, list.Length);
        for (var i = 0; i < n; i++)
        {
            if (list[i].StringId is { } s)
            {
                response.AddItem(s, list[i].Score);
            }
            else
            {
                response.AddItem(list[i].Int64Id, list[i].Score);
            }
        }

        return ServiceStatus.Ok;
    }

    internal static MemoryListService Create(Entry[] items, string? byInput, Dictionary<string, Entry[]> lists, int latencyMs, bool fail) =>
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
public sealed class MemoryListServiceFactory : BindingFactory<IServiceClient, MemoryListSettings>
{
    public const string TypeName = "memory_list@1";

    public override string Type => TypeName;

    /// <remarks>A test fixture: it reads its data file here, at create time, so it needs no load.</remarks>
    /// <exception cref="BindingException">A setting or a member of the data file is invalid: at the setting, or at
    /// <c>/data_file</c> naming the place in the file, with a hint.</exception>
    protected override IServiceClient Create(MemoryListSettings settings, BindingContext context)
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

        var lists = new Dictionary<string, MemoryListService.Entry[]>(StringComparer.Ordinal);
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

        return MemoryListService.Create(items, byInput, lists, latency, settings.Fail ?? data?.Fail ?? false);
    }

    /// <summary>A list: ids or <c>{"id", "score"}</c> objects, best first; scores default to a descending ramp.</summary>
    private static MemoryListService.Entry[] Entries(BindingContext context, List<JsonElement> list, string pointer)
    {
        var result = new MemoryListService.Entry[list.Count];
        for (var i = 0; i < list.Count; i++)
        {
            var at = pointer + "/" + i;
            var score = 1.0 - (i / (double)Math.Max(1, result.Length));
            var id = list[i];
            if (id.ValueKind == JsonValueKind.Object)
            {
                var item = BindingSettings.Bind<MemoryListItem>(context, id, at, "an item of " + TypeName);
                (id, score) = (item.Id, item.Score ?? score);
            }

            result[i] = id.ValueKind switch
            {
                JsonValueKind.String => new MemoryListService.Entry(0, id.GetString(), score),
                JsonValueKind.Number when id.TryGetInt64(out var int64) => new MemoryListService.Entry(int64, null, score),
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
