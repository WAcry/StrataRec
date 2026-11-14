using System.Globalization;
using System.Text;
using System.Text.Json;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;

namespace StrataRec.Llm;

/// <summary>What the elements of a generated answer name.</summary>
public enum AnswerKind : byte
{
    /// <summary>1-based positions in the window, as <c>{{rank}}</c> shows them.</summary>
    Rank = 1,

    /// <summary>Entity ids: JSON integers for int64 keys, JSON strings for string keys.</summary>
    EntityId,
}

/// <summary>What became of an answer's elements (<c>stratarec.model.answer_ids</c>).</summary>
/// <param name="Used">Elements that named a window row first: the rows the new order lists, in the answer's order.</param>
/// <param name="Unknown">Elements that named no window row: out of range, an id not in the window, or a value of the wrong JSON type.</param>
/// <param name="Repeated">Elements that named a row an earlier element named.</param>
public readonly record struct AnswerCounts(int Used, int Unknown, int Repeated)
{
    public int Elements => Used + Unknown + Repeated;
}

/// <summary>
/// Parses a generated answer into window positions (design 5.9 rule 3). The one place that decides what an answer may
/// look like, so every generative operator that reads rows from text agrees.
/// </summary>
/// <remarks>
/// <para>The answer is trimmed and, when it is wrapped in one Markdown code fence (<c>```json</c> or <c>```</c> on the
/// first line, <c>```</c> at the end), the inside is taken: models add fences even when told not to. What remains must be
/// one JSON array (RFC 8259: no comments, no trailing commas, nothing after it). Each element names a window row or is
/// counted: unknown (no such row, or a value of the wrong JSON type, nested arrays and objects included) or repeated
/// (a row named before). An answer that is not such an array is a failure, with the reason; a JSON error's line and byte
/// count within the text parsed, after the trim and the fence.</para>
/// <para>Entity ids are found by scanning the window, which is small (the rows a prompt shows); the first row with the id
/// wins, so a window that somehow holds an id twice resolves the same way every time. Nothing is allocated but the
/// UTF-8 copy of the answer, rented from the request.</para>
/// </remarks>
public static class LlmAnswer
{
    private const string Fence = "```";

    /// <summary>
    /// Parses <paramref name="answer"/>. On success (null), <paramref name="positions"/> starts with the positions the
    /// answer named, in its order (<see cref="AnswerCounts.Used"/> of them), and <paramref name="listed"/> flags them;
    /// otherwise returns why the answer is not a JSON array.
    /// </summary>
    /// <param name="window">The window's physical rows, in rank order.</param>
    /// <param name="positions">At least <paramref name="window"/>'s length.</param>
    /// <param name="listed">At least <paramref name="window"/>'s length, all false.</param>
    public static string? Parse(
        string answer, AnswerKind kind, CandidateTable table, ReadOnlySpan<int> window, RequestMemory memory, Span<int> positions, Span<bool> listed,
        out AnswerCounts counts)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(memory);
        counts = default;
        var text = Unfence(answer.AsSpan().Trim());
        if (text.IsEmpty)
        {
            return "the answer is empty";
        }

        var bytes = memory.Rent<byte>(Encoding.UTF8.GetMaxByteCount(text.Length));
        var length = Encoding.UTF8.GetBytes(text, bytes);
        var reader = new Utf8JsonReader(bytes.AsSpan(0, length), new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
        int used = 0, unknown = 0, repeated = 0;
        try
        {
            reader.Read();
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                return $"it is a JSON {Describe(reader.TokenType)}, not an array";
            }

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                var position = Position(ref reader, kind, table, window);
                if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                {
                    reader.Skip();
                }

                if (position < 0)
                {
                    unknown++;
                }
                else if (listed[position])
                {
                    repeated++;
                }
                else
                {
                    listed[position] = true;
                    positions[used++] = position;
                }
            }

            if (reader.Read())
            {
                return "there is more after the array";
            }
        }
        catch (JsonException ex)
        {
            var at = ex.LineNumber is > 0 and var line
                ? $"line {(line + 1).ToString(CultureInfo.InvariantCulture)}, byte {ex.BytePositionInLine?.ToString(CultureInfo.InvariantCulture)}"
                : $"byte {ex.BytePositionInLine?.ToString(CultureInfo.InvariantCulture)}";
            return $"it is not valid JSON ({at}: {FirstSentence(ex.Message)})";
        }

        counts = new AnswerCounts(used, unknown, repeated);
        return null;
    }

    /// <summary>The inside of one Markdown code fence around the whole answer (<c>```json</c> or <c>```</c>); otherwise the answer as it is.</summary>
    public static ReadOnlySpan<char> Unfence(ReadOnlySpan<char> text)
    {
        if (!text.StartsWith(Fence, StringComparison.Ordinal) || !text.EndsWith(Fence, StringComparison.Ordinal))
        {
            return text;
        }

        var newline = text.IndexOf('\n');
        if (newline < 0 || newline > text.Length - Fence.Length)
        {
            return text;
        }

        var info = text[Fence.Length..newline].Trim();
        if (!info.IsEmpty && !info.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        var inside = text[(newline + 1)..^Fence.Length];
        return inside.Contains(Fence, StringComparison.Ordinal) ? text : inside.Trim();
    }

    /// <summary>The start of an answer for a message: at most <paramref name="max"/> characters, quoted, on one line.</summary>
    public static string Quote(string answer, int max = 200)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var text = answer.AsSpan().Trim();
        var cut = text.Length > max;
        var quoted = new StringBuilder(Math.Min(text.Length, max) + 8).Append('"');
        foreach (var c in cut ? text[..max] : text)
        {
            _ = c switch
            {
                '\n' => quoted.Append("\\n"),
                '\r' => quoted.Append("\\r"),
                '\t' => quoted.Append("\\t"),
                '"' => quoted.Append("\\\""),
                '\\' => quoted.Append("\\\\"),
                _ => quoted.Append(c),
            };
        }

        return quoted.Append(cut ? "…\"" : "\"").ToString();
    }

    /// <summary>The window position an element names, or -1.</summary>
    private static int Position(ref Utf8JsonReader reader, AnswerKind kind, CandidateTable table, ReadOnlySpan<int> window)
    {
        if (kind == AnswerKind.Rank)
        {
            return reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var rank) && rank >= 1 && rank <= window.Length ? (int)(rank - 1) : -1;
        }

        if (table.KeyType == EntityKeyType.Int64)
        {
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var id))
            {
                return -1;
            }

            var ids = table.Int64Ids;
            for (var p = 0; p < window.Length; p++)
            {
                if (ids[window[p]] == id)
                {
                    return p;
                }
            }

            return -1;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            return -1;
        }

        var keys = table.StringIds;
        for (var p = 0; p < window.Length; p++)
        {
            if (reader.ValueTextEquals(keys.Get(window[p])))
            {
                return p;
            }
        }

        return -1;
    }

    private static string Describe(JsonTokenType token) => token switch
    {
        JsonTokenType.StartObject => "object",
        JsonTokenType.String => "string",
        JsonTokenType.Number => "number",
        JsonTokenType.True or JsonTokenType.False => "bool",
        JsonTokenType.Null => "null",
        _ => token.ToString().ToLowerInvariant(),
    };

    /// <summary>System.Text.Json's message up to its position details, which the caller states its own way.</summary>
    private static string FirstSentence(string message)
    {
        var details = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
        return (details < 0 ? message : message[..details]).TrimEnd('.', ' ');
    }
}
