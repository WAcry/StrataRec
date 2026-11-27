using StrataRec.Core.Columns;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Threading;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Cache;
using StrataRec.Features.Local;
using StrataRec.Pipeline.Execution;

namespace StrataRec.Pipeline.Operators;

/// <summary>
/// A node's cache of its calls of a pipeline (<c>cache</c> in the config of an operator that calls one): a call whose
/// result is cached does not run the pipeline. Where the results are kept is the environment's
/// (<c>pipelines.&lt;id&gt;.cache</c> in the bindings).
/// </summary>
/// <remarks>
/// A result is found again by what the called pipeline would run and what it is given: its configuration (or
/// <see cref="Version"/>), the request's experiment arms in it and in the pipelines it calls, and every input it
/// receives. Not by the request's id, seed or time, nor by who calls. Only a run that succeeded and was not degraded is
/// kept.
/// </remarks>
public sealed class PipelineCallCacheConfig
{
    /// <summary>
    /// Required: how long a result is served, in milliseconds, e.g. <c>3600000</c>. Each result's lifetime varies by ±10%
    /// with its key, so results computed together do not all expire together.
    /// </summary>
    public long? TtlMs { get; init; }

    /// <summary>
    /// How much longer (milliseconds, default 0) an expired result may be served when the pipeline fails now; the node is
    /// then degraded.
    /// </summary>
    public long? StaleIfErrorMs { get; init; }

    /// <summary>
    /// Names what the cached results are computed by, e.g. <c>"3"</c>. Without it the cache follows the called pipeline's
    /// configuration: a release that changes its pipeline or catalog, the data or services it is bound to, or an
    /// experiment the request is in, computes anew. With it the results are kept across such releases, until the name
    /// changes. Letters, digits, '.', '_' and '-', at most 64.
    /// </summary>
    [System.ComponentModel.DataAnnotations.RegularExpression(Artifacts.Model.CacheBinding.VersionPattern)]
    public string? Version { get; init; }
}

/// <summary>
/// Columns that say which request's run computed what a call returned (<c>computed_by</c> in the config of an operator
/// that calls a pipeline): this request, or for a result the node's cache answered, the earlier one. The called pipeline's
/// log taps wrote under that request's id, so a log of this request that has these columns joins to them, whether the
/// pipeline ran now or not.
/// </summary>
public sealed class ComputedByColumns
{
    /// <summary>The column (<c>string?</c>) that gets the id of the request whose run computed the result.</summary>
    public string? RequestId { get; init; }

    /// <summary>The column (<c>int64?</c>) that gets that request's time, in Unix milliseconds.</summary>
    public string? TimeMs { get; init; }

    /// <summary>
    /// The column (<c>string?</c>) that gets the call that ran the pipeline in that request, as that run's logs name it
    /// (<c>called_by</c>, e.g. <c>feed/promoted#2</c>): a request that calls a pipeline for several rows logs each run
    /// under its call, and the request's id alone does not say which of them computed a row.
    /// </summary>
    public string? CalledBy { get; init; }

    /// <summary>
    /// Declares the columns for a node to write: undeclared, <c>string?</c> and <c>int64?</c>; declared, they must be
    /// those types (rows the node does not write have no value in them). False after reporting at
    /// <paramref name="configKey"/>.
    /// </summary>
    public bool TryWrite(OperatorCompileContext context, string configKey, out ComputedByOutputs outputs)
    {
        ArgumentNullException.ThrowIfNull(context);
        outputs = default;
        OutputColumn? requestId = null, timeMs = null, calledBy = null;
        var ok = true;
        if (RequestId is null && TimeMs is null && CalledBy is null)
        {
            context.Error(configKey, "COMPUTED_BY_CONFIG", "'computed_by' names no column.", "set 'request_id', 'called_by' or 'time_ms' to a column, or remove 'computed_by'");
            return false;
        }

        if (RequestId is not null)
        {
            ok &= Column(context, RequestId, configKey + "/request_id", ColumnType.String.AsNullable(), out requestId);
        }

        if (TimeMs is not null)
        {
            ok &= Column(context, TimeMs, configKey + "/time_ms", ColumnType.Int64.AsNullable(), out timeMs);
        }

        if (CalledBy is not null)
        {
            ok &= Column(context, CalledBy, configKey + "/called_by", ColumnType.String.AsNullable(), out calledBy);
        }

        outputs = new ComputedByOutputs(requestId, timeMs, calledBy);
        return ok;

        static bool Column(OperatorCompileContext context, string name, string key, ColumnType type, out OutputColumn? column)
        {
            column = null;
            if (!context.TryWriteColumn(name, key, type, out var declared))
            {
                return false;
            }

            if (declared.Type != type)
            {
                context.Error(key, "COLUMN_TYPE_MISMATCH", $"Column '{name}' gets which request computed a pipeline's result as {type}, but it is declared as {declared.Type}.",
                    $"declare it as {{ \"type\": \"{type}\" }}, or leave it undeclared");
                return false;
            }

            column = declared;
            return true;
        }
    }
}

/// <summary>
/// The declared <c>computed_by</c> columns of a node (<see cref="ComputedByColumns.TryWrite"/>): writes, for rows a call
/// returned, which run computed them.
/// </summary>
public readonly struct ComputedByOutputs
{
    private readonly OutputColumn? _requestId;
    private readonly OutputColumn? _timeMs;
    private readonly OutputColumn? _calledBy;

    internal ComputedByOutputs(OutputColumn? requestId, OutputColumn? timeMs, OutputColumn? calledBy)
    {
        _requestId = requestId;
        _timeMs = timeMs;
        _calledBy = calledBy;
    }

    /// <summary>The node declared none: <see cref="Write"/> writes nothing.</summary>
    public bool IsEmpty => _requestId is null && _timeMs is null && _calledBy is null;

    /// <summary>
    /// Writes the columns of <paramref name="count"/> rows from <paramref name="firstRow"/> that <paramref name="result"/>
    /// gave: the run of the request that computed it when the node's cache answered
    /// (<see cref="PipelineCallResult.Cached"/>), this request's run otherwise.
    /// </summary>
    public void Write(OperatorContext context, int firstRow, int count, PipelineCallResult result)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(result);
        var cached = result.Cached;
        if (_requestId is { } requestId)
        {
            Set(context.Output(requestId), firstRow, count, ScalarValue.FromString(cached?.RequestId ?? context.RequestId));
        }

        if (_timeMs is { } timeMs)
        {
            Set(context.Output(timeMs), firstRow, count, ScalarValue.FromInt64(cached?.TimeMs ?? context.RequestTimeMs));
        }

        if (_calledBy is { } calledBy)
        {
            Set(context.Output(calledBy), firstRow, count, ScalarValue.FromString(cached?.CalledBy ?? result.CalledBy));
        }

        static void Set(ColumnBuffer into, int firstRow, int count, in ScalarValue value)
        {
            for (var i = 0; i < count; i++)
            {
                ColumnValues.TrySet(into, firstRow + i, value);
            }
        }
    }
}

/// <summary>Where the cached result of a pipeline call comes from: the run that computed it.</summary>
/// <param name="RequestId">The id of the request that ran it: the called pipeline's log taps wrote under it.</param>
/// <param name="TimeMs">That request's time (Unix milliseconds): when the result was computed.</param>
/// <param name="CalledBy">The call that ran it in that request, as the run's logs name it (<c>called_by</c>, e.g.
/// <c>feed/promoted#2</c>): with the request's id, the one run among those the request made of the pipeline.</param>
/// <param name="Stale">The result is past its <c>ttl_ms</c> and was served because the pipeline failed now
/// (<c>stale_if_error_ms</c>): the calling node is degraded.</param>
public sealed record CachedFrom(string RequestId, long TimeMs, string CalledBy, bool Stale = false);

/// <summary>
/// One run of a cached result's computation that had experiments: the called pipeline's, or one of a pipeline it called.
/// A request served the result was exposed to these arms as the request that computed it was.
/// </summary>
/// <param name="Path">The calls from the called pipeline down to this run (<c>rank/recall#0</c>); null for the called pipeline's own run.</param>
/// <param name="From">For a run that did not happen in the computing request either, because a cache answered its call
/// there: the run that did compute it. Null for a run of the computing request.</param>
internal sealed record CallExposure(
    string PipelineId, string ManifestId, string? Path, IReadOnlyList<string> Arms, IReadOnlyList<string> Triggered, CachedFrom? From = null);

/// <summary>A cached result as a record holds it.</summary>
/// <param name="WrittenMs">When its computation started, by the cache's clock: what its freshness is judged from.</param>
internal sealed record CachedCall(
    ResultColumns Columns, ResultValues Values, CachedFrom From, long WrittenMs, IReadOnlyList<CallExposure> Exposures);

/// <summary>What a read of a tier found of one key.</summary>
/// <param name="Fresh">A result within the reader's <c>ttl_ms</c>.</param>
/// <param name="Stale">A result past it and within <c>stale_if_error_ms</c>: kept for a failure of the pipeline.</param>
/// <param name="CutOff">The read was cut off by the node's budget or the caller: the call ends so (rule 8 of the bounded wait).</param>
internal readonly record struct CachedCallRead(CachedCall? Fresh, CachedCall? Stale, Exception? CutOff = null);

/// <summary>
/// The cache of one node's calls of one pipeline: the node's times and version, over the tiers its environment keeps
/// the pipeline's results in (shared by every node that calls it). The engine, which runs the calls, asks it in tier
/// order and hands it what a run returned.
/// </summary>
/// <remarks>
/// A record is a whole result in the layout every table of whole answers has (<see cref="ServiceCache.Schema"/>), each
/// value with a tag of what it is, so it reads back as the value it was. What the result is of is in its key
/// (<see cref="Format"/>), the declared output included: a record is decoded only under the key it was written with,
/// by the code that wrote it.
/// </remarks>
internal sealed class PipelineCallCache
{
    /// <summary>The entity of every pipeline's table: its metric tag <c>stratarec.entity</c> and its keyspace's middle part.</summary>
    public const string Entity = "results";

    /// <summary>
    /// The format of the keys and records. A change of what a record holds or of what a key is built from changes it: the
    /// records another build wrote are then misses, never misread.
    /// </summary>
    public const string Format = "stratarec.pipeline_call_cache/2026-11-27.2";

    private const int Kind = 3; // 1 and 2 are a service's call and item records (CachedServiceCall)
    private const int Header = 6; // kind, rows, id kind, columns, values, exposures
    private const int Int64Ids = 1;
    private const int StringIds = 2;
    private const int PerValue = 0;

    private readonly ServiceCache _tiers;

    public PipelineCallCache(ServiceCache tiers, CachePolicy policy, string? version)
    {
        _tiers = tiers;
        Policy = policy;
        Version = version;
    }

    /// <summary>The node's <c>ttl_ms</c> and <c>stale_if_error_ms</c>.</summary>
    public CachePolicy Policy { get; }

    /// <summary>The node's <c>cache.version</c>; null when the cache follows the called pipeline's configuration.</summary>
    public string? Version { get; }

    public bool HasDistributed => _tiers.Distributed is not null;

    /// <summary>The cache's clock, in Unix milliseconds: what a record's write time and its age are told by.</summary>
    public long NowMs() => _tiers.TableCache.NowMs();

    /// <summary>The in-process tier's record of <paramref name="key"/>, when it is a result of <paramref name="shape"/> the reader can still use.</summary>
    public CachedCallRead ReadMemory(UInt128 key, PipelineInterface shape)
    {
        if (!_tiers.TableCache.Memory)
        {
            return default;
        }

        var version = _tiers.Table.Pin();
        var found = ServiceCache.Find(version, key);
        if (!found.Found)
        {
            Count(CacheTier.Memory, CacheKeyOutcome.Miss);
            return default;
        }

        var record = new LocalServiceRecord(version, found);
        var freshness = Policy.Classify(record.WrittenMs, (ulong)key, NowMs());
        if (freshness == CacheFreshness.Expired || !TryDecode(record, shape, out var result))
        {
            Count(CacheTier.Memory, freshness == CacheFreshness.Expired ? CacheKeyOutcome.Expired : CacheKeyOutcome.Miss);
            return default;
        }

        if (freshness == CacheFreshness.Fresh)
        {
            found.MarkCacheHit();
            Count(CacheTier.Memory, CacheKeyOutcome.Hit);
            return new CachedCallRead(result, null);
        }

        Count(CacheTier.Memory, CacheKeyOutcome.Expired);
        return new CachedCallRead(null, result);
    }

    /// <summary>
    /// The distributed tier's record of <paramref name="key"/>, in one read bounded by the tier's <c>timeout_ms</c> within
    /// <paramref name="bound"/>. A fresh result goes to the in-process tier too. A read that fails or is late finds
    /// nothing: the pipeline runs.
    /// </summary>
    /// <param name="memory">
    /// The calling run's request memory, held for the read: a store that answers after the wait ended is still in use,
    /// and the plan's bindings, the store among them, are not released while a request of the plan holds its memory.
    /// </param>
    public async ValueTask<CachedCallRead> ReadDistributedAsync(UInt128 key, PipelineInterface shape, CallBound bound, RequestMemory memory)
    {
        var tier = _tiers.Distributed!;
        var records = new ReadOnlyMemory<byte>[1];
        var answeredAt = new long[1];
        var read = await BoundedCall.RunAsync<bool, StoreRead>(
            new StoreRead(tier, _tiers.TableCache, [ServiceCache.KeyText(key)], records, answeredAt), bound, memory, _tiers.Io,
            CallDeadline.After(_tiers.DistributedTimeoutMs)).ConfigureAwait(false);
        switch (read.End)
        {
            case CallEnd.BudgetRanOut or CallEnd.Cancelled:
                return new CachedCallRead(null, null, read.Exception);
            case CallEnd.Answered:
                break;
            default:
                Count(CacheTier.Distributed, CacheKeyOutcome.Error);
                return default;
        }

        if (records[0].IsEmpty)
        {
            Count(CacheTier.Distributed, CacheKeyOutcome.Miss);
            return default;
        }

        var decoded = _tiers.Decode(records[0], out var outcome);
        if (decoded is null)
        {
            Count(CacheTier.Distributed, outcome == CacheKeyOutcome.Error ? CacheKeyOutcome.Error : CacheKeyOutcome.Mismatch);
            return default;
        }

        // Judged when the read answered, by the cache's clock, as a source's and a service's records are.
        var freshness = Policy.Classify(decoded.WrittenMs, (ulong)key, answeredAt[0]);
        if (freshness == CacheFreshness.Expired)
        {
            Count(CacheTier.Distributed, CacheKeyOutcome.Expired);
            return default;
        }

        if (!TryDecode(decoded, shape, out var result))
        {
            Count(CacheTier.Distributed, CacheKeyOutcome.Error); // under this key only this code writes: cut short or damaged
            return default;
        }

        if (freshness != CacheFreshness.Fresh)
        {
            Count(CacheTier.Distributed, CacheKeyOutcome.Expired);
            return new CachedCallRead(null, result);
        }

        // The in-process tier, when the cache has one, keeps it with the write time it was written with.
        Count(CacheTier.Distributed, CacheKeyOutcome.Hit);
        if (_tiers.TableCache.Memory)
        {
            _tiers.TableCache.TryWriteBack(new CacheWriteBatch(KeyParts(key), [decoded.Bytes], 1, partCount: 2));
        }

        return new CachedCallRead(result, null);
    }

    /// <summary>Whether a stale result may serve now: still within the node's <c>stale_if_error_ms</c>.</summary>
    public bool Usable(UInt128 key, CachedCall stale) => Policy.Classify(stale.WrittenMs, (ulong)key, NowMs()) != CacheFreshness.Expired;

    /// <summary>A stale result served because the pipeline failed: counted in the tier it came from.</summary>
    public void CountStale(bool distributed) => Count(distributed ? CacheTier.Distributed : CacheTier.Memory, CacheKeyOutcome.Stale);

    /// <summary>
    /// Hands a run's result to the tiers without waiting, to be served for this node's times; a full queue drops it,
    /// counted. The result's arrays are kept as they are: a result is not changed once returned.
    /// </summary>
    /// <param name="startedMs">When the run started, by <see cref="NowMs"/>.</param>
    public void Write(UInt128 key, ResultColumns columns, ResultValues values, CachedFrom from, IReadOnlyList<CallExposure> exposures, long startedMs)
    {
        var builder = new ServiceRecordBuilder(_tiers, 1, startedMs);
        Encode(builder, columns, values, from, exposures);
        builder.End(key);
        builder.WriteBack(NowMs(), Policy);
    }

    private void Count(CacheTier tier, CacheKeyOutcome outcome) => _tiers.TableCache.CountKeys(tier, outcome, 1);

    private static EntityKey[] KeyParts(UInt128 key) => [EntityKey.FromInt64((long)(ulong)(key >> 64)), EntityKey.FromInt64((long)(ulong)key)];

    private enum Tag
    {
        Missing = 0,
        Bool,
        Int64,
        Float64,
        Float32,
        String,
        BoolList,
        Int64List,
        Float64List,
        Float32List,
        StringList,
    }

    private static Tag TagOf(in ScalarValue value) => value.Kind switch
    {
        0 => Tag.Missing,
        StorageKind.Bool => Tag.Bool,
        StorageKind.Int64 => Tag.Int64,
        StorageKind.Float64 => value.IsFloat32 ? Tag.Float32 : Tag.Float64,
        StorageKind.String => Tag.String,
        StorageKind.List => value.ListElement switch
        {
            StorageKind.Bool => Tag.BoolList,
            StorageKind.Int64 => Tag.Int64List,
            StorageKind.Float64 => value.IsFloat32List ? Tag.Float32List : Tag.Float64List,
            StorageKind.String => Tag.StringList,
            var element => throw new InvalidOperationException($"A list of {element} is not a promoted value."),
        },
        var kind => throw new InvalidOperationException($"A value of kind {kind} is not a promoted value."),
    };

    internal static void Encode(ServiceRecordBuilder builder, ResultColumns columns, ResultValues values, CachedFrom from, IReadOnlyList<CallExposure> exposures)
    {
        var ids = columns.Ids;
        var strings = ids.Length > 0 && ids[0].IsString;
        builder.Layout.Add(Kind);
        builder.Layout.Add(ids.Length);
        builder.Layout.Add(strings ? StringIds : Int64Ids);
        builder.Layout.Add(columns.Names.Count);
        builder.Layout.Add(values.Count);
        builder.Layout.Add(exposures.Count);
        builder.Strings.Add(from.RequestId);
        builder.Strings.Add(from.CalledBy);
        builder.Int64s.Add(from.TimeMs);
        foreach (var id in ids)
        {
            if (strings)
            {
                builder.Strings.Add(id.StringValue!);
            }
            else
            {
                builder.Int64s.Add(id.Int64Value);
            }
        }

        for (var c = 0; c < columns.Names.Count; c++)
        {
            EncodeColumn(builder, columns.Column(c));
        }

        foreach (var value in values.All)
        {
            var one = value.Value;
            EncodeColumn(builder, new ReadOnlySpan<ScalarValue>(in one));
        }

        foreach (var exposure in exposures)
        {
            builder.Layout.Add(exposure.Path is null ? 0 : 1);
            builder.Layout.Add(exposure.From is null ? 0 : 1);
            builder.Layout.Add(exposure.Arms.Count);
            builder.Layout.Add(exposure.Triggered.Count);
            builder.Strings.Add(exposure.PipelineId);
            builder.Strings.Add(exposure.ManifestId);
            if (exposure.Path is { } path)
            {
                builder.Strings.Add(path);
            }

            if (exposure.From is { } computed)
            {
                builder.Strings.Add(computed.RequestId);
                builder.Strings.Add(computed.CalledBy);
                builder.Int64s.Add(computed.TimeMs);
            }

            builder.Strings.AddRange(exposure.Arms);
            builder.Strings.AddRange(exposure.Triggered);
        }
    }

    /// <summary>
    /// One column: the tag every value has, when they all have one and it is no list (a column of scores is its values and
    /// one integer); otherwise <see cref="PerValue"/> and each value's tag, a list's followed by its length.
    /// </summary>
    private static void EncodeColumn(ServiceRecordBuilder builder, ReadOnlySpan<ScalarValue> values)
    {
        var uniform = values.Length > 0 ? TagOf(values[0]) : Tag.Missing;
        foreach (ref readonly var value in values)
        {
            if (TagOf(value) != uniform)
            {
                uniform = Tag.Missing;
                break;
            }
        }

        uniform = uniform is >= Tag.Bool and <= Tag.String ? uniform : Tag.Missing;
        builder.Layout.Add(uniform == Tag.Missing ? PerValue : (int)uniform);
        foreach (ref readonly var value in values)
        {
            var tag = TagOf(value);
            if (uniform == Tag.Missing)
            {
                builder.Layout.Add((int)tag);
            }

            switch (tag)
            {
                case Tag.Missing: break;
                case Tag.Bool: builder.Bools.Add(value.AsBool()); break;
                case Tag.Int64: builder.Int64s.Add(value.AsInt64()); break;
                case Tag.Float64: builder.Float64s.Add(value.AsFloat64()); break;
                case Tag.Float32: builder.Float32s.Add((float)value.AsFloat64()); break;
                case Tag.String: builder.Strings.Add(value.AsString()); break;
                case Tag.BoolList:
                    builder.Layout.Add(value.ListLength);
                    builder.Bools.AddRange(value.AsBoolList());
                    break;
                case Tag.Int64List:
                    builder.Layout.Add(value.ListLength);
                    builder.Int64s.AddRange(value.AsInt64List());
                    break;
                case Tag.Float64List:
                    builder.Layout.Add(value.ListLength);
                    builder.Float64s.AddRange(value.AsFloat64List());
                    break;
                case Tag.Float32List:
                    builder.Layout.Add(value.ListLength);
                    foreach (var element in value.AsFloat64List())
                    {
                        builder.Float32s.Add((float)element);
                    }

                    break;
                default:
                    builder.Layout.Add(value.ListLength);
                    builder.Strings.AddRange(value.AsStringList());
                    break;
            }
        }
    }

    /// <summary>
    /// The result a record holds, as a result of <paramref name="shape"/>; false for a record this code did not write
    /// whole for that shape (another kind, counts that do not add up, a value left over, ids of the other key type, a
    /// value that is not of its column's type or a fixed-length list of another length): nothing of it is used. A
    /// record whose counts were changed so that they still add up would otherwise move values between rows.
    /// </summary>
    internal static bool TryDecode<TRecord>(TRecord record, PipelineInterface shape, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CachedCall? result)
        where TRecord : IServiceRecord
    {
        result = null;
        var reader = new Reader<TRecord>(record);
        var layout = reader.Layout;
        if (layout.Length < Header || layout[0] != Kind || layout[1] < 0 || layout[2] is not (Int64Ids or StringIds)
            || layout[3] != shape.Columns.Length || layout[4] != shape.Values.Length || layout[5] < 0)
        {
            return false;
        }

        var rows = layout[1];
        var strings = layout[2] == StringIds;
        var exposureCount = layout[5];
        reader.Skip(Header);
        var requestId = reader.String();
        var calledBy = reader.String();
        var timeMs = reader.Int64();
        if (reader.Broken || rows > (strings ? reader.StringsLeft : reader.Int64sLeft)
            || (rows > 0 && strings != (shape.KeyType == EntityKeyType.String))) // a result without rows says nothing of its ids
        {
            return false;
        }

        var ids = new EntityKey[rows];
        for (var r = 0; r < rows; r++)
        {
            ids[r] = strings ? EntityKey.FromString(reader.String()) : EntityKey.FromInt64(reader.Int64());
        }

        var names = new string[shape.Columns.Length];
        var types = new ColumnType[names.Length];
        var columns = new ScalarValue[names.Length][];
        for (var c = 0; c < names.Length && !reader.Broken; c++)
        {
            names[c] = shape.Columns[c].Name;
            types[c] = shape.Columns[c].Type;
            columns[c] = new ScalarValue[rows];
            DecodeColumn(ref reader, columns[c], types[c]);
        }

        var values = new ResultValue[shape.Values.Length];
        var one = new ScalarValue[1];
        for (var v = 0; v < values.Length && !reader.Broken; v++)
        {
            DecodeColumn(ref reader, one, shape.Values[v].Type);
            values[v] = new ResultValue(shape.Values[v].Name, shape.Values[v].Type, one[0]);
        }

        var exposures = exposureCount == 0 ? [] : new CallExposure[Math.Min(exposureCount, reader.StringsLeft)];
        for (var e = 0; e < exposures.Length && !reader.Broken; e++)
        {
            var hasPath = reader.Int32();
            var hasFrom = reader.Int32();
            var arms = reader.Int32();
            var triggered = reader.Int32();
            var pipeline = reader.String();
            var manifest = reader.String();
            var path = hasPath == 1 ? reader.String() : null;
            var computed = hasFrom == 1 ? new CachedFrom(reader.String(), reader.Int64(), reader.String()) : null;
            exposures[e] = new CallExposure(pipeline, manifest, path, reader.Strings(arms), reader.Strings(triggered), computed);
        }

        if (reader.Broken || exposures.Length != exposureCount || !reader.AtEnd)
        {
            return false;
        }

        result = new CachedCall(
            new ResultColumns(ids, names, types, columns), ResultValues.OfOutput(values), new CachedFrom(requestId, timeMs, calledBy), record.WrittenMs, exposures);
        return true;
    }

    /// <summary>
    /// Whether a value written with <paramref name="tag"/> is one a column of <paramref name="type"/> holds: no value,
    /// or a value of the type's kind (an integer of either declared width, a float of either width: the widths are the
    /// writer's), a list of its element kind.
    /// </summary>
    private static bool Fits(Tag tag, ColumnType type) => tag == Tag.Missing || (type.ValueKind, type.IsList) switch
    {
        (StorageKind.Bool, false) => tag == Tag.Bool,
        (StorageKind.Int32 or StorageKind.Int64, false) => tag == Tag.Int64,
        (StorageKind.Float32 or StorageKind.Float64, false) => tag is Tag.Float32 or Tag.Float64,
        (StorageKind.String, false) => tag == Tag.String,
        (StorageKind.Bool, true) => tag == Tag.BoolList,
        (StorageKind.Int32 or StorageKind.Int64, true) => tag == Tag.Int64List,
        (StorageKind.Float32 or StorageKind.Float64, true) => tag is Tag.Float32List or Tag.Float64List,
        (StorageKind.String, true) => tag == Tag.StringList,
        _ => false,
    };

    private static void DecodeColumn<TRecord>(ref Reader<TRecord> reader, Span<ScalarValue> values, ColumnType type)
        where TRecord : IServiceRecord
    {
        var uniform = reader.Int32();
        for (var r = 0; r < values.Length && !reader.Broken; r++)
        {
            var tag = uniform == PerValue ? reader.Int32() : uniform;
            if (!Fits((Tag)tag, type))
            {
                reader.Break();
                return;
            }

            switch ((Tag)tag)
            {
                case Tag.Missing: values[r] = ScalarValue.Missing; break;
                case Tag.Bool: values[r] = ScalarValue.FromBool(reader.Bool()); break;
                case Tag.Int64: values[r] = ScalarValue.FromInt64(reader.Int64()); break;
                case Tag.Float64: values[r] = ScalarValue.FromFloat64(reader.Float64()); break;
                case Tag.Float32: values[r] = ScalarValue.FromFloat32(reader.Float32()); break;
                case Tag.String: values[r] = ScalarValue.FromString(reader.String()); break;
                case Tag.BoolList: values[r] = ScalarValue.FromBoolList(reader.Bools(reader.Int32())); break;
                case Tag.Int64List: values[r] = ScalarValue.FromInt64List(reader.Int64s(reader.Int32())); break;
                case Tag.Float64List: values[r] = ScalarValue.FromFloat64List(reader.Float64s(reader.Int32())); break;
                case Tag.Float32List: values[r] = ScalarValue.FromFloat32List(reader.Float32s(reader.Int32())); break;
                case Tag.StringList: values[r] = ScalarValue.FromStringList(reader.Strings(reader.Int32())); break;
                default: reader.Break(); break;
            }

            if (type.IsFixedLength && tag != (int)Tag.Missing && values[r].ListLength != type.FixedLength)
            {
                reader.Break();
            }
        }
    }

    /// <summary>
    /// Reads a record's lists in the order <see cref="Encode"/> wrote them. A read past a list's end breaks the reader
    /// (<see cref="Broken"/>) and returns nothing of the record, so a record cut short or of other counts is told, never
    /// read as other values.
    /// </summary>
    private ref struct Reader<TRecord>
        where TRecord : IServiceRecord
    {
        private readonly TRecord _record;
        private readonly ReadOnlySpan<bool> _bools;
        private readonly ReadOnlySpan<long> _int64s;
        private readonly ReadOnlySpan<float> _float32s;
        private readonly ReadOnlySpan<double> _float64s;
        private readonly int _strings;
        private int _layoutAt;
        private int _boolAt;
        private int _int64At;
        private int _float32At;
        private int _float64At;
        private int _stringAt;

        public Reader(TRecord record)
        {
            _record = record;
            Layout = record.Layout;
            _bools = record.Bools;
            _int64s = record.Int64s;
            _float32s = record.Float32s;
            _float64s = record.Float64s;
            _strings = record.StringCount;
        }

        public ReadOnlySpan<int> Layout { get; }

        public bool Broken { get; private set; }

        public readonly int StringsLeft => _strings - _stringAt;

        public readonly int Int64sLeft => _int64s.Length - _int64At;

        /// <summary>Every list was read to its end: the record holds what was read and nothing more.</summary>
        public readonly bool AtEnd => _layoutAt == Layout.Length && _boolAt == _bools.Length && _int64At == _int64s.Length
            && _float32At == _float32s.Length && _float64At == _float64s.Length && _stringAt == _strings && _record.Int32s.IsEmpty;

        public void Break() => Broken = true;

        public void Skip(int layout) => _layoutAt += layout;

        public int Int32() => Take(ref _layoutAt, Layout.Length, 1) is >= 0 and var at ? Layout[at] : -1;

        public bool Bool() => Take(ref _boolAt, _bools.Length, 1) is >= 0 and var at && _bools[at];

        public long Int64() => Take(ref _int64At, _int64s.Length, 1) is >= 0 and var at ? _int64s[at] : 0;

        public float Float32() => Take(ref _float32At, _float32s.Length, 1) is >= 0 and var at ? _float32s[at] : 0;

        public double Float64() => Take(ref _float64At, _float64s.Length, 1) is >= 0 and var at ? _float64s[at] : 0;

        public string String() => Take(ref _stringAt, _strings, 1) is >= 0 and var at ? _record.String(at) : string.Empty;

        public bool[] Bools(int count) => Take(ref _boolAt, _bools.Length, count) is >= 0 and var at ? _bools.Slice(at, count).ToArray() : [];

        public long[] Int64s(int count) => Take(ref _int64At, _int64s.Length, count) is >= 0 and var at ? _int64s.Slice(at, count).ToArray() : [];

        public ReadOnlySpan<float> Float32s(int count) => Take(ref _float32At, _float32s.Length, count) is >= 0 and var at ? _float32s.Slice(at, count) : [];

        public double[] Float64s(int count) => Take(ref _float64At, _float64s.Length, count) is >= 0 and var at ? _float64s.Slice(at, count).ToArray() : [];

        public string[] Strings(int count)
        {
            var at = Take(ref _stringAt, _strings, count);
            if (at < 0 || count == 0)
            {
                return [];
            }

            var strings = new string[count];
            for (var i = 0; i < count; i++)
            {
                strings[i] = _record.String(at + i);
            }

            return strings;
        }

        /// <summary>The position of the next <paramref name="count"/> values of a list of <paramref name="length"/>, or -1 (and broken) when it has not that many.</summary>
        private int Take(ref int at, int length, int count)
        {
            if (Broken || count < 0 || count > length - at)
            {
                Broken = true;
                return -1;
            }

            var start = at;
            at += count;
            return start;
        }
    }

    /// <summary>The distributed tier's read of one key as a bounded call, which holds the calling run's request memory until the store has answered.</summary>
    private readonly struct StoreRead(DistributedCacheTier tier, LocalTableCache table, string[] keys, ReadOnlyMemory<byte>[] records, long[] answeredAt)
        : IBoundedCall<bool>
    {
        public int Keys => keys.Length;

        public ValueTask<bool> Send(CancellationToken token) => tier.ReadAsync(table.Keyspace, keys, records, token);

        public IoOutcome Judge(bool value, out string? error)
        {
            error = null;
            return IoOutcome.Ok; // a store's failure is thrown (DistributedCacheTier.ReadAsync)
        }

        public void Pending(Task done)
        {
        }

        public void Ended(in CallResult<bool> result, TimeSpan duration) => answeredAt[0] = table.NowMs();
    }
}
