using System.Globalization;
using System.Text;

namespace StrataRec.Expressions.Syntax;

internal enum TokenKind : byte
{
    End,
    Int,
    Float,
    String,
    Ident,
    LParen,
    RParen,
    LBracket,
    RBracket,
    LBrace,
    RBrace,
    Comma,
    Dot,
    Semicolon,
    Question,
    Colon,
    QuestionQuestion,
    OrOr,
    AndAnd,
    EqEq,
    NotEq,
    Lt,
    Le,
    Gt,
    Ge,
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    Bang,
    Assign,
    At,
}

/// <summary>One token. <see cref="IntValue"/> holds the magnitude of integer literals (up to 2^63 so that
/// <c>-9223372036854775808</c> can be written).</summary>
internal readonly record struct Token(TokenKind Kind, int Start, int Length, string? Text = null, ulong IntValue = 0, double FloatValue = 0)
{
    public int End => Start + Length;
}

/// <summary>Thrown inside the front end to abort on the first syntax error; never escapes the compiler.</summary>
internal sealed class SyntaxErrorException(ExpressionDiagnostic diagnostic) : Exception(diagnostic.Message)
{
    public ExpressionDiagnostic Diagnostic { get; } = diagnostic;
}

/// <summary>CEL-compatible tokenizer: literals, identifiers, operators, <c>//</c> comments.</summary>
internal static class Lexer
{
    public static List<Token> Tokenize(string source)
    {
        var tokens = new List<Token>(Math.Min(4096, (source.Length / 3) + 4));
        var i = 0;
        while (true)
        {
            i = SkipTrivia(source, i);
            if (i >= source.Length)
            {
                tokens.Add(new Token(TokenKind.End, source.Length, 0));
                return tokens;
            }

            var c = source[i];
            if (IsDigit(c) || (c == '.' && i + 1 < source.Length && IsDigit(source[i + 1])))
            {
                tokens.Add(LexNumber(source, ref i));
                continue;
            }

            if (IsIdentStart(c))
            {
                // r'..' raw strings; b'..' bytes are not supported.
                if ((c is 'r' or 'R' or 'b' or 'B') && i + 1 < source.Length && source[i + 1] is '\'' or '"')
                {
                    if (c is 'b' or 'B')
                    {
                        throw Error(i, 1, "Byte string literals are not supported.", "remove the 'b' prefix");
                    }

                    tokens.Add(LexString(source, ref i, i + 1, raw: true));
                    continue;
                }

                var start = i;
                while (i < source.Length && IsIdentPart(source[i]))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Ident, start, i - start, source.Substring(start, i - start)));
                continue;
            }

            if (c is '\'' or '"')
            {
                tokens.Add(LexString(source, ref i, i, raw: false));
                continue;
            }

            tokens.Add(LexPunctuation(source, ref i));
        }
    }

    private static int SkipTrivia(string s, int i)
    {
        while (i < s.Length)
        {
            var c = s[i];
            if (c is ' ' or '\t' or '\r' or '\n' or '\f' or '\v')
            {
                i++;
            }
            else if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n')
                {
                    i++;
                }
            }
            else
            {
                break;
            }
        }

        return i;
    }

    private static Token LexPunctuation(string s, ref int i)
    {
        var start = i;
        var c = s[i];
        var next = i + 1 < s.Length ? s[i + 1] : '\0';
        (TokenKind kind, int length) = c switch
        {
            '(' => (TokenKind.LParen, 1),
            ')' => (TokenKind.RParen, 1),
            '[' => (TokenKind.LBracket, 1),
            ']' => (TokenKind.RBracket, 1),
            '{' => (TokenKind.LBrace, 1),
            '}' => (TokenKind.RBrace, 1),
            ',' => (TokenKind.Comma, 1),
            '.' => (TokenKind.Dot, 1),
            ';' => (TokenKind.Semicolon, 1),
            ':' => (TokenKind.Colon, 1),
            '@' => (TokenKind.At, 1),
            '+' => (TokenKind.Plus, 1),
            '-' => (TokenKind.Minus, 1),
            '*' => (TokenKind.Star, 1),
            '/' => (TokenKind.Slash, 1),
            '%' => (TokenKind.Percent, 1),
            '?' => next == '?' ? (TokenKind.QuestionQuestion, 2) : (TokenKind.Question, 1),
            '|' when next == '|' => (TokenKind.OrOr, 2),
            '&' when next == '&' => (TokenKind.AndAnd, 2),
            '=' => next == '=' ? (TokenKind.EqEq, 2) : (TokenKind.Assign, 1),
            '!' => next == '=' ? (TokenKind.NotEq, 2) : (TokenKind.Bang, 1),
            '<' => next == '=' ? (TokenKind.Le, 2) : (TokenKind.Lt, 1),
            '>' => next == '=' ? (TokenKind.Ge, 2) : (TokenKind.Gt, 1),
            _ => (TokenKind.End, 0),
        };

        if (length == 0)
        {
            var hint = c switch
            {
                '|' => "use '||' for logical or",
                '&' => "use '&&' for logical and",
                _ => null,
            };
            throw Error(start, 1, $"Unexpected character '{Printable(c)}'.", hint);
        }

        i += length;
        return new Token(kind, start, length);
    }

    private static Token LexNumber(string s, ref int i)
    {
        var start = i;
        if (s[i] == '0' && i + 1 < s.Length && s[i + 1] is 'x' or 'X')
        {
            i += 2;
            var digitsStart = i;
            while (i < s.Length && IsHexDigit(s[i]))
            {
                i++;
            }

            if (i == digitsStart)
            {
                throw Error(start, i - start, "Hexadecimal literal has no digits.");
            }

            if (!ulong.TryParse(s.AsSpan(digitsStart, i - digitsStart), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex)
                || hex > (ulong)long.MaxValue + 1)
            {
                throw Error(start, i - start, "Integer literal is out of the int64 range.");
            }

            CheckNumberEnd(s, start, ref i);
            return new Token(TokenKind.Int, start, i - start, null, hex);
        }

        var isFloat = false;
        while (i < s.Length && IsDigit(s[i]))
        {
            i++;
        }

        if (i < s.Length && s[i] == '.' && i + 1 < s.Length && IsDigit(s[i + 1]))
        {
            isFloat = true;
            i++;
            while (i < s.Length && IsDigit(s[i]))
            {
                i++;
            }
        }

        if (i < s.Length && s[i] is 'e' or 'E')
        {
            var j = i + 1;
            if (j < s.Length && s[j] is '+' or '-')
            {
                j++;
            }

            if (j < s.Length && IsDigit(s[j]))
            {
                isFloat = true;
                i = j;
                while (i < s.Length && IsDigit(s[i]))
                {
                    i++;
                }
            }
        }

        var text = s.AsSpan(start, i - start);
        if (isFloat)
        {
            CheckNumberEnd(s, start, ref i);
            var value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (double.IsInfinity(value))
            {
                throw Error(start, i - start, "Floating-point literal is out of the float64 range.");
            }

            return new Token(TokenKind.Float, start, i - start, null, 0, value);
        }

        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var magnitude) || magnitude > (ulong)long.MaxValue + 1)
        {
            throw Error(start, i - start, "Integer literal is out of the int64 range.", "use a float literal such as 1e19 for large magnitudes");
        }

        CheckNumberEnd(s, start, ref i);
        return new Token(TokenKind.Int, start, i - start, null, magnitude);
    }

    private static void CheckNumberEnd(string s, int start, ref int i)
    {
        if (i < s.Length && s[i] is 'u' or 'U')
        {
            throw Error(start, i - start + 1, "Unsigned integer literals are not supported.", "drop the 'u' suffix; integers are int64");
        }

        if (i < s.Length && IsIdentPart(s[i]))
        {
            throw Error(start, i - start + 1, $"Invalid number literal '{s.Substring(start, i - start + 1)}'.");
        }
    }

    private static Token LexString(string s, ref int i, int quoteIndex, bool raw)
    {
        var start = i;
        var quote = s[quoteIndex];
        var triple = quoteIndex + 2 < s.Length && s[quoteIndex + 1] == quote && s[quoteIndex + 2] == quote;
        i = quoteIndex + (triple ? 3 : 1);
        var sb = new StringBuilder();
        while (true)
        {
            if (i >= s.Length)
            {
                throw Error(start, s.Length - start, "Unterminated string literal.", $"close the string with {(triple ? new string(quote, 3) : quote.ToString())}");
            }

            var c = s[i];
            if (c == quote && (!triple || (i + 2 < s.Length && s[i + 1] == quote && s[i + 2] == quote)))
            {
                i += triple ? 3 : 1;
                return new Token(TokenKind.String, start, i - start, sb.ToString());
            }

            if (!triple && c is '\n' or '\r')
            {
                throw Error(start, i - start, "Unterminated string literal (newline inside a single-line string).", "use a triple-quoted string for multi-line text");
            }

            if (c == '\\' && !raw)
            {
                ReadEscape(s, ref i, sb);
                continue;
            }

            sb.Append(c);
            i++;
        }
    }

    private static void ReadEscape(string s, ref int i, StringBuilder sb)
    {
        var start = i;
        if (i + 1 >= s.Length)
        {
            throw Error(start, 1, "Incomplete escape sequence at the end of the expression.", "escape a backslash as '\\\\'");
        }

        var e = s[i + 1];
        i += 2;
        switch (e)
        {
            case '\\': sb.Append('\\'); return;
            case '?': sb.Append('?'); return;
            case '"': sb.Append('"'); return;
            case '\'': sb.Append('\''); return;
            case '`': sb.Append('`'); return;
            case 'a': sb.Append('\a'); return;
            case 'b': sb.Append('\b'); return;
            case 'f': sb.Append('\f'); return;
            case 'n': sb.Append('\n'); return;
            case 'r': sb.Append('\r'); return;
            case 't': sb.Append('\t'); return;
            case 'v': sb.Append('\v'); return;
            case 'x' or 'X': AppendCodePoint(s, ref i, 2, 16, start, sb, "write exactly 2 hex digits, e.g. '\\x41'"); return;
            case 'u': AppendCodePoint(s, ref i, 4, 16, start, sb, "write exactly 4 hex digits, e.g. '\\u00e9'"); return;
            case 'U': AppendCodePoint(s, ref i, 8, 16, start, sb, "write exactly 8 hex digits, e.g. '\\U0001F600'"); return;
            case >= '0' and <= '3':
                i--;
                AppendCodePoint(s, ref i, 3, 8, start, sb, "write exactly 3 octal digits, from '\\000' to '\\377'");
                return;
            default:
                throw Error(start, 2, $"Invalid escape sequence '\\{Printable(e)}'.", "escape a backslash as '\\\\'");
        }
    }

    /// <summary>
    /// Reads a fixed-width numeric escape (hex <c>x</c>, <c>u</c>, <c>U</c> or octal) and appends its code point, which
    /// must be a Unicode scalar value: at most U+10FFFF and not a surrogate. Eight hex digits reach 0xFFFFFFFF, so the
    /// value is accumulated in 64 bits. Every rejection is a syntax diagnostic with a hint, never a runtime exception.
    /// </summary>
    private static void AppendCodePoint(string s, ref int i, int digits, int radix, int start, StringBuilder sb, string hint)
    {
        if (i + digits > s.Length)
        {
            throw Error(start, s.Length - start, "Incomplete escape sequence.", hint);
        }

        long value = 0;
        for (var k = 0; k < digits; k++)
        {
            var d = DigitValue(s[i + k]);
            if (d < 0 || d >= radix)
            {
                throw Error(start, i + k - start + 1, "Invalid digit in escape sequence.", hint);
            }

            value = (value * radix) + d;
        }

        i += digits;
        if (value > 0x10FFFF || (value is >= 0xD800 and <= 0xDFFF))
        {
            throw Error(start, i - start, $"Escape sequence '{s.Substring(start, i - start)}' is not a Unicode scalar value.",
                "use a code point from U+0000 to U+10FFFF outside the surrogates U+D800-U+DFFF; write a character above "
                + "U+FFFF as one escape such as '\\U0001F600', not as a surrogate pair");
        }

        sb.Append(char.ConvertFromUtf32((int)value));
    }

    private static int DigitValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    private static bool IsHexDigit(char c) => DigitValue(c) >= 0;

    internal static bool IsIdentStart(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_';

    internal static bool IsIdentPart(char c) => IsIdentStart(c) || IsDigit(c);

    private static string Printable(char c) => char.IsControl(c) ? $"\\u{(int)c:X4}" : c.ToString();

    internal static SyntaxErrorException Error(int offset, int length, string message, string? hint = null) =>
        new(new ExpressionDiagnostic(ExpressionErrorCodes.Syntax, message, offset, Math.Max(1, length), hint));
}
