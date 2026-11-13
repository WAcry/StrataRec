using StrataRec.Core.Hashing;
using StrataRec.Models;

namespace StrataRec.Pipeline.Replay;

/// <summary>
/// Digests of model inputs, so a replay serves a recorded output only for the inputs it was computed from: a changed
/// feature definition or model input changes the digest, and the output becomes a reported gap instead of a stale value.
/// </summary>
internal static class ModelInputDigest
{
    /// <summary>
    /// Digest of row <paramref name="row"/>: every input in name order, the row's slice of row-wise tensors (leading
    /// dimension = batch size), whole tensors and text otherwise.
    /// </summary>
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

    /// <summary>Digest of the whole call, for calls whose outputs are not per row.</summary>
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

    private static void AddInput(ref StableHash.Builder builder, in NamedInput input, int batch, int row)
    {
        builder.Add(input.Name);
        switch (input.ElementType)
        {
            case TensorElementType.Float32:
                var floats = Slice(input.Float32.Span, batch, row);
                builder.AddIndex(floats.Length);
                foreach (var f in floats)
                {
                    builder.Add((double)f);
                }

                break;
            case TensorElementType.Int64:
                var longs = Slice(input.Int64.Span, batch, row);
                builder.AddIndex(longs.Length);
                foreach (var l in longs)
                {
                    builder.Add(l);
                }

                break;
            default:
                builder.Add(input.Text ?? string.Empty);
                break;
        }
    }

    private static ReadOnlySpan<T> Slice<T>(ReadOnlySpan<T> data, int batch, int row)
    {
        if (row < 0 || batch <= 0 || data.Length % batch != 0)
        {
            return data;
        }

        var perRow = data.Length / batch;
        return data.Slice(row * perRow, perRow);
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
