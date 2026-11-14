using System.CommandLine;
using StrataRec.Artifacts.Model;
using StrataRec.Artifacts.Storage;
using StrataRec.Hosting.Manifests;
using StrataRec.Pipeline.Planning;

namespace StrataRec.CommandLine.Commands;

/// <summary>
/// <c>stratarec status</c>: what a store publishes, as nodes serving releases will load it. For every pointer, the
/// manifest it names (its sequence, its floor, and whether it is a revocation), the experiments it releases and the
/// experiments a stop in force removed (<see cref="PublishedRelease.RevokedAsync"/>), which <c>build</c> keeps out.
/// </summary>
/// <remarks>
/// It reads the store only: what nodes applied is theirs to report (design 10.3 step 5). A pointer that cannot be read is
/// reported and the others are still listed, so one damaged pointer does not hide what the rest publish.
/// </remarks>
internal static class StatusCommand
{
    public static Command Create(PlanEnvironment provided)
    {
        _ = provided; // nothing is compiled
        var store = new Option<string>("--store", "-s")
        {
            Description = "Artifact store directory, with the pointers/<environment>/<pipeline>.json nodes read ('stratarec publish' writes them).",
            Required = true,
        };
        var pipeline = new Option<string?>("--pipeline", "-p") { Description = "Only this pipeline." };
        var environment = new Option<string?>("--env", "-e") { Description = "Only this environment." };
        var format = CommonOptions.Format();
        var command = new Command("status", "Show what a store publishes: for each pipeline and environment, the manifest its pointer names, its seq, " +
            "its experiments and the experiments a stop ('stratarec revoke') removed. Exits with 1 when a pointer or its manifest cannot be read, and 2 on usage errors.")
        {
            store, pipeline, environment, format,
        };
        command.SetAction((parse, cancellationToken) => RunAsync(parse.GetValue(store)!, parse.GetValue(pipeline), parse.GetValue(environment),
            parse.GetValue(format)!, parse.InvocationConfiguration.Output, cancellationToken, parse.InvocationConfiguration.Error));
        return command;
    }

    /// <param name="errors">Where diagnostics go; standard error by default.</param>
    internal static async Task<int> RunAsync(
        string storeRoot, string? pipelineId, string? environment, string format, TextWriter output, CancellationToken cancellationToken, TextWriter? errors = null)
    {
        errors ??= Console.Error;
        var diagnostics = new DiagnosticBag();
        if (!Directory.Exists(storeRoot))
        {
            diagnostics.Error("STORE_NOT_FOUND", "--store", string.Empty, $"There is no directory '{Path.GetFullPath(storeRoot)}'.",
                "pass the directory 'stratarec build --store' and 'stratarec publish --store' write");
            Output.Diagnostics(diagnostics.Items, format, errors);
            return 2;
        }

        var pointers = new FileSystemManifestSource(storeRoot);
        var store = new FileSystemArtifactStore(storeRoot);
        var releases = new List<Release>();
        foreach (var (pipeline, env) in pointers.ListPointers())
        {
            if ((pipelineId is not null && pipeline != pipelineId) || (environment is not null && env != environment))
            {
                continue;
            }

            var path = pointers.GetPointerPath(pipeline, env);
            var (read, pointed) = await PublishedRelease.ReadAsync(pointers, path, store, pipeline, env, "what it publishes", null, diagnostics, cancellationToken)
                .ConfigureAwait(false);
            if (read && pointed is { } published)
            {
                var (revoked, unread) = await PublishedRelease.RevokedAsync(store, published.Id, published.Manifest, cancellationToken).ConfigureAwait(false);
                if (unread is not null)
                {
                    diagnostics.Warning("REVOCATIONS_UNKNOWN", "manifest:" + published.Id, "/previous",
                        $"The manifest revokes earlier ones, and manifest '{unread}' in its history cannot be read: what the revocations before it removed is not known.",
                        "restore that manifest to the store");
                }

                releases.Add(new Release(pipeline, env, path, published.Id, published.Manifest, revoked));
            }
        }

        if (diagnostics.Items.Count > 0)
        {
            Output.Diagnostics(diagnostics.Items, format, errors);
        }

        if (format == "json")
        {
            output.WriteLine(Output.ToJson(json => WriteJson(json, releases)));
        }
        else if (releases.Count == 0 && !diagnostics.HasErrors)
        {
            output.WriteLine(pipelineId is null && environment is null
                ? $"nothing is published in {pointers.Root}"
                : $"nothing is published in {pointers.Root} for {Filter(pipelineId, environment)}");
        }
        else
        {
            WriteText(output, releases);
        }

        return diagnostics.HasErrors ? 1 : 0;
    }

    private static string Filter(string? pipelineId, string? environment) => (pipelineId, environment) switch
    {
        ({ } p, { } e) => $"pipeline '{p}' in environment '{e}'",
        ({ } p, null) => $"pipeline '{p}'",
        _ => $"environment '{environment}'",
    };

    private static void WriteText(TextWriter output, List<Release> releases)
    {
        foreach (var release in releases)
        {
            var manifest = release.Manifest;
            output.WriteLine($"{release.PipelineId} ({release.Environment}): {release.Id}");
            output.WriteLine(PublishedRelease.IsRevocation(manifest)
                ? $"  seq          {manifest.Seq}, min_accepted_seq {manifest.MinAcceptedSeq}: a revocation, nodes refuse every earlier manifest"
                : $"  seq          {manifest.Seq}, min_accepted_seq {manifest.MinAcceptedSeq}");
            if (manifest.Previous is { } previous)
            {
                output.WriteLine($"  previous     {previous}");
            }

            output.WriteLine($"  experiments  {Experiments(manifest)}");
            if (release.Revoked.Count > 0)
            {
                var names = new List<string>(release.Revoked.Keys);
                names.Sort(StringComparer.Ordinal);
                var revoked = new List<string>(names.Count);
                foreach (var name in names)
                {
                    revoked.Add($"{name} (by seq {release.Revoked[name].Seq})");
                }

                output.WriteLine($"  revoked      {string.Join(", ", revoked)}: 'stratarec build' keeps them out until they change");
            }

            output.WriteLine($"  pointer      {release.Pointer}");
        }
    }

    private static string Experiments(ReleaseManifest manifest)
    {
        if (manifest.Experiments.Count == 0)
        {
            return "(none)";
        }

        var names = new List<string>(manifest.Experiments.Keys);
        names.Sort(StringComparer.Ordinal);
        var described = new List<string>(names.Count);
        foreach (var name in names)
        {
            described.Add($"{name} (layer {manifest.Experiments[name].Layer})");
        }

        return string.Join(", ", described);
    }

    private static void WriteJson(System.Text.Json.Utf8JsonWriter json, List<Release> releases)
    {
        json.WriteStartObject();
        json.WriteStartArray("published");
        foreach (var release in releases)
        {
            var manifest = release.Manifest;
            json.WriteStartObject();
            json.WriteString("pipeline_id", release.PipelineId);
            json.WriteString("environment", release.Environment);
            json.WriteString("pointer", release.Pointer);
            json.WriteString("manifest", release.Id);
            json.WriteNumber("seq", manifest.Seq);
            json.WriteNumber("min_accepted_seq", manifest.MinAcceptedSeq);
            json.WriteBoolean("revocation", PublishedRelease.IsRevocation(manifest));
            if (manifest.Previous is { } previous)
            {
                json.WriteString("previous", previous);
            }
            else
            {
                json.WriteNull("previous");
            }

            var names = new List<string>(manifest.Experiments.Keys);
            names.Sort(StringComparer.Ordinal);
            json.WriteStartObject("experiments");
            foreach (var name in names)
            {
                json.WriteStartObject(name);
                json.WriteString("layer", manifest.Experiments[name].Layer);
                json.WriteString("artifact", manifest.Experiments[name].Artifact);
                json.WriteEndObject();
            }

            json.WriteEndObject();
            names = new List<string>(release.Revoked.Keys);
            names.Sort(StringComparer.Ordinal);
            json.WriteStartArray("revoked");
            foreach (var name in names)
            {
                var revocation = release.Revoked[name];
                json.WriteStartObject();
                json.WriteString("experiment", name);
                json.WriteString("manifest", revocation.ManifestId);
                json.WriteNumber("seq", revocation.Seq);
                json.WriteString("artifact", revocation.Artifact);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private sealed record Release(
        string PipelineId, string Environment, string Pointer, string Id, ReleaseManifest Manifest, Dictionary<string, Revocation> Revoked);
}
