using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace StrataRec.Artifacts.Json;

/// <summary>
/// <see href="https://www.rfc-editor.org/rfc/rfc7396">RFC 7396</see> JSON Merge Patch, the format of every
/// experiment patch (design 11.3).
/// </summary>
/// <remarks>
/// <para>
/// This is the plain RFC algorithm (section 2): object members merge recursively, <c>null</c> deletes a member,
/// and any non-object patch value (including arrays) replaces the target wholesale. It is deliberately lenient, as
/// the RFC is: deleting a missing member is a no-op and patching a missing member creates it. StrataRec's stricter
/// checks on top of it live in <see cref="MergePatchAnalyzer"/>.
/// </para>
/// <para>
/// Member names are compared exactly (ordinal), regardless of <see cref="JsonNodeOptions"/> on the inputs. The
/// result keeps the target's member order and appends new members in patch order.
/// </para>
/// </remarks>
public static class JsonMergePatch
{
    /// <summary>
    /// Applies <paramref name="patch"/> to <paramref name="target"/> and returns the result as a new tree. Neither
    /// input is modified, and the result shares no nodes with them, so it can be mutated or attached elsewhere.
    /// </summary>
    /// <param name="target">The document to patch; <see langword="null"/> is JSON <c>null</c>.</param>
    /// <param name="patch">The merge patch; <see langword="null"/> (JSON <c>null</c>) yields <see langword="null"/>.</param>
    public static JsonNode? Apply(JsonNode? target, JsonNode? patch)
    {
        if (patch is not JsonObject patchObject)
        {
            return JsonNodeUtilities.IsJsonNull(patch) ? null : patch!.DeepClone();
        }

        RuntimeHelpers.EnsureSufficientExecutionStack();
        var patchMembers = new Dictionary<string, JsonNode?>(patchObject.Count, StringComparer.Ordinal);
        foreach (var member in patchObject)
        {
            patchMembers[member.Key] = member.Value;
        }

        var result = new JsonObject();
        HashSet<string>? targetNames = null;
        if (target is JsonObject targetObject)
        {
            targetNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in targetObject)
            {
                targetNames.Add(member.Key);
                if (!patchMembers.TryGetValue(member.Key, out var patchValue))
                {
                    result[member.Key] = member.Value?.DeepClone();
                }
                else if (!JsonNodeUtilities.IsJsonNull(patchValue))
                {
                    result[member.Key] = Apply(member.Value, patchValue);
                }

                // else: null deletes the member, so it is not copied.
            }
        }

        foreach (var member in patchObject)
        {
            if (targetNames?.Contains(member.Key) == true || JsonNodeUtilities.IsJsonNull(member.Value))
            {
                continue;
            }

            // A target that is not an object is replaced by {} first (RFC 7396 section 2), so new members are
            // merged into nothing; nested nulls in them vanish.
            result[member.Key] = Apply(null, member.Value);
        }

        return result;
    }
}
