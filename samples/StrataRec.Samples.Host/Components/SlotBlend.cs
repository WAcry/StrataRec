using System.ComponentModel;
using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Pipeline.Operators;

namespace StrataRec.Samples.Host.Components;

/// <summary>Config of <c>SlotBlend@1</c>; its JSON Schema is generated from this class, as for <see cref="CategoryBoostConfig"/>.</summary>
public sealed class SlotBlendConfig
{
    [Description("The pipeline whose rows make up the list, e.g. trending_feed.")]
    public required string Organic { get; init; }

    [Description("The pipeline whose rows are placed into the list, e.g. sponsored.")]
    public required string Sponsored { get; init; }

    [Description("Parameter (int64) holding the most rows of the blended list.")]
    public required string LimitParam { get; init; }

    [Description("Parameter (int64) holding how many organic rows come before each sponsored row: 3 puts them at ranks 4, 8, …; 0 places none, and does not call the sponsored pipeline.")]
    public required string EveryParam { get; init; }

    [Description("The most the sponsored pipeline may take, in milliseconds; when it takes longer the page is served without sponsored rows. Without it, as long as the node has.")]
    public int? SponsoredTimeoutMs { get; init; }

    [Description("The organic pipeline's output column to take as a row's score, e.g. score.")]
    public required string OrganicScore { get; init; }

    [Description("The sponsored pipeline's output column to take as a row's score, e.g. bid.")]
    public required string SponsoredScore { get; init; }

    [Description("Column (float32 or float64, declared under columns) that gets each row's score from its pipeline.")]
    public required string ScoreCol { get; init; }

    [Description("Column (bool, declared under columns) written true on sponsored rows and false on the others.")]
    public required string SponsoredCol { get; init; }
}

/// <summary>
/// <c>SlotBlend@1</c>: a page made of two other pipelines. It calls both for the request, takes the organic one's rows in
/// their order, and places a sponsored row after every few of them. The placement rule is this operator's own; the
/// framework's part is calling a pipeline.
/// </summary>
/// <remarks>
/// <para>How an operator calls a pipeline:</para>
/// <list type="bullet">
/// <item>Compile declares each call (<see cref="OperatorCompileContext.TryCallPipeline"/>) and what it needs of the
/// pipeline: that its rows are appended (<see cref="OperatorCompileContext.CallAppendsRows"/>, so its keys must be of
/// this pipeline's type) and the output column it reads (<see cref="OperatorCompileContext.TryCallColumn"/>). In a
/// workspace, a pipeline that does not exist, an output column it does not have or calls that lead back here are errors
/// at the config key, when the configuration is validated.</item>
/// <item>Run calls with the handle (<see cref="OperatorContext.CallPipelineAsync"/>). The called run is the same request:
/// its id, its time, its experiment assignment (each pipeline runs the arms it has), and what is left of this node's
/// budget. Both calls are started before either is awaited, so the two pipelines run at once.</item>
/// <item>A call never throws for a pipeline that failed: its result says so. Here a page without the organic list is no
/// page (the node fails, and its <c>on_failure</c> decides), while one without sponsored rows is served as it is
/// (<see cref="OperatorContext.ReportDegraded"/>). A call the node can do without gets a time limit of its own
/// (<c>sponsored_timeout_ms</c>): a called run otherwise has all that is left of the node's budget, and a slow one would
/// time the node out.</item>
/// <item>It is a row source like any other: it appends only keys the request has not seen
/// (<c>context.Table.ContainsKey</c>), and says how many rows it may append.</item>
/// <item>Each called pipeline writes its own logs, and a recording of this request holds what each call returned, so a
/// replay needs neither pipeline served.</item>
/// </list>
/// <para>The structure of the page is this node's configuration, so an experiment changes it as it changes anything
/// else: how dense the sponsored rows are is a parameter (<c>experiments/exp_home_slots.jsonc</c>), and another arm
/// could name another pipeline, or replace the node.</para>
/// </remarks>
public sealed class SlotBlendFactory : IOperatorFactory
{
    public OperatorDescriptor Descriptor { get; } = new("SlotBlend", 1, Effects.RowSource, typeof(SlotBlendConfig),
        "Calls two pipelines and places a row of the second after every few rows of the first.");

    public Operator? Compile(OperatorCompileContext context)
    {
        var config = context.BindConfig<SlotBlendConfig>();
        if (config is null)
        {
            return null;
        }

        if (context.Schema.KeyType != EntityKeyType.Int64)
        {
            context.Error(string.Empty, "SLOT_BLEND_KEYS", "SlotBlend@1 is written for int64 keys.", "use it in a pipeline with \"entity_key_type\": \"int64\"");
            return null;
        }

        // Both are declared before giving up on either: each mistake is reported.
        var declared = context.TryCallPipeline(config.Organic, "/organic", out var organic)
            & context.TryCallPipeline(config.Sponsored, "/sponsored", out var sponsored);
        var ok = declared
            & context.TryParam(config.LimitParam, "/limit_param", out var limit, StorageKind.Int64, (1, 1000))
            & context.TryParam(config.EveryParam, "/every_param", out var every, StorageKind.Int64, (0, 1000))
            & context.TryWriteColumn(config.ScoreCol, "/score_col", null, out var score)
            & context.TryWriteColumn(config.SponsoredCol, "/sponsored_col", null, out var mark);
        if (!ok)
        {
            return null;
        }

        if (config.SponsoredTimeoutMs is <= 0)
        {
            context.Error("/sponsored_timeout_ms", "SLOT_BLEND_TIMEOUT", "'sponsored_timeout_ms' must be positive.", "leave it out to give the call all the node has");
            ok = false;
        }

        if (!score.Type.IsFloatStorage || score.Type.IsList)
        {
            context.Error("/score_col", "COLUMN_TYPE_MISMATCH", $"Column '{config.ScoreCol}' is {score.Type}; the blend writes a score.", "declare it as float32");
            ok = false;
        }

        if (mark.Type.Kind != StorageKind.Bool || mark.Type.IsList)
        {
            context.Error("/sponsored_col", "COLUMN_TYPE_MISMATCH", $"Column '{config.SponsoredCol}' is {mark.Type}; the blend writes a flag.", "declare it as bool");
            ok = false;
        }

        // What each call needs of its pipeline: the keys' type, and the column read as this pipeline's score.
        ok &= context.CallAppendsRows(organic!, "/organic") & context.CallAppendsRows(sponsored!, "/sponsored");
        ok = ok && context.TryCallColumn(organic!, config.OrganicScore, "/organic_score", score.Type)
            & context.TryCallColumn(sponsored!, config.SponsoredScore, "/sponsored_score", score.Type);
        TimeSpan? wait = config.SponsoredTimeoutMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null;
        return ok ? new SlotBlend(organic!, sponsored!, config.OrganicScore, config.SponsoredScore, limit, every, score, mark, wait) : null;
    }

    private sealed class SlotBlend(
        PipelineCall organic, PipelineCall sponsored, string organicScore, string sponsoredScore, ParamRef limit, ParamRef every, OutputColumn score, OutputColumn mark,
        TimeSpan? sponsoredTimeout)
        : Operator
    {
        /// <summary>A row source says how many rows it may append: the request's memory is sized by it.</summary>
        public override int GetRowLimit(ReadOnlySpan<ScalarValue> parameters) => Count(parameters[limit.Id]);

        /// <summary>A parameter's count; none for one without a value (a map's key set to null).</summary>
        private static int Count(in ScalarValue value) => value.IsMissing ? 0 : (int)Math.Clamp(value.AsInt64(), 0, int.MaxValue);

        public override async ValueTask ExecuteAsync(OperatorContext context)
        {
            // Started one after the other, awaited afterwards: the two pipelines run at once, and the calls are numbered
            // in this order every time, which is what their seeds are derived from. No sponsored rows, no call.
            var gap = Count(context.Param(every));
            var organicCall = context.CallPipelineAsync(organic).AsTask();
            var sponsoredCall = gap > 0 ? context.CallPipelineAsync(sponsored, timeout: sponsoredTimeout).AsTask() : null;
            var list = await organicCall;
            var ads = sponsoredCall is null ? null : await sponsoredCall;
            if (!list.Succeeded)
            {
                context.Fail(list.Error ?? $"pipeline '{organic.PipelineId}' failed");
                return;
            }

            if (ads is { Succeeded: false })
            {
                context.ReportDegraded(ads.Error ?? $"pipeline '{sponsored.PipelineId}' failed"); // the page is served without sponsored rows
                ads = null;
            }

            var organicScores = list.Columns.IndexOf(organicScore);
            var sponsoredScores = ads?.Columns.IndexOf(sponsoredScore) ?? 0;
            if (organicScores < 0 || sponsoredScores < 0)
            {
                context.Fail("a called pipeline returned no score column");
                return;
            }

            // The placement: organic rows in their order, a sponsored one after every `gap` of them. A row source appends
            // only keys the request has not seen: one already there (an earlier node's) is passed over, and takes no place.
            var table = context.Table;
            var most = Math.Min(Count(context.Param(limit)), table.FreeRows);
            var keys = new List<long>(most);
            var scores = new List<ScalarValue>(most);
            var flags = new List<bool>(most);
            var placed = new HashSet<long>();
            var nextAd = 0;
            var sinceAd = 0;
            for (var i = 0; i < list.Columns.Count && keys.Count < most; i++)
            {
                var key = list.Columns.Ids[i];
                if (table.ContainsKey(key) || !placed.Add(key.Int64Value))
                {
                    continue;
                }

                keys.Add(key.Int64Value);
                scores.Add(list.Columns.Column(organicScores)[i]);
                flags.Add(false);
                if (++sinceAd == gap && keys.Count < most && ads is not null)
                {
                    sinceAd = 0;
                    while (nextAd < ads.Columns.Count)
                    {
                        var at = nextAd++;
                        var ad = ads.Columns.Ids[at];
                        if (!table.ContainsKey(ad) && !Contains(list, ad.Int64Value) && placed.Add(ad.Int64Value))
                        {
                            keys.Add(ad.Int64Value);
                            scores.Add(ads.Columns.Column(sponsoredScores)[at]);
                            flags.Add(true);
                            break;
                        }
                    }
                }
            }

            if (keys.Count == 0)
            {
                return;
            }

            var first = context.AppendRows(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(keys));
            var scoreColumn = context.Output(score);
            var markColumn = (ScalarColumn<bool>)context.Output(mark);
            for (var i = 0; i < keys.Count; i++)
            {
                // A score that is missing stays missing: the call was declared to read it into this column's type, so a
                // column that cannot be missing never gets one.
                if (!ColumnValues.TrySet(scoreColumn, first + i, scores[i]))
                {
                    context.Fail($"a called pipeline returned the score {scores[i]}, which '{score.Name}' ({scoreColumn.Type}) cannot hold");
                    return;
                }

                markColumn.Set(first + i, flags[i]);
            }

            markColumn.SetValid(first, keys.Count);
        }

        /// <summary>Whether the organic list has the id: a sponsored row is not one the page shows anyway.</summary>
        private static bool Contains(PipelineCallResult list, long id)
        {
            foreach (var key in list.Columns.Ids)
            {
                if (key.Int64Value == id)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
