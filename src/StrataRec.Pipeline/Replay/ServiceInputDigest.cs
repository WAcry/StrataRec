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
/// and the row's digest changed with the batch). What a row saw of an input enters its digest inline when it is a few
/// values (at most <see cref="MaxInlineValues"/>) or missing, and as a 128-bit digest of that encoding otherwise (a text
/// always): which, depends only on what the row saw, so the same row has the same digest whether it saw a slice or all of
/// a shared input. A digest the rows share is computed once for the call: a shared vector is not hashed again for every
/// row (review of 43bdf97: a call of 2,048 rows hashed a shared vector of 4,096 values 2,048 times, which a full cache
/// hit spent its node's budget on), and a row's scalar is not hashed twice (review of f0f6871: every view as a digest
/// made a call of many per-row inputs half as slow again). A missing input contributes its name, element
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
    /// <summary>The digest of each row of a call about items, into <paramref name="digests"/>: every input in name order, as what the row saw of it (see the remarks).</summary>
    public static void Rows(ServiceCall call, int[] order, Span<ulong> digests)
    {
        var inputs = call.Inputs.Span;
        var stack = order.Length <= MaxStackInputs;
        Span<UInt128> seen = stack ? stackalloc UInt128[order.Length] : new UInt128[order.Length];
        Span<Form> forms = stack ? stackalloc Form[order.Length] : new Form[order.Length];
        var builder = default(StableHash.Builder);
        var part = default(StableHash.Builder);
        try
        {
            Shared(ref part, call, order, seen, forms, all: true);
            for (var row = 0; row < digests.Length; row++)
            {
                builder.Reset();
                AddOptions(ref builder, call.Options);
                for (var k = 0; k < order.Length; k++)
                {
                    AddSeen(ref builder, ref part, inputs[order[k]], call.BatchSize, row, forms[k], seen[k]);
                }

                digests[row] = builder.Finish();
            }
        }
        finally
        {
            builder.Dispose();
            part.Dispose();
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
    private const string CacheFormat = "stratarec.service_cache/2026-11-21.5";

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
        var inputs = call.Inputs.Span;
        var stack = order.Length <= MaxStackInputs;
        Span<UInt128> seen = stack ? stackalloc UInt128[order.Length] : new UInt128[order.Length];
        Span<Form> forms = stack ? stackalloc Form[order.Length] : new Form[order.Length];
        var builder = default(StableHash.Builder);
        var part = default(StableHash.Builder);
        try
        {
            AddCachePrefix(ref builder, call, "item", identity);
            AddOptions(ref builder, call.Options);
            foreach (var i in order)
            {
                if (!RowView(inputs[i], call.BatchSize))
                {
                    AddInput(ref builder, inputs[i], call.BatchSize, -1);
                }
            }

            var shared = builder.Finish128();
            Shared(ref part, call, order, seen, forms, all: false);
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

                for (var k = 0; k < order.Length; k++)
                {
                    var input = inputs[order[k]];
                    if (RowView(input, call.BatchSize))
                    {
                        AddSeen(ref builder, ref part, input, call.BatchSize, row, forms[k], seen[k]);
                    }
                }

                keys[row] = builder.Finish128();
            }
        }
        finally
        {
            builder.Dispose();
            part.Dispose();
        }
    }

    /// <summary>Inputs a row's digest or key takes on the stack (more take an array).</summary>
    private const int MaxStackInputs = 32;

    /// <summary>
    /// Values a row's view of an input holds inline in its digest or key (see the remarks); a view of more, or a text,
    /// enters as a 128-bit digest of its own.
    /// </summary>
    private const int MaxInlineValues = 16;

    /// <summary>How what a row saw of an input enters its digest or key.</summary>
    private enum Form : byte
    {
        /// <summary>Its encoding: a view of a few values, or a missing input. Each row encodes it.</summary>
        Inline,

        /// <summary>The digest of its encoding, the same for every row: computed once for the call.</summary>
        Shared,

        /// <summary>The digest of its encoding, the row's own slice: computed for each row.</summary>
        PerRow,
    }

    /// <summary>
    /// How each input enters a row's digest or key, by name order, of every input (<paramref name="all"/>) or of those
    /// with a leading dimension of rows, into <paramref name="forms"/>; the digest of each <see cref="Form.Shared"/> one
    /// into <paramref name="seen"/>.
    /// </summary>
    private static void Shared(ref StableHash.Builder part, ServiceCall call, int[] order, scoped Span<UInt128> seen, scoped Span<Form> forms, bool all)
    {
        var inputs = call.Inputs.Span;
        for (var k = 0; k < order.Length; k++)
        {
            var input = inputs[order[k]];
            if (!all && !RowView(input, call.BatchSize))
            {
                continue;
            }

            var view = new View(input, call.BatchSize, 0); // the count is every row's
            forms[k] = input.IsMissing || (input.ElementType != TensorElementType.Text && view.Count <= MaxInlineValues) ? Form.Inline
                : PerRow(input, call.BatchSize) ? Form.PerRow
                : Form.Shared;
            if (forms[k] == Form.Shared)
            {
                seen[k] = Seen(ref part, input, call.BatchSize, 0);
            }
        }
    }

    /// <summary>Each row has a slice of its own of the input, which differs by row: row-wise in a batch of more than one, present, of whole rows.</summary>
    private static bool PerRow(in NamedInput input, int batch) => batch > 1 && !input.IsMissing && Splits(input, batch);

    /// <summary>
    /// What row <paramref name="row"/> saw of the input, into its digest or key, in its <paramref name="form"/>: inline,
    /// or as a digest (starting with an int64, where an inline view starts with the input's name, so the two cannot be
    /// read as each other).
    /// </summary>
    private static void AddSeen(ref StableHash.Builder builder, ref StableHash.Builder part, in NamedInput input, int batch, int row, Form form, UInt128 shared)
    {
        if (form == Form.Inline)
        {
            AddInput(ref builder, input, batch, row);
            return;
        }

        var digest = form == Form.Shared ? shared : Seen(ref part, input, batch, row);
        builder.Add((long)(ulong)(digest >> 64));
        builder.Add((long)(ulong)digest);
    }

    /// <summary>The 128-bit digest of what row <paramref name="row"/> saw of the input.</summary>
    private static UInt128 Seen(ref StableHash.Builder part, in NamedInput input, int batch, int row)
    {
        part.Reset();
        AddInput(ref part, input, batch, row);
        return part.Finish128();
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
        var view = new View(input, batch, row);
        builder.Add(input.Name);
        builder.AddIndex((int)input.ElementType);
        var shape = input.Shape;
        builder.AddIndex(shape.Length);
        for (var d = 0; d < shape.Length; d++)
        {
            builder.AddIndex(view.Normalized && d == 0 ? -1 : shape[d]); // the rows' dimension, normalized
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

        var count = view.Count;
        var start = view.Start;
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

    /// <summary>
    /// What a row sees of an input: whether the rows' dimension is left out of its shape, and which of its values. Row-wise
    /// data that does not split evenly into rows is malformed; it is seen whole, which can only miss a match. A shared
    /// <c>[1, ...]</c> input is what every row saw, all of it (in a batch of one, it is row-wise and its slice is all).
    /// </summary>
    private readonly struct View
    {
        /// <param name="row">The row that sees it; -1 sees every input whole.</param>
        public View(in NamedInput input, int batch, int row)
        {
            var length = input.ElementType == TensorElementType.Text ? 0 : input.Values.Length;
            var sliced = row >= 0 && IsRowWise(input, batch) && length % batch == 0;
            Normalized = sliced || (row >= 0 && RowView(input, batch) && input.Shape[0] == 1);
            Count = sliced ? length / batch : length;
            Start = sliced ? row * Count : 0;
        }

        public bool Normalized { get; }

        public int Start { get; }

        public int Count { get; }
    }
}
