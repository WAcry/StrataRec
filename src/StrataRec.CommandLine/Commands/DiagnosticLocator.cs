using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Hosting.Workspace;

namespace StrataRec.CommandLine.Commands;

/// <summary>Where a diagnostic is in a file, as a terminal or editor opens it.</summary>
/// <param name="File">The file, as reachable from the current directory: the workspace directory as given, then its workspace-relative path.</param>
/// <param name="Line">1-based line.</param>
/// <param name="Column">1-based column (UTF-16 code units).</param>
/// <param name="Exact">False when the diagnostic's artifact label says more than the file (an arm combination, a side of a diff), so it is printed too.</param>
internal readonly record struct SourceLocation(string File, int Line, int Column, bool Exact);

/// <summary>
/// Turns a diagnostic's artifact and pointer into <c>file:line:column</c> when its artifact is a workspace file
/// (<see cref="ConfigWorkspace.FileOf"/>, <see cref="JsoncLocator"/>), when the CLI prints it. Producers keep reporting the
/// location they know (artifact, pointer, an offset inside an expression); only the printer reads the files, once each.
/// </summary>
/// <remarks>
/// A command that compiles two configurations (<c>diff</c>) labels each side's diagnostics with a prefix
/// (<c>--from pipeline:x</c>); each prefix maps to its own workspace, so a label never resolves in the other one.
/// </remarks>
internal sealed class DiagnosticLocator
{
    private readonly List<(string Prefix, string Directory)> _workspaces = new();
    private readonly HashSet<string> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]?> _texts = new(StringComparer.Ordinal);

    /// <summary>Locates the diagnostics of the one workspace at <paramref name="directory"/>.</summary>
    public static DiagnosticLocator ForWorkspace(string directory) => new DiagnosticLocator().Add(string.Empty, directory);

    /// <summary>Locates diagnostics whose artifact starts with <paramref name="prefix"/> and a space in the workspace at <paramref name="directory"/>.</summary>
    public DiagnosticLocator Add(string prefix, string directory)
    {
        _workspaces.Add((prefix, directory));
        return this;
    }

    /// <summary>Locates diagnostics whose artifact is <paramref name="path"/>, a file outside any workspace (a request file).</summary>
    public DiagnosticLocator AddFile(string path)
    {
        _files.Add(path);
        return this;
    }

    /// <summary>The diagnostic's file, line and column, or null when its artifact is not a file of a known workspace.</summary>
    public SourceLocation? Locate(ConfigDiagnostic diagnostic)
    {
        if (_files.Contains(diagnostic.Artifact))
        {
            return At(diagnostic.Artifact, diagnostic.Artifact, diagnostic, exact: true);
        }

        foreach (var (prefix, directory) in _workspaces)
        {
            string label;
            if (prefix.Length == 0)
            {
                label = diagnostic.Artifact;
            }
            else if (diagnostic.Artifact.Length > prefix.Length && diagnostic.Artifact.StartsWith(prefix + " ", StringComparison.Ordinal))
            {
                label = diagnostic.Artifact[(prefix.Length + 1)..];
            }
            else
            {
                continue;
            }

            if (ConfigWorkspace.FileOf(directory, label, out var exact) is not { } relative)
            {
                return null;
            }

            return At(Display(directory, relative), Path.Combine(directory, relative), diagnostic, exact && prefix.Length == 0);
        }

        return null;
    }

    private SourceLocation At(string file, string path, ConfigDiagnostic diagnostic, bool exact)
    {
        if (diagnostic.Line is { } line)
        {
            return new SourceLocation(file, line, diagnostic.Column ?? 1, exact);
        }

        var at = Text(path) is { } text ? JsoncLocator.Locate(text, diagnostic.Pointer, diagnostic.Offset) : null;
        return at is { } found ? new SourceLocation(file, found.Line, found.Column, exact) : new SourceLocation(file, 1, 1, exact);
    }

    /// <summary>The path as a terminal opens it from the current directory, with '/' separators.</summary>
    private static string Display(string directory, string relative)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(directory);
        return trimmed is "." or "" ? relative : (trimmed + "/" + relative).Replace('\\', '/');
    }

    private byte[]? Text(string path)
    {
        if (!_texts.TryGetValue(path, out var text))
        {
            try
            {
                text = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                text = null; // the location falls back to the file's first line
            }

            _texts[path] = text;
        }

        return text;
    }
}
