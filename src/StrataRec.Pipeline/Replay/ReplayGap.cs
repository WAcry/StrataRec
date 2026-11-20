namespace StrataRec.Pipeline.Replay;

/// <summary>
/// Data a replay needed and the bundle did not have (design 13.4). The replay treated it as missing (a feature value, a
/// candidate list, a model output) and never invented it; this is the list a comparison report shows.
/// </summary>
/// <param name="Kind"><c>feature</c>, <c>candidate_source</c> or <c>model</c>.</param>
/// <param name="Source">Feature source, candidate source or model.</param>
/// <param name="Field">Feature field, or the query value of a candidate source, when the gap is about one.</param>
/// <param name="Reason">
/// <c>not_recorded</c> (never fetched, or only by a call that failed), <c>type_changed</c> (the field's type changed
/// incompatibly), <c>limit_exceeds_recording</c> (a larger quota than the recording could answer),
/// <c>inputs_changed</c> (model inputs differ from the recorded ones, so the recorded output does not apply) or
/// <c>query_changed</c> (a value of a candidate source's query differs from the recorded one, so the recorded candidates
/// answer another query; they are returned all the same, as the recording has no others).
/// </param>
/// <param name="Count">Keys, candidates or rows affected.</param>
/// <param name="Examples">Up to five affected keys (or nodes).</param>
public sealed record ReplayGap(string Kind, string Source, string? Field, string Reason, int Count, IReadOnlyList<string> Examples)
{
    public string Description => (Kind, Reason) switch
    {
        ("feature", "type_changed") => $"feature field {Source}/{Field}: {Count} recorded value(s) do not fit its new type; treated as missing",
        ("feature", _) => $"feature field {Source}/{Field}: {Count} key(s) have no recorded value; treated as missing",
        ("candidate_source", "limit_exceeds_recording") => $"candidate source {Source}: asked for {Count} more candidate(s) than the recording holds",
        ("candidate_source", "query_changed") =>
            $"candidate source {Source}: its query value '{Field}' differs from the recorded one in {Count} call(s); the recorded candidates were returned",
        ("candidate_source", _) => $"candidate source {Source} was not called in the recording; it returned no candidates",
        ("model", "inputs_changed") => $"model {Source}: {Count} row(s) have different inputs than recorded; their outputs are missing",
        ("model", _) => $"model {Source}: {Count} row(s) or call(s) have no recorded output; treated as missing",
        _ => $"{Kind} {Source}: {Reason} ({Count})",
    };
}

/// <summary>How many values of one feature field a replay requested and how many came back missing.</summary>
public sealed record ReplayFieldStats(string Source, string Field, long Requested, long Missing)
{
    public double MissingRate => Requested == 0 ? 0.0 : (double)Missing / Requested;
}
