using StrataRec.Artifacts.Model;
using StrataRec.Expressions;

namespace StrataRec.Pipeline.Planning;

internal static class ExpressionErrors
{
    public static ConfigDiagnostic ToDiagnostic(ExpressionDiagnostic error, string artifact, string pointer, string source) =>
        new(DiagnosticSeverity.Error, error.Code, artifact, pointer, $"{error.Message} (at offset {error.Offset} in '{source}')", error.Hint)
        {
            Offset = error.Offset,
            Brief = error.Message,
        };

    public static void Report(DiagnosticBag diagnostics, IReadOnlyList<ExpressionDiagnostic> errors, string artifact, string pointer, string source)
    {
        foreach (var error in errors)
        {
            diagnostics.Add(ToDiagnostic(error, artifact, pointer, source));
        }
    }
}
