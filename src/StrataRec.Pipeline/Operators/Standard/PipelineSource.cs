using StrataRec.Artifacts.Json;
using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Pipeline.Execution;

namespace StrataRec.Pipeline.Operators.Standard;

/// <summary>Config of <c>PipelineSource@1</c>.</summary>
public sealed class PipelineSourceConfig
{
    /// <summary>The pipeline to call: one the engine serves, of this pipeline's <c>entity_key_type</c>.</summary>
    public required string Pipeline { get; init; }

    /// <summary>
    /// Inputs to pass: the called pipeline's input name → a request-level expression (<c>ctx.*</c>, <c>req.*</c>,
    /// <c>param.*</c>), such as <c>{ "slots": "param.ad_slots" }</c>. Every other input the called pipeline declares gets
    /// this request's input of the same name, when it has one.
    /// </summary>
    public Dictionary<string, string>? Inputs { get; init; }

    /// <summary>
    /// Parameter holding the most rows to append: the result's first rows the request has not seen yet, so a row already
    /// there does not use one up. Required, as every row source's limit is: it bounds the rows a request may hold
    ///. A parameter without a value (a map's key set to null) appends none.
    /// </summary>
    public required string LimitParam { get; init; }

    /// <summary>
    /// Columns to copy: this pipeline's column (declared under <c>columns</c>) → the called pipeline's output column,
    /// such as <c>{ "ad.bid": "bid" }</c>. Written on the appended rows only.
    /// </summary>
    public Dictionary<string, string>? Columns { get; init; }

    /// <summary>
    /// A bool column written <c>true</c> on the appended rows, to tell them from the others, which have no value in it:
    /// read it as <c>col.x ?? false</c>. Undeclared, it is declared <c>bool?</c>; declared, declare it <c>bool?</c> (a
    /// default does not fill the rows of other row sources).
    /// </summary>
    public string? Mark { get; init; }

    /// <summary>
    /// Caches the call's result: while a result is cached the pipeline does not run, such as
    /// <c>{ "ttl_ms": 3600000 }</c>. The environment says where the results are kept (<c>pipelines.&lt;id&gt;.cache</c> in
    /// the bindings).
    /// </summary>
    public PipelineCallCacheConfig? Cache { get; init; }

    /// <summary>
    /// Columns written on the appended rows that say which request's run computed them, such as
    /// <c>{ "request_id": "rank.request_id" }</c>.
    /// </summary>
    public ComputedByColumns? ComputedBy { get; init; }
}

/// <summary>
/// <c>PipelineSource@1</c> (RowSource): calls another pipeline for the request and appends the rows it returns, in its
/// order, after the rows already there. A key the request has seen is skipped, as by every row source, and rows that no
/// longer fit the table are dropped. Fails when the call fails, so <c>on_failure</c> decides whether the request goes on
/// without them.
/// </summary>
/// <remarks>
/// It uses only what any operator may (<see cref="OperatorCompileContext.TryCallPipeline"/>,
/// <see cref="OperatorContext.CallPipelineAsync"/>): what to do with a called pipeline's rows beyond appending them,
/// such as placing them at given ranks, is an operator of one's own. <c>PipelineColumns@1</c> keeps them as a row's lists.
/// </remarks>
public sealed class PipelineSourceFactory : IOperatorFactory
{
    public OperatorDescriptor Descriptor { get; } = new("PipelineSource", 1, Effects.RowSource, typeof(PipelineSourceConfig),
        "Calls another pipeline for the request and appends the rows it returns as candidates.");

    public Operator? Compile(OperatorCompileContext context)
    {
        var config = context.BindConfig<PipelineSourceConfig>();
        if (config is null || !context.TryCallPipeline(config.Pipeline, "/pipeline", out var call, config.Cache))
        {
            return null;
        }

        var ok = context.CallAppendsRows(call, "/pipeline");
        var inputs = new List<(string Name, CompiledExpression Expression)>();
        foreach (var name in Sorted(config.Inputs))
        {
            var key = "/inputs/" + JsonPointer.Escape(name);
            var expression = context.CompileExpression(config.Inputs![name], key, new CompileOptions { RequestLevelOnly = true, AllowNullableResult = true });
            if (expression is null)
            {
                ok = false;
                continue;
            }

            // The declared type of what it reads, not the type its arithmetic is done in: an int32 input passed on as it is
            // is an int32, though an expression computes with int64.
            ok &= context.TryCallInput(call, name, key, context.Schema.DeclaredType(expression));
            inputs.Add((name, expression));
        }

        ok &= context.TryParam(config.LimitParam, "/limit_param", out var limit, StorageKind.Int64, (0, int.MaxValue));

        var columns = new List<(OutputColumn Into, string From)>();
        foreach (var name in Sorted(config.Columns))
        {
            var key = "/columns/" + JsonPointer.Escape(name);
            if (!context.TryWriteColumn(name, key, null, out var into))
            {
                ok = false;
                continue;
            }

            ok &= context.TryCallColumn(call, config.Columns![name], key, into.Type);
            columns.Add((into, config.Columns[name]));
        }

        OutputColumn? mark = null;
        if (config.Mark is not null)
        {
            if (!context.TryWriteColumn(config.Mark, "/mark", ColumnType.Bool.AsNullable(), out var column))
            {
                ok = false;
            }
            else if (column.Type.Kind != StorageKind.Bool || column.Type.IsList)
            {
                context.Error("/mark", "COLUMN_TYPE_MISMATCH", $"PipelineSource@1 writes '{config.Mark}' as bool, but it is declared as {column.Type}.",
                    "declare it as { \"type\": \"bool?\" } and read it as 'col.x ?? false', or leave it undeclared");
                ok = false;
            }
            else
            {
                mark = column;
            }
        }

        var computedBy = default(ComputedByOutputs);
        ok &= config.ComputedBy?.TryWrite(context, "/computed_by", out computedBy) ?? true;
        return ok ? new PipelineSource(call, [.. inputs], limit, [.. columns], mark, computedBy) : null;
    }

    private static List<string> Sorted(Dictionary<string, string>? map)
    {
        var names = new List<string>();
        if (map is not null)
        {
            names.AddRange(map.Keys);
            names.Sort(StringComparer.Ordinal);
        }

        return names;
    }

    private sealed class PipelineSource(
        PipelineCall call, (string Name, CompiledExpression Expression)[] inputs, ParamRef limit, (OutputColumn Into, string From)[] columns, OutputColumn? mark,
        ComputedByOutputs computedBy)
        : Operator
    {
        public override int GetRowLimit(ReadOnlySpan<ScalarValue> parameters) => Limit(parameters[limit.Id]);

        /// <summary>The most rows to append; none for a parameter without a value (a map's key set to null).</summary>
        private static int Limit(in ScalarValue value) => value.IsMissing ? 0 : (int)Math.Clamp(value.AsInt64(), 0, int.MaxValue);

        public override async ValueTask ExecuteAsync(OperatorContext context)
        {
            RequestInputs? given = null;
            if (inputs.Length > 0)
            {
                given = new RequestInputs(inputs.Length);
                foreach (var (name, expression) in inputs)
                {
                    given.Set(name, expression.EvaluateScalar(context.EvalContext));
                }
            }

            var result = await context.CallPipelineAsync(call, given).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                context.Fail(result.Error ?? $"pipeline '{call.PipelineId}' failed");
                return;
            }

            Append(context, result);
        }

        /// <summary>
        /// Appends the result's first rows the request has not seen, as many as the limit and the table allow, and writes
        /// their columns. A column the result lacks, or a value its column cannot hold, fails the node: the call was
        /// checked against the pipeline's interface, so either is a defect, not data.
        /// </summary>
        private void Append(OperatorContext context, PipelineCallResult result)
        {
            var rows = result.Columns;
            var most = Limit(context.Param(limit));

            var from = new int[columns.Length];
            for (var c = 0; c < columns.Length; c++)
            {
                from[c] = rows.IndexOf(columns[c].From);
                if (from[c] < 0)
                {
                    context.Fail($"pipeline '{call.PipelineId}' returned no column '{columns[c].From}'");
                    return;
                }
            }

            var table = context.Table;
            var ids = rows.Ids;
            var stringKeys = table.KeyType == EntityKeyType.String;
            var free = Math.Min(most, table.FreeRows);
            var taken = new List<int>(Math.Min(rows.Count, free));
            for (var r = 0; r < rows.Count && taken.Count < free; r++)
            {
                if (ids[r].IsString == stringKeys && !table.ContainsKey(ids[r]))
                {
                    taken.Add(r);
                }
            }

            if (taken.Count == 0)
            {
                return;
            }

            int first;
            if (stringKeys)
            {
                var keys = new string[taken.Count];
                for (var i = 0; i < keys.Length; i++)
                {
                    keys[i] = ids[taken[i]].StringValue!;
                }

                first = context.AppendRows(keys);
            }
            else
            {
                var keys = new long[taken.Count];
                for (var i = 0; i < keys.Length; i++)
                {
                    keys[i] = ids[taken[i]].Int64Value;
                }

                first = context.AppendRows(keys);
            }

            for (var c = 0; c < columns.Length; c++)
            {
                var into = context.Output(columns[c].Into);
                var values = rows.Column(from[c]);
                for (var i = 0; i < taken.Count; i++)
                {
                    // Converted as the check allows (an integer list read into a float one), then stored.
                    if (!values[taken[i]].TryConvert(into.Type, out var value) || !ColumnValues.TrySet(into, first + i, value))
                    {
                        context.Fail($"column '{columns[c].From}' of pipeline '{call.PipelineId}' returned {values[taken[i]]}, which '{columns[c].Into.Name}' ({into.Type}) cannot hold");
                        return;
                    }
                }
            }

            if (mark is { } column)
            {
                var marked = (ScalarColumn<bool>)context.Output(column);
                for (var i = 0; i < taken.Count; i++)
                {
                    marked.Set(first + i, true);
                }

                marked.SetValid(first, taken.Count);
            }

            computedBy.Write(context, first, taken.Count, result);
        }
    }
}
