using System.CommandLine;
using StrataRec.Artifacts.Model;
using StrataRec.Artifacts.Storage;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Manifests;
using StrataRec.Hosting.Workspace;

namespace StrataRec.Cli.Commands;

/// <summary>
/// <c>stratarec build</c> (design 10.1 item 2): validates, uploads canonical artifacts to a content-addressed store and
/// writes the release manifest. Nothing is uploaded when validation fails.
/// </summary>
internal static class BuildCommand
{
    public static Command Create()
    {
        var config = CommonOptions.Config();
        var environment = CommonOptions.Environment();
        var pipeline = CommonOptions.Pipeline();
        var store = new Option<string>("--store", "-s") { Description = "Artifact store directory (blobs/sha256/...).", Required = true };
        var manifest = new Option<string?>("--manifest", "-m") { Description = "Write the manifest here instead of stdout." };
        var seq = new Option<long>("--seq") { Description = "Manifest sequence number.", DefaultValueFactory = _ => 1 };
        var command = new Command("build", "Validate, upload artifacts by content hash and emit a release manifest.") { config, environment, pipeline, store, manifest, seq };
        command.SetAction((parse, cancellationToken) => RunAsync(parse.GetValue(config)!, parse.GetValue(environment)!, parse.GetValue(pipeline),
            parse.GetValue(store)!, parse.GetValue(manifest), parse.GetValue(seq), Console.Out, cancellationToken));
        return command;
    }

    internal static async Task<int> RunAsync(string config, string environment, string? pipeline, string storeRoot, string? manifestPath, long seq, TextWriter output, CancellationToken cancellationToken)
    {
        var diagnostics = new DiagnosticBag();
        var (workspace, request, compilation) = CommonOptions.Compile(config, environment, pipeline, diagnostics);
        if (request is null || compilation is not { Succeeded: true } || diagnostics.HasErrors)
        {
            Output.Diagnostics(diagnostics.Items, "text", Console.Error, locator: DiagnosticLocator.ForWorkspace(config));
            return 1;
        }

        var store = new FileSystemArtifactStore(storeRoot);
        var manifest = await ManifestBuilder.BuildAsync(workspace, request.Pipeline.PipelineId, environment, store, diagnostics, seq, cancellationToken).ConfigureAwait(false);
        if (manifest is null)
        {
            Output.Diagnostics(diagnostics.Items, "text", Console.Error, locator: DiagnosticLocator.ForWorkspace(config));
            return 1;
        }

        var node = ArtifactSerializer.ToNode(manifest);
        var manifestId = await store.PutAsync(node, cancellationToken).ConfigureAwait(false);
        var text = node.ToJsonString(ArtifactSerializer.Options);
        if (manifestPath is null)
        {
            output.WriteLine(text);
        }
        else
        {
            await File.WriteAllTextAsync(manifestPath, text + "\n", cancellationToken).ConfigureAwait(false);
        }

        Console.Error.WriteLine($"manifest {manifestId} ({manifest.Requires.Count} capabilities, {manifest.Experiments.Count} experiments)");
        return 0;
    }
}
