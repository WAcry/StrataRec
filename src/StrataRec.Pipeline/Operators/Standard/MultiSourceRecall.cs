using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Pipeline.Sources;

namespace StrataRec.Pipeline.Operators.Standard;

/// <summary>Config of <c>MultiSourceRecall@1</c>.</summary>
public sealed class MultiSourceRecallConfig
{
    /// <summary>Map parameter <c>source name → quota</c> (e.g. <c>recall_quotas</c>); a quota of 0 disables a source.</summary>
    public required string QuotasParam { get; init; }

    /// <summary>Merge order of sources; defaults to ordinal order of their names. Earlier sources win the primary source.</summary>
    public List<string>? SourceOrder { get; init; }
}

/// <summary>
/// <c>MultiSourceRecall@1</c> (RowSource): calls every source with a positive quota concurrently, merges results in
/// the declared source order (independent of arrival order, design 5.7), deduplicates by <c>entity_id</c> and appends
/// the rows with <c>recall.sources_mask</c>, <c>recall.best_rank</c>, <c>recall.best_score</c> and
/// <c>recall.primary_source</c>. Fails only if every called source fails; otherwise Degraded.
/// </summary>
public sealed class MultiSourceRecallFactory : IOperatorFactory
{
    public OperatorDescriptor Descriptor { get; } = new("MultiSourceRecall", 1, Effects.RowSource, typeof(MultiSourceRecallConfig),
        "Concurrent multi-source retrieval with deterministic merge and entity_id dedup.");

    public Operator? Compile(OperatorCompileContext context)
    {
        var config = context.BindConfig<MultiSourceRecallConfig>();
        if (config is null)
        {
            return null;
        }

        var leaves = context.MapParam(config.QuotasParam, "/quotas_param", StorageKind.Int64);
        if (leaves.Count == 0)
        {
            return null;
        }

        var prefix = config.QuotasParam.Length + 1;
        var byName = new Dictionary<string, ParamRef>(StringComparer.Ordinal);
        foreach (var leaf in leaves)
        {
            byName[leaf.Name[prefix..]] = leaf;
        }

        var names = new List<string>();
        if (config.SourceOrder is not null)
        {
            foreach (var name in config.SourceOrder)
            {
                if (!byName.ContainsKey(name))
                {
                    context.Error("/source_order", "RECALL_UNKNOWN_SOURCE", $"'{name}' has no entry in parameter '{config.QuotasParam}'.");
                    return null;
                }

                names.Add(name);
            }

            foreach (var name in byName.Keys)
            {
                if (!names.Contains(name))
                {
                    context.Error("/source_order", "RECALL_SOURCE_ORDER", $"source_order must list every source; '{name}' is missing.");
                    return null;
                }
            }
        }
        else
        {
            names.AddRange(byName.Keys);
            names.Sort(StringComparer.Ordinal);
        }

        if (names.Count > 64)
        {
            context.Error("/quotas_param", "RECALL_TOO_MANY_SOURCES", "At most 64 sources are supported (recall.sources_mask is an int64 bitmask).");
            return null;
        }

        var sources = new SourceEntry[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            var bound = context.CandidateSource(names[i], "/quotas_param");
            if (bound is null)
            {
                return null;
            }

            sources[i] = new SourceEntry(names[i], bound.Value.Source, bound.Value.TimeoutMs, byName[names[i]]);
        }

        if (!context.TryWriteColumn("recall.sources_mask", string.Empty, ColumnType.Int64, out var mask)
            || !context.TryWriteColumn("recall.best_rank", string.Empty, ColumnType.Int64, out var bestRank)
            || !context.TryWriteColumn("recall.best_score", string.Empty, ColumnType.Float64, out var bestScore)
            || !context.TryWriteColumn("recall.primary_source", string.Empty, ColumnType.String, out var primary))
        {
            return null;
        }

        return new MultiSourceRecall(sources, mask, bestRank, bestScore, primary);
    }

    private sealed record SourceEntry(string Name, ICandidateSource Source, int? TimeoutMs, ParamRef Quota);

    private sealed class MultiSourceRecall(SourceEntry[] sources, OutputColumn mask, OutputColumn bestRank, OutputColumn bestScore, OutputColumn primary) : Operator
    {
        public override int GetRowLimit(ReadOnlySpan<ScalarValue> parameters)
        {
            long total = 0;
            foreach (var s in sources)
            {
                var quota = parameters[s.Quota.Id];
                total += quota.IsMissing ? 0 : Math.Max(0, quota.AsInt64());
            }

            return (int)Math.Min(total, int.MaxValue);
        }

        public override async ValueTask ExecuteAsync(OperatorContext context)
        {
            var calls = new Task<CandidateSourceStatus>?[sources.Length];
            var sinks = new Sink[sources.Length];
            for (var i = 0; i < sources.Length; i++)
            {
                var quotaValue = context.Param(sources[i].Quota);
                var quota = quotaValue.IsMissing ? 0 : (int)Math.Clamp(quotaValue.AsInt64(), 0, int.MaxValue);
                if (quota == 0)
                {
                    continue;
                }

                sinks[i] = new Sink(quota);
                var query = new CandidateQuery { SourceName = sources[i].Name, Limit = quota, Inputs = context.Inputs, RequestId = context.RequestId, Seed = context.Seed };
                calls[i] = CallAsync(sources[i], query, sinks[i], context.CancellationToken);
            }

            var attempted = 0;
            var failed = new List<string>();
            for (var i = 0; i < calls.Length; i++)
            {
                if (calls[i] is null)
                {
                    continue;
                }

                attempted++;
                var status = await calls[i]!.ConfigureAwait(false);
                if (!status.Succeeded)
                {
                    failed.Add($"{sources[i].Name}: {status.Error}");
                    sinks[i] = null!;
                }
            }

            if (attempted > 0 && failed.Count == attempted)
            {
                context.Fail("every recall source failed (" + string.Join("; ", failed) + ")");
                return;
            }

            if (failed.Count > 0)
            {
                context.ReportDegraded("recall sources failed: " + string.Join("; ", failed));
            }

            Merge(context, sinks);
        }

        private static async Task<CandidateSourceStatus> CallAsync(SourceEntry source, CandidateQuery query, Sink sink, CancellationToken cancellationToken)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (source.TimeoutMs is { } timeout)
            {
                cts.CancelAfter(timeout);
            }

            try
            {
                return await source.Source.RetrieveAsync(query, sink, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return CandidateSourceStatus.Failed("timed out");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return CandidateSourceStatus.Failed(ex.Message);
            }
        }

        private void Merge(OperatorContext context, Sink[] sinks)
        {
            var table = context.Table;
            var stringKeys = table.KeyType == EntityKeyType.String;
            var index = new Dictionary<EntityKey, int>();
            var keys = new List<EntityKey>();
            var masks = new List<long>();
            var ranks = new List<long>();
            var scores = new List<double>();
            var primaries = new List<int>();
            for (var s = 0; s < sinks.Length; s++)
            {
                var sink = sinks[s];
                if (sink is null)
                {
                    continue;
                }

                for (var r = 0; r < sink.Count; r++)
                {
                    var key = sink.Keys[r];
                    if (key.IsString != stringKeys || table.FindRow(key) >= 0)
                    {
                        continue; // wrong key type, or already in the table from an earlier row source
                    }

                    if (index.TryGetValue(key, out var at))
                    {
                        masks[at] |= 1L << s;
                        ranks[at] = Math.Min(ranks[at], r + 1);
                        scores[at] = Math.Max(scores[at], sink.Scores[r]);
                        continue;
                    }

                    if (keys.Count >= table.FreeRows)
                    {
                        break;
                    }

                    index[key] = keys.Count;
                    keys.Add(key);
                    masks.Add(1L << s);
                    ranks.Add(r + 1);
                    scores.Add(sink.Scores[r]);
                    primaries.Add(s);
                }
            }

            if (keys.Count == 0)
            {
                return;
            }

            int first;
            if (stringKeys)
            {
                var ids = new string[keys.Count];
                for (var i = 0; i < ids.Length; i++)
                {
                    ids[i] = keys[i].StringValue!;
                }

                first = context.AppendRows(ids);
            }
            else
            {
                var ids = context.Memory.Rent<long>(keys.Count);
                for (var i = 0; i < keys.Count; i++)
                {
                    ids[i] = keys[i].Int64Value;
                }

                first = context.AppendRows(ids.AsSpan(0, keys.Count));
            }

            var maskColumn = (ScalarColumn<long>)context.Output(mask);
            var rankColumn = (ScalarColumn<long>)context.Output(bestRank);
            var scoreColumn = context.Output(bestScore);
            var primaryColumn = (DictStringColumn)context.Output(primary);
            for (var i = 0; i < keys.Count; i++)
            {
                var row = first + i;
                maskColumn.Set(row, masks[i]);
                rankColumn.Set(row, ranks[i]);
                ColumnValues.Set(scoreColumn, row, ScalarValue.FromFloat64(scores[i]));
                primaryColumn.Set(row, sources[primaries[i]].Name);
            }
        }
    }

    /// <summary>Collects one source's results; capped at the quota (excess is dropped, design 4.8).</summary>
    private sealed class Sink(int limit) : ICandidateSink
    {
        public List<EntityKey> Keys { get; } = new();

        public List<double> Scores { get; } = new();

        public int Count => Keys.Count;

        public int Truncated { get; private set; }

        public void Add(long entityId, double score) => Add(EntityKey.FromInt64(entityId), score);

        public void Add(string entityId, double score) => Add(EntityKey.FromString(entityId), score);

        private void Add(EntityKey key, double score)
        {
            if (Keys.Count >= limit)
            {
                Truncated++;
                return;
            }

            Keys.Add(key);
            Scores.Add(double.IsFinite(score) ? score : 0.0);
        }
    }
}
