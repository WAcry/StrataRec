using System.Globalization;
using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.Cli.Commands;

/// <summary>Diagnostic and value rendering shared by commands.</summary>
internal static class Output
{
    /// <summary>Prints diagnostics (errors first) and returns the exit code: 1 when there are errors.</summary>
    public static int Diagnostics(IReadOnlyList<ConfigDiagnostic> diagnostics, string format, TextWriter writer, bool warningsAsErrors = false)
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
                    WriteDiagnostic(json, d);
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
                writer.WriteLine(d.ToString());
            }

            if (sorted.Count > 0)
            {
                writer.WriteLine();
            }

            writer.WriteLine($"{errors} error(s), {warnings} warning(s)");
        }

        return errors > 0 || (warningsAsErrors && warnings > 0) ? 1 : 0;
    }

    public static void WriteDiagnostic(Utf8JsonWriter json, ConfigDiagnostic d)
    {
        json.WriteStartObject();
        json.WriteString("severity", d.Severity.ToString().ToLowerInvariant());
        json.WriteString("code", d.Code);
        json.WriteString("artifact", d.Artifact);
        json.WriteString("pointer", d.Pointer);
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
                    json.WriteNumberValue(f);
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
