using System.Text.Json;
using System.Text.Json.Nodes;

namespace StrataRec.Artifacts.Model;

/// <summary>
/// Members an earlier format accepted and this one does not, with why and what to do instead. Unknown members are
/// rejected anyway (<see cref="ArtifactSerializer"/>); this replaces that generic error with one that says what changed,
/// so a file or manifest written for the earlier format fails explicitly, at the member, with a fix.
/// </summary>
/// <remarks>
/// Consulted only once deserialization has failed, so a valid document costs nothing. Add a member here when a format
/// drops one that existing files are likely to still carry.
/// </remarks>
internal static class RetiredMembers
{
    private const string Allocation =
        "traffic allocation (ratios, ramping, salts, holdouts) is the experiment platform's, and a request's arms are its answer " +
        "(RecRequest.Assignment, or an IAssignmentProvider)";

    private const string ReleaseFix = "each experiment of a release only names its layer: \"experiments\": { \"<id>\": { \"layer\": \"<layer>\" } }";

    private const string ManifestFix = "rebuild the manifest with 'stratarec build' from a release file without assignment or arm ranges";

    private static readonly Member[] Members =
    {
        new(typeof(ReleaseArtifact), new[] { "assignment" }, "A release no longer assigns traffic: " + Allocation + ".", "remove \"assignment\"; " + ReleaseFix),
        new(typeof(ReleaseArtifact), new[] { "experiments", "*", "arms" }, "A release no longer has arm bucket ranges: " + Allocation + ".", "remove \"arms\"; " + ReleaseFix),
        new(typeof(ReleaseManifest), new[] { "assignment" }, "A manifest no longer assigns traffic: " + Allocation + ".", ManifestFix),
        new(typeof(ReleaseManifest), new[] { "experiments", "*", "arms" }, "A manifest no longer has arm bucket ranges: " + Allocation + ".", ManifestFix),
    };

    /// <summary>The error for the first retired member <paramref name="document"/> has, or null when it has none.</summary>
    public static ArtifactFormatException? Find(Type type, JsonNode? document, string? source)
    {
        foreach (var member in Members)
        {
            if (member.Type == type && Locate(document, member.Path, 0, string.Empty) is { } pointer)
            {
                return new ArtifactFormatException(source, pointer, member.Reason, hint: member.Hint);
            }
        }

        return null;
    }

    /// <inheritdoc cref="Find(Type, JsonNode?, string?)"/>
    public static ArtifactFormatException? Find(Type type, string json, string? source)
    {
        var applies = false;
        foreach (var member in Members)
        {
            applies |= member.Type == type;
        }

        if (!applies)
        {
            return null;
        }

        try
        {
            return Find(type, JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }), source);
        }
        catch (JsonException)
        {
            return null; // not JSON: the serializer's own error says where
        }
    }

    /// <summary>The RFC 6901 pointer of the member at <paramref name="path"/> (<c>*</c> matches any name), or null.</summary>
    private static string? Locate(JsonNode? node, string[] path, int depth, string at)
    {
        if (depth == path.Length)
        {
            return at;
        }

        if (node is not JsonObject obj)
        {
            return null;
        }

        if (path[depth] != "*")
        {
            return obj.TryGetPropertyValue(path[depth], out var child) ? Locate(child, path, depth + 1, at + "/" + Json.JsonPointer.Escape(path[depth])) : null;
        }

        foreach (var (name, child) in obj)
        {
            if (Locate(child, path, depth + 1, at + "/" + Json.JsonPointer.Escape(name)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private sealed record Member(Type Type, string[] Path, string Reason, string Hint);
}
