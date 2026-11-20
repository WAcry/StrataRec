using System.Text;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Threading;
using StrataRec.Experiments.Compilation;

namespace StrataRec.Hosting;

/// <summary>Requests that try a configuration of their own (<see cref="DebugOptions.ConfigOverride"/>, design 13.3).</summary>
public sealed partial class RecEngine
{
    /// <summary>
    /// The plan a request with a configuration override runs: the served plan's configuration with the request's arms and
    /// then the override applied (<see cref="PlanCompiler.CompileMerged"/>), compiled within the request's budget. It has
    /// a binding lease of its own, which takes over what the served plan has loaded (bindings are shared by identity) and
    /// loads only what the override adds. Entered (<see cref="Bindings.BindingLease.TryEnter"/>) and never served: the
    /// caller ends it with <see cref="EndOverride"/>. Nothing registers it with the engine, so nothing of it outlives the
    /// request, and there is no cache of such plans to bound or evict: a debugging request pays for its compile.
    /// </summary>
    /// <returns>The plan, or why the request is rejected: the override's mistake (with its pointer when it is in the
    /// override itself), or the deadline.</returns>
    private async ValueTask<(LoadedPipeline? Pipeline, string? Error, RecErrorKind ErrorKind, string? Pointer)> CompileOverrideAsync(
        LoadedPipeline served, JsonObject configOverride, IReadOnlyList<string> arms, TimeSpan budget, CancellationToken cancellationToken)
    {
        var lease = Bindings.OpenLease();
        var handedOut = false;
        using var deadline = new ObservedCancellationSource(CancellationSite.PlanLoad, cancellationToken);
        deadline.CancelAfterObserved(budget);
        var environment = Environment;
        var source = served.Source.Read(); // the configuration as it was loaded, whatever the host did with its objects since
        try
        {
            // A compile that waited for model signatures runs again (BindingLease.CompileAsync): that one reads the
            // override as the first one checked it, not the host's tree, which writes its CLR values again and may have
            // changed since (review 85).
            var patch = configOverride;
            var compilation = await lease.CompileAsync(
                bindings =>
                {
                    var compiled = PlanCompiler.CompileMerged(source, arms, environment, bindings, patch);
                    patch = compiled.ConfigOverride ?? patch;
                    return compiled;
                },
                deadline.Token).ConfigureAwait(false);
            if (compilation.Plan is not { } plan)
            {
                var (error, pointer) = Describe("does not compile", compilation.Diagnostics);
                return (null, error, RecErrorKind.InvalidRequest, pointer);
            }

            var failures = await lease.WaitLoadedAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (failures.Count > 0)
            {
                var diagnostics = new List<ConfigDiagnostic>(failures.Count);
                foreach (var failure in failures)
                {
                    diagnostics.Add(new ConfigDiagnostic(DiagnosticSeverity.Error, failure.Code, failure.Artifact, failure.Pointer, failure.Message, failure.Hint));
                }

                var (error, _) = Describe("binds what fails to load", diagnostics);
                return (null, error, RecErrorKind.InvalidRequest, null);
            }

            var pipeline = new LoadedPipeline
            {
                Plan = plan,
                FallbackPipeline = served.FallbackPipeline,
                Report = compilation.Report,
                Sequence = served.Sequence,
                ArtifactIds = served.ArtifactIds,
                Bindings = lease,
                Layout = new PlanLayout(plan),
                Source = served.Source,
                ConfigOverride = compilation.ConfigOverride,
            };
            handedOut = lease.TryEnter(); // a lease nothing has retired: always entered
            return (pipeline, null, RecErrorKind.None, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "deadline exceeded while compiling the configuration override", RecErrorKind.DeadlineExceeded, null);
        }
        finally
        {
            if (!handedOut)
            {
                lease.Dispose(); // what only it loaded stops; what the served plan shares stays
            }
        }
    }

    /// <summary>Ends a request's override plan: its lease is retired, and lets go of its instances once its runs and their calls have ended.</summary>
    private static void EndOverride(LoadedPipeline? overridden)
    {
        if (overridden is null)
        {
            return;
        }

        overridden.Bindings.Dispose();
        overridden.Bindings.RequestEnded();
    }

    /// <summary>The first error of an override that failed, how many there were, and its pointer when it is the override's own.</summary>
    private static (string Error, string? Pointer) Describe(string what, IReadOnlyList<ConfigDiagnostic> diagnostics)
    {
        ConfigDiagnostic? first = null;
        var errors = 0;
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.IsError)
            {
                first ??= diagnostic;
                errors++;
            }
        }

        var text = new StringBuilder("the configuration override ").Append(what);
        if (first is null)
        {
            return (text.ToString(), null);
        }

        text.Append(errors == 1 ? ": " : $" ({errors} errors; the first): ").Append(first.Code).Append(' ').Append(first.Artifact).Append(first.Pointer)
            .Append(": ").Append(first.Message);
        if (first.Hint is { } hint)
        {
            text.Append(" (hint: ").Append(hint).Append(')');
        }

        return (text.ToString(), first.Artifact == PlanCompiler.OverrideArtifact ? first.Pointer : null);
    }
}
