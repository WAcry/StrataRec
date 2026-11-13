using System.Text.Json.Nodes;

namespace StrataRec.Artifacts.Json;

/// <summary>How a merge patch touches one footprint path (design 11.5).</summary>
public enum PatchOpKind
{
    /// <summary>
    /// The key does not exist in the base document; the whole patch value becomes a new entry. The analyzer does
    /// not recurse into it: callers validate it as a complete object against the collection's schema (design 11.3).
    /// </summary>
    Add,

    /// <summary>
    /// An existing value is overwritten by a non-object value, or an existing non-object is overwritten by an
    /// object. Either way the whole subtree at the path is replaced.
    /// </summary>
    Replace,

    /// <summary>The patch sets an existing key to <c>null</c>, removing its subtree.</summary>
    Delete,
}

/// <summary>
/// One entry of a patch footprint: an RFC 6901 path that a merge patch adds, replaces or deletes as a whole
/// (design 11.5 item 1). The paths of one footprint never overlap: no path is a prefix of another.
/// </summary>
public sealed class PatchOperation
{
    /// <summary>Creates a footprint entry.</summary>
    /// <param name="path">The path the operation covers.</param>
    /// <param name="kind">What happens at the path.</param>
    /// <param name="value">
    /// For <see cref="PatchOpKind.Add"/> and <see cref="PatchOpKind.Replace"/>, the value the path will hold after
    /// the merge (may be <see langword="null"/> when not tracked); must be <see langword="null"/> for
    /// <see cref="PatchOpKind.Delete"/>.
    /// </param>
    public PatchOperation(JsonPointer path, PatchOpKind kind, JsonNode? value = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown patch operation kind.");
        }

        if (kind == PatchOpKind.Delete && value is not null)
        {
            throw new ArgumentException("A delete operation has no value.", nameof(value));
        }

        Path = path;
        Kind = kind;
        Value = value;
    }

    /// <summary>The path covered by this operation; the whole subtree below it is affected.</summary>
    public JsonPointer Path { get; }

    /// <summary>What the patch does at <see cref="Path"/>.</summary>
    public PatchOpKind Kind { get; }

    /// <summary>
    /// For <see cref="PatchOpKind.Add"/> and <see cref="PatchOpKind.Replace"/> produced by
    /// <see cref="MergePatchAnalyzer"/>: the effective new value, i.e. the patch value with RFC 7396 semantics
    /// applied (members set to <c>null</c> inside a new object are dropped). It is a detached copy owned by the
    /// analysis; treat it as read-only and clone it before attaching it to another tree. <see langword="null"/>
    /// for <see cref="PatchOpKind.Delete"/>.
    /// </summary>
    public JsonNode? Value { get; }

    /// <summary>For example <c>add /nodes/mmr_diversifier</c>.</summary>
    public override string ToString() => Kind switch
    {
        PatchOpKind.Add => $"add {Path}",
        PatchOpKind.Replace => $"replace {Path}",
        _ => $"delete {Path}",
    };
}

/// <summary>A problem found while checking a patch against its base document.</summary>
/// <param name="Code">Stable machine-readable code, e.g. <see cref="MergePatchAnalyzer.DeleteMissingCode"/>.</param>
/// <param name="Message">Human-readable description.</param>
/// <param name="Path">Where in the patch (and the patched document) the problem is.</param>
/// <param name="Hint">Optional fix suggestion, e.g. <c>did you mean '/nodes/diversity_cap'?</c>.</param>
public sealed record PatchDiagnostic(string Code, string Message, JsonPointer Path, string? Hint = null)
{
    public override string ToString() =>
        Hint is null ? $"{Code} at '{Path}': {Message}" : $"{Code} at '{Path}': {Message} ({Hint})";
}

/// <summary>
/// Two footprint entries that overlap: equal paths, or one path is a prefix of the other (design 11.5). Whether an
/// overlap is acceptable (for example identical additions to an add-only collection) is decided by the caller.
/// </summary>
/// <param name="First">The entry from the first footprint.</param>
/// <param name="Second">The entry from the second footprint.</param>
public sealed record FootprintConflict(PatchOperation First, PatchOperation Second)
{
    public override string ToString() => $"{First} overlaps {Second}";
}

/// <summary>The result of <see cref="MergePatchAnalyzer.Analyze"/>.</summary>
public sealed class MergePatchAnalysis
{
    internal MergePatchAnalysis(IReadOnlyList<PatchOperation> footprint, IReadOnlyList<PatchDiagnostic> errors)
    {
        Footprint = footprint;
        Errors = errors;
        var additions = new List<PatchOperation>();
        for (var i = 0; i < footprint.Count; i++)
        {
            if (footprint[i].Kind == PatchOpKind.Add)
            {
                additions.Add(footprint[i]);
            }
        }

        Additions = additions.AsReadOnly();
    }

    /// <summary>All operations, sorted by path (<see cref="JsonPointer.CompareTo"/>), so the order is deterministic.</summary>
    public IReadOnlyList<PatchOperation> Footprint { get; }

    /// <summary>
    /// The <see cref="PatchOpKind.Add"/> entries of <see cref="Footprint"/>, each with its complete new value, to be
    /// validated against the schema of the collection it is added to.
    /// </summary>
    public IReadOnlyList<PatchOperation> Additions { get; }

    /// <summary>Strict-patch violations, sorted by path. Empty when the patch is valid.</summary>
    public IReadOnlyList<PatchDiagnostic> Errors { get; }

    /// <summary>True when <see cref="Errors"/> is empty.</summary>
    public bool IsValid => Errors.Count == 0;
}
