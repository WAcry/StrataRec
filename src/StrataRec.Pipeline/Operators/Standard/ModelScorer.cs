using StrataRec.Artifacts.Json;
using StrataRec.Core.Columns;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Memory;
using StrataRec.Models;

namespace StrataRec.Pipeline.Operators.Standard;

/// <summary>Config of <c>ModelScorer@1</c> (design 6.9).</summary>
public sealed class ModelScorerConfig : ModelCallConfig
{
    /// <summary>Model output name → column to write (float32 or float64).</summary>
    public required Dictionary<string, string> Outputs { get; init; }

    public int BatchSize { get; init; } = 256;
}

/// <summary>
/// <c>ModelScorer@1</c> (ColumnWriter): pointwise scoring through <see cref="IModelClient"/>. Rows are split into
/// batches called concurrently; outputs commit only when every batch succeeds (design 4.6, 6.9).
/// </summary>
/// <remarks>
/// <para>Each batch sends one tensor per mapped input and reads one float32 per row from each mapped output. The tensor of
/// an input follows the declared type of the column or context value it maps, not the model: float32 <c>[rows]</c> for
/// float32, float64 and bool (0/1), int64 <c>[rows]</c> for int32 and int64 (exact), float32 <c>[rows, N]</c> for
/// <c>vec&lt;float32,N&gt;</c>; a context value is broadcast to every row. Other types are compile errors at the input.
/// Every model version the node may use must declare those tensors, and the mapped outputs as float32 <c>[-1]</c>; this
/// is checked at load time. <see cref="ModelTensorRule"/> holds the rule, and why the column decides.</para>
/// <para>Inputs are plain references, so each is gathered straight from its column's storage (or its request-level value)
/// into one dense buffer per call, rented from the request and ordered like the live rows; batches are slices of it
/// (<see cref="ModelInput"/>, shared with the other model operators).</para>
/// </remarks>
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

        var compiler = ModelCallCompiler.Begin(context, config, TensorLayout.Rows);
        if (compiler is null)
        {
            return null;
        }

        if (config.BatchSize < 1)
        {
            context.Error("/batch_size", "MODEL_BAD_BATCH", "batch_size must be at least 1.");
            return null;
        }

        var ok = compiler.CompileInputs();
        var outputNames = new List<string>(config.Outputs.Keys);
        outputNames.Sort(StringComparer.Ordinal);
        var outputs = new (string Name, OutputColumn Column)[outputNames.Count];
        for (var i = 0; i < outputNames.Count; i++)
        {
            if (!context.TryWriteColumn(config.Outputs[outputNames[i]], "/outputs/" + JsonPointer.Escape(outputNames[i]), null, out var column))
            {
                ok = false;
                continue;
            }

            if (!column.Type.IsFloat)
            {
                context.Error("/outputs/" + JsonPointer.Escape(outputNames[i]), "COLUMN_TYPE_MISMATCH", $"Model output column '{column.Name}' must be float32 or float64.");
                ok = false;
            }

            outputs[i] = (outputNames[i], column);
        }

        if (!ok)
        {
            return null;
        }

        // In the config's order, as the signature check has always reported them.
        var read = new List<ModelTensorRule.MappedOutput>(config.Outputs.Count);
        foreach (var (name, _) in config.Outputs)
        {
            read.Add(new ModelTensorRule.MappedOutput(name, "/outputs/" + JsonPointer.Escape(name)));
        }

        var call = compiler.Finish(read);
        return call is null ? null : new ModelScorer(call, outputs, config.BatchSize);
    }

    private sealed class ModelScorer(CompiledModelCall call, (string Name, OutputColumn Column)[] outputs, int batchSize) : Operator
    {
        public override IReadOnlyDictionary<string, string> NamedInputs => call.NamedInputs;

        public override IoTarget ModelTarget => call.Target;

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
            var inputs = call.Inputs;
            var values = new GatheredInput[inputs.Length];
            for (var i = 0; i < inputs.Length; i++)
            {
                values[i] = inputs[i].Gather(context, rows.AsSpan(0, n), Math.Min(n, batchSize));
            }

            var version = call.Version(context);
            var batches = Batching.Count(n, batchSize);
            var writers = new ModelOutputWriter[batches];
            var calls = new Task<ModelStatus>[batches];
            for (var b = 0; b < batches; b++)
            {
                var offset = b * batchSize;
                var count = Math.Min(batchSize, n - offset);
                var named = new NamedInput[inputs.Length];
                for (var i = 0; i < inputs.Length; i++)
                {
                    named[i] = inputs[i].Batch(values[i], TensorLayout.Rows, offset, count);
                }

                writers[b] = new ModelOutputWriter(memory);
                calls[b] = call.InvokeAsync(context, version, named, count, writers[b], rows.AsMemory(offset, count));
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

            WriteOutputs(context, rows.AsSpan(0, n), writers);
        }

        private void WriteOutputs(OperatorContext context, ReadOnlySpan<int> rows, ModelOutputWriter[] writers)
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
                        context.Fail($"model '{call.Model}' did not return output '{name}' for every row");
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
}
