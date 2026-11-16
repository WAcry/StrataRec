using System.CommandLine;
using StrataRec.Artifacts.Model;
using StrataRec.Artifacts.Storage;
using StrataRec.Hosting.Manifests;
using StrataRec.Pipeline.Planning;

namespace StrataRec.CommandLine.Commands;

/// <summary>
/// <c>stratarec revoke</c>: the emergency stop of design 10.6 for the release controller that <c>stratarec publish</c> is.
/// It publishes, at once, what the pointer publishes without the named experiments, as the next release (<c>seq</c> one
/// more), which lists them as revoked. Nodes take it as they take any newer release. Nothing is compiled or validated
/// (<see cref="ManifestBuilder.RevokeAsync"/>), so the stop takes the nodes' polling interval and a compile, and needs
/// neither the repository nor CI.
/// </summary>
/// <remarks>
/// <para>It reads the pointer and moves it under the pointer's lock, as publish does, so no publish comes between what it
/// read and what it writes. Run again, it changes nothing: an experiment the published manifest lists as revoked already is
/// reported as such, and the command succeeds, so a stop can be retried without thought.</para>
/// <para>The stop lasts without controller state of its own: while the published manifest lists an experiment as revoked,
/// <c>stratarec build</c> refuses to release it unchanged (see <see cref="BuildCommand"/>), and the owner's change to the
/// experiment, or <c>--restore</c>, releases it again.</para>
/// </remarks>
internal static class RevokeCommand
{
    public static Command Create(PlanEnvironment provided)
    {
        _ = provided; // nothing is compiled: the experiments are removed from what is published as it is
        var experiments = new Argument<string[]>("experiments")
        {
            Description = "The experiments to remove from the published release.",
            Arity = ArgumentArity.OneOrMore,
        };
        var store = new Option<string>("--store", "-s")
        {
            Description = "Artifact store directory, with the pointers/<environment>/<pipeline>.json nodes read ('stratarec publish' writes them).",
            Required = true,
        };
        var pipeline = new Option<string>("--pipeline", "-p") { Description = "The pipeline whose release to change.", Required = true };
        var environment = CommonOptions.Environment();
        var format = CommonOptions.Format();
        var command = new Command("revoke", "Emergency stop (design 10.6): publish at once what is published without the experiments, revoking every earlier " +
            "manifest, so a node that restarts does not bring them back either. Exits with 1 when nothing is published, an experiment is not released, or the " +
            "store cannot be read or written, and 2 on usage errors.")
        {
            experiments, store, pipeline, environment, format,
        };
        command.SetAction((parse, cancellationToken) => RunAsync(parse.GetValue(experiments)!, parse.GetValue(store)!, parse.GetValue(pipeline)!,
            parse.GetValue(environment)!, parse.GetValue(format)!, parse.InvocationConfiguration.Output, cancellationToken, parse.InvocationConfiguration.Error));
        return command;
    }

    /// <param name="errors">Where diagnostics and advice go; standard error by default.</param>
    internal static async Task<int> RunAsync(
        string[] experiments, string storeRoot, string pipelineId, string environment, string format, TextWriter output, CancellationToken cancellationToken,
        TextWriter? errors = null)
    {
        errors ??= Console.Error;
        var diagnostics = new DiagnosticBag();
        var pointers = new FileSystemManifestSource(storeRoot);
        string path;
        try
        {
            path = pointers.GetPointerPath(pipelineId, environment);
        }
        catch (ArgumentException ex)
        {
            diagnostics.Error("POINTER_NAME_INVALID", ex.ParamName == "environment" ? "--env" : "--pipeline", string.Empty, ex.Message,
                "pipeline ids and environment names are the file names of pointers: use letters, digits, '_', '-' and '.'");
            Output.Diagnostics(diagnostics.Items, format, errors);
            return 2;
        }

        if (!File.Exists(path))
        {
            NothingPublished(path, pipelineId, environment, diagnostics);
            Output.Diagnostics(diagnostics.Items, format, errors);
            return 1;
        }

        Outcome? outcome;
        int code;
        try
        {
            (outcome, code) = await RevokeAsync(experiments, pointers, path, new FileSystemArtifactStore(storeRoot), pipelineId, environment, diagnostics,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Output.StoreUnwritable(ex, diagnostics); // what it reads is reported where it is read
            (outcome, code) = (null, 1);
        }
        if (diagnostics.Items.Count > 0)
        {
            Output.Diagnostics(diagnostics.Items, format, errors);
        }

        if (outcome is not { } done)
        {
            return code;
        }

        if (format == "json")
        {
            output.WriteLine(Output.ToJson(json =>
            {
                json.WriteStartObject();
                json.WriteString("pipeline_id", pipelineId);
                json.WriteString("environment", environment);
                json.WriteString("pointer", path);
                json.WriteStartObject("previous");
                json.WriteString("manifest", done.Previous.Id);
                json.WriteNumber("seq", done.Previous.Seq);
                json.WriteEndObject();
                json.WriteString("manifest", done.Id);
                json.WriteNumber("seq", done.Seq);
                json.WriteStartArray("revoked");
                foreach (var name in done.Revoked)
                {
                    json.WriteStringValue(name);
                }

                json.WriteEndArray();
                json.WriteStartArray("revoked_already");
                foreach (var (name, revocation) in done.RevokedAlready)
                {
                    json.WriteStartObject();
                    json.WriteString("experiment", name);
                    json.WriteNumber("seq", revocation.Seq);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteBoolean("changed", done.Revoked.Count > 0);
                json.WriteEndObject();
            }));
            return 0;
        }

        foreach (var (name, revocation) in done.RevokedAlready)
        {
            output.WriteLine($"{pipelineId} ({environment}): {name} was revoked already (at seq {revocation.Seq})");
        }

        if (done.Revoked.Count > 0)
        {
            output.WriteLine($"{pipelineId} ({environment}): revoked {string.Join(", ", done.Revoked)}");
            output.WriteLine($"  {done.Previous.Id} (seq {done.Previous.Seq}) -> {done.Id} (seq {done.Seq})");
            output.WriteLine($"pointer {path}");
            errors.WriteLine("'stratarec build' refuses to release a revoked experiment unchanged: remove it from the release file until it is fixed");
        }

        return 0;
    }

    /// <summary>
    /// Reads what the pointer publishes and moves the pointer to its revocation, under the pointer's lock. Null, with the
    /// exit code, after reporting errors.
    /// </summary>
    private static async Task<(Outcome? Outcome, int Code)> RevokeAsync(
        string[] experiments, FileSystemManifestSource pointers, string path, IArtifactStore store, string pipelineId, string environment,
        DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        using var turn = PublishCommand.TakeTurn(path, path, diagnostics);
        if (turn is null)
        {
            return (null, 1);
        }

        var (read, pointed) = await PublishedRelease.ReadAsync(pointers, path, store, pipelineId, environment, "what is published", null, diagnostics,
            cancellationToken).ConfigureAwait(false);
        if (!read)
        {
            return (null, 1);
        }

        if (pointed is not { } published)
        {
            NothingPublished(path, pipelineId, environment, diagnostics);
            return (null, 1);
        }

        var removing = new List<string>();
        var already = new List<(string Name, RevokedExperiment Revocation)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in experiments)
        {
            if (!seen.Add(name))
            {
                continue;
            }

            if (published.Manifest.Experiments.ContainsKey(name))
            {
                removing.Add(name);
            }
            else if (published.Manifest.Revoked.TryGetValue(name, out var revocation))
            {
                already.Add((name, revocation));
            }
            else
            {
                diagnostics.Error("EXPERIMENT_NOT_RELEASED", "manifest:" + published.Id, "/experiments",
                    $"Manifest '{published.Id}' (seq {published.Manifest.Seq}), which pipeline '{pipelineId}' in environment '{environment}' publishes, " +
                    $"does not release experiment '{name}'.",
                    NameSuggestions.DidYouMean(name, published.Manifest.Experiments.Keys) ?? Released(published.Manifest));
            }
        }

        if (diagnostics.HasErrors)
        {
            return (null, 1);
        }

        var previous = (published.Id, published.Manifest.Seq);
        if (removing.Count == 0)
        {
            var current = published.Manifest;
            return (new Outcome(published.Id, current.Seq, previous, removing, already), 0);
        }

        if (PublishedRelease.NextSeq(published.Id, published.Manifest, diagnostics) is null
            || await ManifestBuilder.RevokeAsync(published.Manifest, published.Id, removing, store, diagnostics, cancellationToken).ConfigureAwait(false)
                is not { } revocationManifest)
        {
            return (null, 1);
        }

        var id = await store.PutAsync(ArtifactSerializer.ToNode(revocationManifest), cancellationToken).ConfigureAwait(false);
        await pointers.WritePointerAsync(pipelineId, environment, id, cancellationToken).ConfigureAwait(false);
        return (new Outcome(id, revocationManifest.Seq, previous, removing, already), 0);
    }

    private static void NothingPublished(string path, string pipelineId, string environment, DiagnosticBag diagnostics) =>
        diagnostics.Error("NOTHING_PUBLISHED", path, string.Empty, $"Nothing is published for pipeline '{pipelineId}' in environment '{environment}': there is no pointer.",
            "check --store, --pipeline and --env; 'stratarec publish' writes the pointer");

    private static string Released(ReleaseManifest manifest)
    {
        if (manifest.Experiments.Count == 0)
        {
            return "it releases no experiment";
        }

        var names = new List<string>(manifest.Experiments.Keys);
        names.Sort(StringComparer.Ordinal);
        return "it releases " + string.Join(", ", names);
    }

    /// <summary>What a revoke did: the manifest published now (the revocation, or what was published already), and the one before.</summary>
    private sealed record Outcome(
        string Id, long Seq, (string Id, long Seq) Previous, List<string> Revoked, List<(string Name, RevokedExperiment Revocation)> RevokedAlready);
}
