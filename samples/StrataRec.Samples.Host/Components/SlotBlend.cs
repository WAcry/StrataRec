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

    [Description("Parameter (int64) holding how many organic rows come before each sponsored row: 3 puts them at ranks 4, 8, …; 0 places none.")]
    public required string EveryParam { get; init; }

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
/// (<see cref="OperatorContext.ReportDegraded"/>).</item>
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
        return ok ? new SlotBlend(organic!, sponsored!, config.OrganicScore, config.SponsoredScore, limit, every, score, mark) : null;
    }

    private sealed class SlotBlend(
        PipelineCall organic, PipelineCall sponsored, string organicScore, string sponsoredScore, ParamRef limit, ParamRef every, OutputColumn score, OutputColumn mark)
        : Operator
    {
        /// <summary>A row source says how many rows it may append: the request's memory is sized by it.</summary>
        public override int GetRowLimit(ReadOnlySpan<ScalarValue> parameters) => (int)parameters[limit.Id].AsInt64();

        public override async ValueTask ExecuteAsync(OperatorContext context)
        {
            // Started one after the other, awaited afterwards: the two pipelines run at once, and the calls are numbered
            // in this order every time, which is what their seeds are derived from.
            var organicCall = context.CallPipelineAsync(organic).AsTask();
            var sponsoredCall = context.CallPipelineAsync(sponsored).AsTask();
            var list = await organicCall;
            var ads = await sponsoredCall;
            if (!list.Succeeded)
            {
                context.Fail(list.Error ?? $"pipeline '{organic.PipelineId}' failed");
                return;
            }

            if (!ads.Succeeded)
            {
                context.ReportDegraded(ads.Error ?? $"pipeline '{sponsored.PipelineId}' failed"); // the page is served without sponsored rows
            }

            var organicScores = list.Columns.IndexOf(organicScore);
            var sponsoredScores = ads.Succeeded ? ads.Columns.IndexOf(sponsoredScore) : -1;
            if (organicScores < 0 || (ads.Succeeded && sponsoredScores < 0))
            {
                context.Fail("a called pipeline returned no score column");
                return;
            }

            // The placement: organic rows in their order, a sponsored one after every `gap` of them, none twice.
            var most = (int)Math.Min(context.Param(limit).AsInt64(), context.Table.FreeRows);
            var gap = (int)context.Param(every).AsInt64();
            var keys = new List<long>(most);
            var scores = new List<double>(most);
            var flags = new List<bool>(most);
            var placed = new HashSet<long>();
            var nextAd = 0;
            var sinceAd = 0;
            for (var i = 0; i < list.Columns.Count && keys.Count < most; i++)
            {
                if (gap > 0 && sinceAd == gap && TakeAd(ads, sponsoredScores, placed, list, ref nextAd, out var ad, out var bid) && keys.Count < most - 1)
                {
                    keys.Add(ad);
                    scores.Add(bid);
                    flags.Add(true);
                    sinceAd = 0;
                }

                var id = list.Columns.Ids[i].Int64Value;
                if (placed.Add(id))
                {
                    keys.Add(id);
                    scores.Add(list.Columns.Column(organicScores)[i].AsFloat64());
                    flags.Add(false);
                    sinceAd++;
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
                ColumnValues.Set(scoreColumn, first + i, ScalarValue.FromFloat64(scores[i]));
                markColumn.Set(first + i, flags[i]);
            }

            scoreColumn.SetValid(first, keys.Count);
            markColumn.SetValid(first, keys.Count);
        }

        /// <summary>The next sponsored row that is not in the organic list and was not placed yet.</summary>
        private static bool TakeAd(PipelineCallResult ads, int scores, HashSet<long> placed, PipelineCallResult list, ref int next, out long id, out double bid)
        {
            while (ads.Succeeded && next < ads.Columns.Count)
            {
                var at = next++;
                id = ads.Columns.Ids[at].Int64Value;
                if (!placed.Contains(id) && !Contains(list, id) && placed.Add(id))
                {
                    bid = ads.Columns.Column(scores)[at].AsFloat64();
                    return true;
                }
            }

            id = 0;
            bid = 0;
            return false;
        }

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
