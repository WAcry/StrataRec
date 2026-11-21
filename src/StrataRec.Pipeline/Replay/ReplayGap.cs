namespace StrataRec.Pipeline.Replay;

/// <summary>
/// Data a replay needed and the bundle did not have (design 13.4). The replay treated it as missing (a feature value, a
/// service's answer) and never invented it; this is the list a comparison report shows.
/// </summary>
/// <param name="Kind"><c>feature</c>, or the role a service was called in: <c>model</c>, <c>recall</c>, or an operator's own.</param>
/// <param name="Source">Feature source, or service.</param>
/// <param name="Field">Feature field, or a service's output, when the gap is about one.</param>
/// <param name="Reason">
/// <c>not_recorded</c> (never fetched or called, or only by a call that failed), <c>type_changed</c> (the field's type
/// changed incompatibly), <c>limit_exceeds_recording</c> (asked for more items than the recording could answer),
/// <c>limit_changed</c> (a service's output recorded for another limit, which it may depend on) or
/// <c>inputs_changed</c> (a service's inputs differ from the recorded ones, so the recorded answer does not apply: the item
/// or the call is not answered).
/// </param>
/// <param name="Count">Keys, items, rows or calls affected.</param>
/// <param name="Examples">Up to five affected keys (or nodes).</param>
public sealed record ReplayGap(string Kind, string Source, string? Field, string Reason, int Count, IReadOnlyList<string> Examples)
{
    public string Description => (Kind, Reason) switch
    {
        ("feature", "type_changed") => $"feature field {Source}/{Field}: {Count} recorded value(s) do not fit its new type; treated as missing",
        ("feature", _) => $"feature field {Source}/{Field}: {Count} key(s) have no recorded value; treated as missing",
        (_, "limit_exceeds_recording") => $"{Kind} {Source}: asked for {Count} more item(s) than the recording holds",
        (_, "inputs_changed") => $"{Kind} {Source}: {Count} item(s) or call(s) have other inputs than recorded; not answered",
        (_, "limit_changed") => $"{Kind} {Source}: output '{Field}' was recorded for another limit in {Count} call(s); not answered",
        (_, "not_recorded") when Field is not null => $"{Kind} {Source}: output '{Field}' is not in the recording for {Count} item(s) or call(s)",
        (_, "not_recorded") => $"{Kind} {Source}: {Count} item(s) or call(s) have no recorded answer; not answered",
        _ => $"{Kind} {Source}: {Reason} ({Count})",
    };
}

/// <summary>How many values of one feature field a replay requested and how many came back missing.</summary>
public sealed record ReplayFieldStats(string Source, string Field, long Requested, long Missing)
{
    public double MissingRate => Requested == 0 ? 0.0 : (double)Missing / Requested;
}
