using System.Runtime.InteropServices;
using StrataRec.Artifacts.Json;
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

    /// <summary>
    /// Model input name → column or context reference (<c>col.*</c> / <c>ctx.*</c>) of a non-nullable float32, float64,
    /// bool, int32, int64 or <c>vec&lt;float32,N&gt;</c> type; the type decides the tensor (<see cref="ScorerSignature"/>).
    /// </summary>
    public required Dictionary<string, string> Inputs { get; init; }

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
/// Every model version the node may use must declare exactly those tensors, and the mapped outputs as float32
/// <c>[-1]</c>; this is checked at load time. <see cref="ScorerSignature"/> holds the rule, and why the column decides.</para>
/// <para>Inputs are plain references, so each is gathered straight from its column's storage (or its request-level value)
/// into one dense buffer per call, rented from the request and ordered like the live rows; batches are slices of it.</para>
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
        var inputs = new ScorerInput[inputNames.Count];
        var mapped = new ScorerSignature.MappedInput[inputNames.Count];
        var ok = true;
        for (var i = 0; i < inputNames.Count; i++)
        {
            var name = inputNames[i];
            var source = config.Inputs[name];

            // Any type, nullable included: ScorerSignature decides which types an input may have, and says why in model terms.
            var expr = context.CompileExpression(source, "/inputs/" + JsonPointer.Escape(name), CompileOptions.Any);
            if (expr is null)
            {
                ok = false;
                continue;
            }

            if (expr.Reads.Count != 1 || !string.Equals(expr.Reads[0].Name, source.Trim(), StringComparison.Ordinal))
            {
                context.Error("/inputs/" + JsonPointer.Escape(name), "MODEL_INPUT_NOT_REFERENCE",
                    $"Model input '{name}' must be a plain column or context reference, not an expression.",
                    "define the transformation as a derived feature so training and serving share one definition (design 6.9)");
                ok = false;
                continue;
            }

            var symbol = expr.Reads[0];
            if (!ScorerSignature.TryGetInputTensor(symbol.Type, out var tensor))
            {
                var problem = ScorerSignature.UnsendableInput(name, symbol.Name, symbol.Type);
                context.Error(problem.ConfigKey, problem.Code, problem.Message, problem.Hint);
                ok = false;
                continue;
            }

            inputs[i] = new ScorerInput(name, expr, symbol, tensor);
            mapped[i] = new ScorerSignature.MappedInput(name, symbol.Name, symbol.Type);
        }

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

        foreach (var version in versions)
        {
            // Null when it cannot be checked now: an error (reported), or the engine checks it at load (see ModelSignature).
            if (context.ModelSignature(bound.Value.Client, config.Model, version, "/model") is not { } signature)
            {
                ok &= !context.HasErrors;
                continue;
            }

            foreach (var problem in ScorerSignature.Check($"Model '{config.Model}' version '{version ?? "default"}'", mapped, config.Outputs, signature))
            {
                context.Error(problem.ConfigKey, problem.Code, problem.Message, problem.Hint);
                ok = false;
            }
        }

        if (!ok)
        {
            return null;
        }

        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            named[input.Name] = input.Source;
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
        ScorerInput[] inputs,
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
            var values = new GatheredInput[inputs.Length];
            for (var i = 0; i < inputs.Length; i++)
            {
                values[i] = inputs[i].Gather(context, rows.AsSpan(0, n));
            }

            var resolvedVersion = versionParam is { } p ? context.Param(p).AsString() : version;
            var batches = (n + batchSize - 1) / batchSize;
            var writers = new OutputWriter[batches];
            var calls = new Task<ModelStatus>[batches];
            for (var b = 0; b < batches; b++)
            {
                var offset = b * batchSize;
                var count = Math.Min(batchSize, n - offset);
                var named = new NamedInput[inputs.Length];
                for (var i = 0; i < inputs.Length; i++)
                {
                    named[i] = inputs[i].Slice(values[i], offset, count);
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
                calls[b] = context.InvokeModelAsync(client, target, call, writers[b], rows.AsMemory(offset, count), timeoutMs);
            }

            // Each wait is bounded by the model's timeout and the node's budget; a batch that failed or was cut off is never
            // read, since its abandoned client may still be writing it.
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

    /// <summary>One input's values for a call's rows: dense, ordered like the rows, rented from the request.</summary>
    /// <param name="Float32">Rows × the tensor's width floats, for a float32 tensor.</param>
    /// <param name="Int64">One long per row, for an int64 tensor.</param>
    private readonly record struct GatheredInput(float[]? Float32, long[]? Int64);

    /// <summary>
    /// One mapped input: the plain reference it reads and the tensor it becomes (<see cref="ScorerSignature"/>). The one
    /// place that turns column storage or a request-level value into tensor data.
    /// </summary>
    private sealed class ScorerInput(string name, CompiledExpression expression, Symbol symbol, ScorerSignature.InputTensor tensor)
    {
        private readonly ColumnRef _column = new(symbol.Id, symbol.FixedSlot, symbol.Type, symbol.Name);

        public string Name => name;

        public string Source => expression.Source;

        /// <summary>
        /// Copies the input's value of every row into one buffer: from the column's storage, so int64 values stay exact and
        /// vectors are copied as stored, or a request-level value broadcast to every row. No per-row allocation.
        /// </summary>
        public GatheredInput Gather(OperatorContext context, ReadOnlySpan<int> rows) =>
            symbol.Kind == SymbolKind.Column ? GatherColumn(context, rows) : Broadcast(context, rows.Length);

        /// <summary>The batch of <paramref name="count"/> rows from <paramref name="offset"/>, as the tensor the rule gives.</summary>
        public NamedInput Slice(in GatheredInput values, int offset, int count)
        {
            if (values.Int64 is { } longs)
            {
                return NamedInput.FromInt64(name, new[] { count }, longs.AsMemory(offset, count));
            }

            var width = tensor.Dimension;
            return width == 0
                ? NamedInput.FromFloat32(name, new[] { count }, values.Float32.AsMemory(offset, count))
                : NamedInput.FromFloat32(name, new[] { count, width }, values.Float32.AsMemory(offset * width, count * width));
        }

        private GatheredInput GatherColumn(OperatorContext context, ReadOnlySpan<int> rows)
        {
            // Non-nullable, so every row has a value and validity is not read, as the expression evaluator does not read it.
            var buffer = context.Read(_column)
                         ?? throw new InvalidOperationException($"Column '{symbol.Name}' is declared non-nullable ({symbol.Type}) but has no data in this " +
                                                                "request (unresolved slot or never written). The engine must produce it before a model reads it.");
            var memory = context.Memory;
            var n = rows.Length;
            var int64 = tensor.ElementType == TensorElementType.Int64;
            var width = tensor.Dimension;
            switch (buffer)
            {
                case ScalarColumn<long> c when int64:
                {
                    var src = c.Values;
                    var dst = memory.Rent<long>(n);
                    for (var r = 0; r < n; r++)
                    {
                        dst[r] = src[rows[r]];
                    }

                    return new GatheredInput(null, dst);
                }

                case ScalarColumn<int> c when int64:
                {
                    var src = c.Values;
                    var dst = memory.Rent<long>(n);
                    for (var r = 0; r < n; r++)
                    {
                        dst[r] = src[rows[r]];
                    }

                    return new GatheredInput(null, dst);
                }

                case ScalarColumn<float> c when !int64 && width == 0:
                {
                    var src = c.Values;
                    var dst = memory.Rent<float>(n);
                    for (var r = 0; r < n; r++)
                    {
                        dst[r] = src[rows[r]];
                    }

                    return new GatheredInput(dst, null);
                }

                case ScalarColumn<double> c when !int64 && width == 0:
                {
                    var src = c.Values;
                    var dst = memory.Rent<float>(n);
                    for (var r = 0; r < n; r++)
                    {
                        dst[r] = (float)src[rows[r]];
                    }

                    return new GatheredInput(dst, null);
                }

                case ScalarColumn<bool> c when !int64 && width == 0:
                {
                    // Any nonzero byte is true, as the expression evaluator reads it.
                    var src = MemoryMarshal.AsBytes(c.Values.AsSpan());
                    var dst = memory.Rent<float>(n);
                    for (var r = 0; r < n; r++)
                    {
                        dst[r] = src[rows[r]] != 0 ? 1f : 0f;
                    }

                    return new GatheredInput(dst, null);
                }

                case VectorColumn c when !int64 && width > 0 && c.Dimension == width:
                {
                    var dst = memory.Rent<float>(checked(n * width));
                    for (var r = 0; r < n; r++)
                    {
                        c.Row(rows[r]).CopyTo(dst.AsSpan(r * width, width));
                    }

                    return new GatheredInput(dst, null);
                }

                default:
                    throw new InvalidOperationException($"Column '{symbol.Name}' is declared {symbol.Type} but its buffer holds {buffer.Type}.");
            }
        }

        private GatheredInput Broadcast(OperatorContext context, int n)
        {
            // Non-nullable: the evaluator returns a value or throws.
            var eval = context.EvalContext;
            var value = expression.EvaluateScalar(eval);
            var memory = context.Memory;
            if (tensor.ElementType == TensorElementType.Int64)
            {
                var dst = memory.Rent<long>(n);
                dst.AsSpan(0, n).Fill(value.AsInt64());
                return new GatheredInput(null, dst);
            }

            var width = tensor.Dimension;
            if (width == 0)
            {
                var dst = memory.Rent<float>(n);
                dst.AsSpan(0, n).Fill(value.Kind == StorageKind.Bool ? (value.AsBool() ? 1f : 0f) : (float)value.AsFloat64());
                return new GatheredInput(dst, null);
            }

            var vector = value.AsVector();
            if (vector.Length != width)
            {
                throw new InvalidOperationException($"'{symbol.Name}' is declared {symbol.Type} but holds {vector.Length} elements in this request.");
            }

            var rows = memory.Rent<float>(checked(n * width));
            for (var r = 0; r < n; r++)
            {
                vector.CopyTo(rows.AsSpan(r * width, width));
            }

            return new GatheredInput(rows, null);
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
