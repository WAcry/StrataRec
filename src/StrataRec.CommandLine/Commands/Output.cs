using System.Buffers;
using System.Globalization;
using System.Text;
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
            using (var json = new Utf8JsonWriter(stream, CommonOptions.JsonWriterOptions))
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
    /// combination); otherwise it is <see cref="ConfigDiagnostic.ToString"/>. Under the source line and its caret, the
    /// message is the <see cref="ConfigDiagnostic.Brief"/> one, which does not quote the expression the line shows.
    /// </summary>
    public static string Text(ConfigDiagnostic d, SourceLocation? location)
    {
        if (location is not { } at)
        {
            return d.ToString();
        }

        var where = at.Exact ? d.Pointer : d.Artifact + d.Pointer;
        var source = at.Source is { } line ? "\n" + Snippet(line, at.Column) : string.Empty;
        var message = at.Source is not null && d.Brief is { } brief ? brief : d.Message;
        var hint = d.Hint is null ? string.Empty : $"\n    hint: {d.Hint}";
        return $"{at.File}:{at.Line}:{at.Column}: {d.SeverityName} {d.Code}{(where.Length > 0 ? " " + where : string.Empty)}: {message}{source}{hint}";
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
        // By code point (review 34): a mark, a format character or a variation selector takes no cell, whatever block it is
        // in; then a wide character two (a full-width space) and any other one.
        for (var i = start; i < at;)
        {
            if (Rune.DecodeFromUtf16(line.AsSpan(i, at - i), out var rune, out var used) != OperationStatus.Done)
            {
                if (char.IsHighSurrogate(line[i]) && i + 1 == at)
                {
                    break; // the column is the pair's low surrogate: the caret goes under the character
                }

                caret.Append(' '); // an unpaired surrogate, drawn as one replacement character
                i++;
                continue;
            }

            i += used;
            if (rune.Value == '\t')
            {
                caret.Append('\t');
            }
            else if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
            {
                // no cell
            }
            else
            {
                caret.Append(IsWide(rune.Value) ? '\u3000' : ' ');
            }
        }

        caret.Append('^');
        return $"    {before}{line[start..end]}{after}\n    {caret}";
    }

    /// <summary>
    /// East Asian wide and full-width characters, which terminals draw in two cells: the CJK, Hangul and full-width blocks,
    /// the emoji blocks and the supplementary ideographic planes. Other characters, those of other supplementary blocks
    /// included, take one.
    /// </summary>
    private static bool IsWide(int c) => c is (>= 0x1100 and <= 0x115F) or (>= 0x2E80 and <= 0x303E) or (>= 0x3041 and <= 0x33FF)
        or (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF) or (>= 0xA000 and <= 0xA4CF) or (>= 0xAC00 and <= 0xD7A3)
        or (>= 0xF900 and <= 0xFAFF) or (>= 0xFE30 and <= 0xFE4F) or (>= 0xFF00 and <= 0xFF60) or (>= 0xFFE0 and <= 0xFFE6)
        or (>= 0x1F300 and <= 0x1F64F) or (>= 0x1F680 and <= 0x1F6FF) or (>= 0x1F900 and <= 0x1F9FF) or (>= 0x1FA70 and <= 0x1FAFF)
        or (>= 0x20000 and <= 0x3FFFD);

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

    /// <summary>
    /// Writes a value as JSON, exactly: missing is null, a float in its shortest round-trip form (a float32 value as the
    /// float, <see cref="ScalarValue.IsFloat32"/>), NaN and ±∞, which JSON numbers cannot be, as strings.
    /// </summary>
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
                if (!double.IsFinite(d))
                {
                    json.WriteStringValue(Logging.LogBatchJson.NonFiniteName(d));
                }
                else if (value.IsFloat32)
                {
                    json.WriteNumberValue((float)d);
                }
                else
                {
                    json.WriteNumberValue(d);
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
            case StorageKind.List:
                // Each element as a value of its element type is (list<int32> and list<float32> are held promoted).
                json.WriteStartArray();
                for (var i = 0; i < value.ListLength; i++)
                {
                    WriteValue(json, value.ListElement switch
                    {
                        StorageKind.Bool => ScalarValue.FromBool(value.AsBoolList()[i]),
                        StorageKind.Int64 => ScalarValue.FromInt64(value.AsInt64List()[i]),
                        StorageKind.Float64 when value.IsFloat32List => ScalarValue.FromFloat32((float)value.AsFloat64List()[i]),
                        StorageKind.Float64 => ScalarValue.FromFloat64(value.AsFloat64List()[i]),
                        _ => ScalarValue.FromString(value.AsStringList()[i]),
                    });
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

    /// <summary>
    /// Writes a file the command line names (<c>build --manifest</c>, <c>run --record</c>), creating its directory as
    /// <c>init</c> and <c>schema --out</c> do. What cannot be written is OUTPUT_UNWRITABLE at <paramref name="option"/>, with
    /// what the system said, not an exception and its stack (review 43). False when it was not written.
    /// </summary>
    public static bool WriteFile(string option, string path, Action<string> write, DiagnosticBag diagnostics)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            write(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            diagnostics.Error("OUTPUT_UNWRITABLE", option, string.Empty, $"Cannot write '{path}': {ex.Message}",
                $"check that this process may write there, or pass another {option}");
            return false;
        }
    }

    /// <summary>
    /// A store that cannot be written (build, publish and revoke write it; a node only reads it): STORE_UNWRITABLE at
    /// <c>--store</c> with what the system said, which names the path, not an exception and its stack (review 43).
    /// </summary>
    public static void StoreUnwritable(Exception ex, DiagnosticBag diagnostics) =>
        diagnostics.Error("STORE_UNWRITABLE", "--store", string.Empty, ex.Message,
            "check that this process may write the store, and that its blobs/ and pointers/ and the directories in them are directories");

    public static string ToJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, CommonOptions.JsonWriterOptions))
        {
            write(json);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
