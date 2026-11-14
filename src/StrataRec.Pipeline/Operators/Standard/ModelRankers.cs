using System.Globalization;
using StrataRec.Core.Columns;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Models;

namespace StrataRec.Pipeline.Operators.Standard;

/// <summary>Config of <c>PairwiseRanker@1</c> (design 5.9).</summary>
public sealed class PairwiseRankerConfig : ModelCallConfig
{
    /// <summary>
    /// The model output read: float32 <c>[-1]</c>, one value per pair, the probability that the pair's first (higher-ranked)
    /// row is preferred.
    /// </summary>
    public required string Output { get; init; }

    /// <summary>The leading ranks to rerank (at least 2); later rows keep their order after them.</summary>
    public int Window { get; init; } = 20;

    /// <summary>
    /// Load-time ceiling on the pairs a request sends: <c>window · (window − 1) / 2</c> above it is a config error. Every
    /// pair in the window is compared; none is dropped at run time. The default allows a window of 50.
    /// </summary>
    public int MaxPairs { get; init; } = 1225;

    /// <summary>Pairs per model call; the calls run concurrently.</summary>
    public int BatchSize { get; init; } = 256;

    /// <summary>
    /// Optional column for each window row's aggregated score (its expected wins against the other rows of the window):
    /// float32? or float64?, missing outside the window. An undeclared column is float32?.
    /// </summary>
    public string? ScoreCol { get; init; }
}

/// <summary>
/// <c>PairwiseRanker@1</c> (RowOrder; ColumnWriter with <c>score_col</c>): reranks the leading <c>window</c> rows by a
/// model that compares two rows at a time (design 5.9).
/// </summary>
/// <remarks>
/// <para>Every pair (i, j), i &lt; j, of the first n = min(window, rows) rows is sent once, the higher-ranked row first,
/// in order of i then j; batches of <c>batch_size</c> pairs are called concurrently, and nothing is applied unless every
/// call succeeded. A column input whose rows give T <c>[rows]</c> (or <c>[rows, N]</c>) is sent as T <c>[pairs, 2]</c>
/// (<c>[pairs, 2, N]</c>); a context value as T <c>[pairs]</c> (<c>[pairs, N]</c>); see <see cref="ModelTensorRule"/>.</para>
/// <para>The output is the probability p that the first row is preferred, so the pair (i, j) gives i a win of p and j a
/// win of 1 − p. A row's score is the sum of its wins (its expected number of wins, a Borda count): unlike counting
/// p &gt; 0.5, it keeps how sure the model was, and it needs no tie rule per pair. A preference that is NaN or outside
/// [0, 1] fails the node rather than being clamped: a model that returns logits would otherwise rank by noise. The
/// window is reordered by score (ties by <c>entity_id</c>); rows after it keep their order.</para>
/// <para>Calls are recorded and replayed by the digest of each whole call (no per-row keys), since an output belongs to
/// a pair, not a row. <see cref="StrataRecMetrics.ModelPairs"/> counts the pairs sent.</para>
/// </remarks>
public sealed class PairwiseRankerFactory : IOperatorFactory
{
    public OperatorDescriptor Descriptor { get; } = new("PairwiseRanker", 1, Effects.RowOrder | Effects.ColumnWriter, typeof(PairwiseRankerConfig),
        "Reranks the leading rows by a model's pairwise preferences, aggregated into expected wins.");

    public Operator? Compile(OperatorCompileContext context)
    {
        var config = context.BindConfig<PairwiseRankerConfig>();
        if (config is null)
        {
            return null;
        }

        var compiler = ModelCallCompiler.Begin(context, config, TensorLayout.Pairs);
        if (compiler is null)
        {
            return null;
        }

        if (config.BatchSize < 1)
        {
            context.Error("/batch_size", "MODEL_BAD_BATCH", "batch_size must be at least 1.");
            return null;
        }

        if (config.Window < 2)
        {
            context.Error("/window", "RANKER_WINDOW", $"window is {config.Window}, but PairwiseRanker@1 compares pairs of rows: it must be at least 2.");
            return null;
        }

        if (config.MaxPairs < 1)
        {
            context.Error("/max_pairs", "RANKER_MAX_PAIRS", "max_pairs must be at least 1.");
            return null;
        }

        var pairs = (long)config.Window * (config.Window - 1) / 2;
        if (pairs > config.MaxPairs)
        {
            context.Error("/window", "RANKER_MAX_PAIRS",
                $"A window of {config.Window} rows makes {pairs.ToString(CultureInfo.InvariantCulture)} pairs, more than max_pairs ({config.MaxPairs}).",
                "lower 'window', or raise 'max_pairs' if the model can take that many pairs in one request: every pair in the window is compared, "
                + "none is dropped at run time (design 5.9)");
            return null;
        }

        var ok = compiler.CompileInputs();
        ok &= RankerColumns.TryCompile(context, config.Output, config.ScoreCol, out var scoreCol);
        if (!ok)
        {
            return null;
        }

        var call = compiler.Finish(new[] { new ModelTensorRule.MappedOutput(config.Output, "/output") });
        return call is null ? null : new PairwiseRanker(call, config.Output, config.Window, config.BatchSize, scoreCol);
    }

    private sealed class PairwiseRanker(CompiledModelCall call, string output, int window, int batchSize, OutputColumn? scoreCol) : Operator
    {
        public override IReadOnlyDictionary<string, string> NamedInputs => call.NamedInputs;

        public override IoTarget ModelTarget => call.Target;

        public override async ValueTask ExecuteAsync(OperatorContext context)
        {
            var total = context.LiveRows.Length;
            var n = Math.Min(window, total);
            if (n < 2)
            {
                if (n == 1 && scoreCol is { } single)
                {
                    // One row has no other row to win against: its score is the empty sum.
                    RankerColumns.Write(context, single, context.LiveRows[..1], [0.0]);
                }

                return;
            }

            // The rows as of the call, kept across the await; rented, and never handed to the model client.
            var memory = context.Memory;
            var rows = memory.Rent<int>(total);
            context.LiveRows.CopyTo(rows);
            var pairs = PairCount(n);
            var pairRows = memory.Rent<int>(checked(2 * pairs));
            var p = 0;
            for (var i = 0; i < n; i++)
            {
                for (var j = i + 1; j < n; j++)
                {
                    pairRows[2 * p] = rows[i];
                    pairRows[(2 * p) + 1] = rows[j];
                    p++;
                }
            }

            var inputs = call.Inputs;
            var values = new GatheredInput[inputs.Length];
            for (var k = 0; k < inputs.Length; k++)
            {
                values[k] = inputs[k].Gather(context, pairRows.AsSpan(0, 2 * pairs), Math.Min(pairs, batchSize));
            }

            var version = call.Version(context);
            var batches = Batching.Count(pairs, batchSize);
            var writers = new ModelOutputWriter[batches];
            var calls = new Task<ModelStatus>[batches];
            for (var b = 0; b < batches; b++)
            {
                var offset = b * batchSize;
                var count = Math.Min(batchSize, pairs - offset);
                var named = new NamedInput[inputs.Length];
                for (var k = 0; k < inputs.Length; k++)
                {
                    named[k] = inputs[k].Batch(values[k], TensorLayout.Pairs, offset, count);
                }

                writers[b] = new ModelOutputWriter(memory);
                calls[b] = call.InvokeAsync(context, version, named, count, writers[b], ReadOnlyMemory<int>.Empty);
            }

            if (StrataRecMetrics.ModelPairs.Enabled && !context.Replays)
            {
                StrataRecMetrics.ModelPairs.Add(pairs, call.Target.Tags);
            }

            // Each wait is bounded by the model's timeout and the node's budget; a batch that failed or was cut off is never
            // read, since its abandoned client may still be writing it.
            var results = await Task.WhenAll(calls).ConfigureAwait(false);
            foreach (var status in results)
            {
                if (!status.Succeeded)
                {
                    context.Fail($"model '{call.Model}' failed: {status.Error}");
                    return;
                }
            }

            var scores = memory.RentCleared<double>(n);
            if (!Aggregate(context, rows, n, writers, scores))
            {
                return;
            }

            if (scoreCol is { } column)
            {
                RankerColumns.Write(context, column, rows.AsSpan(0, n), scores.AsSpan(0, n));
            }

            RowRanking.Sort(rows.AsSpan(0, n), scores.AsSpan(0, n), context.Table, memory);
            context.SetOrder(rows.AsSpan(0, total));
        }

        /// <summary>n·(n−1)/2, at most max_pairs (checked at load), computed without overflowing on the way.</summary>
        private static int PairCount(int n) => checked((int)((long)n * (n - 1) / 2));

        /// <summary>
        /// Adds each pair's preference to its rows' scores, walking the pairs in the order they were sent; false after
        /// failing the node on an output that is missing, short, or not a probability.
        /// </summary>
        private bool Aggregate(OperatorContext context, int[] rows, int n, ModelOutputWriter[] writers, double[] scores)
        {
            var pairs = PairCount(n);
            var batch = 0;
            var index = 0;
            ReadOnlySpan<float> preferences = default;
            for (var i = 0; i < n; i++)
            {
                for (var j = i + 1; j < n; j++)
                {
                    if (index == 0)
                    {
                        var count = Math.Min(batchSize, pairs - (batch * batchSize));
                        if (!writers[batch].TryGet(output, out var values) || values.Length < count)
                        {
                            context.Fail($"model '{call.Model}' did not return output '{output}' for every pair");
                            return false;
                        }

                        preferences = values.Span[..count];
                    }

                    var preference = preferences[index];
                    if (!(preference >= 0f && preference <= 1f))
                    {
                        context.Fail($"model '{call.Model}' returned {preference.ToString("R", CultureInfo.InvariantCulture)} as the preference of "
                            + $"{context.Table.GetKey(rows[i])} over {context.Table.GetKey(rows[j])}, which is not a probability in [0, 1]; "
                            + $"output '{output}' must be the probability that the pair's first row is preferred (apply the sigmoid inside the model)");
                        return false;
                    }

                    scores[i] += preference;
                    scores[j] += 1.0 - preference;
                    if (++index == preferences.Length)
                    {
                        batch++;
                        index = 0;
                    }
                }
            }

            return true;
        }
    }
}

/// <summary>Config of <c>ListwiseReranker@1</c> (design 5.9).</summary>
public sealed class ListwiseRerankerConfig : ModelCallConfig
{
    /// <summary>The model output read: float32 <c>[1, -1]</c>, one score per row of the list, higher first.</summary>
    public required string Output { get; init; }

    /// <summary>The leading ranks sent as one list and reordered by their scores; later rows keep their order after them.</summary>
    public int Window { get; init; } = 50;

    /// <summary>Optional column for each window row's score: float32? or float64?, missing outside the window. An undeclared column is float32?.</summary>
    public string? ScoreCol { get; init; }
}

/// <summary>
/// <c>ListwiseReranker@1</c> (RowOrder; ColumnWriter with <c>score_col</c>): scores the leading <c>window</c> rows in one
/// model call that sees them together, and reorders them by the scores (design 5.9).
/// </summary>
/// <remarks>
/// <para>One call per request, batch size 1: a column input whose rows give T <c>[rows]</c> (or <c>[rows, N]</c>) is sent
/// as T <c>[1, n]</c> (<c>[1, n, N]</c>), in rank order; a context value as T <c>[1]</c> (<c>[1, N]</c>); see
/// <see cref="ModelTensorRule"/>. The window is reordered by score as a sort by a column is (higher first, NaN last, ties
/// by <c>entity_id</c>); rows after it keep their order. Nothing is applied unless the call succeeded.</para>
/// <para>No batch size and no cap: the list is the model's unit, so splitting it would change what the model sees, and
/// <c>window</c> bounds the call. The call is recorded and replayed by the digest of its whole input.</para>
/// </remarks>
public sealed class ListwiseRerankerFactory : IOperatorFactory
{
    public OperatorDescriptor Descriptor { get; } = new("ListwiseReranker", 1, Effects.RowOrder | Effects.ColumnWriter, typeof(ListwiseRerankerConfig),
        "Reranks the leading rows by scores a model gives them as one list.");

    public Operator? Compile(OperatorCompileContext context)
    {
        var config = context.BindConfig<ListwiseRerankerConfig>();
        if (config is null)
        {
            return null;
        }

        var compiler = ModelCallCompiler.Begin(context, config, TensorLayout.List);
        if (compiler is null)
        {
            return null;
        }

        if (config.Window < 1)
        {
            context.Error("/window", "RANKER_WINDOW", $"window is {config.Window}, but it must be at least 1.");
            return null;
        }

        var ok = compiler.CompileInputs();
        ok &= RankerColumns.TryCompile(context, config.Output, config.ScoreCol, out var scoreCol);
        if (!ok)
        {
            return null;
        }

        var call = compiler.Finish(new[] { new ModelTensorRule.MappedOutput(config.Output, "/output") });
        return call is null ? null : new ListwiseReranker(call, config.Output, config.Window, scoreCol);
    }

    private sealed class ListwiseReranker(CompiledModelCall call, string output, int window, OutputColumn? scoreCol) : Operator
    {
        public override IReadOnlyDictionary<string, string> NamedInputs => call.NamedInputs;

        public override IoTarget ModelTarget => call.Target;

        public override async ValueTask ExecuteAsync(OperatorContext context)
        {
            var total = context.LiveRows.Length;
            var n = Math.Min(window, total);
            if (n == 0)
            {
                return;
            }

            // The rows as of the call, kept across the await; rented, and never handed to the model client.
            var memory = context.Memory;
            var rows = memory.Rent<int>(total);
            context.LiveRows.CopyTo(rows);
            var inputs = call.Inputs;
            var named = new NamedInput[inputs.Length];
            for (var k = 0; k < inputs.Length; k++)
            {
                var gathered = inputs[k].Gather(context, rows.AsSpan(0, n), 1);
                named[k] = inputs[k].Batch(gathered, TensorLayout.List, 0, n);
            }

            var writer = new ModelOutputWriter(memory);
            var status = await call.InvokeAsync(context, call.Version(context), named, 1, writer, ReadOnlyMemory<int>.Empty).ConfigureAwait(false);
            if (!status.Succeeded)
            {
                context.Fail($"model '{call.Model}' failed: {status.Error}");
                return;
            }

            if (!writer.TryGet(output, out var values) || values.Length < n)
            {
                context.Fail($"model '{call.Model}' did not return output '{output}' for every row of the list");
                return;
            }

            var scores = memory.Rent<double>(n);
            var span = values.Span;
            for (var i = 0; i < n; i++)
            {
                scores[i] = span[i];
            }

            if (scoreCol is { } column)
            {
                RankerColumns.Write(context, column, rows.AsSpan(0, n), scores.AsSpan(0, n));
            }

            RowRanking.Sort(rows.AsSpan(0, n), scores.AsSpan(0, n), context.Table, memory);
            context.SetOrder(rows.AsSpan(0, total));
        }
    }
}

/// <summary>The keys the model rankers share besides the model call: the output they read and the optional score column.</summary>
internal static class RankerColumns
{
    /// <summary>
    /// Checks <c>output</c> and resolves <c>score_col</c>: a nullable float column, since rows outside the window get no
    /// score; an undeclared one is <c>float32?</c>. False after reporting an error.
    /// </summary>
    public static bool TryCompile(OperatorCompileContext context, string? output, string? name, out OutputColumn? column)
    {
        column = null;
        var ok = true;
        if (string.IsNullOrEmpty(output))
        {
            context.Error("/output", "MODEL_OUTPUT", $"{context.Descriptor.Id} needs 'output': the name of the model output it reads.",
                "set \"output\" to an output name of the model's signature");
            ok = false;
        }

        if (name is null)
        {
            return ok;
        }

        var declared = context.Schema.TryGetColumn(name, out _);
        if (!context.TryWriteColumn(name, "/score_col", declared ? null : ColumnType.Float32.AsNullable(), out var written))
        {
            return false;
        }

        if (!written.Type.IsFloat || !written.Type.Nullable)
        {
            context.Error("/score_col", "COLUMN_TYPE_MISMATCH",
                $"Score column '{name}' is {written.Type}, but {context.Descriptor.Id} scores only the window's rows: it must be float32? or float64?.",
                $"declare '{name}' as float32? or float64?, or leave it undeclared to write float32?");
            return false;
        }

        column = written;
        return ok;
    }

    /// <summary>Writes each row's score; rows not written stay missing.</summary>
    public static void Write(OperatorContext context, OutputColumn column, ReadOnlySpan<int> rows, ReadOnlySpan<double> scores)
    {
        switch (context.Output(column))
        {
            case ScalarColumn<float> f:
                for (var i = 0; i < rows.Length; i++)
                {
                    f.Set(rows[i], (float)scores[i]);
                }

                break;
            case ScalarColumn<double> d:
                for (var i = 0; i < rows.Length; i++)
                {
                    d.Set(rows[i], scores[i]);
                }

                break;
            default:
                throw new InvalidOperationException($"Score column '{column.Name}' is {column.Type}, not float32? or float64?.");
        }
    }
}
