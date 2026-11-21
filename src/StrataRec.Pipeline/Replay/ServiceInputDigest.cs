using StrataRec.Core.Hashing;
using StrataRec.Core.Services;

namespace StrataRec.Pipeline.Replay;

/// <summary>
/// Digests of a service call's inputs, so a replay serves a recorded answer only for the inputs it was computed from: a
/// changed feature definition, model input or recall query changes the digest, and the answer becomes a reported gap
/// instead of a stale value.
/// </summary>
/// <remarks>
/// An input is row-wise when its leading dimension is the batch size (<c>Shape[0] == BatchSize</c>, the tensor contract
/// of <see cref="ServiceCall.BatchSize"/>); a row's digest covers its slice of each row-wise input and every other input
/// whole, since a shared input (a user vector, a text prompt) affects every row. Each input contributes its name, element
/// type and shape, so the same values in another layout do not collide; the batch dimension of a row-wise input is left
/// out, so the same row in a differently sized batch has the same digest. A missing input contributes its name, element
/// type and declared shape, and that it is missing: it is neither a zero nor an absent input. A call's
/// <see cref="ServiceCallOptions.MaxOutputTokens"/> is part of every digest when it is set: a generative model answers
/// differently under another limit, so a recorded answer is not served to a call with another one. The seed and the
/// request's inputs are not: they are the request's, which the bundle replays. Nor is the call's
/// <see cref="ServiceCall.Limit"/>: a recorded list answers a call asking for fewer items.
/// </remarks>
internal static class ServiceInputDigest
{
    /// <summary>Digest of row <paramref name="row"/>: every input in name order (see the remarks).</summary>
    public static ulong Row(ServiceCall call, int[] order, int row)
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

    /// <summary>Digest of the whole call, for calls about no items: the batch size and every input whole.</summary>
    public static ulong Call(ServiceCall call, int[] order)
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
    public static int[] NameOrder(ServiceCall call)
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
        batch > 0 && !input.IsMissing && input.ElementType != TensorElementType.Text && input.Shape is { Length: > 0 } shape && shape[0] == batch;

    /// <summary>The options that change what a model answers for the same inputs; nothing for a call that sets none.</summary>
    private static void AddOptions(ref StableHash.Builder builder, in ServiceCallOptions options)
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

        if (input.IsMissing)
        {
            builder.Add("missing"); // after the shape: a missing value is not an empty one
            return;
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
