namespace StrataRec.Samples.Host;

/// <summary>
/// The host's settings, from the <c>StrataRec</c> configuration section (appsettings.json, environment variables such as
/// <c>StrataRec__Environment=prod</c>, or command-line arguments such as <c>--StrataRec:DeadlineMs=150</c>).
/// </summary>
/// <param name="ConfigRoot">The workspace directory, absolute.</param>
/// <param name="Environment">Selects <c>releases/&lt;pipeline&gt;.&lt;env&gt;.jsonc</c> and <c>bindings/&lt;env&gt;.jsonc</c>.</param>
/// <param name="Deadline">The end-to-end budget of one request: assignment, recall, features and models all spend it.
/// It is the service's, not the caller's, so a client cannot make the service hold a request longer.</param>
/// <param name="Store">
/// The directory of an artifact store to serve release manifests from (<c>stratarec build --store</c>, then
/// <c>stratarec publish</c>), absolute; null serves the workspace in <paramref name="ConfigRoot"/> as it is at startup.
/// </param>
/// <param name="Pipelines">The pipelines a node serving releases loads: a store has no workspace to list them.</param>
/// <param name="ReleasePoll">How often a node serving releases reads the pointers.</param>
public sealed record SampleSettings(
    string ConfigRoot, string Environment, TimeSpan Deadline, string? Store, IReadOnlyList<string> Pipelines, TimeSpan ReleasePoll)
{
    public static SampleSettings Read(IConfiguration configuration, string contentRoot)
    {
        var section = configuration.GetSection("StrataRec");
        var deadlineMs = section.GetValue("DeadlineMs", 1000);
        if (deadlineMs <= 0)
        {
            throw new InvalidOperationException($"StrataRec:DeadlineMs must be positive, not {deadlineMs}.");
        }

        var pollMs = section.GetValue("ReleasePollMs", 2000);
        if (pollMs <= 0)
        {
            throw new InvalidOperationException($"StrataRec:ReleasePollMs must be positive, not {pollMs}.");
        }

        var pipelines = section.GetSection("Pipelines").Get<string[]>() is { Length: > 0 } listed ? listed : ["trending_feed"];
        return new SampleSettings(
            Path.GetFullPath(section["ConfigRoot"] ?? "config", contentRoot),
            section["Environment"] ?? "local",
            TimeSpan.FromMilliseconds(deadlineMs),
            section["Store"] is { Length: > 0 } store ? Path.GetFullPath(store, contentRoot) : null,
            pipelines,
            TimeSpan.FromMilliseconds(pollMs));
    }
}
