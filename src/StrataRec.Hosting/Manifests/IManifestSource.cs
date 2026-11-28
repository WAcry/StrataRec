namespace StrataRec.Hosting.Manifests;

/// <summary>
/// Where a serving node finds the release to serve: the manifest pointer of a pipeline in an
/// environment, which the release controller moves (<c>stratarec publish</c>). The node reads the manifest and its
/// artifacts from an <see cref="Artifacts.Storage.IArtifactStore"/> by the id the pointer names.
/// </summary>
/// <remarks>
/// <para>Teams on another configuration system serve releases by implementing this interface and
/// <see cref="Artifacts.Storage.IArtifactStore"/>: a pointer in a key-value store, a database row, an HTTP endpoint with an
/// ETag. <see cref="FileSystemManifestSource"/> reads the pointer files of a directory.</para>
/// <para>The engine keeps no last-known-good copy on disk: a host that must start while the control plane is down
/// composes a source that falls back to a pointer baked into its image, and a store that falls back to a local copy of
/// the blobs.</para>
/// <para>Implementations must be safe for concurrent use. A failure is an exception: the node logs it and asks again later,
/// with growing intervals (<see cref="ReleaseOptions"/>).</para>
/// </remarks>
public interface IManifestSource
{
    /// <summary>
    /// The content id (<c>sha256:…</c>) of the manifest the pointer of <paramref name="pipelineId"/> in
    /// <paramref name="environment"/> names now; null when there is no pointer (nothing is published).
    /// </summary>
    ValueTask<string?> GetManifestIdAsync(string pipelineId, string environment, CancellationToken cancellationToken);
}

/// <summary>
/// Receives the outcome of every manifest a node tried to load, so the release controller can decide
/// whether to go on, and alert on <c>rejected</c> and <c>incompatible</c> nodes. Optional: register one in the container
/// of <see cref="ServiceCollectionExtensions.AddStrataRec"/>.
/// </summary>
/// <remarks>
/// <para>Called once per attempt, in the order of the attempts and never concurrently; also for a manifest that could
/// not be read, as <see cref="ManifestLoadStatus.Rejected"/> with sequence 0. At startup each outcome is reported
/// before the next pipeline loads. In the background the outcomes wait in a bounded queue: a slow reporter delays only
/// the reports, never a poll or a load. When the queue is full the oldest waiting report is dropped (event
/// 1014).</para>
/// <para>The token is cancelled after <see cref="ReleaseOptions.ReportTimeout"/> (event 1015) and when the host stops; the
/// next report still waits for this call to return. An exception it throws is logged (event 1011) and changes
/// nothing.</para>
/// </remarks>
public interface IReleaseStatusReporter
{
    ValueTask ReportAsync(ManifestLoadResult result, CancellationToken cancellationToken);
}
