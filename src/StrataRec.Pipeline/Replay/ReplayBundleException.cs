namespace StrataRec.Pipeline.Replay;

/// <summary>A replay bundle that cannot be used: where it is wrong (a JSON pointer into the bundle) and how to fix it.</summary>
public sealed class ReplayBundleException : Exception
{
    public ReplayBundleException(string pointer, string message, string? hint = null)
        : base(pointer.Length == 0 ? message : $"{pointer}: {message}")
    {
        Pointer = pointer;
        Reason = message;
        Hint = hint;
    }

    /// <summary>RFC 6901 pointer into the bundle.</summary>
    public string Pointer { get; }

    public string Reason { get; }

    public string? Hint { get; }
}
