using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace StrataRec.Artifacts.Json;

/// <summary>
/// The strict checks StrataRec layers on top of RFC 7396 (design 11.3) and the path footprint of a patch
/// (design 11.5 item 1).
/// </summary>
/// <remarks>
/// <para>
/// Plain RFC 7396 silently ignores a <c>null</c> for a missing key and silently creates partial objects under a
/// missing key, so typos vanish. Given the base document (the patchable view, design 11.4) and a patch, the
/// analyzer classifies every patched path:
/// </para>
/// <list type="bullet">
///   <item><c>null</c> for an existing key is a <see cref="PatchOpKind.Delete"/> of its whole subtree.</item>
///   <item>
///     <c>null</c> for a missing key is an error (<see cref="DeleteMissingCode"/>) with a "did you mean" hint
///     naming the closest existing sibling key.
///   </item>
///   <item>
///     Any value for a missing key is an <see cref="PatchOpKind.Add"/> of a complete new entry; the analyzer does not
///     recurse into it. Callers validate <see cref="MergePatchAnalysis.Additions"/> against the collection's schema
///     as whole objects, which is what rejects a misspelled <c>diversty_cap</c> node lacking <c>op</c> and
///     <c>order</c>. A <c>null</c> nested inside a new value deletes nothing and is reported as
///     <see cref="DeleteMissingCode"/> too.
///   </item>
///   <item>
///     A non-object value for an existing key, or an object for an existing non-object, is a
///     <see cref="PatchOpKind.Replace"/> of the whole subtree.
///   </item>
///   <item>An object for an existing object recurses member by member, down to the leaves.</item>
/// </list>
/// <para>
/// Paths in the result are relative to the base document. Policy checks (which roots are patchable or add-only,
/// owners) and schema validation of additions are the caller's job; this class only knows JSON.
/// </para>
/// </remarks>
public static class MergePatchAnalyzer
{
    /// <summary>The patch sets <c>null</c> on a path that does not exist in the base document.</summary>
    public const string DeleteMissingCode = "PATCH_DELETE_MISSING";

    /// <summary>Computes the footprint of <paramref name="patch"/> against <paramref name="baseDocument"/> and checks it.</summary>
    /// <param name="baseDocument">The document the patch applies to; not modified.</param>
    /// <param name="patch">An RFC 7396 merge patch; not modified. A non-object patch replaces (or, if <c>null</c>, deletes) the root.</param>
    public static MergePatchAnalysis Analyze(JsonNode? baseDocument, JsonNode? patch)
    {
        var analyzer = new Analyzer(baseDocument);
        analyzer.AnalyzeMember(exists: true, baseDocument, patch, JsonPointer.Root);
        return analyzer.Finish();
    }

    /// <summary>
    /// True when two footprints overlap: some path of one equals, or is a prefix of, some path of the other
    /// (design 11.5: both buckets would change the same subtree). <paramref name="conflicts"/> lists every
    /// overlapping pair, sorted by the first and then the second path.
    /// </summary>
    public static bool FootprintsConflict(
        IEnumerable<PatchOperation> first,
        IEnumerable<PatchOperation> second,
        out IReadOnlyList<FootprintConflict> conflicts)
    {
        conflicts = FindConflicts(first, second);
        return conflicts.Count > 0;
    }

    /// <summary>
    /// Lists every pair of overlapping entries between two footprints (equal paths, or one a prefix of the other),
    /// sorted by the first and then the second path. Runs in O((n + m) log m + n·depth) plus the output size.
    /// </summary>
    public static IReadOnlyList<FootprintConflict> FindConflicts(IEnumerable<PatchOperation> first, IEnumerable<PatchOperation> second)
    {
        var a = SortByPath(first, nameof(first));
        var b = SortByPath(second, nameof(second));
        var conflicts = new List<FootprintConflict>();
        var ancestorHits = new List<int>();
        foreach (var operation in a)
        {
            // Entries of b at a proper ancestor of this path. Sorting their indexes lists them in path order,
            // outermost first, since all of them sort before this path.
            ancestorHits.Clear();
            for (var ancestor = operation.Path.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                for (var i = LowerBound(b, ancestor); i < b.Length && b[i].Path.Equals(ancestor); i++)
                {
                    ancestorHits.Add(i);
                }
            }

            ancestorHits.Sort();
            foreach (var i in ancestorHits)
            {
                conflicts.Add(new FootprintConflict(operation, b[i]));
            }

            // Entries of b at this path or below it form a contiguous run in sorted order (JsonPointer.CompareTo).
            for (var i = LowerBound(b, operation.Path); i < b.Length && operation.Path.IsPrefixOf(b[i].Path); i++)
            {
                conflicts.Add(new FootprintConflict(operation, b[i]));
            }
        }

        return conflicts.AsReadOnly();
    }

    /// <summary>Copies and stably sorts operations by path; ties keep their input order.</summary>
    private static PatchOperation[] SortByPath(IEnumerable<PatchOperation> operations, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(operations, parameterName);
        var list = new List<PatchOperation>();
        foreach (var operation in operations)
        {
            if (operation is null)
            {
                throw new ArgumentException("The footprint contains a null entry.", parameterName);
            }

            list.Add(operation);
        }

        var items = list.ToArray();
        var order = new int[items.Length];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (x, y) =>
        {
            var c = items[x].Path.CompareTo(items[y].Path);
            return c != 0 ? c : x.CompareTo(y);
        });

        var sorted = new PatchOperation[items.Length];
        for (var i = 0; i < sorted.Length; i++)
        {
            sorted[i] = items[order[i]];
        }

        return sorted;
    }

    /// <summary>Index of the first entry whose path is not less than <paramref name="target"/>.</summary>
    private static int LowerBound(PatchOperation[] sorted, JsonPointer target)
    {
        int low = 0, high = sorted.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (sorted[middle].Path.CompareTo(target) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private sealed class Analyzer
    {
        private readonly JsonNode? _baseRoot;
        private readonly List<PatchOperation> _operations = new();
        private readonly List<PatchDiagnostic> _errors = new();

        public Analyzer(JsonNode? baseRoot) => _baseRoot = baseRoot;

        public void AnalyzeMember(bool exists, JsonNode? baseValue, JsonNode? patchValue, JsonPointer path)
        {
            if (JsonNodeUtilities.IsJsonNull(patchValue))
            {
                if (exists)
                {
                    _operations.Add(new PatchOperation(path, PatchOpKind.Delete));
                }
                else
                {
                    _errors.Add(DeleteMissing(path));
                }

                return;
            }

            if (exists && patchValue is JsonObject patchObject && baseValue is JsonObject baseObject)
            {
                RuntimeHelpers.EnsureSufficientExecutionStack();
                foreach (var member in patchObject)
                {
                    var memberExists = JsonNodeUtilities.TryGetMember(baseObject, member.Key, out var baseMember);
                    AnalyzeMember(memberExists, baseMember, member.Value, path.Append(member.Key));
                }

                return;
            }

            // Merging into a missing key or a non-object starts from {} (RFC 7396 section 2), so the effective value
            // is the patch value with its nested nulls dropped.
            var kind = exists ? PatchOpKind.Replace : PatchOpKind.Add;
            _operations.Add(new PatchOperation(path, kind, JsonMergePatch.Apply(null, patchValue)));
            ReportNullsInNewValue(patchValue, path);
        }

        public MergePatchAnalysis Finish()
        {
            // Paths are unique within one analysis (patch keys are unique and recursion stops at every entry),
            // so an unstable sort is still deterministic.
            _operations.Sort(static (x, y) => x.Path.CompareTo(y.Path));
            _errors.Sort(static (x, y) => x.Path.CompareTo(y.Path));
            return new MergePatchAnalysis(_operations.AsReadOnly(), _errors.AsReadOnly());
        }

        private void ReportNullsInNewValue(JsonNode? value, JsonPointer path)
        {
            if (value is not JsonObject obj)
            {
                return;
            }

            RuntimeHelpers.EnsureSufficientExecutionStack();
            foreach (var member in obj)
            {
                var memberPath = path.Append(member.Key);
                if (JsonNodeUtilities.IsJsonNull(member.Value))
                {
                    _errors.Add(DeleteMissing(memberPath));
                }
                else
                {
                    ReportNullsInNewValue(member.Value, memberPath);
                }
            }
        }

        private PatchDiagnostic DeleteMissing(JsonPointer path)
        {
            var message =
                $"The patch deletes '{path}', which does not exist in the base document; RFC 7396 would silently ignore it.";

            // Walk the base document to the first token of the path that is missing.
            var current = _baseRoot;
            var parent = JsonPointer.Root;
            var depth = 0;
            for (; depth < path.Depth; depth++)
            {
                if (current is not JsonObject obj || !JsonNodeUtilities.TryGetMember(obj, path.Tokens[depth], out var child))
                {
                    break;
                }

                current = child;
                parent = parent.Append(path.Tokens[depth]);
            }

            if (depth == path.Depth)
            {
                return new PatchDiagnostic(DeleteMissingCode, message, path);
            }

            var token = path.Tokens[depth];
            string hint;
            if (current is JsonObject parentObject)
            {
                var names = new List<string>(parentObject.Count);
                foreach (var member in parentObject)
                {
                    names.Add(member.Key);
                }

                var suggestion = NameSuggestions.FindClosest(token, names);
                hint = suggestion is not null
                    ? $"did you mean '{parent.Append(suggestion)}'?"
                    : $"{JsonNodeUtilities.Display(parent)} has no member '{token}'; fix the key or remove the null";
            }
            else
            {
                hint = $"{JsonNodeUtilities.Display(parent)} is {JsonNodeUtilities.DescribeKind(current)} in the base document, " +
                       "not an object; remove the null";
            }

            var missing = parent.Append(token);
            if (!missing.Equals(path))
            {
                hint = $"'{missing}' does not exist in the base document; {hint}";
            }

            return new PatchDiagnostic(DeleteMissingCode, message, path, hint);
        }
    }
}
