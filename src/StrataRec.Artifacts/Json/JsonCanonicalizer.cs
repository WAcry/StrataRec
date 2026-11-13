using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StrataRec.Artifacts.Json;

/// <summary>
/// <see href="https://www.rfc-editor.org/rfc/rfc8785">RFC 8785</see> JSON Canonicalization Scheme (JCS): a unique
/// byte representation of a JSON value, so that the same content always hashes to the same artifact id regardless
/// of key order, whitespace, comments or number spelling (design 9.1 item 3).
/// </summary>
/// <remarks>
/// <para>The output follows RFC 8785 section 3.2:</para>
/// <list type="bullet">
///   <item>No whitespace (section 3.2.1).</item>
///   <item>
///     Literals <c>null</c>, <c>true</c>, <c>false</c> as is (section 3.2.2.1).
///   </item>
///   <item>
///     Strings use only the mandatory escapes: <c>\"</c>, <c>\\</c>, <c>\b</c>, <c>\t</c>, <c>\n</c>, <c>\f</c>,
///     <c>\r</c>, and <c>\u00xx</c> with lowercase hex for the remaining controls below U+0020; everything else,
///     including <c>/</c>, U+007F and non-ASCII, is written literally (section 3.2.2.2).
///   </item>
///   <item>
///     Numbers are IEEE 754 doubles serialized with the ECMAScript <c>Number.prototype.toString</c> algorithm,
///     see <see cref="FormatNumber"/> (section 3.2.2.3).
///   </item>
///   <item>Object members are sorted by name, comparing UTF-16 code units (section 3.2.3).</item>
///   <item>The result is encoded as UTF-8 (section 3.2.4).</item>
/// </list>
/// <para>
/// RFC 8785 assumes I-JSON input (RFC 7493). Values outside it are rejected with a
/// <see cref="JsonCanonicalizationException"/> naming the offending pointer instead of being silently altered:
/// NaN and infinities, numbers that overflow a double or underflow to zero, and strings with unpaired surrogates.
/// Integer literals (no fraction or exponent) beyond ±2^53 are rejected unless their canonical form denotes the
/// same integer: artifacts are stored canonicalized, and a reader binding <see cref="long"/> must get back what the
/// author wrote, not <c>1152921504606847000</c> for 2^60. Numbers written with a fraction or exponent are rounded
/// to the nearest double as the RFC specifies, so <c>0.1</c>, <c>0.10</c> and <c>1e-1</c> canonicalize identically.
/// </para>
/// <para>
/// Leaf values may be backed by any CLR type <see cref="JsonValue"/> supports; numbers are read from their JSON
/// text, so a <see cref="float"/> <c>0.1f</c> canonicalizes as <c>0.1</c>, not as its widened double value.
/// </para>
/// </remarks>
public static class JsonCanonicalizer
{
    private const double TwoPow53 = 9007199254740992.0;
    private const string LowerHex = "0123456789abcdef";

    /// <summary>Returns the canonical UTF-8 bytes of <paramref name="node"/> (<see langword="null"/> is JSON <c>null</c>).</summary>
    /// <exception cref="JsonCanonicalizationException">The tree contains a value outside I-JSON.</exception>
    public static byte[] Canonicalize(JsonNode? node) => JsonNodeUtilities.StrictUtf8.GetBytes(CanonicalizeToString(node));

    /// <summary>Returns the canonical form of <paramref name="node"/> as a string (before UTF-8 encoding).</summary>
    /// <exception cref="JsonCanonicalizationException">The tree contains a value outside I-JSON.</exception>
    public static string CanonicalizeToString(JsonNode? node)
    {
        var writer = new Writer();
        writer.WriteNode(node);
        return writer.ToString();
    }

    /// <summary>
    /// Serializes a double exactly like ECMAScript <c>Number.prototype.toString</c> (ECMA-262 Number::toString,
    /// required by RFC 8785 section 3.2.2.3): the shortest digit string that round-trips, in plain notation for
    /// magnitudes in [1e-6, 1e21) and exponent notation (<c>1e+21</c>, <c>1e-7</c>) otherwise. <c>-0</c> becomes
    /// <c>0</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is NaN or infinite.</exception>
    public static string FormatNumber(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), value, "NaN and Infinity have no JSON representation (RFC 8785 section 3.2.2.3).");
        }

        if (value == 0)
        {
            return "0";
        }

        // .NET Core 3.0+ "R" yields the shortest round-trip digits, e.g. "1.5E-07" or "123.456".
        var shortest = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var exponentIndex = shortest.IndexOf('E', StringComparison.Ordinal);
        var mantissa = exponentIndex < 0 ? shortest.AsSpan() : shortest.AsSpan(0, exponentIndex);
        var exponent = exponentIndex < 0
            ? 0
            : int.Parse(shortest.AsSpan(exponentIndex + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        Span<char> buffer = stackalloc char[mantissa.Length];
        var count = 0;
        foreach (var c in mantissa)
        {
            if (c != '.')
            {
                buffer[count++] = c;
            }
        }

        // ECMA-262 terms: |value| = s × 10^(n−k) with s a k-digit integer. n is the position of the decimal point
        // relative to the first significant digit.
        var point = mantissa.IndexOf('.');
        var n = (point < 0 ? mantissa.Length : point) + exponent;
        var start = 0;
        while (start < count - 1 && buffer[start] == '0')
        {
            start++;
            n--;
        }

        var end = count;
        while (end > start + 1 && buffer[end - 1] == '0')
        {
            end--;
        }

        ReadOnlySpan<char> s = buffer[start..end];
        var k = s.Length;

        var builder = new StringBuilder(32);
        if (value < 0)
        {
            builder.Append('-');
        }

        if (k <= n && n <= 21)
        {
            builder.Append(s).Append('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            builder.Append(s[..n]).Append('.').Append(s[n..]);
        }
        else if (-6 < n && n <= 0)
        {
            builder.Append("0.").Append('0', -n).Append(s);
        }
        else
        {
            var e = n - 1;
            builder.Append(s[0]);
            if (k > 1)
            {
                builder.Append('.').Append(s[1..]);
            }

            builder.Append('e').Append(e < 0 ? '-' : '+').Append(Math.Abs(e).ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private sealed class Writer
    {
        private readonly StringBuilder _text = new();

        // Path from the root to the value being written; Key is null for array elements. Only read on error.
        private readonly List<(string? Key, int Index)> _path = new();

        public override string ToString() => _text.ToString();

        public void WriteNode(JsonNode? node)
        {
            switch (node)
            {
                case null:
                    _text.Append("null");
                    break;
                case JsonObject obj:
                    WriteObject(obj);
                    break;
                case JsonArray array:
                    WriteArray(array);
                    break;
                case JsonValue value:
                    WriteValue(value);
                    break;
                default:
                    throw Error($"Unsupported node type {node.GetType().Name}.");
            }
        }

        private void WriteObject(JsonObject obj)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            var members = new KeyValuePair<string, JsonNode?>[obj.Count];
            var count = 0;
            foreach (var member in obj)
            {
                members[count++] = member;
            }

            Array.Sort(members, static (x, y) => string.CompareOrdinal(x.Key, y.Key));

            _text.Append('{');
            _path.Add((null, 0));
            for (var i = 0; i < members.Length; i++)
            {
                if (i > 0)
                {
                    if (string.Equals(members[i - 1].Key, members[i].Key, StringComparison.Ordinal))
                    {
                        _path[^1] = (members[i].Key, 0);
                        throw Error("The object contains the same member name twice.");
                    }

                    _text.Append(',');
                }

                _path[^1] = (members[i].Key, 0);
                WriteString(members[i].Key);
                _text.Append(':');
                WriteNode(members[i].Value);
            }

            _path.RemoveAt(_path.Count - 1);
            _text.Append('}');
        }

        private void WriteArray(JsonArray array)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            _text.Append('[');
            _path.Add((null, 0));
            for (var i = 0; i < array.Count; i++)
            {
                if (i > 0)
                {
                    _text.Append(',');
                }

                _path[^1] = (null, i);
                WriteNode(array[i]);
            }

            _path.RemoveAt(_path.Count - 1);
            _text.Append(']');
        }

        private void WriteValue(JsonValue value)
        {
            // GetValueKind reports Number for a NaN double, so non-finite CLR values must be caught first. Only values
            // that actually hold a CLR double/float can be non-finite: a parsed (JsonElement-backed) number is JSON
            // text, and TryGetValue<float> would *convert* it — 1e100 becomes float +∞ although it is a finite binary64
            // value. Its range is checked by ParseNumber against binary64, which is what RFC 8785 uses.
            if (!value.TryGetValue(out JsonElement _) &&
                ((value.TryGetValue(out double d) && !double.IsFinite(d)) ||
                 (value.TryGetValue(out float f) && !float.IsFinite(f))))
            {
                throw Error(
                    "NaN and Infinity are not JSON numbers (RFC 8785 section 3.2.2.3).",
                    "use a finite number or an explicit string such as \"none\"");
            }

            var kind = value.GetValueKind();
            switch (kind)
            {
                case JsonValueKind.String:
                    WriteString(ReadString(value));
                    break;
                case JsonValueKind.Number:
                    _text.Append(CanonicalNumber(ReadJsonText(value)));
                    break;
                case JsonValueKind.True:
                    _text.Append("true");
                    break;
                case JsonValueKind.False:
                    _text.Append("false");
                    break;
                case JsonValueKind.Null:
                    _text.Append("null");
                    break;
                case JsonValueKind.Object or JsonValueKind.Array:
                    // A JsonValue wrapping a CLR object or a JsonElement container: canonicalize its JSON form.
                    WriteNode(JsonNode.Parse(ReadJsonText(value)));
                    break;
                default:
                    throw Error($"Unsupported JSON value kind {kind}.");
            }
        }

        private string ReadString(JsonValue value)
        {
            try
            {
                if (value.TryGetValue(out string? text) && text is not null)
                {
                    return text;
                }

                // CLR types that serialize as JSON strings (char, Guid, DateTime, ...): use their JSON form.
                using var document = JsonDocument.Parse(ReadJsonText(value));
                return document.RootElement.GetString()!;
            }
            catch (InvalidOperationException)
            {
                throw UnpairedSurrogate();
            }
        }

        private string ReadJsonText(JsonValue value)
        {
            if (value.TryGetValue(out JsonElement element))
            {
                return element.GetRawText();
            }

            try
            {
                return value.ToJsonString();
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
            {
                throw Error($"The value cannot be written as JSON: {ex.Message}");
            }
        }

        private string CanonicalNumber(string literal)
        {
            var number = ParseNumber(literal);
            var canonical = FormatNumber(number);

            // Artifacts are stored in canonical form, so a reader binding an integer type must get back the integer
            // the author wrote. Up to 2^53 that always holds; beyond it the shortest round-trip digits may denote a
            // different integer (2^60 is written 1152921504606847000), or the literal may not be a double at all.
            // Compare with >=: 2^53 + 1 rounds down to exactly 2^53.
            if (Math.Abs(number) >= TwoPow53 &&
                literal.AsSpan().IndexOfAny('.', 'e', 'E') < 0 &&
                BigInteger.Parse(literal, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) != IntegerValueOf(canonical))
            {
                throw Error(
                    $"The integer {literal} cannot be represented exactly in RFC 8785 form; its canonical form would be " +
                    $"{canonical}, a different integer.",
                    "write integers beyond ±2^53 (9007199254740992) as strings");
            }

            return canonical;
        }

        /// <summary>Exact integer denoted by a canonical number that has no fractional part ("123", "-1.5e+22").</summary>
        private static BigInteger IntegerValueOf(string canonical)
        {
            var text = canonical.AsSpan();
            var negative = text[0] == '-';
            if (negative)
            {
                text = text[1..];
            }

            BigInteger value;
            var e = text.IndexOf('e');
            if (e < 0)
            {
                value = BigInteger.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
            }
            else
            {
                var mantissa = text[..e];
                var exponent = int.Parse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                var point = mantissa.IndexOf('.');
                var fractionDigits = point < 0 ? 0 : mantissa.Length - point - 1;
                var digits = point < 0 ? mantissa.ToString() : string.Concat(mantissa[..point], mantissa[(point + 1)..]);

                // Exponent notation only occurs from 1e21 up, where the exponent exceeds the fraction digits.
                value = BigInteger.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture) *
                        BigInteger.Pow(10, Math.Max(0, exponent - fractionDigits));
            }

            return negative ? -value : value;
        }

        private double ParseNumber(string literal)
        {
            if (!double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                throw Error($"'{literal}' is not a JSON number.");
            }

            if (!double.IsFinite(number))
            {
                throw Error(
                    $"The number {literal} is outside the IEEE 754 double-precision range that RFC 8785 section 3.2.2.3 requires.",
                    "use a smaller magnitude, or write the value as a string");
            }

            if (number == 0 && HasNonZeroDigitBeforeExponent(literal))
            {
                throw Error(
                    $"The number {literal} is too small for IEEE 754 double precision and would silently become 0.",
                    "use 0 or a larger magnitude, or write the value as a string");
            }

            return number;
        }

        private static bool HasNonZeroDigitBeforeExponent(string literal)
        {
            foreach (var c in literal)
            {
                if (c is 'e' or 'E')
                {
                    return false;
                }

                if (c is >= '1' and <= '9')
                {
                    return true;
                }
            }

            return false;
        }

        private void WriteString(string value)
        {
            _text.Append('"');
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                switch (c)
                {
                    case '"':
                        _text.Append("\\\"");
                        break;
                    case '\\':
                        _text.Append("\\\\");
                        break;
                    case '\b':
                        _text.Append("\\b");
                        break;
                    case '\t':
                        _text.Append("\\t");
                        break;
                    case '\n':
                        _text.Append("\\n");
                        break;
                    case '\f':
                        _text.Append("\\f");
                        break;
                    case '\r':
                        _text.Append("\\r");
                        break;
                    default:
                        if (c < 0x20)
                        {
                            _text.Append("\\u00").Append(LowerHex[c >> 4]).Append(LowerHex[c & 0xF]);
                        }
                        else if (char.IsHighSurrogate(c))
                        {
                            if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                            {
                                throw UnpairedSurrogate();
                            }

                            _text.Append(c).Append(value[i + 1]);
                            i++;
                        }
                        else if (char.IsLowSurrogate(c))
                        {
                            throw UnpairedSurrogate();
                        }
                        else
                        {
                            _text.Append(c);
                        }

                        break;
                }
            }

            _text.Append('"');
        }

        private JsonCanonicalizationException UnpairedSurrogate() => Error(
            "A string contains an unpaired UTF-16 surrogate, which has no UTF-8 encoding (RFC 8785 section 3.2.4).",
            "use a complete surrogate pair or remove the stray code unit");

        private JsonCanonicalizationException Error(string message, string? hint = null)
        {
            var tokens = new string[_path.Count];
            for (var i = 0; i < tokens.Length; i++)
            {
                var (key, index) = _path[i];
                tokens[i] = key ?? index.ToString(CultureInfo.InvariantCulture);
            }

            return new JsonCanonicalizationException(message, JsonPointer.Create(tokens), hint);
        }
    }
}
