using StrataRec.Artifacts.Json;
using StrataRec.Core.Columns;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Core.Services;

namespace StrataRec.Pipeline.Operators.Standard;

/// <summary>Config of <c>ModelScorer@1</c>.</summary>
public sealed class ModelScorerConfig : ModelCallConfig
{
    /// <summary>Model output name → column to write, whose declared type is the output's tensor type (as an input's is).</summary>
    public required Dictionary<string, string> Outputs { get; init; }

    /// <summary>Most rows per model call: the rows are split into the fewest calls, of even size, which run concurrently.</summary>
    public int BatchSize { get; init; } = 256;
}

/// <summary>
/// <c>ModelScorer@1</c> (ColumnWriter): pointwise scoring through <see cref="IServiceClient"/>. Rows are split into
/// batches called concurrently; outputs commit only when every batch succeeds.
/// </summary>
/// <remarks>
/// <para>Each batch sends one tensor per mapped input and reads one per mapped output, each of the declared type of its
/// column, not the model's: an input of an int32 column is an int32 <c>[rows]</c> tensor, an embedding
/// <c>list&lt;float32,N&gt;</c> a float32 <c>[rows, N]</c> one, and an output written to a float64 column a float64
/// <c>[rows]</c> tensor; a context value is broadcast to every row. Nullable inputs and lists of any length are compile
/// errors at the input. Every model version the node may use must declare those tensors; this is checked at load time.
/// <see cref="ModelTensorRule"/> holds the rule, and why the column decides.</para>
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

            if (!ModelTensorRule.TryGetOutputTensor(column.Type, out _))
            {
                context.Error("/outputs/" + JsonPointer.Escape(outputNames[i]), "COLUMN_TYPE_MISMATCH",
                    $"Model output column '{column.Name}' is {column.Type}, a list of any length: a model output has the same number of values for every row.",
                    $"declare '{column.Name}' with its length under /columns, e.g. {ColumnType.List(column.Type.Element, column.Type.Nullable, 64)}");
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
            read.Add(new ModelTensorRule.MappedOutput(name, "/outputs/" + JsonPointer.Escape(name), outputs[outputNames.IndexOf(name)].Column.Type));
        }

        var call = compiler.Finish(read);
        return call is null ? null : new ModelScorer(call, outputs, config.BatchSize);
    }

    private sealed class ModelScorer(CompiledModelCall call, (string Name, OutputColumn Column)[] outputs, int batchSize) : Operator
    {
        public override IReadOnlyDictionary<string, string> NamedInputs => call.NamedInputs;

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
            var stride = Batching.Size(n, batchSize); // the fewest batches, even
            var inputs = call.Inputs;
            var values = new GatheredInput[inputs.Length];
            for (var i = 0; i < inputs.Length; i++)
            {
                values[i] = inputs[i].Gather(context, rows.AsSpan(0, n), stride);
            }

            var version = call.Version(context);
            var batches = Batching.Count(n, stride);
            var writers = new ServiceResponse[batches];
            var calls = new Task<ServiceStatus>[batches];
            for (var b = 0; b < batches; b++)
            {
                var offset = b * stride;
                var count = Math.Min(stride, n - offset);
                var named = new NamedInput[inputs.Length];
                for (var i = 0; i < inputs.Length; i++)
                {
                    named[i] = inputs[i].Batch(values[i], TensorLayout.Rows, offset, count);
                }

                writers[b] = new ServiceResponse(memory);
                calls[b] = call.InvokeAsync(context, version, named, count, writers[b], rows.AsSpan(offset, count));
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

        private void WriteOutputs(OperatorContext context, ReadOnlySpan<int> rows, ServiceResponse[] writers)
        {
            var stride = Batching.Size(rows.Length, batchSize); // as the batches were sent
            foreach (var (name, column) in outputs)
            {
                ModelTensorRule.TryGetOutputTensor(column.Type, out var tensor);
                var width = Math.Max(1, tensor.Dimension);
                var destination = context.Output(column);
                for (var b = 0; b < writers.Length; b++)
                {
                    var offset = b * stride;
                    var count = Math.Min(stride, rows.Length - offset);
                    if (!writers[b].TryGetValues(name, out var values))
                    {
                        context.Fail($"model '{call.Model}' did not return output '{name}' for every row");
                        return;
                    }

                    if (values.ElementType != tensor.ElementType)
                    {
                        context.Fail($"model '{call.Model}' returned output '{name}' as {TensorValues.Name(values.ElementType)}, but it is written to "
                            + $"'{column.Name}' ({column.Type}), which takes {TensorValues.Name(tensor.ElementType)} as the signature declares");
                        return;
                    }

                    // Exactly the shape's elements: a longer output would be sliced at other row boundaries than its writer used.
                    if (ModelOutputs.WrongLength(call.Model, name, values, count * width, "row") is { } wrong)
                    {
                        context.Fail(wrong);
                        return;
                    }

                    var batchRows = rows.Slice(offset, count);
                    var written = destination switch
                    {
                        ScalarColumn<bool> c => Write(c, batchRows, values.Bool.Span),
                        ScalarColumn<int> c => Write(c, batchRows, values.Int32.Span),
                        ScalarColumn<long> c => Write(c, batchRows, values.Int64.Span),
                        ScalarColumn<float> c => Write(c, batchRows, values.Float32.Span),
                        ScalarColumn<double> c => Write(c, batchRows, values.Float64.Span),
                        StringColumn c => Write(c, batchRows, values.String.Span),
                        ListColumn<bool> c => Write(c, batchRows, values.Bool.Span, width),
                        ListColumn<int> c => Write(c, batchRows, values.Int32.Span, width),
                        ListColumn<long> c => Write(c, batchRows, values.Int64.Span, width),
                        ListColumn<float> c => Write(c, batchRows, values.Float32.Span, width),
                        ListColumn<double> c => Write(c, batchRows, values.Float64.Span, width),
                        ListColumn<string> c => Write(c, batchRows, values.String.Span, width),
                        _ => throw new InvalidOperationException($"Output column '{column.Name}' is {column.Type}, which no model output writes."),
                    };

                    if (!written)
                    {
                        context.Fail($"model '{call.Model}' returned no value for some row of output '{name}' (a string output writes every element)");
                        return;
                    }
                }
            }
        }

        private static bool Write<T>(ScalarColumn<T> column, ReadOnlySpan<int> rows, ReadOnlySpan<T> values)
            where T : unmanaged
        {
            for (var r = 0; r < rows.Length; r++)
            {
                column.Set(rows[r], values[r]);
            }

            return true;
        }

        private static bool Write(StringColumn column, ReadOnlySpan<int> rows, ReadOnlySpan<string> values)
        {
            for (var r = 0; r < rows.Length; r++)
            {
                if (values[r] is not { } value)
                {
                    return false;
                }

                column.Set(rows[r], value);
            }

            return true;
        }

        private static bool Write<T>(ListColumn<T> column, ReadOnlySpan<int> rows, ReadOnlySpan<T> values, int width)
        {
            for (var r = 0; r < rows.Length; r++)
            {
                var row = values.Slice(r * width, width);
                if (typeof(T) == typeof(string))
                {
                    foreach (var element in row)
                    {
                        if (element is null)
                        {
                            return false;
                        }
                    }
                }

                column.Set(rows[r], row);
            }

            return true;
        }
    }
}
