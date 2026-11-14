using System.Globalization;
using System.Text;
using System.Text.Json;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

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
/// <param name="Used">Elements that named a row first: the rows the operator places or appends, in the answer's order.</param>
/// <param name="Unknown">
/// Elements that named no row: a window rank out of range, an id not in the window, or a value of the wrong JSON type.
/// </param>
/// <param name="Repeated">Elements that named a row or id an earlier element named.</param>
/// <param name="Present">Ids a generative retriever skipped because the table already has them.</param>
/// <param name="OverLimit">Elements after the most a generative retriever appends: not read.</param>
public readonly record struct AnswerCounts(int Used, int Unknown, int Repeated, int Present = 0, int OverLimit = 0)
{
    public int Elements => Used + Unknown + Repeated + Present + OverLimit;
}

/// <summary>
/// Parses generated answers (design 5.9 rule 3). The one place that decides what an answer may look like, so every
/// generative operator agrees: a JSON array of window rows (<c>LlmReranker@1</c>) or of new ids
/// (<c>GenerativeRetriever@1</c>), or one value per row (<c>LlmAnnotator@1</c>).
/// </summary>
/// <remarks>
/// <para>An answer is trimmed and, when it is wrapped in one Markdown code fence (<c>```json</c> or <c>```</c> on the
/// first line, <c>```</c> at the end), the inside is taken: models add fences even when told not to. Nothing else is
/// extracted from the text: an answer the operator cannot read as it is reveals a prompt to fix, and what a model wraps
/// around its answer varies by model.</para>
/// <para>An array answer must be one JSON array (RFC 8259: no comments, no trailing commas, nothing after it). Its
/// elements are walked once, by one strict walk; what each names is the operator's (window rows by rank or id, or new ids
/// of the table's key type), and every element is counted by what became of it (<see cref="AnswerCounts"/>): nested
/// arrays and objects are elements of the wrong type. An answer that is not such an array is a failure, with the reason;
/// a JSON error's line and byte count within the text parsed, after the trim and the fence.</para>
/// <para>A per-row value is the trimmed text, one of the declared categories, <c>true</c> / <c>false</c> (any case), or a
/// number in JSON's syntax (invariant, no <c>+</c>, no leading zeros, no <c>NaN</c>) that is finite and fits the column.</para>
/// <para>What is allocated: the UTF-8 copy of an array answer (rented from the request), the strings of new string ids, and
/// the text of a per-row answer when trimming or the fence changed it; a category is the configured string.</para>
/// </remarks>
public static class LlmAnswer
{
    private const string Fence = "```";

    /// <summary>Answers of numbers no longer than this are converted to UTF-8 on the stack.</summary>
    private const int StackNumber = 128;

    /// <summary>
    /// Parses an answer of window rows. On success (null), <paramref name="positions"/> starts with the positions the
    /// answer named, in its order (<see cref="AnswerCounts.Used"/> of them), and <paramref name="listed"/> flags them;
    /// otherwise returns why the answer is not a JSON array.
    /// </summary>
    /// <remarks>
    /// Entity ids are found by scanning the window, which is small (the rows a prompt shows); the first row with the id
    /// wins, so a window that somehow holds an id twice resolves the same way every time.
    /// </remarks>
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
        var elements = new WindowElements(kind, table, window, positions, listed);
        return ParseArray(answer, memory, ref elements, out counts);
    }

    /// <summary>
    /// Parses an answer of new candidates: a JSON array of ids of <paramref name="table"/>'s key type. On success (null),
    /// the first <see cref="AnswerCounts.Used"/> entries of <paramref name="int64Ids"/> or <paramref name="stringIds"/>
    /// are the ids to append, in the answer's order, and those of <paramref name="positions"/> their 1-based positions in
    /// the answer; otherwise returns why the answer is not a JSON array.
    /// </summary>
    /// <remarks>
    /// An id named before is repeated; an id the table already has (live or dropped) is present and skipped, since a row
    /// source appends rows and writes only those; once <paramref name="limit"/> ids are used, every later element is over
    /// the limit and not read, though the rest must still be valid JSON.
    /// </remarks>
    /// <param name="int64Ids">At least <paramref name="limit"/> long for int64 keys; empty for string keys.</param>
    /// <param name="stringIds">At least <paramref name="limit"/> long for string keys; empty for int64 keys.</param>
    /// <param name="positions">At least <paramref name="limit"/> long.</param>
    internal static string? ParseIds(
        string answer, CandidateTable table, int limit, RequestMemory memory, Span<long> int64Ids, Span<string> stringIds, Span<int> positions,
        out AnswerCounts counts)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(memory);
        var elements = new IdElements(table, new EntityKeyIndex(table.KeyType, Math.Max(1, limit), memory), limit, int64Ids, stringIds, positions);
        return ParseArray(answer, memory, ref elements, out counts);
    }

    /// <summary>
    /// Parses a per-row answer as a value of <paramref name="kind"/> (the column's storage): the value, or missing with
    /// why it is not usable in <paramref name="unusable"/>.
    /// </summary>
    /// <param name="categories">The allowed answers of a string column, or null for any non-empty text.</param>
    internal static ScalarValue ParseValue(string answer, StorageKind kind, string[]? categories, RequestMemory memory, out string? unusable)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(memory);
        unusable = null;
        var text = Unfence(answer.AsSpan().Trim());
        if (text.IsEmpty)
        {
            unusable = "it is empty";
            return ScalarValue.Missing;
        }

        switch (kind)
        {
            case StorageKind.String or StorageKind.Text when categories is not null:
                foreach (var category in categories)
                {
                    if (text.SequenceEqual(category))
                    {
                        return ScalarValue.FromString(category);
                    }
                }

                unusable = $"it is not one of the {categories.Length.ToString(CultureInfo.InvariantCulture)} categories";
                return ScalarValue.Missing;
            case StorageKind.String:
                return ScalarValue.FromString(text.Length == answer.Length ? answer : text.ToString());
            case StorageKind.Text:
                return ScalarValue.FromText(text.Length == answer.Length ? answer : text.ToString());
            case StorageKind.Bool:
                if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    return ScalarValue.FromBool(text.Length == 4);
                }

                unusable = "it is not true or false";
                return ScalarValue.Missing;
            case StorageKind.Int32 or StorageKind.Int64 or StorageKind.Float32 or StorageKind.Float64:
                return ParseNumber(text, kind, memory, out unusable);
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "An answer is parsed as text, a string, a bool or a number.");
        }
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

        // Only the outer fence: backticks inside, such as in a JSON string, are content (review 29); anything else after the
        // array, a second code block included, fails the strict parse.
        return text[(newline + 1)..^Fence.Length].Trim();
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

    /// <summary>
    /// The strict walk every array answer takes: trim, unfence, one JSON array and nothing after it; each element is handed
    /// to <paramref name="elements"/> on its first token (a nested array or object is skipped afterwards) and counted by
    /// what it returns.
    /// </summary>
    private static string? ParseArray<TElements>(string answer, RequestMemory memory, ref TElements elements, out AnswerCounts counts)
        where TElements : IAnswerElements, allows ref struct
    {
        counts = default;
        var text = Unfence(answer.AsSpan().Trim());
        if (text.IsEmpty)
        {
            return "the answer is empty";
        }

        var bytes = memory.Rent<byte>(Encoding.UTF8.GetMaxByteCount(text.Length));
        var length = Encoding.UTF8.GetBytes(text, bytes);
        var reader = new Utf8JsonReader(bytes.AsSpan(0, length), new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
        int used = 0, unknown = 0, repeated = 0, present = 0, overLimit = 0;
        try
        {
            reader.Read();
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                return $"it is a JSON {Describe(reader.TokenType)}, not an array";
            }

            var index = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                var outcome = elements.Read(ref reader, index++);
                if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                {
                    reader.Skip();
                }

                switch (outcome)
                {
                    case ElementOutcome.Used:
                        used++;
                        break;
                    case ElementOutcome.Repeated:
                        repeated++;
                        break;
                    case ElementOutcome.Present:
                        present++;
                        break;
                    case ElementOutcome.OverLimit:
                        overLimit++;
                        break;
                    default:
                        unknown++;
                        break;
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

        counts = new AnswerCounts(used, unknown, repeated, present, overLimit);
        return null;
    }

    /// <summary>One JSON number, nothing else, that is finite and fits <paramref name="kind"/>.</summary>
    private static ScalarValue ParseNumber(ReadOnlySpan<char> text, StorageKind kind, RequestMemory memory, out string? unusable)
    {
        // JSON's number grammar is ASCII, so each character is one byte.
        Span<byte> bytes = text.Length <= StackNumber ? stackalloc byte[StackNumber] : memory.Rent<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] > 0x7F)
            {
                unusable = "it is not a JSON number";
                return ScalarValue.Missing;
            }

            bytes[i] = (byte)text[i];
        }

        var reader = new Utf8JsonReader(bytes[..text.Length]);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number)
            {
                unusable = "it is not a JSON number";
                return ScalarValue.Missing;
            }

            // The reader moves on to check that nothing follows; the copy stays on the number.
            var number = reader;
            if (reader.Read())
            {
                unusable = "it is not a JSON number";
                return ScalarValue.Missing;
            }

            return Number(ref number, kind, out unusable);
        }
        catch (JsonException)
        {
            unusable = "it is not a JSON number";
            return ScalarValue.Missing;
        }
    }

    /// <summary>The number token as <paramref name="kind"/>, when it is finite and fits.</summary>
    private static ScalarValue Number(ref Utf8JsonReader number, StorageKind kind, out string? unusable)
    {
        unusable = null;
        switch (kind)
        {
            case StorageKind.Int64 when number.TryGetInt64(out var l):
                return ScalarValue.FromInt64(l);
            case StorageKind.Int32 when number.TryGetInt32(out var i):
                return ScalarValue.FromInt64(i);
            case StorageKind.Float64 when number.TryGetDouble(out var d) && double.IsFinite(d):
                return ScalarValue.FromFloat64(d);
            case StorageKind.Float32 when number.TryGetSingle(out var f) && float.IsFinite(f):
                return ScalarValue.FromFloat64(f);
            case StorageKind.Int64 or StorageKind.Int32:
                unusable = $"it is not an integer in the {(kind == StorageKind.Int64 ? "int64" : "int32")} range";
                return ScalarValue.Missing;
            default:
                unusable = $"it is outside the {(kind == StorageKind.Float64 ? "float64" : "float32")} range";
                return ScalarValue.Missing;
        }
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

    /// <summary>What became of one element of an array answer.</summary>
    private enum ElementOutcome : byte
    {
        Unknown,
        Used,
        Repeated,
        Present,
        OverLimit,
    }

    /// <summary>What an operator reads from the elements of an array answer (<see cref="ParseArray"/>).</summary>
    private interface IAnswerElements
    {
        /// <summary>Reads the element the reader is on (its first token), the <paramref name="index"/>-th (0-based) of the array.</summary>
        ElementOutcome Read(ref Utf8JsonReader reader, int index);
    }

    /// <summary>Window rows, by 1-based rank or by entity id; each named once.</summary>
    private ref struct WindowElements(AnswerKind kind, CandidateTable table, ReadOnlySpan<int> window, Span<int> positions, Span<bool> listed)
        : IAnswerElements
    {
        private readonly ReadOnlySpan<int> _window = window;
        private readonly Span<int> _positions = positions;
        private readonly Span<bool> _listed = listed;
        private int _used;

        public ElementOutcome Read(ref Utf8JsonReader reader, int index)
        {
            var position = Position(ref reader, kind, table, _window);
            if (position < 0)
            {
                return ElementOutcome.Unknown;
            }

            if (_listed[position])
            {
                return ElementOutcome.Repeated;
            }

            _listed[position] = true;
            _positions[_used++] = position;
            return ElementOutcome.Used;
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
            try
            {
                for (var p = 0; p < window.Length; p++)
                {
                    if (reader.ValueTextEquals(keys.Get(window[p])))
                    {
                        return p;
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // A JSON string that is not valid Unicode (an unpaired surrogate escape such as \uD800) is valid JSON but names
                // no entity: an unknown element, like any other string that is not a window row's id (review 29).
            }

            return -1;
        }
    }

    /// <summary>New ids of the table's key type, each named once, not in the table, up to a limit.</summary>
    private ref struct IdElements(CandidateTable table, EntityKeyIndex seen, int limit, Span<long> int64Ids, Span<string> stringIds, Span<int> positions)
        : IAnswerElements
    {
        private readonly Span<long> _int64Ids = int64Ids;
        private readonly Span<string> _stringIds = stringIds;
        private readonly Span<int> _positions = positions;
        private int _used;

        public ElementOutcome Read(ref Utf8JsonReader reader, int index)
        {
            if (_used == limit)
            {
                return ElementOutcome.OverLimit;
            }

            if (table.KeyType == EntityKeyType.Int64)
            {
                if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var id))
                {
                    return ElementOutcome.Unknown;
                }

                if (!seen.TryAdd(id, index))
                {
                    return ElementOutcome.Repeated;
                }

                if (table.FindRow(EntityKey.FromInt64(id)) >= 0)
                {
                    return ElementOutcome.Present;
                }

                _int64Ids[_used] = id;
            }
            else
            {
                if (reader.TokenType != JsonTokenType.String)
                {
                    return ElementOutcome.Unknown;
                }

                string id;
                try
                {
                    id = reader.GetString()!;
                }
                catch (InvalidOperationException)
                {
                    return ElementOutcome.Unknown; // not valid Unicode (an unpaired surrogate escape): no entity has it as its id
                }

                if (!seen.TryAdd(id, index))
                {
                    return ElementOutcome.Repeated;
                }

                if (table.FindRow(EntityKey.FromString(id)) >= 0)
                {
                    return ElementOutcome.Present;
                }

                _stringIds[_used] = id;
            }

            _positions[_used++] = index + 1;
            return ElementOutcome.Used;
        }
    }
}
