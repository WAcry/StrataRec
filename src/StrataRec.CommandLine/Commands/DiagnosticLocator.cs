using System.Text;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Hosting.Workspace;

namespace StrataRec.CommandLine.Commands;

internal readonly record struct SourceLocation(string File, int Line, int Column, bool Exact, string? Source = null);

/// <remarks>
/// A command that compiles two configurations (<c>diff</c>) labels each side's diagnostics with a prefix
/// (<c>--from pipeline:x</c>); each prefix maps to its own workspace, so a label never resolves in the other one.
/// </remarks>
internal sealed class DiagnosticLocator
{
    private readonly List<(string Prefix, string Directory)> _workspaces = new();
    private readonly HashSet<string> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]?> _texts = new(StringComparer.Ordinal);

    public static DiagnosticLocator ForWorkspace(string directory) => new DiagnosticLocator().Add(string.Empty, directory);

    public DiagnosticLocator Add(string prefix, string directory)
    {
        _workspaces.Add((prefix, directory));
        return this;
    }

    public DiagnosticLocator AddFile(string path)
    {
        _files.Add(path);
        return this;
    }

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
        var text = Text(path);
        if (diagnostic.Line is { } line)
        {
            var source = text is not null && diagnostic.Column is not null ? LineOf(text, line) : null;
            return new SourceLocation(file, line, diagnostic.Column ?? 1, exact, source);
        }

        var at = text is not null ? JsoncLocator.Locate(text, diagnostic.Pointer, diagnostic.Offset) : null;
        return at is { } found
            ? new SourceLocation(file, found.Line, found.Column, exact, LineOf(text!, found.Line))
            : new SourceLocation(file, 1, 1, exact);
    }

    /// <summary>Line <paramref name="line"/> (1-based, lines end at '\n' as <see cref="JsoncLocator"/> counts them) without its line break; null past the end.</summary>
    internal static string? LineOf(ReadOnlySpan<byte> text, int line)
    {
        var start = 0;
        for (var i = 1; i < line; i++)
        {
            var next = text[start..].IndexOf((byte)'\n');
            if (next < 0)
            {
                return null;
            }

            start += next + 1;
        }

        var rest = text[start..];
        var end = rest.IndexOf((byte)'\n');
        var bytes = end < 0 ? rest : rest[..end];
        return Encoding.UTF8.GetString(bytes.Length > 0 && bytes[^1] == (byte)'\r' ? bytes[..^1] : bytes);
    }

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
                text = null;
            }

            _texts[path] = text;
        }

        return text;
    }
}
