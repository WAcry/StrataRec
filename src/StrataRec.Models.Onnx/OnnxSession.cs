using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using StrataRec.Core.Bindings;
using OrtElement = Microsoft.ML.OnnxRuntime.Tensors.TensorElementType;

namespace StrataRec.Models.Onnx;

/// <summary>
/// One ONNX Runtime session over one model file, with the model's signature read from the session's metadata. A call
/// runs over the caller's memory: each input becomes a tensor over the call's buffer, and each output is computed
/// straight into the buffer the caller's <see cref="IModelOutputWriter"/> hands out, so no tensor data is copied.
/// </summary>
/// <remarks>
/// A call computes every output whose rows it can size: float32, a dynamic leading (batch) dimension and fixed others.
/// Other outputs are in the signature (a node that maps one is told at load) and never computed.
/// </remarks>
internal sealed class OnnxSession : IDisposable
{
    /// <summary>What a binding's model may use, for hints.</summary>
    private const string TypeHint = "local_onnx@1 supports float32 and int64 tensors: export the model with those (cast inside the graph)";

    private readonly InferenceSession _session;
    private readonly string[] _inputNames;
    private readonly TensorElementType[] _inputTypes;
    private readonly string[] _outputNames;
    private readonly int[][] _outputShapes;

    private OnnxSession(InferenceSession session, string path, ModelSignature signature, string[] inputNames, TensorElementType[] inputTypes,
        string[] outputNames, int[][] outputShapes)
    {
        _session = session;
        Path = path;
        Signature = signature;
        _inputNames = inputNames;
        _inputTypes = inputTypes;
        _outputNames = outputNames;
        _outputShapes = outputShapes;
    }

    public string Path { get; }

    public ModelSignature Signature { get; }

    /// <summary>Opens <paramref name="file"/> and reads its signature: file I/O, for a client's load only.</summary>
    /// <exception cref="BindingException">The file is missing or not a model ONNX Runtime loads, or the model has a tensor
    /// type the client does not support; at the setting that names the file.</exception>
    public static OnnxSession Open(OnnxModelFile file, SessionOptions options)
    {
        if (!File.Exists(file.Path))
        {
            throw new BindingException(file.Pointer, $"The ONNX model file '{file.Path}' does not exist.",
                "check the path: a relative path resolves against the configuration directory");
        }

        InferenceSession session;
        try
        {
            session = new InferenceSession(file.Path, options);
        }
        catch (OnnxRuntimeException ex)
        {
            throw new BindingException(file.Pointer, $"ONNX Runtime cannot load '{file.Path}': {ex.Message}",
                "check that the file is a complete ONNX model with an opset this ONNX Runtime (1.30) supports", ex);
        }

        try
        {
            var inputs = Specs(session.InputMetadata, "input", file);
            var outputs = Specs(session.OutputMetadata, "output", file);
            var inputNames = new string[inputs.Length];
            var inputTypes = new TensorElementType[inputs.Length];
            for (var i = 0; i < inputs.Length; i++)
            {
                inputNames[i] = inputs[i].Name;
                inputTypes[i] = inputs[i].ElementType;
            }

            var outputNames = new List<string>(outputs.Length);
            var outputShapes = new List<int[]>(outputs.Length);
            foreach (var output in outputs)
            {
                if (IsRowOutput(output, out var shape))
                {
                    outputNames.Add(output.Name);
                    outputShapes.Add(shape);
                }
            }

            return new OnnxSession(session, file.Path, new ModelSignature(inputs, outputs), inputNames, inputTypes, outputNames.ToArray(), outputShapes.ToArray());
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs one call on the current thread: <paramref name="cancellationToken"/> terminates the run in ONNX Runtime. A
    /// missing or mistyped input, a size that does not match its shape, or a failed run is a failed status.
    /// </summary>
    /// <exception cref="OperationCanceledException">The token was cancelled before or during the run.</exception>
    public ModelStatus Run(ModelCall call, IModelOutputWriter writer, CancellationToken cancellationToken)
    {
        var inputs = new OrtValue[_inputNames.Length];
        var outputs = new OrtValue[_outputNames.Length];
        try
        {
            var named = call.Inputs.Span;
            for (var i = 0; i < _inputNames.Length; i++)
            {
                if (CreateInput(named, i, out var input) is { } error)
                {
                    return ModelStatus.Failed(error);
                }

                inputs[i] = input!;
            }

            var rows = call.BatchSize;
            for (var o = 0; o < _outputNames.Length; o++)
            {
                var dims = _outputShapes[o];
                var shape = new long[dims.Length];
                long count = rows;
                shape[0] = rows;
                for (var d = 1; d < dims.Length; d++)
                {
                    shape[d] = dims[d];
                    count *= dims[d];
                }

                if (count > int.MaxValue)
                {
                    return ModelStatus.Failed($"Output '{_outputNames[o]}' would hold {count} values, more than one call can.");
                }

                var buffer = writer.GetFloat32(_outputNames[o], (int)count);
                if (buffer.Length < count)
                {
                    return ModelStatus.Failed($"The buffer for output '{_outputNames[o]}' holds {buffer.Length} values; the call needs {count}.");
                }

                outputs[o] = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, buffer[..(int)count], shape);
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var options = new RunOptions();
            using (cancellationToken.UnsafeRegister(static state => ((RunOptions)state!).Terminate = true, options))
            {
                try
                {
                    _session.Run(options, _inputNames, inputs, _outputNames, outputs);
                }
                catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("The model call was cancelled; ONNX Runtime terminated the run.", cancellationToken);
                }
                catch (OnnxRuntimeException ex)
                {
                    return ModelStatus.Failed($"ONNX Runtime failed the call: {ex.Message}");
                }
            }

            return ModelStatus.Ok;
        }
        finally
        {
            Dispose(inputs);
            Dispose(outputs);
        }
    }

    public void Dispose() => _session.Dispose();

    /// <summary>A tensor over the buffer of the call's input named like the model's input <paramref name="index"/>; null, or why not.</summary>
    private string? CreateInput(ReadOnlySpan<NamedInput> named, int index, out OrtValue? value)
    {
        value = null;
        var name = _inputNames[index];
        foreach (var input in named)
        {
            if (!string.Equals(input.Name, name, StringComparison.Ordinal))
            {
                continue;
            }

            if (input.ElementType != _inputTypes[index])
            {
                return $"Input '{name}' is {Describe(_inputTypes[index])} in the model; the call sent {Describe(input.ElementType)}.";
            }

            var shape = new long[input.Shape.Length];
            long count = 1;
            for (var d = 0; d < shape.Length; d++)
            {
                shape[d] = input.Shape[d];
                count *= input.Shape[d];
            }

            var length = input.ElementType == TensorElementType.Float32 ? input.Float32.Length : input.Int64.Length;
            if (count != length)
            {
                return $"Input '{name}' has {length} values for a shape of {count}.";
            }

            // ONNX Runtime only reads an input: the tensor may wrap the caller's read-only buffer.
            value = input.ElementType == TensorElementType.Float32
                ? OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, MemoryMarshal.AsMemory(input.Float32), shape)
                : OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, MemoryMarshal.AsMemory(input.Int64), shape);
            return null;
        }

        return $"The call has no input '{name}', which the model requires.";
    }

    private static TensorSpec[] Specs(IReadOnlyDictionary<string, NodeMetadata> metadata, string kind, OnnxModelFile file)
    {
        var specs = new TensorSpec[metadata.Count];
        var i = 0;
        foreach (var (name, node) in metadata)
        {
            if (!node.IsTensor)
            {
                throw new BindingException(file.Pointer, $"The {kind} '{name}' of '{file.Path}' is a {node.OnnxValueType}, not a tensor.", TypeHint);
            }

            var type = node.ElementDataType switch
            {
                OrtElement.Float => TensorElementType.Float32,
                OrtElement.Int64 => TensorElementType.Int64,
                _ => throw new BindingException(file.Pointer, $"The {kind} '{name}' of '{file.Path}' is a tensor of {node.ElementDataType}.", TypeHint),
            };
            specs[i++] = new TensorSpec(name, type, (int[])node.Dimensions.Clone());
        }

        // Session metadata keeps the graph's order; sorted, the signature is the same however the graph was exported.
        Array.Sort(specs, static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return specs;
    }

    /// <summary>A float32 output with a dynamic leading dimension and fixed others: one call can size its buffer.</summary>
    private static bool IsRowOutput(TensorSpec spec, out int[] shape)
    {
        shape = new int[spec.Shape.Count];
        for (var d = 0; d < shape.Length; d++)
        {
            shape[d] = spec.Shape[d];
        }

        if (spec.ElementType != TensorElementType.Float32 || shape.Length == 0 || shape[0] != -1)
        {
            return false;
        }

        for (var d = 1; d < shape.Length; d++)
        {
            if (shape[d] < 1)
            {
                return false;
            }
        }

        return true;
    }

    private static string Describe(TensorElementType type) => type switch
    {
        TensorElementType.Float32 => "float32",
        TensorElementType.Int64 => "int64",
        TensorElementType.Text => "text",
    };

    /// <summary>Disposes the tensors a call created (unpinning the caller's buffers); slots it did not reach are null.</summary>
    private static void Dispose(OrtValue[] values)
    {
        foreach (var value in values)
        {
            value?.Dispose();
        }
    }
}
