using StrataRec.Core.Columns;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
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

            sources[i] = new SourceEntry(names[i], bound.Value.Source, bound.Value.TimeoutMs, byName[names[i]], new IoTarget(IoKind.CandidateSource, names[i]));
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

    private sealed record SourceEntry(string Name, ICandidateSource Source, int? TimeoutMs, ParamRef Quota, IoTarget Io);

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
            var buffers = new CandidateBuffer?[sources.Length];
            for (var i = 0; i < sources.Length; i++)
            {
                var quotaValue = context.Param(sources[i].Quota);
                var quota = quotaValue.IsMissing ? 0 : (int)Math.Clamp(quotaValue.AsInt64(), 0, int.MaxValue);
                if (quota == 0)
                {
                    continue;
                }

                var buffer = new CandidateBuffer(quota, context.Memory);
                buffers[i] = buffer;
                var query = new CandidateQuery { SourceName = sources[i].Name, Limit = quota, Inputs = context.Inputs, RequestId = context.RequestId, Seed = context.Seed };
                calls[i] = context.RetrieveAsync(sources[i].Source, sources[i].Io, sources[i].TimeoutMs, query, buffer);
            }

            var attempted = 0;
            List<string>? failed = null;
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
                    (failed ??= new List<string>()).Add($"{sources[i].Name}: {status.Error}");
                    buffers[i] = null;
                }
            }

            if (failed is not null && failed.Count == attempted)
            {
                context.Fail("every recall source failed (" + string.Join("; ", failed) + ")");
                return;
            }

            if (failed is not null)
            {
                context.ReportDegraded("recall sources failed: " + string.Join("; ", failed));
            }

            Merge(context, buffers);
        }

        /// <summary>
        /// Merges the buffers in source order: the first occurrence of a key fixes its position and primary source; later
        /// ones add their source bit, the best (lowest) rank and the best score. A source stops contributing at its
        /// first new key that no longer fits in the table. The index and the merged columns are rented from the request
        /// memory, sized to the most keys that can be added, so nothing here allocates per key.
        /// </summary>
        private void Merge(OperatorContext context, CandidateBuffer?[] sinks)
        {
            var table = context.Table;
            var memory = context.Memory;
            var stringKeys = table.KeyType == EntityKeyType.String;
            var freeRows = table.FreeRows;
            long total = 0;
            foreach (var sink in sinks)
            {
                total += sink?.Count ?? 0;
            }

            var capacity = (int)Math.Min(total, freeRows);
            if (capacity == 0)
            {
                return;
            }

            var index = new EntityKeyIndex(table.KeyType, capacity, memory);
            var masks = memory.Rent<long>(capacity);
            var ranks = memory.Rent<long>(capacity);
            var scores = memory.Rent<double>(capacity);
            var primaries = memory.Rent<int>(capacity);
            for (var s = 0; s < sinks.Length; s++)
            {
                var sink = sinks[s];
                if (sink is null)
                {
                    continue;
                }

                var sinkKeys = sink.Keys;
                var sinkScores = sink.Scores;
                for (var r = 0; r < sinkKeys.Length; r++)
                {
                    var key = sinkKeys[r];
                    if (key.IsString != stringKeys || table.FindRow(key) >= 0)
                    {
                        continue; // wrong key type, or already in the table from an earlier row source
                    }

                    int at;
                    if (index.Count >= freeRows)
                    {
                        if (!index.TryGetValue(key, out at))
                        {
                            break; // no room for a new key: the rest of this source is dropped
                        }
                    }
                    else
                    {
                        at = index.GetOrAdd(key, index.Count, out var added);
                        if (added)
                        {
                            masks[at] = 1L << s;
                            ranks[at] = r + 1;
                            scores[at] = sinkScores[r];
                            primaries[at] = s;
                            continue;
                        }
                    }

                    masks[at] |= 1L << s;
                    ranks[at] = Math.Min(ranks[at], r + 1);
                    scores[at] = Math.Max(scores[at], sinkScores[r]);
                }
            }

            var count = index.Count;
            if (count == 0)
            {
                return;
            }

            int first;
            if (stringKeys)
            {
                var ids = memory.Rent<string>(count);
                index.StringKeys.CopyTo(ids);
                first = context.AppendRows(new ArraySegment<string>(ids, 0, count));
            }
            else
            {
                first = context.AppendRows(index.Int64Keys);
            }

            var maskColumn = (ScalarColumn<long>)context.Output(mask);
            var rankColumn = (ScalarColumn<long>)context.Output(bestRank);
            var scoreColumn = context.Output(bestScore);
            var primaryColumn = (DictStringColumn)context.Output(primary);
            for (var i = 0; i < count; i++)
            {
                var row = first + i;
                maskColumn.Set(row, masks[i]);
                rankColumn.Set(row, ranks[i]);
                ColumnValues.Set(scoreColumn, row, ScalarValue.FromFloat64(scores[i]));
                primaryColumn.Set(row, sources[primaries[i]].Name);
            }
        }
    }
}
