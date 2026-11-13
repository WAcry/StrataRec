using StrataRec.Pipeline.Planning;

namespace StrataRec.Experiments.Assignment;

/// <summary>Why an arm id the experimentation platform sent was not applied to the request.</summary>
public enum IgnoredArmReason
{
    /// <summary>
    /// The served plan has no such arm: the experiment is not in this release, the arm is misspelled, or the platform
    /// ramped the experiment up before the release reached this node.
    /// </summary>
    Unknown,

    /// <summary>Another arm of the same layer came too, so that layer runs its baseline.</summary>
    LayerConflict,
}

/// <summary>An arm id the platform sent that the request did not run with, and why.</summary>
/// <param name="Id">The id as sent (a null id is unknown, recorded as the empty string).</param>
/// <param name="Reason">Why it was ignored.</param>
public readonly record struct IgnoredArm(string Id, IgnoredArmReason Reason)
{
    /// <summary>The reason as replay bundles, metrics and the CLI spell it: <c>unknown</c> or <c>layer_conflict</c>.</summary>
    public static string Name(IgnoredArmReason reason) => reason switch
    {
        IgnoredArmReason.Unknown => "unknown",
        IgnoredArmReason.LayerConflict => "layer_conflict",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    /// <summary>Reads a reason <see cref="Name"/> wrote; false for any other text.</summary>
    public static bool TryParse(string? name, out IgnoredArmReason reason)
    {
        switch (name)
        {
            case "unknown":
                reason = IgnoredArmReason.Unknown;
                return true;
            case "layer_conflict":
                reason = IgnoredArmReason.LayerConflict;
                return true;
            default:
                reason = default;
                return false;
        }
    }
}

/// <summary>
/// The rules for the arms an experimentation platform sends for a request (design 11.1), the caller's
/// <c>RecRequest.Assignment</c> or an <see cref="IAssignmentProvider"/>'s answer, in one place.
/// </summary>
/// <remarks>
/// <para>The platform owns allocation and may know experiments this release does not, so what cannot be applied is
/// ignored rather than failing the request; and the outcome must not depend on the order of its list, which is no
/// contract:</para>
/// <list type="bullet">
///   <item>An id the served plan does not have is ignored as <see cref="IgnoredArmReason.Unknown"/>.</item>
///   <item>The same id twice counts once.</item>
///   <item>Two different arms of one layer: that layer gets no arm and runs its baseline, and every arm of it that was
///   sent is ignored as <see cref="IgnoredArmReason.LayerConflict"/>. Not "first wins": the arms of one layer are never
///   validated together (design 11.5), and picking one would depend on the list order.</item>
/// </list>
/// <para>Forced arms (<c>DebugOptions.ForceArms</c>) are strict instead (<see cref="ExecutionPlan.ArmsFromIds"/>): they
/// name what a developer wants to run, so a mistake there fails the request.</para>
/// </remarks>
public static class ExternalArms
{
    private const int NoArm = -1;
    private const int Conflict = -2;

    /// <summary>The request's arm per layer from the platform's ids, and the ids it ignored, sorted by id.</summary>
    /// <param name="layers">The served plan's layers.</param>
    /// <param name="arms">The served plan's arms, indexed by <see cref="ArmInfo.Index"/>.</param>
    /// <param name="ids">The platform's <c>experiment/arm</c> ids; null is no arms.</param>
    /// <param name="ignored">The ids not applied and why; an empty array (no allocation) when every id was applied.</param>
    public static RequestArms Resolve(ReadOnlySpan<LayerInfo> layers, ReadOnlySpan<ArmInfo> arms, IReadOnlyList<string>? ids, out IgnoredArm[] ignored)
    {
        var byLayer = new int[layers.Length];
        Array.Fill(byLayer, NoArm);
        List<IgnoredArm>? skipped = null;
        var conflicts = false;
        var count = ids?.Count ?? 0;
        for (var i = 0; i < count; i++)
        {
            var id = ids![i] ?? string.Empty;
            if (Find(arms, id) is not { } arm)
            {
                Add(ref skipped, id, IgnoredArmReason.Unknown);
                continue;
            }

            var current = byLayer[arm.LayerIndex];
            if (current == arm.Index)
            {
                continue; // the same id again
            }

            if (current == NoArm)
            {
                byLayer[arm.LayerIndex] = arm.Index;
                continue;
            }

            if (current != Conflict)
            {
                Add(ref skipped, arms[current].Id, IgnoredArmReason.LayerConflict); // the arm taken so far goes too
                byLayer[arm.LayerIndex] = Conflict;
                conflicts = true;
            }

            Add(ref skipped, arm.Id, IgnoredArmReason.LayerConflict);
        }

        if (conflicts)
        {
            for (var layer = 0; layer < byLayer.Length; layer++)
            {
                if (byLayer[layer] == Conflict)
                {
                    byLayer[layer] = NoArm;
                }
            }
        }

        if (skipped is null)
        {
            ignored = Array.Empty<IgnoredArm>();
        }
        else
        {
            skipped.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id)); // ids are distinct: the order is the ids'
            ignored = skipped.ToArray();
        }

        return new RequestArms(byLayer);
    }

    private static ArmInfo? Find(ReadOnlySpan<ArmInfo> arms, string id)
    {
        foreach (var arm in arms)
        {
            if (string.Equals(arm.Id, id, StringComparison.Ordinal))
            {
                return arm;
            }
        }

        return null;
    }

    /// <summary>Adds an ignored id once: a repeated id is one id, with one reason.</summary>
    private static void Add(ref List<IgnoredArm>? skipped, string id, IgnoredArmReason reason)
    {
        skipped ??= new List<IgnoredArm>(2);
        foreach (var existing in skipped)
        {
            if (string.Equals(existing.Id, id, StringComparison.Ordinal))
            {
                return;
            }
        }

        skipped.Add(new IgnoredArm(id, reason));
    }
}
