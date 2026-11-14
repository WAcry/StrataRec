using System.CommandLine;
using StrataRec.Artifacts.Model;
using StrataRec.Hosting.Manifests;
using StrataRec.Pipeline.Planning;

namespace StrataRec.CommandLine.Commands;

/// <summary>
/// <c>stratarec publish</c>: the smallest release controller of design 10.1 item 3, without emergency stops or staged
/// rollout. It points the manifest's (pipeline, environment) at the manifest, in the pointer files of the store's
/// directory (<see cref="FileSystemManifestSource"/>), which nodes serving releases poll.
/// </summary>
/// <remarks>
/// <para>A pointer only moves forward and only to something a node can read: every artifact of the manifest must be in the
/// store and read as a node reads it, and its <c>seq</c> must be greater than that of the manifest the pointer names now (a
/// rollback is a new manifest with a greater <c>seq</c>, design 10.2). A manifest file is stored first, under the id
/// <c>build</c> gives it. Capabilities are not checked: the binary that publishes need not be the one that serves, and a
/// node that lacks one reports the manifest <c>incompatible</c> and keeps its plan (design 10.4).</para>
/// <para>Two publishes of one pointer on one machine take turns (a lock file next to the pointer); the pointer itself is
/// replaced atomically, so a node reads the old pointer or the new one.</para>
/// </remarks>
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
            "Exits with 1 when the manifest or its artifacts cannot be read or its seq is not newer, and 2 on usage errors.")
        {
            manifest, store, format,
        };
        command.SetAction((parse, cancellationToken) => RunAsync(provided, parse.GetValue(manifest)!, parse.GetValue(store)!, parse.GetValue(format)!,
            parse.InvocationConfiguration.Output, cancellationToken, parse.InvocationConfiguration.Error));
        return command;
    }

    /// <param name="provided">What the binary provides to plans (<see cref="StrataRecCommandLine"/>).</param>
    /// <param name="errors">Where diagnostics go; standard error by default.</param>
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

        var published = await PublishAsync(reference, storeRoot, diagnostics, cancellationToken).ConfigureAwait(false);
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
                json.WriteEndObject();
            }));
        }
        else
        {
            var previous = done.Previous is { } p ? $"{p.Id} (seq {p.Seq})" : "(none)";
            output.WriteLine($"{done.Manifest.PipelineId} ({done.Manifest.Environment}): {previous} -> {done.Id} (seq {done.Manifest.Seq})");
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
                "pipeline ids and environment names become file names of pointers: use letters, digits, '_', '-' and '.'");
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var turn = TakeTurn(path, diagnostics, id);
        if (turn is null)
        {
            return null;
        }

        (string Id, long Seq)? previous = null;
        string? current;
        try
        {
            current = await pointers.GetManifestIdAsync(manifest.PipelineId, manifest.Environment, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            diagnostics.Error("POINTER_UNREADABLE", path, string.Empty, ex.Message,
                "fix the pointer, or delete it to publish without comparing with the manifest it named");
            return null;
        }

        var floor = manifest.MinAcceptedSeq;
        if (current is not null)
        {
            var unread = new DiagnosticBag();
            if (await ManifestBuilder.ReadAsync(store, current, unread, cancellationToken).ConfigureAwait(false) is not { } published)
            {
                diagnostics.AddRange(unread.Items);
                diagnostics.Error("POINTER_MANIFEST_UNREADABLE", path, string.Empty,
                    $"The pointer names manifest '{current}', which the store cannot read, so the sequence of what is published is unknown.",
                    "restore that manifest to the store, or delete the pointer to publish without comparing with it");
                return null;
            }

            previous = (current, published.Seq);
            if (manifest.Seq <= published.Seq)
            {
                diagnostics.Error("PUBLISH_NOT_NEWER", Label(id), "/seq",
                    current == id
                        ? $"The pointer of pipeline '{manifest.PipelineId}' in environment '{manifest.Environment}' names this manifest already (seq {published.Seq})."
                        : $"Sequence {manifest.Seq} is not greater than {published.Seq}, the sequence of '{current}', which the pointer of pipeline "
                          + $"'{manifest.PipelineId}' in environment '{manifest.Environment}' names now: nodes would not take it as newer.",
                    $"build the manifest with --seq {published.Seq + 1} or higher (stratarec build --seq), also to roll back (design 10.2)");
                return null;
            }

            floor = Math.Max(floor, published.MinAcceptedSeq);
        }

        if (manifest.Seq < floor)
        {
            diagnostics.Error("PUBLISH_REVOKED", Label(id), "/seq",
                $"Sequence {manifest.Seq} is below min_accepted_seq {floor}: every node that saw it would refuse the manifest as revoked.",
                $"build the manifest with --seq {floor} or higher");
            return null;
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
    /// The lock of one pointer: a file next to it, held open exclusively (an advisory lock on Unix, which the system releases
    /// with the process), so two publishes of one pointer on this machine cannot both compare with the same old pointer.
    /// Null after reporting that another publish holds it.
    /// </summary>
    private static FileStream? TakeTurn(string pointer, DiagnosticBag diagnostics, string id)
    {
        var path = Path.Combine(Path.GetDirectoryName(pointer)!, "." + Path.GetFileName(pointer) + ".lock");
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            diagnostics.Error("PUBLISH_BUSY", Label(id), string.Empty, $"Another publish of this pointer is running ({ex.Message}).",
                "wait for it to end and publish again");
            return null;
        }
    }

    private static string Label(string id) => "manifest:" + id;

    private sealed record Published(ReleaseManifest Manifest, string Id, string Pointer, (string Id, long Seq)? Previous);
}
