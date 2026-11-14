using System.Globalization;
using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.CommandLine.Commands;

/// <summary>Diagnostic and value rendering shared by commands.</summary>
internal static class Output
{
    /// <summary>Prints diagnostics (errors first) and returns the exit code: 1 when there are errors.</summary>
    /// <param name="locator">Where the configuration's files are, so each diagnostic in one is printed with its file, line and
    /// column (<c>file:line:column:</c> in text, <c>file</c>, <c>line</c>, <c>column</c> in JSON); null prints artifacts and
    /// pointers only, as for a manifest read from a store.</param>
    public static int Diagnostics(
        IReadOnlyList<ConfigDiagnostic> diagnostics, string format, TextWriter writer, bool warningsAsErrors = false, DiagnosticLocator? locator = null)
    {
        var sorted = new List<ConfigDiagnostic>(diagnostics);
        sorted.Sort(static (a, b) => a.Severity != b.Severity ? a.Severity.CompareTo(b.Severity) : 0);
        var errors = 0;
        var warnings = 0;
        foreach (var d in sorted)
        {
            errors += d.IsError ? 1 : 0;
            warnings += d.Severity == DiagnosticSeverity.Warning ? 1 : 0;
        }

        if (format == "json")
        {
            using var stream = new MemoryStream();
            using (var json = new Utf8JsonWriter(stream, CommonOptions.JsonOptions))
            {
                json.WriteStartObject();
                json.WriteNumber("errors", errors);
                json.WriteNumber("warnings", warnings);
                json.WriteStartArray("diagnostics");
                foreach (var d in sorted)
                {
                    WriteDiagnostic(json, d, locator?.Locate(d));
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            writer.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        }
        else
        {
            foreach (var d in sorted)
            {
                writer.WriteLine(Text(d, locator?.Locate(d)));
            }

            if (sorted.Count > 0)
            {
                writer.WriteLine();
            }

            writer.WriteLine($"{Count(errors, "error")}, {Count(warnings, "warning")}");
        }

        return errors > 0 || (warningsAsErrors && warnings > 0) ? 1 : 0;
    }

    /// <summary><paramref name="count"/> with its noun, singular for one: "1 experiment", "2 experiments", "0 errors".</summary>
    public static string Count(int count, string noun, string? plural = null) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? noun : plural ?? noun + "s");

    /// <summary>
    /// One diagnostic as text. Located, it starts with <c>file:line:column:</c>, which terminals and editors open at the
    /// place, followed by the pointer (and the artifact label when it says more than the file, such as the arms of a
    /// combination); otherwise it is <see cref="ConfigDiagnostic.ToString"/>.
    /// </summary>
    public static string Text(ConfigDiagnostic d, SourceLocation? location)
    {
        if (location is not { } at)
        {
            return d.ToString();
        }

        var where = at.Exact ? d.Pointer : d.Artifact + d.Pointer;
        var source = at.Source is { } line ? "\n" + Snippet(line, at.Column) : string.Empty;
        var hint = d.Hint is null ? string.Empty : $"\n    hint: {d.Hint}";
        return $"{at.File}:{at.Line}:{at.Column}: {d.SeverityName} {d.Code}{(where.Length > 0 ? " " + where : string.Empty)}: {d.Message}{source}{hint}";
    }

    /// <summary>Lines longer than this are cut to this many characters around the column.</summary>
    private const int SnippetWidth = 100;

    /// <summary>
    /// The source line of a located diagnostic and a caret under its column, so a reader in a terminal sees the place
    /// without counting columns (an expression's culprit is often far into a long string). The indentation is dropped and a
    /// long line is cut to a window around the column, marked with "...". The caret line copies tabs, puts nothing under a
    /// zero-width character and a full-width space under a wide one (CJK, an emoji), so the caret stays under the column
    /// where a terminal draws those in two cells.
    /// </summary>
    internal static string Snippet(string line, int column)
    {
        var at = Math.Clamp(column - 1, 0, line.Length);
        var start = 0;
        while (start < at && line[start] is ' ' or '\t')
        {
            start++;
        }

        var end = line.Length;
        while (end > at && char.IsWhiteSpace(line[end - 1]))
        {
            end--;
        }

        var before = string.Empty;
        var after = string.Empty;
        if (end - start > SnippetWidth)
        {
            var to = Math.Min(end, Math.Max(start, at - SnippetWidth / 2) + SnippetWidth);
            var from = Math.Max(start, to - SnippetWidth); // near the end, still a full window
            before = from > start ? "..." : string.Empty;
            after = to < end ? "..." : string.Empty;
            (start, end) = (from, to);
            if (start < at && char.IsLowSurrogate(line[start]))
            {
                start++; // never half a pair at the edges
            }

            if (end > at + 1 && char.IsHighSurrogate(line[end - 1]))
            {
                end--;
            }
        }

        var caret = new System.Text.StringBuilder(before.Length + at - start + 1);
        caret.Append(' ', before.Length);
        for (var i = start; i < at; i++)
        {
            var c = line[i];
            if (c == '\t')
            {
                caret.Append('\t');
            }
            else if (char.IsHighSurrogate(c) || IsWide(c))
            {
                caret.Append('\u3000'); // a pair is one character: its low surrogate adds nothing
            }
            else if (!char.IsLowSurrogate(c) && CharUnicodeInfo.GetUnicodeCategory(c) is not
                     (UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format))
            {
                caret.Append(' ');
            }
        }

        caret.Append('^');
        return $"    {before}{line[start..end]}{after}\n    {caret}";
    }

    /// <summary>East Asian wide and full-width characters of the BMP, which terminals draw in two cells.</summary>
    private static bool IsWide(char c) => c is (>= '\u1100' and <= '\u115F') or (>= '\u2E80' and <= '\u303E') or (>= '\u3041' and <= '\u33FF')
        or (>= '\u3400' and <= '\u4DBF') or (>= '\u4E00' and <= '\u9FFF') or (>= '\uA000' and <= '\uA4CF') or (>= '\uAC00' and <= '\uD7A3')
        or (>= '\uF900' and <= '\uFAFF') or (>= '\uFE30' and <= '\uFE4F') or (>= '\uFF00' and <= '\uFF60') or (>= '\uFFE0' and <= '\uFFE6');

    public static void WriteDiagnostic(Utf8JsonWriter json, ConfigDiagnostic d, SourceLocation? location = null)
    {
        json.WriteStartObject();
        json.WriteString("severity", d.SeverityName);
        json.WriteString("code", d.Code);
        json.WriteString("artifact", d.Artifact);
        json.WriteString("pointer", d.Pointer);
        if (location is { } at)
        {
            json.WriteString("file", at.File);
            json.WriteNumber("line", at.Line);
            json.WriteNumber("column", at.Column);
        }

        json.WriteString("message", d.Message);
        if (d.Hint is not null)
        {
            json.WriteString("hint", d.Hint);
        }

        json.WriteEndObject();
    }

    /// <summary>Writes a value as JSON; missing becomes null.</summary>
    public static void WriteValue(Utf8JsonWriter json, in ScalarValue value)
    {
        switch (value.Kind)
        {
            case 0:
                json.WriteNullValue();
                break;
            case StorageKind.Bool:
                json.WriteBooleanValue(value.AsBool());
                break;
            case StorageKind.Int32 or StorageKind.Int64:
                json.WriteNumberValue(value.AsInt64());
                break;
            case StorageKind.Float32 or StorageKind.Float64:
                var d = value.AsFloat64();
                if (double.IsFinite(d))
                {
                    json.WriteNumberValue(Math.Round(d, 6));
                }
                else
                {
                    json.WriteStringValue(d.ToString(CultureInfo.InvariantCulture));
                }

                break;
            case StorageKind.String or StorageKind.Text:
                json.WriteStringValue(value.AsString());
                break;
            case StorageKind.Vector:
                json.WriteStartArray();
                foreach (var f in value.AsVector())
                {
                    if (float.IsFinite(f))
                    {
                        json.WriteNumberValue(f);
                    }
                    else
                    {
                        json.WriteStringValue(Logging.LogBatchJson.NonFiniteName(f)); // JSON numbers cannot be NaN or ±∞
                    }
                }

                json.WriteEndArray();
                break;
            case StorageKind.ListInt64: // also list<int32> values: ScalarValue holds them promoted
                json.WriteStartArray();
                foreach (var l in value.AsInt64List())
                {
                    json.WriteNumberValue(l);
                }

                json.WriteEndArray();
                break;
            case StorageKind.ListString:
                json.WriteStartArray();
                foreach (var s in value.AsStringList())
                {
                    json.WriteStringValue(s);
                }

                json.WriteEndArray();
                break;
            default:
                json.WriteStringValue(value.ToString());
                break;
        }
    }

    /// <summary>
    /// A JSON value on one line in its RFC 8785 form: members in ordinal order, so a workspace and the manifest built from
    /// it (whose store keeps canonical bytes) print alike, and nothing escaped that a terminal shows as is (quotes in
    /// expressions, non-ASCII).
    /// </summary>
    public static string Compact(System.Text.Json.Nodes.JsonNode? value) => Artifacts.Json.JsonCanonicalizer.CanonicalizeToString(value);

    public static string ToJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, CommonOptions.JsonOptions))
        {
            write(json);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
