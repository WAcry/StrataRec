using System.CommandLine;
using StrataRec.Artifacts.Model;
using StrataRec.Hosting.Manifests;
using StrataRec.Pipeline.Planning;

namespace StrataRec.CommandLine.Commands;

internal static class PublishCommand
{
    public static Command Create(PlanEnvironment provided)
    {
        var manifest = new Argument<string>("manifest")
        {
            Description = "The manifest to publish: its content id (sha256:<hex>) in --store, or a manifest file, which is stored there first.",
        };
        var store = new Option<string>("--store", "-s")
        {
            Description = "Artifact store directory: blobs/sha256/... ('stratarec build --store'), and the pointers/<environment>/<pipeline>.json nodes read.",
            Required = true,
        };
        var format = CommonOptions.Format();
        var command = new Command("publish", "Point the manifest's pipeline and environment at it, for nodes serving releases to load. " +
            "Exits with 1 when the manifest or its artifacts cannot be read, its seq is not newer or the store cannot be written, and 2 on usage errors.")
        {
            manifest, store, format,
        };
        command.SetAction((parse, cancellationToken) => RunAsync(provided, parse.GetValue(manifest)!, parse.GetValue(store)!, parse.GetValue(format)!,
            parse.InvocationConfiguration.Output, cancellationToken, parse.InvocationConfiguration.Error));
        return command;
    }

    internal static async Task<int> RunAsync(
        PlanEnvironment provided, string manifestRef, string storeRoot, string format, TextWriter output, CancellationToken cancellationToken,
        TextWriter? errors = null)
    {
        errors ??= Console.Error;
        var diagnostics = new DiagnosticBag();
        if (ConfigRef.Manifest(manifestRef, storeRoot, "manifest", diagnostics, provided) is not { } reference)
        {
            Output.Diagnostics(diagnostics.Items, format, errors);
            return 2;
        }

        Published? published;
        try
        {
            published = await PublishAsync(reference, storeRoot, diagnostics, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Output.StoreUnwritable(ex, diagnostics); // what it reads is reported where it is read
            published = null;
        }

        if (published is not { } done)
        {
            Output.Diagnostics(diagnostics.Items, format, errors);
            return 1;
        }

        if (format == "json")
        {
            output.WriteLine(Output.ToJson(json =>
            {
                json.WriteStartObject();
                json.WriteString("pipeline_id", done.Manifest.PipelineId);
                json.WriteString("environment", done.Manifest.Environment);
                json.WriteString("pointer", done.Pointer);
                if (done.Previous is { } previous)
                {
                    json.WriteStartObject("previous");
                    json.WriteString("manifest", previous.Id);
                    json.WriteNumber("seq", previous.Seq);
                    json.WriteEndObject();
                }
                else
                {
                    json.WriteNull("previous");
                }

                json.WriteString("manifest", done.Id);
                json.WriteNumber("seq", done.Manifest.Seq);
                json.WriteBoolean("changed", !done.Unchanged);
                json.WriteEndObject();
            }));
        }
        else
        {
            var previous = done.Previous is { } p ? $"{p.Id} (seq {p.Seq})" : "(none)";
            output.WriteLine(done.Unchanged
                ? $"{done.Manifest.PipelineId} ({done.Manifest.Environment}): {done.Id} (seq {done.Manifest.Seq}) is published already"
                : $"{done.Manifest.PipelineId} ({done.Manifest.Environment}): {previous} -> {done.Id} (seq {done.Manifest.Seq})");
            output.WriteLine($"pointer {done.Pointer}");
        }

        return 0;
    }

    private static async Task<Published?> PublishAsync(ConfigRef reference, string storeRoot, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        if (await reference.ReadManifestAsync(diagnostics, cancellationToken).ConfigureAwait(false) is not { } read)
        {
            return null;
        }

        var (manifest, id, store) = read;

        // Read as a node reads it, so a pointer never names a manifest a node cannot load for want of an artifact.
        if (await ManifestBuilder.LoadAsync(manifest, id, store, diagnostics, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        var pointers = new FileSystemManifestSource(storeRoot);
        string path;
        try
        {
            path = pointers.GetPointerPath(manifest.PipelineId, manifest.Environment);
        }
        catch (ArgumentException ex)
        {
            diagnostics.Error("MANIFEST_POINTER_NAME", Label(id), "/pipeline_id", ex.Message,
                FileSystemManifestSource.NameHint);
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var turn = TakeTurn(path, Label(id), diagnostics);
        if (turn is null)
        {
            return null;
        }

        (string Id, long Seq)? previous = null;
        var (readPointer, pointed) = await PublishedRelease.ReadAsync(pointers, path, store, manifest.PipelineId, manifest.Environment,
            "the sequence of what is published", "or delete the pointer to publish without comparing with the manifest it names", diagnostics, cancellationToken)
            .ConfigureAwait(false);
        if (!readPointer)
        {
            return null;
        }

        if (pointed is { } last)
        {
            var (current, published) = last;
            previous = (current, published.Seq);
            if (current == id)
            {
                return new Published(manifest, id, path, previous, Unchanged: true);
            }

            if (manifest.Seq <= published.Seq)
            {
                diagnostics.Error("PUBLISH_NOT_NEWER", Label(id), "/seq",
                    $"Sequence {manifest.Seq} is not greater than {published.Seq}, the sequence of '{current}', which the pointer of pipeline "
                    + $"'{manifest.PipelineId}' in environment '{manifest.Environment}' names now: nodes would not take it as newer.",
                    published.Seq >= BuildCommand.MaxSeq
                        ? "sequence numbers end there (2^53 - 1, the largest safe integer), and nodes that applied it take nothing lower: " +
                          "release under another environment name"
                        : $"build it again without --seq, which makes the next release of what is published, or with --seq {published.Seq + 1} or higher; " +
                          "a rollback is a new build too");
                return null;
            }
        }

        if (reference.Kind == ConfigRefKind.ManifestFile)
        {
            var stored = await store.PutAsync(ArtifactSerializer.ToNode(manifest), cancellationToken).ConfigureAwait(false);
            if (stored != id)
            {
                throw new InvalidOperationException($"The manifest was stored as '{stored}', not under its id '{id}'.");
            }
        }

        await pointers.WritePointerAsync(manifest.PipelineId, manifest.Environment, id, cancellationToken).ConfigureAwait(false);
        return new Published(manifest, id, path, previous);
    }

    /// <summary>
    /// The lock of one pointer: a file next to it, held open exclusively (an advisory lock on Unix, which the system
    /// releases with the process), so two publishes of one pointer on this machine cannot both compare with the same
    /// old pointer.
    /// </summary>
    internal static FileStream? TakeTurn(string pointer, string artifact, DiagnosticBag diagnostics)
    {
        var path = Path.Combine(Path.GetDirectoryName(pointer)!, "." + Path.GetFileName(pointer) + ".lock");
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            diagnostics.Error("PUBLISH_BUSY", artifact, string.Empty, $"Another publish of this pointer is running ({ex.Message}).",
                "wait for it to end and publish again");
            return null;
        }
    }

    private static string Label(string id) => "manifest:" + id;

    private sealed record Published(ReleaseManifest Manifest, string Id, string Pointer, (string Id, long Seq)? Previous, bool Unchanged = false);
}
