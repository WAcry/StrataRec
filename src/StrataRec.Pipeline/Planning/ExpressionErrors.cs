using StrataRec.Artifacts.Model;
using StrataRec.Expressions;

namespace StrataRec.Pipeline.Planning;

/// <summary>
/// The configuration diagnostics of an expression that does not compile: located at the config string that holds it,
/// with the offset of the part at fault both as data (<see cref="ConfigDiagnostic.Offset"/>, which tools turn into a
/// column inside the string) and in the message. Every place that compiles an expression from configuration reports
/// through this, so they all say it alike.
/// </summary>
internal static class ExpressionErrors
{
    public static ConfigDiagnostic ToDiagnostic(ExpressionDiagnostic error, string artifact, string pointer, string source) =>
        new(DiagnosticSeverity.Error, error.Code, artifact, pointer, $"{error.Message} (at offset {error.Offset} in '{source}')", error.Hint)
        {
            Offset = error.Offset,
        };

    public static void Report(DiagnosticBag diagnostics, IReadOnlyList<ExpressionDiagnostic> errors, string artifact, string pointer, string source)
    {
        foreach (var error in errors)
        {
            diagnostics.Add(ToDiagnostic(error, artifact, pointer, source));
        }
    }
}
