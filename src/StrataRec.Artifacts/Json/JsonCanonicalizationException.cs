namespace StrataRec.Artifacts.Json;

/// <summary>
/// A JSON tree contains a value that has no faithful RFC 8785 canonical form, such as NaN, a number outside the
/// IEEE 754 double range, an integer beyond ±2^53 whose canonical form would denote a different integer, or a
/// string with an unpaired surrogate.
/// </summary>
public sealed class JsonCanonicalizationException : Exception
{
    public JsonCanonicalizationException(string message, JsonPointer pointer, string? hint = null)
        : base(Format(message, pointer, hint))
    {
        ArgumentNullException.ThrowIfNull(pointer);
        Reason = message;
        Pointer = pointer;
        Hint = hint;
    }

    /// <summary>The problem without location information.</summary>
    public string Reason { get; }

    /// <summary>Where the offending value is, relative to the canonicalized node.</summary>
    public JsonPointer Pointer { get; }

    /// <summary>Optional suggestion for fixing the value.</summary>
    public string? Hint { get; }

    private static string Format(string message, JsonPointer? pointer, string? hint)
    {
        var location = pointer is null ? "unknown location" : JsonNodeUtilities.Display(pointer);
        return hint is null
            ? $"Cannot canonicalize the value at {location}: {message}"
            : $"Cannot canonicalize the value at {location}: {message} Hint: {hint}";
    }
}
