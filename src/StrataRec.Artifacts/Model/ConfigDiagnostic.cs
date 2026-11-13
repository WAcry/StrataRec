namespace StrataRec.Artifacts.Model;

public enum DiagnosticSeverity : byte
{
    Error = 1,
    Warning,
    Info,
}

/// <summary>
/// A configuration problem located by artifact and RFC 6901 pointer, with a stable code and a fix hint.
/// Every validation layer (schema, catalog, pipeline, experiments, policy) reports through this type so that
/// humans, CI and AI agents get uniform, actionable output.
/// </summary>
/// <param name="Severity">Errors block a plan; warnings require review; info is advisory.</param>
/// <param name="Code">Stable machine-readable code, e.g. <c>COLUMN_NO_PRODUCER</c>.</param>
/// <param name="Artifact">Artifact or arm the problem is in, e.g. <c>pipeline:home_feed</c> or <c>exp_mmr/mmr</c>.</param>
/// <param name="Pointer">RFC 6901 pointer into the artifact (or patchable view), e.g. <c>/nodes/fusion/config/base_expr</c>.</param>
/// <param name="Message">What is wrong.</param>
/// <param name="Hint">How to fix it, when known.</param>
public sealed record ConfigDiagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Artifact,
    string Pointer,
    string Message,
    string? Hint = null)
{
    public bool IsError => Severity == DiagnosticSeverity.Error;

    public override string ToString()
    {
        var sev = Severity switch
        {
            DiagnosticSeverity.Error => "error",
            DiagnosticSeverity.Warning => "warning",
            _ => "info",
        };
        var hint = Hint is null ? string.Empty : $"\n    hint: {Hint}";
        return $"{sev} {Code} {Artifact}{Pointer}: {Message}{hint}";
    }
}

/// <summary>Collects diagnostics during a compile or validation pass.</summary>
public sealed class DiagnosticBag
{
    private readonly List<ConfigDiagnostic> _items = new();

    public IReadOnlyList<ConfigDiagnostic> Items => _items;

    public bool HasErrors => _items.Exists(d => d.IsError);

    public int ErrorCount => _items.Count(d => d.IsError);

    public void Add(ConfigDiagnostic diagnostic) => _items.Add(diagnostic);

    public void AddRange(IEnumerable<ConfigDiagnostic> diagnostics) => _items.AddRange(diagnostics);

    public void Error(string code, string artifact, string pointer, string message, string? hint = null) =>
        _items.Add(new ConfigDiagnostic(DiagnosticSeverity.Error, code, artifact, pointer, message, hint));

    public void Warning(string code, string artifact, string pointer, string message, string? hint = null) =>
        _items.Add(new ConfigDiagnostic(DiagnosticSeverity.Warning, code, artifact, pointer, message, hint));

    public void Info(string code, string artifact, string pointer, string message, string? hint = null) =>
        _items.Add(new ConfigDiagnostic(DiagnosticSeverity.Info, code, artifact, pointer, message, hint));

    /// <summary>Throws <see cref="ConfigException"/> when any error was recorded.</summary>
    public void ThrowIfErrors(string what)
    {
        if (HasErrors)
        {
            throw new ConfigException(what, _items.Where(d => d.IsError).ToArray());
        }
    }
}

/// <summary>Raised when configuration fails to compile; carries every error found.</summary>
public sealed class ConfigException : Exception
{
    public ConfigException(string what, IReadOnlyList<ConfigDiagnostic> diagnostics)
        : base($"{what} failed with {diagnostics.Count} error(s):\n" + string.Join("\n", diagnostics))
    {
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; }
}

/// <summary>Small string helpers for "did you mean" hints.</summary>
public static class NameSuggestions
{
    /// <summary>Returns the candidate closest to <paramref name="name"/> by edit distance, if close enough.</summary>
    public static string? Closest(string name, IEnumerable<string> candidates)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var c in candidates)
        {
            var d = Distance(name, c);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = c;
            }
        }

        return best is not null && bestDistance <= Math.Max(2, name.Length / 3) ? best : null;
    }

    public static string? DidYouMean(string name, IEnumerable<string> candidates)
    {
        var c = Closest(name, candidates);
        return c is null ? null : $"did you mean '{c}'?";
    }

    private static int Distance(string a, string b)
    {
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
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }
}
