using StrataRec.Expressions.Syntax;

namespace StrataRec.Expressions.Binding;

/// <summary>The fixed function whitelist (design 7.6, <see cref="ExpressionCompiler.FunctionSetCapability"/>).</summary>
internal static class Builtins
{
    /// <summary>Function names with their arity (min, max; -1 = variadic) and a usage string for hints.</summary>
    private static readonly Dictionary<string, (int Min, int Max, string Usage)> Functions = new(StringComparer.Ordinal)
    {
        ["min"] = (1, -1, "min(a, b, ...)"),
        ["max"] = (1, -1, "max(a, b, ...)"),
        ["clamp"] = (3, 3, "clamp(x, lo, hi)"),
        ["abs"] = (1, 1, "abs(x)"),
        ["sqrt"] = (1, 1, "sqrt(x)"),
        ["pow"] = (2, 2, "pow(x, y)"),
        ["exp"] = (1, 1, "exp(x)"),
        ["log"] = (1, 1, "log(x)"),
        ["log1p"] = (1, 1, "log1p(x)"),
        ["sigmoid"] = (1, 1, "sigmoid(x)"),
        ["logit"] = (1, 1, "logit(p)"),
        ["soft_floor"] = (2, 3, "soft_floor(x, floor[, k])"),
        ["linear_decay"] = (3, 3, "linear_decay(x, start, end)"),
        ["piecewise_linear"] = (3, 3, "piecewise_linear(x, [x0, x1, ...], [y0, y1, ...])"),
        ["dot"] = (2, 2, "dot(a, b)"),
        ["cosine"] = (2, 2, "cosine(a, b)"),
        ["l2_norm"] = (1, 1, "l2_norm(v)"),
        ["size"] = (1, 1, "size(list_or_text)"),
        ["contains"] = (2, 2, "contains(list, x) or contains(text, s)"),
        ["overlap_count"] = (2, 2, "overlap_count(list1, list2)"),
        ["startsWith"] = (2, 2, "startsWith(s, prefix)"),
        ["hash01"] = (1, -1, "hash01(a, b, ...)"),
        ["lookup"] = (3, 3, "lookup('table', key, default)"),
        ["has"] = (1, 1, "has(x)"),
    };

    /// <summary>Words that cannot name a let binding.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "col", "ctx", "param", "true", "false", "null", "in", "let",
        "as", "break", "const", "continue", "else", "for", "function", "if", "import", "loop", "package",
        "namespace", "return", "var", "void", "while",
    };

    public static IEnumerable<string> FunctionNames => Functions.Keys;

    public static bool IsFunction(string name) => Functions.ContainsKey(name);

    public static bool TryGetArity(string name, out int min, out int max, out string usage)
    {
        if (Functions.TryGetValue(name, out var f))
        {
            (min, max, usage) = f;
            return true;
        }

        (min, max, usage) = (0, 0, string.Empty);
        return false;
    }

    public static bool IsReserved(string name) => Reserved.Contains(name);

    public static bool IsNamespace(string name) => name is "col" or "ctx" or "param";

    public static string TokenSpelling(TokenKind kind) => kind switch
    {
        TokenKind.LParen => "(",
        TokenKind.RParen => ")",
        TokenKind.LBracket => "[",
        TokenKind.RBracket => "]",
        TokenKind.LBrace => "{",
        TokenKind.RBrace => "}",
        TokenKind.Comma => ",",
        TokenKind.Dot => ".",
        TokenKind.Semicolon => ";",
        TokenKind.Question => "?",
        TokenKind.Colon => ":",
        TokenKind.QuestionQuestion => "??",
        TokenKind.OrOr => "||",
        TokenKind.AndAnd => "&&",
        TokenKind.EqEq => "==",
        TokenKind.NotEq => "!=",
        TokenKind.Lt => "<",
        TokenKind.Le => "<=",
        TokenKind.Gt => ">",
        TokenKind.Ge => ">=",
        TokenKind.Plus => "+",
        TokenKind.Minus => "-",
        TokenKind.Star => "*",
        TokenKind.Slash => "/",
        TokenKind.Percent => "%",
        TokenKind.Bang => "!",
        TokenKind.Assign => "=",
        TokenKind.At => "@",
        TokenKind.End => "end of expression",
        _ => kind.ToString(),
    };
}

/// <summary>"Did you mean" suggestions by optimal-string-alignment edit distance.</summary>
internal static class NameSuggester
{
    public static string? Hint(string query, IEnumerable<string> candidates)
    {
        var best = Suggest(query, candidates, 3);
        return best.Count switch
        {
            0 => null,
            1 => $"did you mean '{best[0]}'?",
            _ => "did you mean one of " + string.Join(", ", Quote(best)) + "?",
        };
    }

    public static List<string> Suggest(string query, IEnumerable<string> candidates, int max)
    {
        var strippedQuery = StripNamespace(query);
        var threshold = Math.Clamp(query.Length / 3, 1, 4);
        var scored = new List<(int Distance, string Name)>();
        foreach (var candidate in candidates)
        {
            if (candidate == query)
            {
                continue;
            }

            int distance;
            if (candidate.EndsWith("." + query, StringComparison.Ordinal))
            {
                // The namespace prefix was forgotten, e.g. 'item.clicks_7d' for 'col.item.clicks_7d'.
                distance = 0;
            }
            else
            {
                distance = Math.Min(
                    Distance(query, candidate, threshold),
                    Distance(strippedQuery, StripNamespace(candidate), threshold));
            }

            if (distance <= threshold)
            {
                scored.Add((distance, candidate));
            }
        }

        scored.Sort(static (a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : string.CompareOrdinal(a.Name, b.Name));
        var result = new List<string>(Math.Min(max, scored.Count));
        for (var i = 0; i < scored.Count && i < max; i++)
        {
            result.Add(scored[i].Name);
        }

        return result;
    }

    private static List<string> Quote(List<string> names)
    {
        var quoted = new List<string>(names.Count);
        foreach (var n in names)
        {
            quoted.Add("'" + n + "'");
        }

        return quoted;
    }

    private static string StripNamespace(string name)
    {
        foreach (var prefix in new[] { "col.", "ctx.", "param." })
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return name[prefix.Length..];
            }
        }

        return name;
    }

    /// <summary>Optimal string alignment distance; returns a value above <paramref name="limit"/> early when the
    /// lengths alone rule a match out.</summary>
    internal static int Distance(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit)
        {
            return limit + 1;
        }

        var prev2 = new int[b.Length + 1];
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var d = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d = Math.Min(d, prev2[j - 2] + 1);
                }

                cur[j] = d;
            }

            (prev2, prev, cur) = (prev, cur, prev2);
        }

        return prev[b.Length];
    }
}
