using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace StrataRec.Artifacts.Json;

/// <summary>
/// An immutable <see href="https://www.rfc-editor.org/rfc/rfc6901">RFC 6901</see> JSON Pointer, the path syntax
/// StrataRec uses for patch footprints, policy scopes and the location of every configuration error (design 9.1,
/// 11.4, 11.5).
/// </summary>
/// <remarks>
/// <para>
/// A pointer is a sequence of reference tokens. The string form is empty for the whole document, otherwise each
/// token is prefixed with <c>/</c> and escaped per RFC 6901 section 3: <c>~</c> is written <c>~0</c> and
/// <c>/</c> is written <c>~1</c>. Because those are the only escapes and both are mandatory, the string form is
/// unique: <c>Parse(p).ToString() == p</c> for every valid <c>p</c>.
/// </para>
/// <para>
/// Equality, hashing and ordering are token-wise and ordinal (UTF-16 code units). The ordering sorts a pointer
/// immediately before all pointers it is a prefix of, so <c>/a</c> &lt; <c>/a/b</c> &lt; <c>/ab</c>; footprints
/// sorted this way list every subtree contiguously.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonPointerJsonConverter))]
[DebuggerDisplay("{ToString(),nq}")]
public sealed class JsonPointer : IEquatable<JsonPointer>, IComparable<JsonPointer>
{
    private readonly ImmutableArray<string> _tokens;
    private readonly int _hashCode;

    // Lazily formatted string form. Racing initializations compute identical strings, so no lock is needed.
    private string? _text;

    private JsonPointer(ImmutableArray<string> tokens, string? text)
    {
        _tokens = tokens;
        _text = text;
        var hash = new HashCode();
        foreach (var token in tokens)
        {
            hash.Add(token, StringComparer.Ordinal);
        }

        hash.Add(tokens.Length);
        _hashCode = hash.ToHashCode();
    }

    /// <summary>The empty pointer <c>""</c>, which refers to the whole document (RFC 6901 section 5).</summary>
    public static JsonPointer Root { get; } = new(ImmutableArray<string>.Empty, string.Empty);

    /// <summary>The unescaped reference tokens, outermost first.</summary>
    public ImmutableArray<string> Tokens => _tokens;

    /// <summary>Number of reference tokens; 0 for <see cref="Root"/>.</summary>
    public int Depth => _tokens.Length;

    /// <summary>True for the pointer to the whole document.</summary>
    public bool IsRoot => _tokens.IsEmpty;

    /// <summary>The last (innermost) unescaped token, or <see langword="null"/> for <see cref="Root"/>.</summary>
    public string? LastToken => _tokens.IsEmpty ? null : _tokens[^1];

    /// <summary>The pointer with the last token removed, or <see langword="null"/> for <see cref="Root"/>.</summary>
    public JsonPointer? Parent => _tokens.Length switch
    {
        0 => null,
        1 => Root,
        _ => new JsonPointer(_tokens.RemoveAt(_tokens.Length - 1), null),
    };

    /// <summary>Creates a pointer from unescaped reference tokens.</summary>
    /// <exception cref="ArgumentException">A token is <see langword="null"/>.</exception>
    public static JsonPointer Create(params string[] tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        return Create((IEnumerable<string>)tokens);
    }

    /// <summary>Creates a pointer from unescaped reference tokens.</summary>
    /// <exception cref="ArgumentException">A token is <see langword="null"/>.</exception>
    public static JsonPointer Create(IEnumerable<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (var token in tokens)
        {
            if (token is null)
            {
                throw new ArgumentException("JSON pointer tokens must not be null.", nameof(tokens));
            }

            builder.Add(token);
        }

        return builder.Count == 0 ? Root : new JsonPointer(builder.ToImmutable(), null);
    }

    /// <summary>Parses the RFC 6901 string representation, e.g. <c>/nodes/fusion/config</c> or <c>/a~1b</c>.</summary>
    /// <exception cref="FormatException">
    /// The text is not empty and does not start with <c>/</c>, or contains a <c>~</c> not followed by <c>0</c> or
    /// <c>1</c> (RFC 6901 section 3).
    /// </exception>
    public static JsonPointer Parse(string pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        return TryParseCore(pointer, out var result, out var error) ? result : throw new FormatException(error);
    }

    /// <summary>Parses the RFC 6901 string representation without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? pointer, [NotNullWhen(true)] out JsonPointer? result)
    {
        if (pointer is not null && TryParseCore(pointer, out var parsed, out _))
        {
            result = parsed;
            return true;
        }

        result = null;
        return false;
    }

    /// <summary>
    /// Computes the pointer from the root of <paramref name="node"/>'s tree (the ancestor without a parent) to
    /// <paramref name="node"/>. Useful for reporting where in a parsed document a problem was found.
    /// </summary>
    public static JsonPointer FromNode(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var tokens = new List<string>();
        for (var current = node; current.Parent is { } parent; current = parent)
        {
            tokens.Add(parent is JsonArray
                ? current.GetElementIndex().ToString(CultureInfo.InvariantCulture)
                : current.GetPropertyName());
        }

        tokens.Reverse();
        return Create(tokens);
    }

    /// <summary>Returns a pointer with one more (unescaped) token, e.g. <c>/nodes</c> + <c>a/b</c> = <c>/nodes/a~1b</c>.</summary>
    public JsonPointer Append(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var text = _text is null ? null : string.Concat(_text, "/", Escape(token));
        return new JsonPointer(_tokens.Add(token), text);
    }

    /// <summary>Returns a pointer with an array index token appended.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
    public JsonPointer Append(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return Append(index.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// True when this pointer's tokens are a leading subsequence of <paramref name="other"/>'s, compared token by
    /// token: <c>/a</c> is a prefix of <c>/a</c> and <c>/a/b</c> but not of <c>/ab</c>. <see cref="Root"/> is a
    /// prefix of every pointer. Use <see cref="IsProperPrefixOf"/> to exclude equality.
    /// </summary>
    public bool IsPrefixOf(JsonPointer other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (_tokens.Length > other._tokens.Length)
        {
            return false;
        }

        for (var i = 0; i < _tokens.Length; i++)
        {
            if (!string.Equals(_tokens[i], other._tokens[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when this pointer is a prefix of <paramref name="other"/> and not equal to it.</summary>
    public bool IsProperPrefixOf(JsonPointer other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return _tokens.Length < other._tokens.Length && IsPrefixOf(other);
    }

    /// <summary>
    /// Evaluates the pointer against a document (RFC 6901 section 4), distinguishing a member that is absent
    /// (returns <see langword="false"/>) from one that is present with JSON <c>null</c> (returns
    /// <see langword="true"/> with <paramref name="value"/> set to <see langword="null"/>).
    /// </summary>
    /// <remarks>
    /// Object members are matched by exact name. Array tokens must be <c>0</c> or a decimal number without leading
    /// zeros; <c>-</c> (the element after the last) and indexes past the end are absent. Descending into a scalar
    /// or <c>null</c> is absent.
    /// </remarks>
    public bool TryEvaluate(JsonNode? root, out JsonNode? value)
    {
        var current = root;
        foreach (var token in _tokens)
        {
            switch (current)
            {
                case JsonObject obj:
                    if (!JsonNodeUtilities.TryGetMember(obj, token, out current))
                    {
                        value = null;
                        return false;
                    }

                    break;

                case JsonArray array:
                    if (!TryParseArrayIndex(token, out var index) || index >= array.Count)
                    {
                        value = null;
                        return false;
                    }

                    current = array[index];
                    break;

                default:
                    value = null;
                    return false;
            }
        }

        value = current;
        return true;
    }

    /// <summary>The RFC 6901 string representation (empty for <see cref="Root"/>).</summary>
    public override string ToString() => _text ??= Format(_tokens);

    public bool Equals(JsonPointer? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null || _hashCode != other._hashCode || _tokens.Length != other._tokens.Length)
        {
            return false;
        }

        for (var i = 0; i < _tokens.Length; i++)
        {
            if (!string.Equals(_tokens[i], other._tokens[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as JsonPointer);

    public override int GetHashCode() => _hashCode;

    /// <summary>
    /// Orders token by token using ordinal comparison; a pointer sorts before every pointer it is a proper prefix
    /// of. <see langword="null"/> sorts first.
    /// </summary>
    public int CompareTo(JsonPointer? other)
    {
        if (other is null)
        {
            return 1;
        }

        var common = Math.Min(_tokens.Length, other._tokens.Length);
        for (var i = 0; i < common; i++)
        {
            var c = string.CompareOrdinal(_tokens[i], other._tokens[i]);
            if (c != 0)
            {
                return c;
            }
        }

        return _tokens.Length.CompareTo(other._tokens.Length);
    }

    public static bool operator ==(JsonPointer? left, JsonPointer? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(JsonPointer? left, JsonPointer? right) => !(left == right);

    public static bool operator <(JsonPointer? left, JsonPointer? right) => Compare(left, right) < 0;

    public static bool operator <=(JsonPointer? left, JsonPointer? right) => Compare(left, right) <= 0;

    public static bool operator >(JsonPointer? left, JsonPointer? right) => Compare(left, right) > 0;

    public static bool operator >=(JsonPointer? left, JsonPointer? right) => Compare(left, right) >= 0;

    /// <summary>Escapes a single reference token: <c>~</c> becomes <c>~0</c>, <c>/</c> becomes <c>~1</c>.</summary>
    public static string Escape(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.AsSpan().IndexOfAny('~', '/') < 0)
        {
            return token;
        }

        var builder = new StringBuilder(token.Length + 4);
        AppendEscaped(builder, token);
        return builder.ToString();
    }

    internal static bool TryParseCore(
        string text,
        [NotNullWhen(true)] out JsonPointer? result,
        [NotNullWhen(false)] out string? error)
    {
        if (text.Length == 0)
        {
            result = Root;
            error = null;
            return true;
        }

        if (text[0] != '/')
        {
            result = null;
            error = $"Invalid JSON pointer '{text}': a non-empty pointer must start with '/' (RFC 6901 section 3).";
            return false;
        }

        var tokens = ImmutableArray.CreateBuilder<string>();
        var start = 1;
        while (true)
        {
            var end = text.IndexOf('/', start);
            if (end < 0)
            {
                end = text.Length;
            }

            var segment = text.AsSpan(start, end - start);
            if (segment.IndexOf('~') < 0)
            {
                tokens.Add(segment.ToString());
            }
            else
            {
                var builder = new StringBuilder(segment.Length);
                for (var i = 0; i < segment.Length; i++)
                {
                    var c = segment[i];
                    if (c != '~')
                    {
                        builder.Append(c);
                        continue;
                    }

                    var next = i + 1 < segment.Length ? segment[i + 1] : '\0';
                    if (next is not ('0' or '1'))
                    {
                        result = null;
                        error = $"Invalid JSON pointer '{text}': '~' at offset {start + i} must be followed by '0' or '1' " +
                                "(RFC 6901 section 3); write '~0' for a literal '~' and '~1' for a literal '/'.";
                        return false;
                    }

                    builder.Append(next == '0' ? '~' : '/');
                    i++;
                }

                tokens.Add(builder.ToString());
            }

            if (end == text.Length)
            {
                break;
            }

            start = end + 1;
        }

        result = new JsonPointer(tokens.ToImmutable(), text);
        error = null;
        return true;
    }

    /// <summary>RFC 6901 section 4 array index: <c>0</c> or a non-zero digit followed by digits.</summary>
    internal static bool TryParseArrayIndex(string token, out int index)
    {
        index = 0;
        if (token.Length == 0 || (token.Length > 1 && token[0] == '0'))
        {
            return false;
        }

        foreach (var c in token)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    private static int Compare(JsonPointer? left, JsonPointer? right) =>
        left is null ? (right is null ? 0 : -1) : left.CompareTo(right);

    private static string Format(ImmutableArray<string> tokens)
    {
        var builder = new StringBuilder();
        foreach (var token in tokens)
        {
            builder.Append('/');
            AppendEscaped(builder, token);
        }

        return builder.ToString();
    }

    private static void AppendEscaped(StringBuilder builder, string token)
    {
        foreach (var c in token)
        {
            switch (c)
            {
                case '~':
                    builder.Append("~0");
                    break;
                case '/':
                    builder.Append("~1");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }
    }
}
