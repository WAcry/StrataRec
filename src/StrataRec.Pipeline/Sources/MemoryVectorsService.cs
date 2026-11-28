using System.Buffers;
using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Services;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Connectors;

namespace StrataRec.Pipeline.Sources;

/// <summary>
/// <c>memory_vectors@1</c>: an in-memory vector index for local runs and tests, the stand-in for a two-tower retrieval
/// service. It scores every item by the dot product of its vector with the query vector, a value of the source's query
/// (<c>MultiSourceRecall@1</c>'s <c>queries</c>) such as the user's embedding from the feature catalog, and
/// returns the best, ties by id (int64 ids before strings). A pipeline written against it runs unchanged against the real
/// service, which gets the same query.
/// </summary>
/// <remarks>
/// <para>A request whose query vector is missing (a user without an embedding and no default) gets no candidates: the
/// node's other sources answer it; so does an index without items. A query without the configured value, a value that is
/// not a list of float32s or float64s (the declared type of the expression it is, e.g. <c>list&lt;float32,64&gt;</c>), a
/// vector of another length than the items', or a score that is not finite (vectors too large for a float64 dot product)
/// is a failed call, naming them. A float32 vector is scored in float64, as a float64 one.</para>
/// <para>An exhaustive scan, for the few thousand items of a fixture.</para>
/// </remarks>
public sealed class MemoryVectorsService : IServiceClient
{
    private readonly long[] _int64Ids;
    private readonly string?[] _stringIds;
    private readonly double[] _vectors; // item i's vector at [i * dimensions, (i + 1) * dimensions)
    private readonly int _dimensions;
    private readonly string _query;
    private readonly double? _minScore;
    private readonly int _latencyMs;
    private readonly bool _fail;

    private MemoryVectorsService(
        long[] int64Ids, string?[] stringIds, double[] vectors, int dimensions, string query, double? minScore, int latencyMs, bool fail)
    {
        (_int64Ids, _stringIds, _vectors, _dimensions) = (int64Ids, stringIds, vectors, dimensions);
        (_query, _minScore, _latencyMs, _fail) = (query, minScore, latencyMs, fail);
    }

    public async ValueTask<ServiceStatus> CallAsync(ServiceCall call, IServiceResponse response, CancellationToken cancellationToken)
    {
        if (_latencyMs > 0)
        {
            await Task.Delay(_latencyMs, cancellationToken).ConfigureAwait(false);
        }

        if (_fail)
        {
            return ServiceStatus.Failed(MemoryVectorsServiceFactory.TypeName + " source is configured to fail.");
        }

        if (!call.TryGetInput(_query, out var input))
        {
            return ServiceStatus.Failed(
                $"{MemoryVectorsServiceFactory.TypeName} reads the query vector '{_query}', and the query of '{call.Service}' has no such value; " +
                $"give it one in the recall node's queries, e.g. \"queries\": {{ \"{call.Service}\": {{ \"{_query}\": \"ctx.user.embedding\" }} }}.");
        }

        // The query's type first, so a wrong one fails whether or not this request has a value.
        if (input.ElementType is not (TensorElementType.Float32 or TensorElementType.Float64) || input.Shape.Length != 1)
        {
            return ServiceStatus.Failed(
                $"The query vector '{_query}' must be a list of floats; it is {TensorValues.Name(input.ElementType)} of shape [{string.Join(", ", input.Shape.ToArray())}].");
        }

        if (input.Shape[0] > 0 && _dimensions > 0 && input.Shape[0] != _dimensions)
        {
            return ServiceStatus.Failed($"The query vector '{_query}' has {input.Shape[0]} elements; the items' vectors have {_dimensions}.");
        }

        if (input.IsMissing || _dimensions == 0)
        {
            return ServiceStatus.Ok;
        }

        // The shape says how many elements the vector has; its data may be a longer buffer.
        if (input.Shape[0] != _dimensions)
        {
            return ServiceStatus.Failed($"The query vector '{_query}' has {input.Shape[0]} elements; the items' vectors have {_dimensions}.");
        }

        if (input.ElementType == TensorElementType.Float64)
        {
            return Search(input.Float64.Span, call.Limit, response);
        }

        var widened = ArrayPool<double>.Shared.Rent(_dimensions);
        try
        {
            var vector = input.Float32.Span;
            for (var d = 0; d < _dimensions; d++)
            {
                widened[d] = vector[d];
            }

            return Search(widened.AsSpan(0, _dimensions), call.Limit, response);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(widened);
        }
    }

    private ServiceStatus Search(ReadOnlySpan<double> vector, int limit, IServiceResponse sink)
    {
        var count = _int64Ids.Length;
        var scores = ArrayPool<double>.Shared.Rent(Math.Max(1, count));
        var order = ArrayPool<int>.Shared.Rent(Math.Max(1, count));
        try
        {
            var kept = 0;
            for (var i = 0; i < count; i++)
            {
                double score = 0;
                var item = _vectors.AsSpan(i * _dimensions, _dimensions);
                for (var d = 0; d < item.Length; d++)
                {
                    score += item[d] * vector[d];
                }

                if (!double.IsFinite(score))
                {
                    return ServiceStatus.Failed(
                        $"The dot product of item {(_stringIds[i] ?? _int64Ids[i].ToString(System.Globalization.CultureInfo.InvariantCulture))} with the query vector " +
                        $"'{_query}' is not finite; scale the vectors so it fits a float64.");
                }

                if (_minScore is { } min && !(score >= min))
                {
                    continue;
                }

                scores[i] = score;
                order[kept++] = i;
            }

            var ranked = order.AsSpan(0, kept);
            ranked.Sort(new Ranking(scores, _int64Ids, _stringIds));
            var n = Math.Min(limit, kept);
            for (var r = 0; r < n; r++)
            {
                var i = ranked[r];
                if (_stringIds[i] is { } id)
                {
                    sink.AddItem(id, scores[i]);
                }
                else
                {
                    sink.AddItem(_int64Ids[i], scores[i]);
                }
            }

            return ServiceStatus.Ok;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(order);
            ArrayPool<double>.Shared.Return(scores);
        }
    }

    internal static MemoryVectorsService Create(
        long[] int64Ids, string?[] stringIds, double[] vectors, int dimensions, string query, double? minScore, int latencyMs, bool fail) =>
        new(int64Ids, stringIds, vectors, dimensions, query, minScore, latencyMs, fail);

    private readonly struct Ranking(double[] scores, long[] int64Ids, string?[] stringIds) : IComparer<int>
    {
        public int Compare(int a, int b)
        {
            var byScore = scores[b].CompareTo(scores[a]);
            if (byScore != 0)
            {
                return byScore;
            }

            return (stringIds[a], stringIds[b]) switch
            {
                (null, null) => int64Ids[a].CompareTo(int64Ids[b]),
                (null, _) => -1,
                (_, null) => 1,
                var (x, y) => string.CompareOrdinal(x, y),
            };
        }
    }
}

/// <summary>The members of <c>memory_vectors@1</c>'s settings that its data file may hold too.</summary>
public class MemoryVectorsData
{
    /// <summary>The indexed items, each <c>{ "id": …, "vector": [ … ] }</c>; every vector of one length.</summary>
    public List<MemoryVectorsItem>? Items { get; init; }
}

/// <summary>Settings of <c>memory_vectors@1</c>.</summary>
public sealed class MemoryVectorsSettings : MemoryVectorsData
{
    /// <summary>
    /// The query value holding the query vector (<c>MultiSourceRecall@1</c>'s <c>queries</c>), such as
    /// <c>"user_embedding"</c>: a list of floats of the items' vectors' length.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>The least score an item is returned with; without it, every item may be.</summary>
    public double? MinScore { get; init; }

    /// <summary>
    /// A JSON file of the items (<c>{ "items": [ … ] }</c>): a path relative to the configuration directory, an absolute path
    /// or a file:// URI. Items in the binding are the binding's.
    /// </summary>
    public LocalFile? DataFile { get; init; }

    /// <summary>Simulated latency of every retrieval, in milliseconds (default 0).</summary>
    public int? LatencyMs { get; init; }

    /// <summary>Fail every retrieval (fault injection).</summary>
    public bool? Fail { get; init; }
}

/// <summary>An item of a <c>memory_vectors@1</c> index.</summary>
public sealed class MemoryVectorsItem
{
    /// <summary>The candidate's id: a string or an int64.</summary>
    public required JsonElement Id { get; init; }

    /// <summary>Its vector, scored by its dot product with the query vector.</summary>
    public required List<double> Vector { get; init; }
}

/// <summary>Factory for <c>memory_vectors@1</c>.</summary>
public sealed class MemoryVectorsServiceFactory : BindingFactory<IServiceClient, MemoryVectorsSettings>
{
    public const string TypeName = "memory_vectors@1";

    public override string Type => TypeName;

    /// <remarks>A test fixture: it reads its data file here, at create time, so it needs no load.</remarks>
    /// <exception cref="BindingException">A setting or an item is invalid: at the setting, or at <c>/data_file</c> naming
    /// the place in the file, with a hint.</exception>
    protected override IServiceClient Create(MemoryVectorsSettings settings, BindingContext context)
    {
        if (settings.Query.Length == 0)
        {
            throw new BindingException("/query", "'query' must name a value of the source's query.",
                "give the query value holding the query vector, e.g. \"query\": \"user_embedding\"");
        }

        if (settings.LatencyMs is < 0)
        {
            throw new BindingException("/latency_ms", $"'latency_ms' must be 0 or more, got {settings.LatencyMs}.",
                "give the simulated latency of every retrieval in milliseconds, or remove it for none");
        }

        if (settings.MinScore is { } min && !double.IsFinite(min))
        {
            throw new BindingException("/min_score", $"'min_score' must be a finite number, got {min}.", "remove it to return every item");
        }

        if (settings.Items is { } own)
        {
            return Index(settings, own);
        }

        if (settings.DataFile is not { } file)
        {
            throw new BindingException(string.Empty, $"{Type} has no items.", "give \"items\" or a \"data_file\" holding them");
        }

        var data = Read(file, () => BindingSettings.Bind<MemoryVectorsData>(context, DataFile.Read(file), string.Empty, "the file"));
        return Read(file, () => Index(settings, data.Items ?? []));
    }

    private static MemoryVectorsService Index(MemoryVectorsSettings settings, List<MemoryVectorsItem> items)
    {
        const string pointer = "/items";
        var dimensions = items.Count == 0 ? 0 : items[0].Vector.Count;
        var int64Ids = new long[items.Count];
        var stringIds = new string?[items.Count];
        var vectors = new double[items.Count * dimensions];
        for (var i = 0; i < items.Count; i++)
        {
            var at = pointer + "/" + i;
            var id = items[i].Id;
            switch (id.ValueKind)
            {
                case JsonValueKind.String:
                    stringIds[i] = id.GetString();
                    break;
                case JsonValueKind.Number when id.TryGetInt64(out var int64):
                    int64Ids[i] = int64;
                    break;
                default:
                    throw new BindingException(at + "/id", $"An id must be a string or an int64; got {id.GetRawText()}.");
            }

            var vector = items[i].Vector;
            if (vector.Count != dimensions || dimensions == 0)
            {
                throw new BindingException(at + "/vector", $"Item {id.GetRawText()} has a vector of {vector.Count} elements; the first item's has {dimensions}.",
                    "give every item a non-empty vector of one length");
            }

            for (var d = 0; d < dimensions; d++)
            {
                if (!double.IsFinite(vector[d]))
                {
                    throw new BindingException(at + "/vector/" + d, $"A vector element must be a finite number; got {vector[d]}.");
                }

                vectors[(i * dimensions) + d] = vector[d];
            }
        }

        return MemoryVectorsService.Create(
            int64Ids, stringIds, vectors, dimensions, settings.Query, settings.MinScore, settings.LatencyMs ?? 0, settings.Fail ?? false);
    }

    private static T Read<T>(LocalFile file, Func<T> read)
    {
        try
        {
            return read();
        }
        catch (BindingException ex)
        {
            throw new BindingException(file.Pointer, $"'{file.Path}' at {(ex.Pointer.Length == 0 ? "its root" : ex.Pointer)}: {ex.Reason}", ex.Hint);
        }
    }
}
