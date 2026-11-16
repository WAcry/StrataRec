using System.Runtime.ExceptionServices;
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

    /// <summary>
    /// Merge order of sources: the listed ones first, then the others by name (all by name without it). Earlier sources
    /// win the primary source. An experiment adds a source by giving it a quota, and lists it here to move it ahead.
    /// </summary>
    public List<string>? SourceOrder { get; init; }
}

/// <summary>
/// <c>MultiSourceRecall@1</c> (RowSource): calls every source with a positive quota concurrently, merges results in
/// the declared source order (independent of arrival order, design 5.7), deduplicates by <c>entity_id</c> and appends
/// the rows with <c>recall.sources_mask</c>, <c>recall.best_rank</c>, <c>recall.best_score</c> and
/// <c>recall.primary_source</c>. Fails only if every called source fails; otherwise Degraded. Bit <c>i</c> of the mask
/// is the <c>i</c>-th source in merge order among those the request has a quota for (0 included): a source only an
/// experiment's arm gives a quota has none without that arm, and takes no bit there, as in a compile without it.
/// </summary>
public sealed class MultiSourceRecallFactory : IOperatorFactory
{
    public OperatorDescriptor Descriptor { get; } = new("MultiSourceRecall", 1, Effects.RowSource, typeof(MultiSourceRecallConfig),
        "Concurrent multi-source retrieval with deterministic merge and entity_id dedup.");

    public Operator? Compile(OperatorCompileContext context)
    {
        // The recall columns are the same whatever the config says: declared first, the nodes that read them compile on
        // when something below is wrong (a misspelled source, an unbound one), instead of reporting them unknown.
        var declared = TryWrite(context, "recall.sources_mask", ColumnType.Int64, out var mask)
            & TryWrite(context, "recall.best_rank", ColumnType.Int64, out var bestRank)
            & TryWrite(context, "recall.best_score", ColumnType.Float64, out var bestScore)
            & TryWrite(context, "recall.primary_source", ColumnType.String, out var primary);
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

        // The listed sources, then the others by name. Every source is one some arm combination has, so a list required
        // to name them all would refuse the baseline wherever an arm adds a source with its quota alone. A listed source
        // is bound by name: an arm listing one only another layer's arm adds has none without it.
        var names = new List<string>();
        if (config.SourceOrder is not null)
        {
            for (var i = 0; i < config.SourceOrder.Count; i++)
            {
                var name = config.SourceOrder[i];
                if (!byName.ContainsKey(name))
                {
                    context.Error($"/source_order/{i}", "RECALL_UNKNOWN_SOURCE", $"'{name}' has no entry in parameter '{config.QuotasParam}'.",
                        Spelling.DidYouMean(name, byName.Keys) ?? $"give it a quota in '{config.QuotasParam}', or remove it from source_order");
                    return null;
                }

                if (names.Contains(name))
                {
                    context.Error($"/source_order/{i}", "RECALL_SOURCE_ORDER", $"'{name}' is listed twice in source_order.", "list each source once");
                    return null;
                }

                if (!context.TryParam(byName[name].Name, $"/source_order/{i}", out _))
                {
                    return null;
                }

                names.Add(name);
            }
        }

        var unlisted = new List<string>();
        foreach (var name in byName.Keys)
        {
            if (!names.Contains(name))
            {
                unlisted.Add(name);
            }
        }

        unlisted.Sort(StringComparer.Ordinal);
        names.AddRange(unlisted);

        if (names.Count > 64)
        {
            context.Error("/quotas_param", "RECALL_TOO_MANY_SOURCES",
                $"'{config.QuotasParam}' has {names.Count} sources with the ones experiments add, and at most 64 are supported (recall.sources_mask is an int64 bitmask).",
                "every source an arm gives a quota counts, even when those arms are never assigned together: give fewer sources a quota");
            return null;
        }

        // Every source is bound before giving up on one that is not: each unbound source is reported.
        var sources = new SourceEntry[names.Count];
        var unbound = false;
        for (var i = 0; i < names.Count; i++)
        {
            var bound = context.CandidateSource(names[i], "/quotas_param");
            if (bound is null)
            {
                unbound = true;
                continue;
            }

            sources[i] = new SourceEntry(names[i], bound.Value.Source, bound.Value.TimeoutMs, byName[names[i]], new IoTarget(IoKind.CandidateSource, names[i]));
        }

        if (!declared || unbound)
        {
            return null;
        }

        return new MultiSourceRecall(sources, mask, bestRank, bestScore, primary);
    }

    /// <summary>A recall column: undeclared, it is declared as <paramref name="type"/>; declared, it must be of that kind.</summary>
    private static bool TryWrite(OperatorCompileContext context, string name, ColumnType type, out OutputColumn column)
    {
        if (!context.TryWriteColumn(name, string.Empty, type, out column))
        {
            return false;
        }

        if (column.Type.Kind == type.Kind)
        {
            return true;
        }

        context.Error(string.Empty, "COLUMN_TYPE_MISMATCH", $"{context.Descriptor.Id} writes '{name}' as {type}, but it is declared as {column.Type}.",
            $"declare it as {type} or {type.AsNullable()}, or leave it undeclared");
        return false;
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
                total += quota.IsMissing ? 0 : Math.Clamp(quota.AsInt64(), 0, int.MaxValue); // 64 terms of at most 2^31: no overflow
            }

            return (int)Math.Min(total, int.MaxValue);
        }

        public override async ValueTask ExecuteAsync(OperatorContext context)
        {
            var calls = new Task<CandidateSourceStatus>?[sources.Length];
            var buffers = new CandidateBuffer?[sources.Length];
            var bits = context.Memory.Rent<int>(sources.Length);
            var bit = 0;
            for (var i = 0; i < sources.Length; i++)
            {
                var quotaValue = context.Param(sources[i].Quota);
                bits[i] = quotaValue.IsMissing ? -1 : bit++; // a source without a quota here has no bit (see the type's remarks)
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
            OperationCanceledException? cutOff = null;
            for (var i = 0; i < calls.Length; i++)
            {
                if (calls[i] is null)
                {
                    continue;
                }

                attempted++;
                CandidateSourceStatus status;
                try
                {
                    status = await calls[i]!.ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    // The node's budget or the caller: every call is cut off by the same bound, so the others end at once.
                    // Waiting for them keeps every call recorded before the node's outcome is.
                    cutOff ??= ex;
                    buffers[i] = null;
                    continue;
                }

                if (!status.Succeeded)
                {
                    // Failed or timed out: its buffer is never read (an abandoned source may still be writing it).
                    (failed ??= new List<string>()).Add($"{sources[i].Name}: {status.Error}");
                    buffers[i] = null;
                }
            }

            if (cutOff is not null)
            {
                ExceptionDispatchInfo.Throw(cutOff);
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

            Merge(context, buffers, bits);
        }

        /// <summary>
        /// Merges the buffers in source order: the first occurrence of a key fixes its position and primary source; later
        /// ones add their source bit, the best (lowest) rank and the best score. A source stops contributing at its
        /// first new key that no longer fits in the table. The index and the merged columns are rented from the request
        /// memory, sized to the most keys that can be added, so nothing here allocates per key.
        /// </summary>
        private void Merge(OperatorContext context, CandidateBuffer?[] sinks, int[] bits)
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

            // Everything here is over when the rows are appended and their values written (AppendRows copies the keys).
            var index = new EntityKeyIndex(table.KeyType, capacity, memory, scratch: true);
            var masks = memory.RentScratch<long>(capacity);
            var ranks = memory.RentScratch<long>(capacity);
            var scores = memory.RentScratch<double>(capacity);
            var primaries = memory.RentScratch<int>(capacity);
            string[]? ids = null;
            int[]? sourceCodes = null;
            try
            {
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
                        if (key.IsString != stringKeys || table.ContainsKey(key))
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
                                masks[at] = 1L << bits[s];
                                ranks[at] = r + 1;
                                scores[at] = sinkScores[r];
                                primaries[at] = s;
                                continue;
                            }
                        }

                        masks[at] |= 1L << bits[s];
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
                    ids = memory.RentScratch<string>(count);
                    index.StringKeys.CopyTo(ids);
                    first = context.AppendRows(new ArraySegment<string>(ids, 0, count)); // copied
                }
                else
                {
                    first = context.AppendRows(index.Int64Keys); // copied
                }

                // The appended rows are consecutive: values are written row by row, their validity once for the whole range
                // (a bit set per row and column would wait on the store to the same word before it).
                var maskColumn = (ScalarColumn<long>)context.Output(mask);
                var rankColumn = (ScalarColumn<long>)context.Output(bestRank);
                var scoreColumn = (ScalarColumn<double>)context.Output(bestScore);
                var primaryColumn = (DictStringColumn)context.Output(primary);
                var maskValues = maskColumn.Values;
                var rankValues = rankColumn.Values;
                var scoreValues = scoreColumn.Values;
                var primaryCodes = primaryColumn.Codes;
                sourceCodes = memory.RentScratch<int>(sources.Length); // a source's code, looked up once (−1: not yet)
                sourceCodes.AsSpan(0, sources.Length).Fill(-1);
                for (var i = 0; i < count; i++)
                {
                    var row = first + i;
                    maskValues[row] = masks[i];
                    rankValues[row] = ranks[i];
                    scoreValues[row] = scores[i];
                    ref var code = ref sourceCodes[primaries[i]];
                    if (code < 0)
                    {
                        code = primaryColumn.GetOrAddCode(sources[primaries[i]].Name);
                    }

                    primaryCodes[row] = code;
                }

                maskColumn.SetValid(first, count);
                rankColumn.SetValid(first, count);
                scoreColumn.SetValid(first, count);
                primaryColumn.SetValid(first, count);
            }
            finally
            {
                index.ReturnArrays();
                memory.ReturnScratch(masks);
                memory.ReturnScratch(ranks);
                memory.ReturnScratch(scores);
                memory.ReturnScratch(primaries);
                if (ids is not null)
                {
                    memory.ReturnScratch(ids);
                }

                if (sourceCodes is not null)
                {
                    memory.ReturnScratch(sourceCodes);
                }
            }
        }
    }
}
