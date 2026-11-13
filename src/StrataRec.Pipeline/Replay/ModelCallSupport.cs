using StrataRec.Core.Hashing;
using StrataRec.Models;

namespace StrataRec.Pipeline.Replay;

/// <summary>
/// Digests of model inputs, so a replay serves a recorded output only for the inputs it was computed from: a changed
/// feature definition or model input changes the digest, and the output becomes a reported gap instead of a stale value.
/// </summary>
/// <remarks>
/// An input is row-wise when its leading dimension is the batch size (<c>Shape[0] == BatchSize</c>, the tensor contract of
/// <see cref="ModelCall.BatchSize"/>); a row's digest covers its slice of each row-wise input and every other input whole,
/// since a shared input (a user vector, a text prompt) affects every row. Each input contributes its name, element type
/// and shape, so the same values in another layout do not collide; the batch dimension of a row-wise input is left out,
/// so the same row in a differently sized batch has the same digest.
/// </remarks>
internal static class ModelInputDigest
{
    /// <summary>Digest of row <paramref name="row"/>: every input in name order (see the remarks).</summary>
    public static ulong Row(ModelCall call, int[] order, int row)
    {
        var builder = default(StableHash.Builder);
        try
        {
            var inputs = call.Inputs.Span;
            foreach (var i in order)
            {
                AddInput(ref builder, inputs[i], call.BatchSize, row);
            }

            return builder.Finish();
        }
        finally
        {
            builder.Dispose();
        }
    }

    /// <summary>Digest of the whole call, for calls whose outputs are not per row: the batch size and every input whole.</summary>
    public static ulong Call(ModelCall call, int[] order)
    {
        var builder = default(StableHash.Builder);
        try
        {
            builder.Add(call.BatchSize);
            var inputs = call.Inputs.Span;
            foreach (var i in order)
            {
                AddInput(ref builder, inputs[i], call.BatchSize, -1);
            }

            return builder.Finish();
        }
        finally
        {
            builder.Dispose();
        }
    }

    /// <summary>Input indices in ordinal name order, so the digest does not depend on the order an operator lists them.</summary>
    public static int[] NameOrder(ModelCall call)
    {
        var inputs = call.Inputs.Span;
        var names = new string[inputs.Length];
        var order = new int[inputs.Length];
        for (var i = 0; i < inputs.Length; i++)
        {
            names[i] = inputs[i].Name;
            order[i] = i;
        }

        Array.Sort(names, order, StringComparer.Ordinal);
        return order;
    }

    /// <summary>The input's leading dimension is the batch: each row has its own slice.</summary>
    public static bool IsRowWise(in NamedInput input, int batch) =>
        batch > 0 && input.ElementType != TensorElementType.Text && input.Shape is { Length: > 0 } shape && shape[0] == batch;

    /// <param name="row">The row whose digest is being built; -1 hashes every input whole.</param>
    private static void AddInput(ref StableHash.Builder builder, in NamedInput input, int batch, int row)
    {
        var length = input.ElementType switch
        {
            TensorElementType.Float32 => input.Float32.Length,
            TensorElementType.Int64 => input.Int64.Length,
            _ => 0,
        };

        // Row-wise data that does not split evenly into rows is malformed; it is hashed whole, which can only miss a match.
        var sliced = row >= 0 && IsRowWise(input, batch) && length % batch == 0;
        builder.Add(input.Name);
        builder.AddIndex((int)input.ElementType);
        var shape = input.Shape ?? Array.Empty<int>();
        builder.AddIndex(shape.Length);
        for (var d = 0; d < shape.Length; d++)
        {
            builder.AddIndex(sliced && d == 0 ? -1 : shape[d]); // the batch dimension, normalized
        }

        var count = sliced ? length / batch : length;
        var start = sliced ? row * count : 0;
        switch (input.ElementType)
        {
            case TensorElementType.Float32:
                builder.AddIndex(count);
                foreach (var f in input.Float32.Span.Slice(start, count))
                {
                    builder.Add((double)f);
                }

                break;
            case TensorElementType.Int64:
                builder.AddIndex(count);
                foreach (var l in input.Int64.Span.Slice(start, count))
                {
                    builder.Add(l);
                }

                break;
            default:
                builder.Add(input.Text ?? string.Empty);
                break;
        }
    }
}

/// <summary>
/// Forwards a model's outputs to the operator's writer and remembers them, so a recorded call can copy what the model
/// returned. Used only for requests that record.
/// </summary>
internal sealed class RecordingOutputWriter(IModelOutputWriter inner) : IModelOutputWriter
{
    private readonly object _gate = new();
    private readonly List<(string Name, Memory<float> Values)> _floats = new();
    private readonly List<(string Name, string Text)> _texts = new();

    public Memory<float> GetFloat32(string name, int elementCount)
    {
        var buffer = inner.GetFloat32(name, elementCount);
        lock (_gate)
        {
            _floats.Add((name, buffer));
        }

        return buffer;
    }

    public void SetText(string name, string text)
    {
        inner.SetText(name, text);
        lock (_gate)
        {
            _texts.Add((name, text));
        }
    }

    /// <summary>The outputs by name; a name requested twice keeps its last buffer, as the operator's writer does.</summary>
    public List<(string Name, Memory<float> Values)> Floats()
    {
        lock (_gate)
        {
            return LastByName(_floats);
        }
    }

    public List<(string Name, string Text)> Texts()
    {
        lock (_gate)
        {
            return LastByName(_texts);
        }
    }

    private static List<(string Name, T Value)> LastByName<T>(List<(string Name, T Value)> entries)
    {
        var result = new List<(string Name, T Value)>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var replaced = false;
            for (var j = 0; j < result.Count; j++)
            {
                if (string.Equals(result[j].Name, entries[i].Name, StringComparison.Ordinal))
                {
                    result[j] = entries[i];
                    replaced = true;
                    break;
                }
            }

            if (!replaced)
            {
                result.Add(entries[i]);
            }
        }

        return result;
    }
}
