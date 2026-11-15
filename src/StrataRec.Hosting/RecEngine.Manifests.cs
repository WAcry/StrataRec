using StrataRec.Artifacts.Model;
using StrataRec.Artifacts.Storage;
using StrataRec.Core.Threading;
using StrataRec.Hosting.Manifests;

namespace StrataRec.Hosting;

/// <summary>
/// What became of a release manifest a node tried to load (design 10.3 item 5): the status a node reports to the release
/// controller, which decides from it whether to go on.
/// </summary>
public enum ManifestLoadStatus
{
    /// <summary>Its plan is served now.</summary>
    Applied = 1,

    /// <summary>
    /// Not newer than what the pipeline serves (the same manifest included), or a newer one was applied while it loaded.
    /// Nothing is wrong; nothing was downloaded when the engine knew before loading.
    /// </summary>
    Stale,

    /// <summary>
    /// Its <c>seq</c> is below the highest <c>min_accepted_seq</c> the engine has seen for its pipeline: the release
    /// controller withdrew it (design 10.2, 10.3 item 6). The current plan stays.
    /// </summary>
    Revoked,

    /// <summary>
    /// It requires capabilities this binary does not provide (design 10.4 item 2), listed in
    /// <see cref="ManifestLoadResult.MissingCapabilities"/>. The current plan stays, and the node does not look for an older
    /// manifest itself: which release to serve instead is the controller's decision.
    /// </summary>
    Incompatible,

    /// <summary>
    /// It, or an artifact it names, could not be read, or its plan does not compile, or what the plan binds failed to load
    /// (design 10.3 item 4): the diagnostics say which. The current plan stays. Unlike the other statuses, retrying may
    /// succeed (a store not yet consistent, a table source that was down).
    /// </summary>
    Rejected,
}

/// <summary>
/// The outcome of <see cref="RecEngine.LoadManifestAsync"/>: what a node logs and reports to the release controller
/// (design 10.3 item 5, <see cref="IReleaseStatusReporter"/>).
/// </summary>
/// <param name="PipelineId">The manifest's pipeline.</param>
/// <param name="ManifestId">The manifest's content id.</param>
/// <param name="Sequence">The manifest's <c>seq</c>; 0 when the manifest itself could not be read.</param>
/// <param name="Status">What became of it.</param>
/// <param name="MissingCapabilities">For <see cref="ManifestLoadStatus.Incompatible"/>, the capabilities the binary lacks,
/// sorted (ordinal); empty otherwise.</param>
/// <param name="Diagnostics">Every diagnostic of the attempt: the errors that rejected it, the warnings of a plan that was
/// applied.</param>
/// <param name="CompileTime">The plan's compile time, when it compiled; zero otherwise.</param>
/// <param name="Reason">Why it was not applied, in one line for logs and status reports; null when applied.</param>
public sealed record ManifestLoadResult(
    string PipelineId,
    string ManifestId,
    long Sequence,
    ManifestLoadStatus Status,
    IReadOnlyList<string> MissingCapabilities,
    IReadOnlyList<ConfigDiagnostic> Diagnostics,
    TimeSpan CompileTime,
    string? Reason)
{
    public bool Applied => Status == ManifestLoadStatus.Applied;

    /// <summary>
    /// The sequence the pipeline serves after the attempt (the design's <c>applied_seq</c>), whichever manifest it came
    /// from; null when it serves none.
    /// </summary>
    public long? ServedSequence { get; init; }

    /// <summary>The manifest the pipeline serves after the attempt (the design's <c>applied_manifest</c>); null when none.</summary>
    public string? ServedManifestId { get; init; }

    /// <summary>
    /// When the attempt started, by the polling's clock (<see cref="ReleaseOptions.TimeProvider"/>); null for a load that
    /// is not a release poll's. A node reports its background outcomes after the fact (<see cref="IReleaseStatusReporter"/>),
    /// so this, not the time of the report, says when the node tried the manifest.
    /// </summary>
    public DateTimeOffset? AttemptedAt { get; init; }

    /// <summary>The reason of a rejection: its first error, and how many more there are.</summary>
    internal static string RejectedReason(IReadOnlyList<ConfigDiagnostic> diagnostics)
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

        return first is null
            ? "rejected"
            : $"{first.Code} {first.Artifact}{first.Pointer}: {first.Message}{(errors > 1 ? $" (and {errors - 1} more error{(errors > 2 ? "s" : string.Empty)})" : string.Empty)}";
    }
}

public sealed partial class RecEngine
{
    /// <summary>Per pipeline, the highest <c>min_accepted_seq</c> of any manifest offered to the engine; under the lifecycle lock.</summary>
    private readonly Dictionary<string, long> _minAcceptedSeq = new(StringComparer.Ordinal);

    /// <summary>
    /// Loads a release manifest by the rules of a serving node (design 10.3, 10.4), in this order: the engine records the
    /// manifest's <c>min_accepted_seq</c> (the highest one per pipeline, also from manifests it does not apply); a manifest
    /// whose <c>seq</c> is below it is <see cref="ManifestLoadStatus.Revoked"/>; one not newer than what its pipeline serves
    /// (the same manifest included) is <see cref="ManifestLoadStatus.Stale"/>; one that requires capabilities the binary
    /// lacks is <see cref="ManifestLoadStatus.Incompatible"/>. None of these downloads or compiles anything. Otherwise the
    /// artifacts are read from <paramref name="store"/> and the plan is loaded as by <see cref="LoadAsync"/> at the
    /// manifest's sequence. Whatever is not applied leaves the current plan serving.
    /// </summary>
    /// <remarks>
    /// Each outcome is logged and counted (<c>stratarec.plan.loads</c>; events 1001, 1002 and 1004–1008). Failures are
    /// results, not exceptions; a store that throws something other than I/O and integrity errors (a transport's own
    /// exception) does propagate, as does cancellation. The store's reads are part of the load: they get the load's own
    /// token, cancelled with <paramref name="cancellationToken"/> and by the engine's disposal, whose callbacks run on the
    /// thread pool (<see cref="ObservedCancellationSource"/>), not on the thread that cancels.
    /// </remarks>
    /// <param name="manifestId">The manifest's content id (<see cref="ManifestBuilder.ContentId"/>): plans, results and
    /// replay bundles carry it.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before the plan was
    /// applied; the current plan is unchanged.</exception>
    /// <exception cref="ObjectDisposedException">The engine is disposed, or was disposed before the plan was applied.</exception>
    public async Task<ManifestLoadResult> LoadManifestAsync(
        ReleaseManifest manifest, string manifestId, IArtifactStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(manifestId);
        ArgumentNullException.ThrowIfNull(store);
        cancellationToken.ThrowIfCancellationRequested();
        var pipelineId = manifest.PipelineId;
        var sequence = manifest.Seq;
        long floor;
        long? served;
        lock (_publishGate)
        {
            if (_disposed)
            {
                throw Disposed(null);
            }

            floor = _minAcceptedSeq.TryGetValue(pipelineId, out var seen) && seen > manifest.MinAcceptedSeq ? seen : manifest.MinAcceptedSeq;
            _minAcceptedSeq[pipelineId] = floor;
            served = _pipelines.TryGetValue(pipelineId, out var current) ? current.Sequence : null;
        }

        var diagnostics = new DiagnosticBag();
        if (sequence < floor)
        {
            Revoked(diagnostics, manifestId, pipelineId, sequence, floor);
            PlanLoadTelemetry.Revoked(_logger, pipelineId, manifestId, sequence, floor);
            return Result(manifest, manifestId, ManifestLoadStatus.Revoked, diagnostics, TimeSpan.Zero, RevokedReason(sequence, floor));
        }

        if (served is { } servedSequence && servedSequence >= sequence)
        {
            diagnostics.Info("PLAN_STALE", ManifestBuilder.Label(manifestId), "/seq",
                $"Not applied: pipeline '{pipelineId}' already serves sequence {servedSequence} (this manifest is {sequence}).");
            PlanLoadTelemetry.Stale(_logger, pipelineId, manifestId, sequence, servedSequence);
            return Result(manifest, manifestId, ManifestLoadStatus.Stale, diagnostics, TimeSpan.Zero, StaleReason(pipelineId, servedSequence));
        }

        var missing = ManifestBuilder.CheckCapabilities(manifest, manifestId, Environment, diagnostics);
        if (missing.Count > 0)
        {
            PlanLoadTelemetry.Incompatible(_logger, pipelineId, manifestId, sequence, missing);
            return Result(manifest, manifestId, ManifestLoadStatus.Incompatible, diagnostics, TimeSpan.Zero,
                "this binary lacks " + string.Join(", ", missing), missing);
        }

        // A load from here on, as LoadAsync's (see there): registered before the download, so a disposal cancels the
        // download too, and the store gets the load's token, whose callbacks run on the pool, never the caller's.
        PlanLoadResult loaded;
        var load = BeginLoad(cancellationToken);
        try
        {
            var request = await ManifestBuilder.LoadAsync(manifest, manifestId, store, diagnostics, load.Cancellation.Token).ConfigureAwait(false);
            load.Cancellation.Token.ThrowIfCancellationRequested(); // a store that swallowed the cancellation: not a rejection
            if (request is null)
            {
                PlanLoadTelemetry.ArtifactsFailed(_logger, pipelineId, manifestId, diagnostics);
                return Result(manifest, manifestId, ManifestLoadStatus.Rejected, diagnostics, TimeSpan.Zero, ManifestLoadResult.RejectedReason(diagnostics.Items));
            }

            // Compile and load, as any plan; it logs and counts its own outcome.
            loaded = await LoadWithBindingsAsync(request, sequence, released: true, load, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (DisposedDuring(load, cancellationToken))
        {
            throw Disposed(ex);
        }
        finally
        {
            EndLoad(load);
        }

        diagnostics.AddRange(loaded.Diagnostics);
        var (status, reason) = loaded switch
        {
            { Applied: true } => (ManifestLoadStatus.Applied, (string?)null),
            { Stale: true } => (ManifestLoadStatus.Stale, StaleReason(pipelineId, ServedSequence(pipelineId) ?? sequence)),
            { Revoked: true } => (ManifestLoadStatus.Revoked, RevokedReason(sequence, MinAcceptedSeq(pipelineId))),
            _ => (ManifestLoadStatus.Rejected, ManifestLoadResult.RejectedReason(diagnostics.Items)),
        };
        return Result(manifest, manifestId, status, diagnostics, loaded.CompileTime, reason);
    }

    private ManifestLoadResult Result(
        ReleaseManifest manifest, string manifestId, ManifestLoadStatus status, DiagnosticBag diagnostics, TimeSpan compileTime, string? reason,
        IReadOnlyList<string>? missing = null)
    {
        _pipelines.TryGetValue(manifest.PipelineId, out var served);
        return new ManifestLoadResult(manifest.PipelineId, manifestId, manifest.Seq, status, missing ?? Array.Empty<string>(), diagnostics.Items, compileTime, reason)
        {
            ServedSequence = served?.Sequence,
            ServedManifestId = served?.Plan.ManifestId,
        };
    }

    private long? ServedSequence(string pipelineId) => _pipelines.TryGetValue(pipelineId, out var served) ? served.Sequence : null;

    private long MinAcceptedSeq(string pipelineId)
    {
        lock (_publishGate)
        {
            return _minAcceptedSeq.TryGetValue(pipelineId, out var floor) ? floor : 0;
        }
    }

    private static string StaleReason(string pipelineId, long served) => $"pipeline '{pipelineId}' already serves sequence {served}";

    private static string RevokedReason(long sequence, long floor) => $"sequence {sequence} is below min_accepted_seq {floor}";

    /// <summary>The error of a revoked manifest: before it loads, or when a newer manifest revoked it while it loaded.</summary>
    private static void Revoked(DiagnosticBag diagnostics, string manifestId, string pipelineId, long sequence, long floor) =>
        diagnostics.Error("MANIFEST_REVOKED", ManifestBuilder.Label(manifestId), "/seq",
            $"Sequence {sequence} is below min_accepted_seq {floor}, the highest a manifest of pipeline '{pipelineId}' has declared: the release controller revoked it.",
            $"release the configuration again in a manifest with seq {floor} or higher (stratarec build --seq)");
}
