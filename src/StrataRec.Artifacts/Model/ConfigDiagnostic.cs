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
/// <remarks>
/// The artifact and pointer are the location every producer knows. A tool that has the artifact's file turns them into a
/// line and column (<see cref="Json.JsoncLocator"/>) when it prints the diagnostic; <see cref="Offset"/> and
/// <see cref="Line"/> refine that where the producer knows more than the pointer.
/// </remarks>
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

    /// <summary>
    /// 0-based UTF-16 offset of the part at fault inside the string at <see cref="Pointer"/> (an expression or a template),
    /// or null when the problem is the value as a whole.
    /// </summary>
    public int? Offset { get; init; }

    /// <summary>
    /// 1-based line in the artifact's text when the producer read the text itself and the problem has no pointer (a JSONC
    /// syntax error); null otherwise.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>1-based column (UTF-16 code units) that goes with <see cref="Line"/>.</summary>
    public int? Column { get; init; }

    /// <summary><c>error</c>, <c>warning</c> or <c>info</c>, as tools print it.</summary>
    public string SeverityName => Severity switch
    {
        DiagnosticSeverity.Error => "error",
        DiagnosticSeverity.Warning => "warning",
        _ => "info",
    };

    public override string ToString()
    {
        var where = Line is { } line ? $" (line {line}, column {Column ?? 1})" : string.Empty;
        var hint = Hint is null ? string.Empty : $"\n    hint: {Hint}";
        return $"{SeverityName} {Code} {Artifact}{Pointer}: {Message}{where}{hint}";
    }
}

/// <summary>Collects diagnostics during a compile or validation pass.</summary>
public sealed class DiagnosticBag
{
    private readonly List<ConfigDiagnostic> _items = new();

    public IReadOnlyList<ConfigDiagnostic> Items => _items;

    public bool HasErrors => _items.Exists(static d => d.IsError);

    public int ErrorCount
    {
        get
        {
            var count = 0;
            foreach (var d in _items)
            {
                if (d.IsError)
                {
                    count++;
                }
            }

            return count;
        }
    }

    public int Count => _items.Count;

    public void Add(ConfigDiagnostic diagnostic) => _items.Add(diagnostic);

    /// <summary>
    /// Removes and returns the diagnostics recorded since <paramref name="start"/> (a previous <see cref="Count"/>), so a
    /// caller can re-attribute them, e.g. to the experiment arm whose patch caused them.
    /// </summary>
    public List<ConfigDiagnostic> RemoveFrom(int start)
    {
        var taken = _items.GetRange(start, _items.Count - start);
        _items.RemoveRange(start, _items.Count - start);
        return taken;
    }

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
            throw new ConfigException(what, _items.FindAll(static d => d.IsError));
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

/// <summary>"Did you mean" hints for configuration diagnostics.</summary>
public static class NameSuggestions
{
    /// <summary>Returns the candidate closest to <paramref name="name"/> by edit distance, if close enough.</summary>
    public static string? Closest(string name, IEnumerable<string> candidates) => Json.NameSuggestions.FindClosest(name, candidates);

    /// <summary>Formats a hint such as <c>did you mean 'item.clicks_7d'?</c>, or returns null.</summary>
    public static string? DidYouMean(string name, IEnumerable<string> candidates)
    {
        var c = Closest(name, candidates);
        return c is null ? null : $"did you mean '{c}'?";
    }
}
