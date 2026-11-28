using StrataRec.Core.Hashing;
using StrataRec.Core.Services;

namespace StrataRec.Pipeline.Replay;

/// <remarks>
/// A row's digest covers what the row saw of each input. Each input contributes its name, element type and shape, so
/// the same values in another layout do not collide; the leading dimension of a row-wise or shared <c>[1, ...]</c>
/// input is left out, so the same row in a differently sized batch has the same digest.
/// </remarks>
internal static class ServiceInputDigest
{
    public static void Rows(ServiceCall call, int[] order, Span<ulong> digests)
    {
        var inputs = call.Inputs.Span;
        Span<Segment> segments = order.Length <= MaxStackInputs ? stackalloc Segment[order.Length + 1] : new Segment[order.Length + 1];
        var builder = default(StableHash.Builder);
        var part = default(StableHash.Builder);
        var alike = default(StableHash.Builder);
        try
        {
            AddOptions(ref alike, call.Options);
            segments = segments[..Segments(ref part, ref alike, call, order, segments, all: true)];
            for (var row = 0; row < digests.Length; row++)
            {
                builder.Reset();
                AddRow(ref builder, ref part, inputs, call.BatchSize, row, segments, alike.Encoded);
                digests[row] = builder.Finish();
            }
        }
        finally
        {
            builder.Dispose();
            part.Dispose();
            alike.Dispose();
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
    /// The format of service cache keys (docs/decisions/external-calls.md). A change of what a record holds or of how a key is
    /// built changes it: the records another build wrote are then misses, never misread.
    /// </summary>
    private const string CacheFormat = "stratarec.service_cache/2026-11-21.7";

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

    public static void CacheItemKeys(ServiceCall call, int[] order, string identity, Span<UInt128> keys)
    {
        var inputs = call.Inputs.Span;
        Span<Segment> segments = order.Length <= MaxStackInputs ? stackalloc Segment[order.Length + 1] : new Segment[order.Length + 1];
        var builder = default(StableHash.Builder);
        var part = default(StableHash.Builder);
        var alike = default(StableHash.Builder);
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
            segments = segments[..Segments(ref part, ref alike, call, order, segments, all: false)];
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

                AddRow(ref builder, ref part, inputs, call.BatchSize, row, segments, alike.Encoded);
                keys[row] = builder.Finish128();
            }
        }
        finally
        {
            builder.Dispose();
            part.Dispose();
            alike.Dispose();
        }
    }

    private const int MaxStackInputs = 64;

    /// <summary>
    /// A row's own view as a digest costs the row one more hash; a shared view inline is copied into every row's bytes
    /// (encoded once, <see cref="Segments"/>) and hashed with each, where its digest is 16 bytes.
    /// </summary>
    private const int MaxInlineValues = 16;

    /// <summary>
    /// A run of what every row of a call adds alike to its digest or key, encoded once for the call, then what a row adds
    /// of its own of one input: its few values, encoded, or its slice's digest.
    /// </summary>
    private struct Segment
    {
        public int Start;

        public int Length;

        /// <summary>The input a row adds its own part of, by its index in the call's inputs; -1 for none (the last run).</summary>
        public int Input;

        /// <summary>The row's own part is its few values, encoded after the input's header (which ends the run); otherwise its slice's digest.</summary>
        public bool Inline;

        /// <summary>Of a row's own digest: where the input's header is among the call's encoded parts (outside every run), which each row's digest starts with.</summary>
        public int HeaderStart;

        public int HeaderLength;
    }

    /// <summary>
    /// Lays out what each row of a call adds to its digest or key, by name order, of every input
    /// (<paramref name="all"/>) or of those with a leading dimension of rows, into <paramref name="segments"/>; returns
    /// how many.
    /// </summary>
    private static int Segments(ref StableHash.Builder part, ref StableHash.Builder alike, ServiceCall call, int[] order, scoped Span<Segment> segments, bool all)
    {
        var inputs = call.Inputs.Span;
        var count = 0;
        var start = 0;
        foreach (var i in order)
        {
            ref readonly var input = ref inputs[i];
            if (!all && !RowView(input, call.BatchSize))
            {
                continue;
            }

            // A string's size is not its count: one string may be long.
            var view = new View(input, call.BatchSize, 0); // the count and the header are every row's
            var inline = input.IsMissing || (input.ElementType is not (TensorElementType.Text or TensorElementType.String) && view.Count <= MaxInlineValues);
            if (!PerRow(input, call.BatchSize))
            {
                if (inline)
                {
                    AddInput(ref alike, input, call.BatchSize, 0);
                }
                else
                {
                    AddDigest(ref alike, Seen(ref part, input, call.BatchSize, 0));
                }

                continue;
            }

            if (inline)
            {
                AddHeader(ref alike, input, view.Normalized);
                segments[count++] = new Segment { Start = start, Length = alike.Encoded.Length - start, Input = i, Inline = true };
            }
            else
            {
                // The header of the row's own digest, after the run and outside every run: encoded once, not once a row.
                var end = alike.Encoded.Length;
                AddHeader(ref alike, input, view.Normalized);
                segments[count++] = new Segment
                {
                    Start = start, Length = end - start, Input = i, HeaderStart = end, HeaderLength = alike.Encoded.Length - end,
                };
            }

            start = alike.Encoded.Length;
        }

        segments[count++] = new Segment { Start = start, Length = alike.Encoded.Length - start, Input = -1 };
        return count;
    }

    private static bool PerRow(in NamedInput input, int batch) => batch > 1 && !input.IsMissing && Splits(input, batch);

    /// <summary>
    /// A digest starts with an int64, where an inline view starts with the input's name, so the two cannot be read as
    /// each other.
    /// </summary>
    private static void AddRow(
        ref StableHash.Builder builder, ref StableHash.Builder part, ReadOnlySpan<NamedInput> inputs, int batch, int row, scoped ReadOnlySpan<Segment> segments,
        scoped ReadOnlySpan<byte> alike)
    {
        foreach (ref readonly var segment in segments)
        {
            builder.AddEncoded(alike.Slice(segment.Start, segment.Length));
            if (segment.Input < 0)
            {
                continue;
            }

            ref readonly var input = ref inputs[segment.Input];
            var view = new View(input, batch, row);
            if (segment.Inline)
            {
                AddBody(ref builder, input, view);
            }
            else
            {
                // The digest of the header and the row's values, as Seen has it.
                part.Reset();
                part.AddEncoded(alike.Slice(segment.HeaderStart, segment.HeaderLength));
                AddBody(ref part, input, view);
                AddDigest(ref builder, part.Finish128());
            }
        }
    }

    private static void AddDigest(ref StableHash.Builder builder, UInt128 digest)
    {
        builder.Add((long)(ulong)(digest >> 64));
        builder.Add((long)(ulong)digest);
    }

    private static UInt128 Seen(ref StableHash.Builder part, in NamedInput input, int batch, int row)
    {
        part.Reset();
        AddInput(ref part, input, batch, row);
        return part.Finish128();
    }

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

    public static bool IsRowWise(in NamedInput input, int batch) =>
        batch > 0 && input.ElementType != TensorElementType.Text && input.Shape is { Length: > 0 } shape && shape[0] == batch;

    /// <summary>
    /// Whether a row's digest has what the row saw of an input with a leading dimension of rows, without that dimension
    /// (see the remarks).
    /// </summary>
    private static bool RowView(in NamedInput input, int batch) =>
        batch > 0 && input.ElementType != TensorElementType.Text && input.Shape is { Length: > 0 } shape && (shape[0] == batch || shape[0] == 1);

    private static void AddLimit(ref StableHash.Builder builder, ServiceCall call)
    {
        if (!call.Outputs.IsEmpty)
        {
            builder.Add("limit");
            builder.Add(call.Limit);
        }
    }

    private static void AddOptions(ref StableHash.Builder builder, in ServiceCallOptions options)
    {
        if (options.MaxOutputTokens is { } max)
        {
            builder.Add("max_output_tokens");
            builder.Add(max);
        }
    }

    private static void AddInput(ref StableHash.Builder builder, in NamedInput input, int batch, int row)
    {
        var view = new View(input, batch, row);
        AddHeader(ref builder, input, view.Normalized);
        AddBody(ref builder, input, view);
    }

    private static void AddHeader(ref StableHash.Builder builder, in NamedInput input, bool normalized)
    {
        builder.Add(input.Name);
        builder.AddIndex((int)input.ElementType);
        var shape = input.Shape;
        builder.AddIndex(shape.Length);
        for (var d = 0; d < shape.Length; d++)
        {
            builder.AddIndex(normalized && d == 0 ? -1 : shape[d]);
        }
    }

    private static void AddBody(ref StableHash.Builder builder, in NamedInput input, in View view)
    {
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
    /// Row-wise data that does not split evenly into rows is malformed; it is seen whole, which can only miss a match.
    /// A shared <c>[1, ...]</c> input is what every row saw, all of it (in a batch of one, it is row-wise and its slice
    /// is all).
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
