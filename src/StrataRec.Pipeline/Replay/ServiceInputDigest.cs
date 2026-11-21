using StrataRec.Core.Hashing;
using StrataRec.Core.Services;

namespace StrataRec.Pipeline.Replay;

/// <summary>
/// Digests of a service call's inputs, so a replay serves a recorded answer only for the inputs it was computed from: a
/// changed feature definition, model input or recall query changes the digest, and the answer becomes a reported gap
/// instead of a stale value.
/// </summary>
/// <remarks>
/// A row's digest covers what the row saw of each input. An input is row-wise when its leading dimension is the batch
/// size (<c>Shape[0] == BatchSize</c>, the tensor contract of <see cref="ServiceCall.BatchSize"/>): the row saw its slice.
/// A shared input of leading dimension 1 (a user vector sent as <c>[1, d]</c>) is the same for every row, which saw all
/// of it; any other input (a scalar, a text prompt) is whole too. Each input contributes its name, element type and
/// shape, so the same values in another layout do not collide; the leading dimension of a row-wise or shared
/// <c>[1, ...]</c> input is left out, so the same row in a differently sized batch has the same digest, a batch of one
/// too, whose shared input is row-wise by its shape (review of the IO redesign: there its leading dimension was kept,
/// and the row's digest changed with the batch). A missing input contributes its name, element
/// type and declared shape, and that it is missing: it is neither a zero nor an absent input; its declared shape says
/// whether it is row-wise, as a present one's does (missing on every row). A call's
/// <see cref="ServiceCallOptions.MaxOutputTokens"/> is part of every digest when it is set: a generative model answers
/// differently under another limit, so a recorded answer is not served to a call with another one. The seed is not:
/// it is the request's, which the bundle replays. The call's <see cref="ServiceCall.Limit"/> is not: a recorded list
/// answers a call asking for fewer items (a service's shorter list is the start of its longer one), and the replay
/// answers the named outputs a call reads only from a call recorded with its limit (an output or a text may say how many
/// items it was asked for), so a call reading one output more or less still meets its recording. A cache key has it
/// when the call reads named outputs: a cache matches by key alone.
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

    /// <summary>Digest of the whole call, for calls about no items: the batch size and every input whole (not the limit: a recorded call answers named outputs only for its own limit, <see cref="RunReplay"/>).</summary>
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

    /// <summary>
    /// The format of service cache keys (docs/decisions.md, IO step 3). A change of what a record holds or of how a key is
    /// built changes it: the records another build wrote are then misses, never misread.
    /// </summary>
    private const string CacheFormat = "stratarec.service_cache/2026-11-21.3";

    /// <summary>
    /// The key of a call about no items in its service's cache: the format, the binding's answer identity, the service
    /// version, the outputs the call reads in its order, and the call's digest (the batch size, the limit when it reads
    /// outputs, the options that change an answer, every input whole); 128 bits, so very many cached answers do not
    /// collide. Not the seed or the request id (see the decisions).
    /// </summary>
    /// <param name="identity">What the binding answers with (<c>ServiceCache.AnswerIdentity</c>): another client or other settings are other answers.</param>
    public static UInt128 CacheCallKey(ServiceCall call, int[] order, string identity)
    {
        var builder = default(StableHash.Builder);
        try
        {
            AddCachePrefix(ref builder, call, "call", identity);
            builder.Add(call.BatchSize);
            AddLimit(ref builder, call);
            AddOptions(ref builder, call.Options);
            var inputs = call.Inputs.Span;
            foreach (var i in order)
            {
                AddInput(ref builder, inputs[i], call.BatchSize, -1);
            }

            return builder.Finish128();
        }
        finally
        {
            builder.Dispose();
        }
    }

    /// <summary>
    /// The key of each item of a call about items (<see cref="ServiceCall.Items"/>) in its service's cache, into
    /// <paramref name="keys"/>: what the items share (the format, the version, the outputs read, the options, every input
    /// with no leading dimension of rows) hashed once, then for each item that, its entity and what it saw of every input
    /// with one (<see cref="RowView"/>), as a row's digest has it.
    /// </summary>
    public static void CacheItemKeys(ServiceCall call, int[] order, string identity, Span<UInt128> keys)
    {
        var builder = default(StableHash.Builder);
        try
        {
            AddCachePrefix(ref builder, call, "item", identity);
            AddOptions(ref builder, call.Options);
            var inputs = call.Inputs.Span;
            foreach (var i in order)
            {
                if (!RowView(inputs[i], call.BatchSize))
                {
                    AddInput(ref builder, inputs[i], call.BatchSize, -1);
                }
            }

            var shared = builder.Finish128();
            var items = call.Items.Span;
            for (var row = 0; row < keys.Length; row++)
            {
                builder.Reset();
                builder.Add((long)(ulong)(shared >> 64));
                builder.Add((long)(ulong)shared);
                if (items[row].StringValue is { } text)
                {
                    builder.Add(text);
                }
                else
                {
                    builder.Add(items[row].Int64Value);
                }

                foreach (var i in order)
                {
                    if (RowView(inputs[i], call.BatchSize))
                    {
                        AddInput(ref builder, inputs[i], call.BatchSize, row);
                    }
                }

                keys[row] = builder.Finish128();
            }
        }
        finally
        {
            builder.Dispose();
        }
    }

    /// <summary>The input is split by row in a row's digest and key: row-wise, of whole rows.</summary>
    public static bool Splits(in NamedInput input, int batch) => IsRowWise(input, batch) && input.Values.Length % batch == 0;

    private static void AddCachePrefix(ref StableHash.Builder builder, ServiceCall call, string unit, string identity)
    {
        builder.Add(CacheFormat);
        builder.Add(identity);
        builder.Add(unit);
        builder.Add(call.Version is not null);
        if (call.Version is { } version)
        {
            builder.Add(version);
        }

        var outputs = call.Outputs.Span;
        builder.AddIndex(outputs.Length);
        foreach (var output in outputs)
        {
            builder.Add(output);
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

    /// <summary>The input's leading dimension is the batch: each row has its own slice (a missing one, missing on every row).</summary>
    public static bool IsRowWise(in NamedInput input, int batch) =>
        batch > 0 && input.ElementType != TensorElementType.Text && input.Shape is { Length: > 0 } shape && shape[0] == batch;

    /// <summary>
    /// The input has a leading dimension of rows: its batch's (each row its slice) or 1 (shared, each row all of it). A
    /// row's digest has what the row saw of it, without that dimension (see the remarks).
    /// </summary>
    private static bool RowView(in NamedInput input, int batch) =>
        batch > 0 && input.ElementType != TensorElementType.Text && input.Shape is { Length: > 0 } shape && (shape[0] == batch || shape[0] == 1);

    /// <summary>The limit of a call that reads named outputs, in a cache key (see the remarks); nothing for one that reads only its items.</summary>
    private static void AddLimit(ref StableHash.Builder builder, ServiceCall call)
    {
        if (!call.Outputs.IsEmpty)
        {
            builder.Add("limit");
            builder.Add(call.Limit);
        }
    }

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
        // A shared [1, ...] input is what every row saw, all of it (in a batch of one, it is row-wise and its slice is all).
        var sliced = row >= 0 && IsRowWise(input, batch) && length % batch == 0;
        var shared = row >= 0 && !sliced && RowView(input, batch) && input.Shape[0] == 1;
        builder.Add(input.Name);
        builder.AddIndex((int)input.ElementType);
        var shape = input.Shape;
        builder.AddIndex(shape.Length);
        for (var d = 0; d < shape.Length; d++)
        {
            builder.AddIndex((sliced || shared) && d == 0 ? -1 : shape[d]); // the rows' dimension, normalized
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
