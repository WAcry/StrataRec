using StrataRec.Core.Columns;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Models;

namespace StrataRec.Pipeline.Operators.Standard;

/// <summary>Config of <c>ModelScorer@1</c> (design 6.9).</summary>
public sealed class ModelScorerConfig
{
    /// <summary>Logical model name, bound per environment.</summary>
    public required string Model { get; init; }

    /// <summary>Fixed model version; or use <see cref="VersionParam"/>.</summary>
    public string? Version { get; init; }

    /// <summary>String parameter (with an <c>enum</c> of allowed versions) selecting the version per request.</summary>
    public string? VersionParam { get; init; }

    /// <summary>Model input name → column or context reference (<c>col.*</c> / <c>ctx.*</c>), non-nullable numeric.</summary>
    public required Dictionary<string, string> Inputs { get; init; }

    /// <summary>Model output name → column to write (float32 or float64).</summary>
    public required Dictionary<string, string> Outputs { get; init; }

    public int BatchSize { get; init; } = 256;
}

/// <summary>
/// <c>ModelScorer@1</c> (ColumnWriter): pointwise scoring through <see cref="IModelClient"/>. Rows are split into
/// batches called concurrently; outputs commit only when every batch succeeds (design 4.6, 6.9).
/// </summary>
public sealed class ModelScorerFactory : IOperatorFactory
{
    public OperatorDescriptor Descriptor { get; } = new("ModelScorer", 1, Effects.ColumnWriter, typeof(ModelScorerConfig),
        "Scores rows with a model: named inputs from columns, named outputs to columns, batched.");

    public Operator? Compile(OperatorCompileContext context)
    {
        var config = context.BindConfig<ModelScorerConfig>();
        if (config is null)
        {
            return null;
        }

        var bound = context.Model(config.Model, "/model");
        if (bound is null)
        {
            return null;
        }

        if (config.BatchSize < 1)
        {
            context.Error("/batch_size", "MODEL_BAD_BATCH", "batch_size must be at least 1.");
            return null;
        }

        var versions = new List<string?>();
        ParamRef? versionParam = null;
        if (config.VersionParam is not null)
        {
            if (config.Version is not null)
            {
                context.Error("/version", "MODEL_VERSION", "Set at most one of 'version' and 'version_param'.");
                return null;
            }

            if (!context.TryParam(config.VersionParam, "/version_param", out var p, StorageKind.String))
            {
                return null;
            }

            versionParam = p;
            if (!context.Schema.TryGetParam(p.Name, out var leaf) || leaf.Enum is null)
            {
                context.Error("/version_param", "MODEL_VERSION_ENUM", $"Parameter '{p.Name}' must declare an enum of allowed model versions.",
                    "every version is signature-checked at load time (design 6.9)");
                return null;
            }

            foreach (var v in leaf.Enum)
            {
                versions.Add(v.AsString());
            }
        }
        else
        {
            versions.Add(config.Version);
        }

        var inputNames = new List<string>(config.Inputs.Keys);
        inputNames.Sort(StringComparer.Ordinal);
        var inputs = new (string Name, CompiledExpression Expr)[inputNames.Count];
        var ok = true;
        for (var i = 0; i < inputNames.Count; i++)
        {
            var source = config.Inputs[inputNames[i]];
            var expr = context.CompileExpression(source, "/inputs/" + inputNames[i], CompileOptions.Score);
            if (expr is null)
            {
                ok = false;
                continue;
            }

            if (expr.Reads.Count != 1 || !string.Equals(expr.Reads[0].Name, source.Trim(), StringComparison.Ordinal))
            {
                context.Error("/inputs/" + inputNames[i], "MODEL_INPUT_NOT_REFERENCE",
                    $"Model input '{inputNames[i]}' must be a plain column or context reference, not an expression.",
                    "define the transformation as a derived feature so training and serving share one definition (design 6.9)");
                ok = false;
                continue;
            }

            inputs[i] = (inputNames[i], expr);
        }

        var outputNames = new List<string>(config.Outputs.Keys);
        outputNames.Sort(StringComparer.Ordinal);
        var outputs = new (string Name, OutputColumn Column)[outputNames.Count];
        for (var i = 0; i < outputNames.Count; i++)
        {
            if (!context.TryWriteColumn(config.Outputs[outputNames[i]], "/outputs/" + outputNames[i], null, out var column))
            {
                ok = false;
                continue;
            }

            if (!column.Type.IsFloat)
            {
                context.Error("/outputs/" + outputNames[i], "COLUMN_TYPE_MISMATCH", $"Model output column '{column.Name}' must be float32 or float64.");
                ok = false;
            }

            outputs[i] = (outputNames[i], column);
        }

        if (!ok)
        {
            return null;
        }

        foreach (var version in versions)
        {
            ModelSignature signature;
            try
            {
                signature = bound.Value.Client.GetSignature(config.Model, version);
            }
            catch (ArgumentException ex)
            {
                context.Error("/model", "MODEL_SIGNATURE", ex.Message);
                return null;
            }

            foreach (var (name, _) in inputs)
            {
                if (signature.FindInput(name) is null)
                {
                    context.Error("/inputs/" + name, "MODEL_SIGNATURE", $"Model '{config.Model}' version '{version ?? "default"}' has no input '{name}'.");
                    ok = false;
                }
            }

            foreach (var (name, _) in outputs)
            {
                if (signature.FindOutput(name) is null)
                {
                    context.Error("/outputs/" + name, "MODEL_SIGNATURE", $"Model '{config.Model}' version '{version ?? "default"}' has no output '{name}'.");
                    ok = false;
                }
            }
        }

        if (!ok)
        {
            return null;
        }

        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, expr) in inputs)
        {
            named[name] = expr.Source;
        }

        return new ModelScorer(config.Model, config.Version, versionParam, bound.Value.Client, bound.Value.TimeoutMs, inputs, outputs, config.BatchSize, named,
            new IoTarget(IoKind.Model, config.Model));
    }

    private sealed class ModelScorer(
        string model,
        string? version,
        ParamRef? versionParam,
        IModelClient client,
        int? timeoutMs,
        (string Name, CompiledExpression Expr)[] inputs,
        (string Name, OutputColumn Column)[] outputs,
        int batchSize,
        IReadOnlyDictionary<string, string> namedInputs,
        IoTarget target) : Operator
    {
        public override IReadOnlyDictionary<string, string> NamedInputs => namedInputs;

        internal override IoTarget ModelTarget => target;

        public override async ValueTask ExecuteAsync(OperatorContext context)
        {
            var n = context.LiveRows.Length;
            if (n == 0)
            {
                return;
            }

            // The live rows as of the call, kept across the await; rented, and never handed to the model client.
            var memory = context.Memory;
            var rows = memory.Rent<int>(n);
            context.LiveRows.CopyTo(rows);
            var tensors = GatherInputs(context, rows.AsSpan(0, n));
            var resolvedVersion = versionParam is { } p ? context.Param(p).AsString() : version;
            var batches = (n + batchSize - 1) / batchSize;
            var writers = new OutputWriter[batches];
            var calls = new Task<ModelStatus>[batches];
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            if (timeoutMs is { } t)
            {
                cts.CancelAfter(t);
            }

            for (var b = 0; b < batches; b++)
            {
                var offset = b * batchSize;
                var count = Math.Min(batchSize, n - offset);
                var named = new NamedInput[inputs.Length];
                for (var i = 0; i < inputs.Length; i++)
                {
                    named[i] = NamedInput.FromFloat32(inputs[i].Name, new[] { count }, tensors[i].AsMemory(offset, count));
                }

                writers[b] = new OutputWriter(memory);
                var call = new ModelCall
                {
                    Model = model,
                    Version = resolvedVersion,
                    Inputs = named,
                    BatchSize = count,
                    Options = new ModelCallOptions(Seed: context.Seed),
                };
                calls[b] = context.InvokeModelAsync(client, target, call, writers[b], rows.AsMemory(offset, count), cts.Token);
            }

            var results = await Task.WhenAll(calls).ConfigureAwait(false);
            foreach (var status in results)
            {
                if (!status.Succeeded)
                {
                    context.Fail($"model '{model}' failed: {status.Error}");
                    return;
                }
            }

            WriteOutputs(context, rows.AsSpan(0, n), writers);
        }

        /// <summary>Evaluates every input into a dense float32 tensor ordered like <paramref name="rows"/>.</summary>
        private float[][] GatherInputs(OperatorContext context, ReadOnlySpan<int> rows)
        {
            var memory = context.Memory;
            var capacity = Math.Max(1, context.Table.StorageCapacity);
            var eval = context.EvalContext;
            var tensors = new float[inputs.Length][];
            for (var i = 0; i < inputs.Length; i++)
            {
                var values = new ScalarColumn<double>(ColumnType.Float64, capacity, memory);
                inputs[i].Expr.Evaluate(eval, rows, values);
                var dense = memory.Rent<float>(rows.Length);
                for (var r = 0; r < rows.Length; r++)
                {
                    dense[r] = (float)values[rows[r]];
                }

                tensors[i] = dense;
            }

            return tensors;
        }

        private void WriteOutputs(OperatorContext context, ReadOnlySpan<int> rows, OutputWriter[] writers)
        {
            foreach (var (name, column) in outputs)
            {
                var destination = context.Output(column);
                for (var b = 0; b < writers.Length; b++)
                {
                    var offset = b * batchSize;
                    var count = Math.Min(batchSize, rows.Length - offset);
                    if (!writers[b].TryGet(name, out var values) || values.Length < count)
                    {
                        context.Fail($"model '{model}' did not return output '{name}' for every row");
                        return;
                    }

                    var span = values.Span;
                    switch (destination)
                    {
                        case ScalarColumn<float> f:
                            for (var r = 0; r < count; r++)
                            {
                                f.Set(rows[offset + r], span[r]);
                            }

                            break;
                        case ScalarColumn<double> d:
                            for (var r = 0; r < count; r++)
                            {
                                d.Set(rows[offset + r], span[r]);
                            }

                            break;
                        default:
                            context.Fail($"output column '{column.Name}' must be float32 or float64");
                            return;
                    }
                }
            }
        }
    }

    private sealed class OutputWriter(Core.Memory.RequestMemory memory) : IModelOutputWriter
    {
        private readonly Dictionary<string, Memory<float>> _outputs = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public Memory<float> GetFloat32(string name, int elementCount)
        {
            var buffer = memory.Rent<float>(Math.Max(1, elementCount)).AsMemory(0, elementCount);
            lock (_gate)
            {
                _outputs[name] = buffer;
            }

            return buffer;
        }

        public void SetText(string name, string text)
        {
        }

        public bool TryGet(string name, out Memory<float> values)
        {
            lock (_gate)
            {
                return _outputs.TryGetValue(name, out values);
            }
        }
    }
}
