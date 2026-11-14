namespace StrataRec.Samples.Host;

/// <summary>
/// The host's settings, from the <c>StrataRec</c> configuration section (appsettings.json, environment variables such as
/// <c>StrataRec__Environment=prod</c>, or command-line arguments such as <c>--StrataRec:DeadlineMs=150</c>).
/// </summary>
/// <param name="ConfigRoot">The workspace directory, absolute.</param>
/// <param name="Environment">Selects <c>releases/&lt;pipeline&gt;.&lt;env&gt;.jsonc</c> and <c>bindings/&lt;env&gt;.jsonc</c>.</param>
/// <param name="Deadline">
/// The end-to-end budget of one request: assignment, recall, features and models all spend it. It is the service's, not the
/// caller's, so a client cannot make the service hold a request longer. A production service sets it from its latency
/// objective and warms the process up before it reports ready: the first request of a cold process includes compiling the
/// code it runs (JIT), which is why the sample's default is generous.
/// </param>
public sealed record SampleSettings(string ConfigRoot, string Environment, TimeSpan Deadline)
{
    public static SampleSettings Read(IConfiguration configuration, string contentRoot)
    {
        var section = configuration.GetSection("StrataRec");
        var deadlineMs = section.GetValue("DeadlineMs", 1000);
        if (deadlineMs <= 0)
        {
            throw new InvalidOperationException($"StrataRec:DeadlineMs must be positive, not {deadlineMs}.");
        }

        return new SampleSettings(
            Path.GetFullPath(section["ConfigRoot"] ?? "config", contentRoot),
            section["Environment"] ?? "local",
            TimeSpan.FromMilliseconds(deadlineMs));
    }
}
