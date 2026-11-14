using System.CommandLine;
using StrataRec.Artifacts.Model;
using StrataRec.Artifacts.Storage;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Manifests;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Planning;

namespace StrataRec.CommandLine.Commands;

/// <summary>
/// <c>stratarec build</c> (design 10.1 item 2): validates, uploads canonical artifacts to a content-addressed store and
/// writes the release manifest. Nothing is uploaded when validation fails.
/// </summary>
internal static class BuildCommand
{
    public static Command Create(PlanEnvironment provided)
    {
        var config = CommonOptions.Config();
        var environment = CommonOptions.Environment();
        var pipeline = CommonOptions.Pipeline();
        var store = new Option<string>("--store", "-s") { Description = "Artifact store directory (blobs/sha256/...).", Required = true };
        var manifest = new Option<string?>("--manifest", "-m") { Description = "Write the manifest here instead of stdout." };
        var seq = new Option<long>("--seq") { Description = "Manifest sequence number.", DefaultValueFactory = _ => 1 };
        var command = new Command("build", "Validate, upload artifacts by content hash and emit a release manifest. Exits with 1 when the configuration has errors.") { config, environment, pipeline, store, manifest, seq };
        command.SetAction((parse, cancellationToken) => RunAsync(provided, parse.GetValue(config)!, parse.GetValue(environment)!, parse.GetValue(pipeline),
            parse.GetValue(store)!, parse.GetValue(manifest), parse.GetValue(seq), parse.InvocationConfiguration.Output, cancellationToken,
            parse.InvocationConfiguration.Error));
        return command;
    }

    /// <param name="errors">Where diagnostics and the manifest id go; standard error by default.</param>
    /// <param name="provided">What the binary provides to plans (<see cref="StrataRecCommandLine"/>).</param>
    internal static async Task<int> RunAsync(
        PlanEnvironment provided, string config, string environment, string? pipeline, string storeRoot, string? manifestPath, long seq, TextWriter output,
        CancellationToken cancellationToken, TextWriter? errors = null)
    {
        errors ??= Console.Error;
        var diagnostics = new DiagnosticBag();
        var (workspace, request, compilation) = CommonOptions.Compile(provided, config, environment, pipeline, diagnostics);
        if (request is null || compilation is not { Succeeded: true } || diagnostics.HasErrors)
        {
            Output.Diagnostics(diagnostics.Items, "text", errors, locator: DiagnosticLocator.ForWorkspace(config));
            return 1;
        }

        var store = new FileSystemArtifactStore(storeRoot);
        var manifest = await ManifestBuilder.BuildAsync(workspace, request.Pipeline.PipelineId, environment, store, diagnostics, seq, cancellationToken).ConfigureAwait(false);
        if (manifest is null)
        {
            Output.Diagnostics(diagnostics.Items, "text", errors, locator: DiagnosticLocator.ForWorkspace(config));
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

        errors.WriteLine($"manifest {manifestId} ({manifest.Requires.Count} capabilities, {manifest.Experiments.Count} experiments)");
        return 0;
    }
}
