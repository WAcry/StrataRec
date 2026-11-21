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
/// so the same row in a differently sized batch has the same digest. A call's <see cref="ModelCallOptions.MaxOutputTokens"/>
/// is part of every digest when it is set: a generative model answers differently under another limit, so a recorded
/// answer is not served to a call with another one. The seed is not: it is the request's, which the bundle replays.
/// Calls without a limit hash as before, so older recordings keep matching.
/// </remarks>
internal static class ModelInputDigest
{
    /// <summary>Digest of row <paramref name="row"/>: every input in name order (see the remarks).</summary>
    public static ulong Row(ModelCall call, int[] order, int row)
    {
        var builder = default(StableHash.Builder);
        try
        {
            AddOptions(ref builder, call.Options);
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
            AddOptions(ref builder, call.Options);
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

    /// <summary>The options that change what a model answers for the same inputs; nothing for a call that sets none.</summary>
    private static void AddOptions(ref StableHash.Builder builder, in ModelCallOptions options)
    {
        if (options.MaxOutputTokens is { } max)
        {
            builder.Add("max_output_tokens");
            builder.Add(max);
        }
    }

    /// <param name="row">The row whose digest is being built; -1 hashes every input whole.</param>
    private static void AddInput(ref StableHash.Builder builder, in NamedInput input, int batch, int row)
    {
        var length = input.ElementType == TensorElementType.Text ? 0 : input.Values.Length;

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

        if (input.ElementType == TensorElementType.Text)
        {
            builder.Add(input.Text);
            return;
        }

        var count = sliced ? length / batch : length;
        var start = sliced ? row * count : 0;
        builder.AddIndex(count);
        var values = input.Values;
        switch (values.ElementType)
        {
            case TensorElementType.Bool:
                foreach (var b in values.Bool.Span.Slice(start, count))
                {
                    builder.Add(b);
                }

                break;
            case TensorElementType.Int32:
                foreach (var i in values.Int32.Span.Slice(start, count))
                {
                    builder.Add((long)i);
                }

                break;
            case TensorElementType.Int64:
                foreach (var l in values.Int64.Span.Slice(start, count))
                {
                    builder.Add(l);
                }

                break;
            case TensorElementType.Float32:
                foreach (var f in values.Float32.Span.Slice(start, count))
                {
                    builder.Add((double)f);
                }

                break;
            case TensorElementType.Float64:
                foreach (var d in values.Float64.Span.Slice(start, count))
                {
                    builder.Add(d);
                }

                break;
            default:
                foreach (var text in values.String.Span.Slice(start, count))
                {
                    builder.Add(text);
                }

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
    private readonly List<(string Name, TensorValues Values)> _tensors = new();
    private readonly List<(string Name, string Text)> _texts = new();

    public Memory<T> GetValues<T>(string name, int elementCount)
    {
        var buffer = inner.GetValues<T>(name, elementCount);
        lock (_gate)
        {
            _tensors.Add((name, TensorValues.Of<T>(buffer)));
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
    public List<(string Name, TensorValues Values)> Tensors()
    {
        lock (_gate)
        {
            return LastByName(_tensors);
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
