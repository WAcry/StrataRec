using System.Text.Json;
using StrataRec.Core.Bindings;
using StrataRec.Core.Types;
using StrataRec.Pipeline.Sources;

namespace StrataRec.Samples.Host.Components;

/// <summary>
/// <c>trending@1</c>: what is trending, overall or in the request's region, from an in-memory list. A production source
/// would refresh the list from a stream job in the background; the request path would stay the same.
/// </summary>
/// <remarks>
/// <para>Binding settings: <c>items</c> (required), each <c>{"id": int64, "score": number, "region": "us"}</c> with an
/// optional region, and <c>region_input</c> (optional): the request input that picks a region's list. A request without
/// that input, or with a region that has no list, gets the overall list. That is the explicit meaning of a missing region;
/// no region is ever made up.</para>
/// <para>The rules of the contract, and how this source keeps them:</para>
/// <list type="bullet">
/// <item>The lists are built and sorted once, from the settings, when the source is created (CPU only, no I/O, so no
/// <see cref="ILoadable"/> is needed). Ties rank by id ascending, so the order is deterministic.</item>
/// <item>A retrieval is called concurrently by every request: it reads immutable arrays and adds candidates to the sink in
/// rank order, at most <see cref="CandidateQuery.Limit"/>, with no allocation. It answers synchronously, so the engine's
/// bounded wait allocates no task either.</item>
/// <item>The sink belongs to this call only: nothing may keep it or add to it after returning.</item>
/// <item>A failure is a <see cref="CandidateSourceStatus.Failed"/> with a reason; the recall node then reports the source
/// failed (and degrades, or fails if every source failed).</item>
/// </list>
/// </remarks>
public sealed class TrendingCandidateSource : ICandidateSource
{
    private readonly Entry[] _overall;
    private readonly Dictionary<string, Entry[]> _byRegion;
    private readonly string? _regionInput;

    private TrendingCandidateSource(Entry[] overall, Dictionary<string, Entry[]> byRegion, string? regionInput)
    {
        _overall = overall;
        _byRegion = byRegion;
        _regionInput = regionInput;
    }

    public string ClientType => TrendingCandidateSourceFactory.Type;

    public ValueTask<CandidateSourceStatus> RetrieveAsync(CandidateQuery query, ICandidateSink sink, CancellationToken cancellationToken)
    {
        // A request already cut off gets nothing; a source doing I/O would pass the token to it instead.
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<CandidateSourceStatus>(cancellationToken);
        }

        var list = _overall;
        if (_regionInput is not null)
        {
            var region = query.Inputs[_regionInput]; // ScalarValue.Missing when the request has no region
            if (region.Kind == StorageKind.String && _byRegion.TryGetValue(region.AsString(), out var regional))
            {
                list = regional;
            }
        }

        var count = Math.Min(query.Limit, list.Length);
        for (var i = 0; i < count; i++)
        {
            sink.Add(list[i].Id, list[i].Score);
        }

        return new ValueTask<CandidateSourceStatus>(CandidateSourceStatus.Ok);
    }

    internal static TrendingCandidateSource Create(List<(long Id, double Score, string? Region)> items, string? regionInput)
    {
        var overall = new List<Entry>(items.Count);
        var regional = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        foreach (var (id, score, region) in items)
        {
            overall.Add(new Entry(id, score));
            if (region is not null)
            {
                if (!regional.TryGetValue(region, out var list))
                {
                    regional[region] = list = new List<Entry>();
                }

                list.Add(new Entry(id, score));
            }
        }

        var byRegion = new Dictionary<string, Entry[]>(StringComparer.Ordinal);
        foreach (var (region, list) in regional)
        {
            byRegion[region] = Ranked(list);
        }

        return new TrendingCandidateSource(Ranked(overall), byRegion, regionInput);
    }

    private static Entry[] Ranked(List<Entry> entries)
    {
        var ranked = entries.ToArray();
        Array.Sort(ranked, static (a, b) => a.Score != b.Score ? b.Score.CompareTo(a.Score) : a.Id.CompareTo(b.Id));
        return ranked;
    }

    private readonly record struct Entry(long Id, double Score);
}

/// <summary>Creates <c>trending@1</c> sources; registered in <see cref="SampleEnvironment"/>.</summary>
public sealed class TrendingCandidateSourceFactory : ICandidateSourceFactory
{
    public const string Type = "trending@1";

    public string ClientType => Type;

    /// <summary>Reads and checks the settings; a wrong one is a <see cref="BindingException"/> at its pointer, with a hint.</summary>
    public ICandidateSource Create(BindingContext context)
    {
        string? regionInput = null;
        List<(long, double, string?)>? items = null;
        foreach (var (name, value) in context.Settings)
        {
            switch (name)
            {
                case "region_input":
                    regionInput = value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } input
                        ? input
                        : throw new BindingException("/region_input", "'region_input' must name a request input.", "e.g. \"region\", a string input of the pipeline's request_schema");
                    break;
                case "items":
                    items = ReadItems(value);
                    break;
                default:
                    throw new BindingException("/" + BindingContext.EscapePointerToken(name), $"trending@1 has no setting '{name}'.", "its settings are items and region_input");
            }
        }

        return TrendingCandidateSource.Create(
            items ?? throw new BindingException("/items", "'items' is required.", "list the trending items: [{ \"id\": 7, \"score\": 0.9, \"region\": \"us\" }]"),
            regionInput);
    }

    private static List<(long, double, string?)> ReadItems(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new BindingException("/items", "'items' must be an array.", "e.g. [{ \"id\": 7, \"score\": 0.9, \"region\": \"us\" }]");
        }

        var items = new List<(long, double, string?)>(value.GetArrayLength());
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            var pointer = "/items/" + index++;
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var key)
                || !item.TryGetProperty("score", out var score) || score.ValueKind != JsonValueKind.Number || !double.IsFinite(score.GetDouble()))
            {
                throw new BindingException(pointer, "An item needs an int64 'id' and a finite 'score'.", "e.g. { \"id\": 7, \"score\": 0.9, \"region\": \"us\" }");
            }

            string? region = null;
            if (item.TryGetProperty("region", out var r) && r.ValueKind != JsonValueKind.Null)
            {
                region = r.ValueKind == JsonValueKind.String ? r.GetString() : throw new BindingException(pointer + "/region", "'region' must be a string.");
            }

            items.Add((key, score.GetDouble(), region));
        }

        return items;
    }
}
