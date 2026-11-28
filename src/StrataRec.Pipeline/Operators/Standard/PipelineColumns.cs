using System.Globalization;
using StrataRec.Artifacts.Json;
using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Pipeline.Execution;

namespace StrataRec.Pipeline.Operators.Standard;

/// <summary>Config of <c>PipelineColumns@1</c>.</summary>
public sealed class PipelineColumnsConfig
{
    /// <summary>
    /// The pipeline every row calls. With <c>pipelines</c>, the one for the rows none of its entries is for; without
    /// either, such a row calls nothing and gets no values.
    /// </summary>
    public string? Pipeline { get; init; }

    /// <summary>
    /// What says which entry of <c>pipelines</c> a row calls: a row-level expression of a string or an integer, such as
    /// <c>col.kind</c>. Without it, the row's key (<c>col.entity_id</c>).
    /// </summary>
    public string? By { get; init; }

    /// <summary>
    /// The pipelines by the value of <c>by</c>, such as <c>{ "ads": { "pipeline": "sponsored" } }</c>: rows of different
    /// kinds call different pipelines, all at once. What the node writes (<c>ids</c>, <c>columns</c>, <c>values</c>) is
    /// every entry's, which may add to it and replace it by name; how a pipeline is called (<c>inputs</c>) is its own.
    /// </summary>
    public Dictionary<string, PipelineColumnsEntry>? Pipelines { get; init; }

    /// <summary>
    /// Inputs to pass to <c>pipeline</c>: its input name → an expression, which may read the row (<c>col.*</c>), such as
    /// <c>{ "genre": "col.genre" }</c>. Every other input the called pipeline declares gets this request's input of the
    /// same name, when it has one. An entry of <c>pipelines</c> has inputs of its own.
    /// </summary>
    public Dictionary<string, string>? Inputs { get; init; }

    /// <summary>
    /// The column (<c>list&lt;int64&gt;</c> or <c>list&lt;string&gt;</c>, as the called pipeline's keys are, declared under
    /// <c>columns</c>) that gets the ids of the rows the call returned, in their order.
    /// </summary>
    public string? Ids { get; init; }

    /// <summary>
    /// Lists to fill: this pipeline's list column (declared under <c>columns</c>) → the called pipeline's output column,
    /// such as <c>{ "game_scores": "score" }</c>. Element i of each list is of the row whose id is element i of
    /// <c>ids</c>. The output column must have a value on every row: a list has no missing elements.
    /// </summary>
    public Dictionary<string, string>? Columns { get; init; }

    /// <summary>
    /// Values to take: this pipeline's column (declared under <c>columns</c>) → the called pipeline's output value
    /// (<c>output.values</c>), such as <c>{ "best": "score.best" }</c>.
    /// </summary>
    public Dictionary<string, string>? Values { get; init; }

    /// <summary>
    /// The most rows it calls for, 1 to 1000: the first ones in rank order. Required: every row is a run of a pipeline,
    /// so the node says how many it may start. The rows after them get no values, and the node is degraded.
    /// </summary>
    public required int MaxCalls { get; init; }

    /// <summary>
    /// The deadline of each called run, in milliseconds from when it is called; without it, what the node has left. It is
    /// the called pipeline's deadline as a request's is: a pipeline that fails when it runs out of time fails the call,
    /// and its row gets no values; one that serves what it has by then (a node of its own with <c>on_failure: skip</c>)
    /// answers with that.
    /// </summary>
    public int? TimeoutMs { get; init; }

    /// <summary>
    /// Caches the results of <c>pipeline</c>'s calls, each by the inputs its row passes: while a result is cached the
    /// pipeline does not run for that row, such as <c>{ "ttl_ms": 3600000 }</c>. The environment says where the results
    /// are kept (<c>pipelines.&lt;id&gt;.cache</c> in the bindings). An entry of <c>pipelines</c> has a cache of its own.
    /// </summary>
    public PipelineCallCacheConfig? Cache { get; init; }

    /// <summary>
    /// Columns written on each row whose call returned, that say which request's run computed what the row got, such as
    /// <c>{ "request_id": "games.request_id" }</c>. Every entry's.
    /// </summary>
    public ComputedByColumns? ComputedBy { get; init; }
}

/// <summary>An entry of <c>pipelines</c> of <c>PipelineColumns@1</c>: the pipeline the rows of one kind call.</summary>
public sealed class PipelineColumnsEntry
{
    /// <summary>The pipeline these rows call.</summary>
    public required string Pipeline { get; init; }

    /// <summary>Inputs to pass to this pipeline, as the node's <c>inputs</c> are to <c>pipeline</c>.</summary>
    public Dictionary<string, string>? Inputs { get; init; }

    /// <summary>The column that gets the ids, when not the node's <c>ids</c>.</summary>
    public string? Ids { get; init; }

    /// <summary>
    /// Lists to fill besides the node's <c>columns</c>, such as an output column only this pipeline has
    /// (<c>{ "track_ids": "track_id" }</c>); one for the same list replaces the node's.
    /// </summary>
    public Dictionary<string, string>? Columns { get; init; }

    /// <summary>Values to take besides the node's <c>values</c>; one for the same column replaces the node's.</summary>
    public Dictionary<string, string>? Values { get; init; }

    /// <summary>Caches the results of this pipeline's calls, as the node's <c>cache</c> does those of <c>pipeline</c>.</summary>
    public PipelineCallCacheConfig? Cache { get; init; }
}

/// <summary>
/// <c>PipelineColumns@1</c> (ColumnWriter): calls a pipeline for each row, and writes what it returned into the row's
/// columns: the ids of its rows and their output columns as lists, its output values as values. The rows of a page are
/// its sections, say, and each section's items are the list a pipeline returned for it: an item may be in several
/// sections, since each row holds its own list, and a section is ranked, filtered and logged as any row is.
/// </summary>
/// <remarks>
/// <para>The calls are started in rank order and run at once. A row whose call fails gets no values and the node is
/// degraded; the node fails when every call fails. A row without a value has an empty list in a list column that is not
/// nullable and is missing in a nullable one; a column that is not a list must be nullable.</para>
/// </remarks>
public sealed class PipelineColumnsFactory : IOperatorFactory
{
    private const int MostCalls = 1000;

    public OperatorDescriptor Descriptor { get; } = new("PipelineColumns", 1, Effects.ColumnWriter, typeof(PipelineColumnsConfig),
        "Calls a pipeline for each row and writes what it returns into the row's columns: its rows as lists, its values as values.");

    public Operator? Compile(OperatorCompileContext context)
    {
        var config = context.BindConfig<PipelineColumnsConfig>();
        if (config is null)
        {
            return null;
        }

        var ok = true;
        var entries = config.Pipelines is { Count: > 0 } ? config.Pipelines : null;
        if (config.Pipeline is null && entries is null)
        {
            context.Error(string.Empty, "PIPELINE_COLUMNS_CONFIG", "PipelineColumns@1 names no pipeline.", "set 'pipeline', or 'pipelines' with an entry per kind of row");
            ok = false;
        }

        if (config.MaxCalls is < 1 or > MostCalls)
        {
            context.Error("/max_calls", "PIPELINE_COLUMNS_CONFIG", $"'max_calls' is {config.MaxCalls}, and must be 1 to {MostCalls}.",
                "every row is a run of a pipeline: keep the rows that are called few (TopK@1 before the node)");
            ok = false;
        }

        if (config.TimeoutMs is <= 0)
        {
            context.Error("/timeout_ms", "PIPELINE_COLUMNS_CONFIG", "'timeout_ms' must be positive.", "leave it out to give each call all the node has");
            ok = false;
        }

        CompiledExpression? by = null;
        if (config.By is not null && entries is null)
        {
            context.Error("/by", "PIPELINE_COLUMNS_CONFIG", "'by' chooses among 'pipelines', and there are none.", "add 'pipelines', or remove 'by'");
            ok = false;
        }
        else if (entries is not null)
        {
            by = context.CompileExpression(config.By ?? "col.entity_id", "/by", CompileOptions.Any);
            if (by is null)
            {
                ok = false;
            }
            else if (by.ResultType.IsList || by.ResultType.Kind is not (StorageKind.String or StorageKind.Int64))
            {
                context.Error("/by", "PIPELINE_COLUMNS_CONFIG", $"'by' is {by.ResultType}, and must be a string or an integer: it names an entry of 'pipelines'.");
                ok = false;
            }
        }

        if (config.Inputs is { Count: > 0 } && config.Pipeline is null)
        {
            context.Error("/inputs", "PIPELINE_COLUMNS_CONFIG", "'inputs' are the inputs of 'pipeline', and there is none.",
                "give each entry of 'pipelines' its inputs under the entry");
            ok = false;
        }

        if (config.Cache is not null && config.Pipeline is null)
        {
            context.Error("/cache", "PIPELINE_COLUMNS_CONFIG", "'cache' is the cache of the calls of 'pipeline', and there is none.",
                "give each entry of 'pipelines' its cache under the entry");
            ok = false;
        }

        var build = new Builder(context);
        var computedBy = default(ComputedByOutputs);
        ok &= config.ComputedBy?.TryWrite(context, "/computed_by", out computedBy) ?? true;
        Callee? all = null;
        if (config.Pipeline is not null)
        {
            ok &= build.Callee(config.Pipeline, "/pipeline", config, null, null, out all);
        }

        var byKey = new Dictionary<string, Callee>(StringComparer.Ordinal);
        if (entries is not null)
        {
            foreach (var key in Sorted(entries.Keys))
            {
                var entry = entries[key];
                if (entry is null)
                {
                    context.Error("/pipelines/" + JsonPointer.Escape(key), "PIPELINE_COLUMNS_CONFIG", $"Entry '{key}' of 'pipelines' is null.", "give it a pipeline, or remove it");
                    ok = false;
                    continue;
                }

                if (build.Callee(entry.Pipeline, "/pipelines/" + JsonPointer.Escape(key) + "/pipeline", config, entry, "/pipelines/" + JsonPointer.Escape(key), out var callee))
                {
                    byKey.Add(key, callee);
                }
                else
                {
                    ok = false;
                }
            }
        }

        if (ok && build.Targets.Count == 0 && config.ComputedBy is null)
        {
            context.Error(string.Empty, "PIPELINE_COLUMNS_CONFIG", "PipelineColumns@1 writes no column.", "set 'ids', 'columns', 'values' or 'computed_by'");
            ok = false;
        }

        return ok
            ? new PipelineColumns([.. build.Targets], all, byKey, by, config.MaxCalls, config.TimeoutMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null, computedBy)
            : null;
    }

    private static List<string> Sorted(IEnumerable<string>? keys)
    {
        var names = new List<string>();
        if (keys is not null)
        {
            names.AddRange(keys);
            names.Sort(StringComparer.Ordinal);
        }

        return names;
    }

    /// <param name="Ids">The target that gets the ids; -1 for none.</param>
    private sealed record Callee(
        PipelineCall Call, (string Name, CompiledExpression Expression)[] Inputs, int Ids, (int Target, string From)[] Columns, (int Target, string From)[] Values);

    private sealed class Builder(OperatorCompileContext context)
    {
        private readonly Dictionary<string, int> _targets = new(StringComparer.Ordinal);

        public List<OutputColumn> Targets { get; } = new();

        public bool Callee(string pipelineId, string at, PipelineColumnsConfig node, PipelineColumnsEntry? entry, string? entryAt, out Callee callee)
        {
            callee = null!;
            if (!context.TryCallPipeline(pipelineId, at, out var call, entry is null ? node.Cache : entry.Cache, entryAt + "/cache"))
            {
                return false;
            }

            var ok = true;

            var inputs = new List<(string Name, CompiledExpression Expression)>();
            foreach (var (name, source, key) in entry is null ? Merged(node.Inputs, "/inputs/", null, null!) : Merged(null, null!, entry.Inputs, entryAt + "/inputs/"))
            {
                var expression = context.CompileExpression(source, key, CompileOptions.Any);
                if (expression is null)
                {
                    ok = false;
                    continue;
                }

                ok &= context.TryCallInput(call, name, key, context.Schema.DeclaredType(expression));
                inputs.Add((name, expression));
            }

            var ids = -1;
            var idsColumn = entry?.Ids ?? node.Ids;
            if (idsColumn is not null)
            {
                var key = entry?.Ids is not null ? entryAt + "/ids" : "/ids";
                if (!Target(idsColumn, key, out ids, out var type))
                {
                    ok = false;
                }
                else if (!type.IsList || type.IsFixedLength || type.Element is not (StorageKind.Int64 or StorageKind.String))
                {
                    context.Error(key, "COLUMN_TYPE_MISMATCH", $"Column '{idsColumn}' is {type}; the ids of a pipeline's rows are a list of int64 or of string.",
                        "declare it as { \"type\": \"list<int64>\" }, or list<string> for a pipeline with string keys");
                    ok = false;
                }
                else
                {
                    ok &= context.CallReadsKeys(call, key, type.Element == StorageKind.String ? EntityKeyType.String : EntityKeyType.Int64);
                }
            }

            var columns = new List<(int Target, string From)>();
            foreach (var (name, source, key) in Merged(node.Columns, "/columns/", entry?.Columns, entryAt + "/columns/"))
            {
                if (name == idsColumn)
                {
                    ok = Twice(name, key);
                }
                else if (!Target(name, key, out var target, out var type))
                {
                    ok = false;
                }
                else if (!type.IsList || type.IsFixedLength)
                {
                    context.Error(key, "COLUMN_TYPE_MISMATCH", $"Column '{name}' is {type}; an output column of the called pipeline's rows is a list, of any length.",
                        $"declare it as a list of the output column's type, e.g. {{ \"type\": \"list<float32>\" }}");
                    ok = false;
                }
                else
                {
                    ok &= context.TryCallColumn(call, source, key, type.ElementType);
                    columns.Add((target, source));
                }
            }

            var values = new List<(int Target, string From)>();
            foreach (var (name, source, key) in Merged(node.Values, "/values/", entry?.Values, entryAt + "/values/"))
            {
                if (name == idsColumn || (node.Columns?.ContainsKey(name) ?? false) || (entry?.Columns?.ContainsKey(name) ?? false))
                {
                    ok = Twice(name, key);
                }
                else if (!Target(name, key, out var target, out var type))
                {
                    ok = false;
                }
                else if ((!type.IsList || type.IsFixedLength) && !type.Nullable)
                {
                    // A list of any length is empty there; one of a fixed length has no empty value.
                    context.Error(key, "COLUMN_TYPE_MISMATCH", $"Column '{name}' is {type}; a row whose call fails, or that calls nothing, has no value in it.",
                        $"declare it as {{ \"type\": \"{type.AsNullable()}\" }}");
                    ok = false;
                }
                else
                {
                    // A list that is not nullable gets an empty list where there is no value, so the value may be missing.
                    ok &= context.TryCallValue(call, source, key, type.IsList ? type.AsNullable() : type);
                    values.Add((target, source));
                }
            }

            callee = new Callee(call, [.. inputs], ids, [.. columns], [.. values]);
            return ok;
        }

        private bool Twice(string column, string at)
        {
            context.Error(at, "COLUMN_WRITTEN_TWICE", $"Column '{column}' gets two things of a pipeline's result.", "give each of 'ids', 'columns' and 'values' a column of its own");
            return false;
        }

        private bool Target(string name, string key, out int target, out ColumnType type)
        {
            type = default;
            if (!_targets.TryGetValue(name, out target))
            {
                target = -1;
                if (context.TryWriteColumn(name, key, null, out var column))
                {
                    target = Targets.Count;
                    Targets.Add(column);
                }

                _targets.Add(name, target);
            }

            if (target < 0)
            {
                return false;
            }

            type = Targets[target].Type;
            return true;
        }

        private static List<(string Name, string Source, string Key)> Merged(
            Dictionary<string, string>? node, string nodeAt, Dictionary<string, string>? entry, string entryAt)
        {
            var merged = new List<(string Name, string Source, string Key)>();
            foreach (var name in Sorted(node?.Keys))
            {
                if (entry is null || !entry.ContainsKey(name))
                {
                    merged.Add((name, node![name], nodeAt + JsonPointer.Escape(name)));
                }
            }

            foreach (var name in Sorted(entry?.Keys))
            {
                merged.Add((name, entry![name], entryAt + JsonPointer.Escape(name)));
            }

            merged.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            return merged;
        }
    }

    private sealed class PipelineColumns(
        OutputColumn[] targets, Callee? all, Dictionary<string, Callee> byKey, CompiledExpression? by, int maxCalls, TimeSpan? timeout,
        ComputedByOutputs computedBy)
        : Operator
    {
        public override async ValueTask ExecuteAsync(OperatorContext context)
        {
            var live = context.LiveRows;
            if (live.Length == 0)
            {
                return;
            }

            // The rows called, in rank order: the span of live rows does not outlive an await.
            var rows = live[..Math.Min(live.Length, maxCalls)].ToArray();
            var uncalled = live[rows.Length..].ToArray();
            var liveCount = live.Length;
            var callees = new Callee?[rows.Length];
            var given = Prepare(context, rows, callees);
            var calls = new Task<PipelineCallResult>?[rows.Length];
            var made = new List<Task<PipelineCallResult>>(rows.Length);

            // Started in rank order, once every row's inputs are there: a call's place among the node's calls is its
            // row's, whatever order they finish in, so the called runs get the same seeds every time and a recording
            // replays; and nothing that can fail lies between two calls, so none is left running by a failure here.
            for (var i = 0; i < rows.Length; i++)
            {
                if (callees[i] is { } callee)
                {
                    calls[i] = context.CallPipelineAsync(callee.Call, given[i], timeout).AsTask();
                    made.Add(calls[i]!);
                }
            }

            // Every call is awaited before any is read: one that throws (the caller cancelled) leaves none running unobserved.
            if (made.Count > 0)
            {
                await Task.WhenAll(made).ConfigureAwait(false);
            }

            var buffers = new ColumnBuffer[targets.Length];
            for (var t = 0; t < targets.Length; t++)
            {
                buffers[t] = context.Output(targets[t]);
            }

            var written = new bool[targets.Length];
            var failed = 0;
            string? firstError = null;
            for (var i = 0; i < rows.Length; i++)
            {
                Array.Clear(written);
                if (calls[i] is { } call)
                {
                    var result = await call.ConfigureAwait(false);
                    if (!result.Succeeded)
                    {
                        failed++;
                        firstError ??= result.Error ?? $"pipeline '{result.PipelineId}' failed";
                    }
                    else if (Write(callees[i]!, result, rows[i], buffers, written) is { } defect)
                    {
                        context.Fail(defect);
                        return;
                    }
                    else
                    {
                        computedBy.Write(context, rows[i], 1, result);
                    }
                }

                Blank(rows[i], buffers, written);
            }

            Array.Clear(written);
            foreach (var row in uncalled)
            {
                Blank(row, buffers, written);
            }

            if (failed > 0 && failed == made.Count)
            {
                context.Fail(made.Count == 1 ? firstError! : $"each of the {made.Count} calls failed; the first: {firstError}");
            }
            else if (failed > 0)
            {
                context.ReportDegraded($"{failed} of {made.Count} calls failed; the first: {firstError}");
            }
            else if (uncalled.Length > 0)
            {
                context.ReportDegraded($"{uncalled.Length} of {liveCount} rows were not called: max_calls is {maxCalls}");
            }
        }

        /// <summary>
        /// Which pipeline each row calls (none for a row of no kind the node has a pipeline for) and the inputs it passes,
        /// before any call is made: an input that cannot be read fails the node with nothing started.
        /// </summary>
        private RequestInputs?[] Prepare(OperatorContext context, int[] rows, Callee?[] callees)
        {
            var eval = context.EvalContext;
            var capacity = Math.Max(1, context.Table.StorageCapacity);
            ColumnBuffer? kinds = null;
            if (by is not null)
            {
                kinds = ColumnBuffer.Create(by.ResultType.AsNullable(), capacity, context.Memory);
                by.Evaluate(eval, rows, kinds);
            }

            // A row-level input is evaluated once for the rows called, whichever pipelines take it: for all of them, a
            // row of another kind too, since telling them apart first costs more than it saves beside a call a row.
            Dictionary<CompiledExpression, ColumnBuffer>? perRow = null;
            var inputs = new RequestInputs?[rows.Length];
            for (var i = 0; i < rows.Length; i++)
            {
                var callee = all;
                if (kinds is not null && ColumnValues.Get(kinds, rows[i]) is { IsMissing: false } kind
                    && byKey.TryGetValue(kind.Kind == StorageKind.String ? kind.AsString() : kind.AsInt64().ToString(CultureInfo.InvariantCulture), out var entry))
                {
                    callee = entry;
                }

                if (callee is null)
                {
                    continue;
                }

                RequestInputs? given = null;
                if (callee.Inputs.Length > 0)
                {
                    given = new RequestInputs(callee.Inputs.Length);
                    foreach (var (name, expression) in callee.Inputs)
                    {
                        if (!expression.IsRowDependent)
                        {
                            given.Set(name, expression.EvaluateScalar(eval));
                            continue;
                        }

                        perRow ??= new Dictionary<CompiledExpression, ColumnBuffer>(ReferenceEqualityComparer.Instance);
                        if (!perRow.TryGetValue(expression, out var values))
                        {
                            values = ColumnBuffer.Create(expression.ResultType.AsNullable(), capacity, context.Memory);
                            expression.Evaluate(eval, rows, values);
                            perRow.Add(expression, values);
                        }

                        given.Set(name, ColumnValues.Get(values, rows[i]));
                    }
                }

                callees[i] = callee;
                inputs[i] = given;
            }

            return inputs;
        }

        /// <summary>
        /// Writes a result into the row's columns; returns what does not fit, which fails the node: the call was checked
        /// against the pipeline's interface, so a column the result lacks or a value its column cannot hold is a defect.
        /// </summary>
        private string? Write(Callee callee, PipelineCallResult result, int row, ColumnBuffer[] buffers, bool[] written)
        {
            var rows = result.Columns;
            if (callee.Ids >= 0)
            {
                var ids = rows.Ids;
                switch (buffers[callee.Ids])
                {
                    case ListColumn<long> list:
                        var longs = list.SetUninitialized(row, ids.Length);
                        for (var i = 0; i < ids.Length; i++)
                        {
                            if (ids[i].IsString)
                            {
                                return $"pipeline '{result.PipelineId}' returned string keys, which '{targets[callee.Ids].Name}' ({list.Type}) cannot hold";
                            }

                            longs[i] = ids[i].Int64Value;
                        }

                        break;
                    case ListColumn<string> list:
                        var strings = list.SetUninitialized(row, ids.Length);
                        for (var i = 0; i < ids.Length; i++)
                        {
                            if (!ids[i].IsString)
                            {
                                return $"pipeline '{result.PipelineId}' returned int64 keys, which '{targets[callee.Ids].Name}' ({list.Type}) cannot hold";
                            }

                            strings[i] = ids[i].StringValue!;
                        }

                        break;
                }

                written[callee.Ids] = true;
            }

            foreach (var (target, from) in callee.Columns)
            {
                var index = rows.IndexOf(from);
                if (index < 0)
                {
                    return $"pipeline '{result.PipelineId}' returned no column '{from}'";
                }

                if (!TrySetList((ListColumn)buffers[target], row, rows.Column(index), out var misfit))
                {
                    return $"column '{from}' of pipeline '{result.PipelineId}' returned {misfit}, which an element of '{targets[target].Name}' ({buffers[target].Type}) cannot hold";
                }

                written[target] = true;
            }

            foreach (var (target, from) in callee.Values)
            {
                if (!result.Values.TryGetValue(from, out var value))
                {
                    return $"pipeline '{result.PipelineId}' returned no value '{from}'";
                }

                if (value.IsMissing)
                {
                    continue;
                }

                var into = buffers[target];
                if (!value.TryConvert(into.Type, out var converted) || !ColumnValues.TrySet(into, row, converted))
                {
                    return $"value '{from}' of pipeline '{result.PipelineId}' is {value}, which '{targets[target].Name}' ({into.Type}) cannot hold";
                }

                written[target] = true;
            }

            return null;
        }

        private static bool TrySetList(ListColumn into, int row, ReadOnlySpan<ScalarValue> values, out ScalarValue misfit)
        {
            var element = into.Type.ElementType;
            misfit = default;
            switch (into)
            {
                case ListColumn<long> list:
                    var longs = list.SetUninitialized(row, values.Length);
                    for (var i = 0; i < values.Length; i++)
                    {
                        if (!Element(values[i], element, out var value, out misfit))
                        {
                            return false;
                        }

                        longs[i] = value.AsInt64();
                    }

                    return true;
                case ListColumn<int> list:
                    var ints = list.SetUninitialized(row, values.Length);
                    for (var i = 0; i < values.Length; i++)
                    {
                        if (!Element(values[i], element, out var value, out misfit))
                        {
                            return false;
                        }

                        ints[i] = value.AsInt32();
                    }

                    return true;
                case ListColumn<double> list:
                    var doubles = list.SetUninitialized(row, values.Length);
                    for (var i = 0; i < values.Length; i++)
                    {
                        if (!Element(values[i], element, out var value, out misfit))
                        {
                            return false;
                        }

                        doubles[i] = value.AsFloat64();
                    }

                    return true;
                case ListColumn<float> list:
                    var floats = list.SetUninitialized(row, values.Length);
                    for (var i = 0; i < values.Length; i++)
                    {
                        if (!Element(values[i], element, out var value, out misfit))
                        {
                            return false;
                        }

                        floats[i] = value.AsFloat32();
                    }

                    return true;
                case ListColumn<bool> list:
                    var bools = list.SetUninitialized(row, values.Length);
                    for (var i = 0; i < values.Length; i++)
                    {
                        if (!Element(values[i], element, out var value, out misfit))
                        {
                            return false;
                        }

                        bools[i] = value.AsBool();
                    }

                    return true;
                case ListColumn<string> list:
                    var strings = list.SetUninitialized(row, values.Length);
                    for (var i = 0; i < values.Length; i++)
                    {
                        if (!Element(values[i], element, out var value, out misfit))
                        {
                            return false;
                        }

                        strings[i] = value.AsString();
                    }

                    return true;
                default:
                    return false;
            }
        }

        private static bool Element(in ScalarValue value, ColumnType element, out ScalarValue converted, out ScalarValue misfit)
        {
            misfit = value;
            return value.TryConvert(element, out converted) && !converted.IsMissing;
        }

        /// <summary>
        /// Written, not left as the buffer is: a row an earlier attempt of the node wrote has that attempt's extent.
        /// </summary>
        private void Blank(int row, ColumnBuffer[] buffers, bool[] written)
        {
            for (var t = 0; t < targets.Length; t++)
            {
                if (written[t] || targets[t].Type.Nullable)
                {
                    continue;
                }

                switch (buffers[t])
                {
                    case ListColumn<long> list: list.SetUninitialized(row, 0); break;
                    case ListColumn<int> list: list.SetUninitialized(row, 0); break;
                    case ListColumn<double> list: list.SetUninitialized(row, 0); break;
                    case ListColumn<float> list: list.SetUninitialized(row, 0); break;
                    case ListColumn<bool> list: list.SetUninitialized(row, 0); break;
                    case ListColumn<string> list: list.SetUninitialized(row, 0); break;
                }
            }
        }
    }
}
