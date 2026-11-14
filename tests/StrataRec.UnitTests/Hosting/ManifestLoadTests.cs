using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Diagnostics;
using StrataRec.Hosting;
using StrataRec.Hosting.Manifests;

namespace StrataRec.UnitTests.Hosting;

/// <summary>
/// <see cref="RecEngine.LoadManifestAsync"/>: a node's rules for a release manifest (design 10.3, 10.4), each status, what
/// is downloaded for it, and that whatever is not applied leaves the current plan serving.
/// </summary>
public sealed class ManifestLoadTests : IDisposable
{
    private readonly ReleaseFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task AManifest_IsApplied_AtItsSequence_AndServesRequests()
    {
        var (manifest, id) = await _fixture.Pipeline("p").BuildAsync("p", seq: 3);
        await using var engine = new RecEngine(_fixture.Environment);

        var result = await engine.LoadManifestAsync(manifest, id, _fixture.Store);

        Assert.Equal((ManifestLoadStatus.Applied, "p", id, 3L), (result.Status, result.PipelineId, result.ManifestId, result.Sequence));
        Assert.True(result.Applied);
        Assert.Null(result.Reason);
        Assert.Empty(result.MissingCapabilities);
        Assert.Equal((3L, id), (result.ServedSequence, result.ServedManifestId));
        Assert.True(result.CompileTime > TimeSpan.Zero);
        Assert.True(engine.TryGetPipeline("p", out var served));
        Assert.Equal((3L, id), (served.Sequence, served.Plan.ManifestId));

        var response = await engine.ExecuteAsync(ReleaseFixture.Request("p"));
        Assert.True(response.Succeeded, response.Error);
        Assert.Equal(id, response.ManifestId);
        Assert.Equal(new long[] { 9, 8 }, response.Items.Select(i => i.Id.Int64Value));
    }

    [Fact]
    public async Task TheSameManifest_OrAnOlderOne_IsStale_AndNothingIsDownloaded()
    {
        var (first, firstId) = await _fixture.Pipeline("p").BuildAsync("p", seq: 1);
        var (second, secondId) = await _fixture.Pipeline("p", k: 3).BuildAsync("p", seq: 2);
        var store = new WatchedStore(_fixture.Store);
        await using var engine = new RecEngine(_fixture.Environment);
        Assert.True((await engine.LoadManifestAsync(second, secondId, store)).Applied);
        var reads = store.Reads;

        foreach (var (manifest, id) in new[] { (second, secondId), (first, firstId) })
        {
            var result = await engine.LoadManifestAsync(manifest, id, store);

            Assert.Equal(ManifestLoadStatus.Stale, result.Status);
            Assert.Equal("pipeline 'p' already serves sequence 2", result.Reason);
            Assert.DoesNotContain(result.Diagnostics, d => d.IsError);
            var stale = Assert.Single(result.Diagnostics);
            Assert.Equal((DiagnosticSeverity.Info, "PLAN_STALE", "manifest:" + id, "/seq"), (stale.Severity, stale.Code, stale.Artifact, stale.Pointer));
            Assert.Equal((2L, secondId), (result.ServedSequence, result.ServedManifestId));
        }

        Assert.Equal(reads, store.Reads); // known before any download
    }

    [Fact]
    public async Task AnIncompatibleManifest_ListsWhatTheBinaryLacks_AndTheCurrentPlanStays()
    {
        var (current, currentId) = await _fixture.Pipeline("p").BuildAsync("p", seq: 1);
        var (next, _) = await _fixture.Pipeline("p", k: 3).BuildAsync("p", seq: 2);
        var incompatible = ReleaseFixture.With(next, requires: ["Zebra@1", "Alpaca@2", "Zebra@1"]);
        var incompatibleId = await _fixture.PutAsync(incompatible);
        var store = new WatchedStore(_fixture.Store);
        await using var engine = new RecEngine(_fixture.Environment);
        Assert.True((await engine.LoadManifestAsync(current, currentId, store)).Applied);
        var reads = store.Reads;

        var result = await engine.LoadManifestAsync(incompatible, incompatibleId, store);

        Assert.Equal(ManifestLoadStatus.Incompatible, result.Status);
        Assert.Equal(new[] { "Alpaca@2", "Zebra@1" }, result.MissingCapabilities);
        Assert.Equal("this binary lacks Alpaca@2, Zebra@1", result.Reason);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(("MANIFEST_INCOMPATIBLE", "manifest:" + incompatibleId, "/requires"), (error.Code, error.Artifact, error.Pointer));
        Assert.Equal("The manifest requires capabilities this binary does not provide: Alpaca@2, Zebra@1.", error.Message);
        Assert.Contains("'stratarec operators' lists what a binary provides", error.Hint);
        Assert.Equal(reads, store.Reads); // nothing downloaded

        Assert.Equal((1L, currentId), (result.ServedSequence, result.ServedManifestId));
        var response = await engine.ExecuteAsync(ReleaseFixture.Request("p"));
        Assert.True(response.Succeeded, response.Error);
        Assert.Equal(currentId, response.ManifestId);

        // The same check, from the shared helper the command line uses.
        Assert.Equal(new[] { "Alpaca@2", "Zebra@1" }, ManifestBuilder.MissingCapabilities(incompatible, engine.Environment));
        Assert.Empty(ManifestBuilder.MissingCapabilities(current, engine.Environment));
    }

    [Fact]
    public async Task AManifestBelowAMinAcceptedSeqTheEngineHasSeen_IsRevoked_AlsoWhenTheManifestThatDeclaredItWasNotApplied()
    {
        var (current, currentId) = await _fixture.Pipeline("p").BuildAsync("p", seq: 1);
        var (next, _) = await _fixture.Pipeline("p", k: 3).BuildAsync("p", seq: 3);
        await using var engine = new RecEngine(_fixture.Environment);
        Assert.True((await engine.LoadManifestAsync(current, currentId, _fixture.Store)).Applied);

        // The controller revoked everything below 4, in a manifest this binary cannot serve: still learned.
        var revoking = ReleaseFixture.With(next, seq: 5, minAcceptedSeq: 4, requires: ["Missing@1"]);
        Assert.Equal(ManifestLoadStatus.Incompatible, (await engine.LoadManifestAsync(revoking, await _fixture.PutAsync(revoking), _fixture.Store)).Status);

        var revokedId = await _fixture.PutAsync(next);
        var revoked = await engine.LoadManifestAsync(next, revokedId, _fixture.Store);

        Assert.Equal(ManifestLoadStatus.Revoked, revoked.Status);
        Assert.Equal("sequence 3 is below min_accepted_seq 4", revoked.Reason);
        var error = Assert.Single(revoked.Diagnostics);
        Assert.Equal(("MANIFEST_REVOKED", "manifest:" + revokedId, "/seq"), (error.Code, error.Artifact, error.Pointer));
        Assert.Contains("seq 4 or higher", error.Hint);
        Assert.Equal((1L, currentId), (revoked.ServedSequence, revoked.ServedManifestId));

        // A lower min_accepted_seq does not lower what was learned; a manifest at the floor is applied.
        var atFloor = ReleaseFixture.With(next, seq: 4, minAcceptedSeq: 0);
        var applied = await engine.LoadManifestAsync(atFloor, await _fixture.PutAsync(atFloor), _fixture.Store);
        Assert.Equal(ManifestLoadStatus.Applied, applied.Status);
        var older = ReleaseFixture.With(current, seq: 2, minAcceptedSeq: 0);
        Assert.Equal(ManifestLoadStatus.Revoked, (await engine.LoadManifestAsync(older, await _fixture.PutAsync(older), _fixture.Store)).Status);
    }

    [Fact]
    public async Task AManifestRevokedWhileItLoads_IsNotApplied()
    {
        var (current, currentId) = await _fixture.Pipeline("p").BuildAsync("p", seq: 1);
        var (next, nextId) = await _fixture.Pipeline("p", k: 3).BuildAsync("p", seq: 2);
        var revoking = ReleaseFixture.With(next, seq: 3, minAcceptedSeq: 3, requires: ["Missing@1"]);
        var revokingId = await _fixture.PutAsync(revoking);
        await using var engine = new RecEngine(_fixture.Environment);
        Assert.True((await engine.LoadManifestAsync(current, currentId, _fixture.Store)).Applied);

        // Right before the loaded plan is published, a manifest revokes its sequence (it is incompatible, so it neither
        // waits nor replaces the plan itself).
        engine.Publishing = () =>
        {
            engine.Publishing = null;
            Assert.Equal(ManifestLoadStatus.Incompatible, engine.LoadManifestAsync(revoking, revokingId, _fixture.Store).GetAwaiter().GetResult().Status);
        };
        var result = await engine.LoadManifestAsync(next, nextId, _fixture.Store);

        Assert.Equal(ManifestLoadStatus.Revoked, result.Status);
        Assert.Equal("sequence 2 is below min_accepted_seq 3", result.Reason);
        Assert.Contains(result.Diagnostics, d => d.Code == "MANIFEST_REVOKED");
        Assert.True(engine.TryGetPipeline("p", out var served));
        Assert.Equal(currentId, served.Plan.ManifestId);
    }

    [Fact]
    public async Task AManifestWhoseArtifactIsMissing_IsRejected_AndTheCurrentPlanStays()
    {
        var (current, currentId) = await _fixture.Pipeline("p").BuildAsync("p", seq: 1);
        var (next, nextId) = await _fixture.Pipeline("p", k: 3).BuildAsync("p", seq: 2);
        File.Delete(_fixture.Store.GetBlobPath(next.Artifacts.Pipeline));
        await using var engine = new RecEngine(_fixture.Environment);
        Assert.True((await engine.LoadManifestAsync(current, currentId, _fixture.Store)).Applied);

        var result = await engine.LoadManifestAsync(next, nextId, _fixture.Store);

        Assert.Equal(ManifestLoadStatus.Rejected, result.Status);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(("ARTIFACT_MISSING", "manifest:" + nextId, "/artifacts/pipeline"), (error.Code, error.Artifact, error.Pointer));
        Assert.StartsWith($"ARTIFACT_MISSING manifest:{nextId}/artifacts/pipeline: Artifact '{next.Artifacts.Pipeline}' is not in the store.", result.Reason);
        Assert.Equal((1L, currentId), (result.ServedSequence, result.ServedManifestId));
        Assert.Equal(currentId, (await engine.ExecuteAsync(ReleaseFixture.Request("p"))).ManifestId);
    }

    [Fact]
    public async Task EachOutcome_IsLoggedAndCounted()
    {
        // A pipeline id of its own: the meter listener is process-wide.
        const string Pipeline = "telemetry_p";
        var (current, currentId) = await _fixture.Pipeline(Pipeline).BuildAsync(Pipeline, seq: 1);
        var (next, nextId) = await _fixture.Pipeline(Pipeline, k: 3).BuildAsync(Pipeline, seq: 2);
        var incompatible = ReleaseFixture.With(next, seq: 3, minAcceptedSeq: 3, requires: ["Missing@1"]);
        var incompatibleId = await _fixture.PutAsync(incompatible);
        var outcomes = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == StrataRecTelemetry.Name && instrument.Name == StrataRecMetrics.Names.PlanLoads)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? pipeline = null, outcome = null;
            foreach (var tag in tags)
            {
                pipeline = tag.Key == StrataRecTags.Pipeline ? (string?)tag.Value : pipeline;
                outcome = tag.Key == StrataRecTags.Outcome ? (string?)tag.Value : outcome;
            }

            if (pipeline == Pipeline)
            {
                lock (outcomes)
                {
                    outcomes.Add(outcome!);
                }
            }
        });
        listener.Start();
        var log = new ReleaseLog();
        using var loggers = LoggerFactory.Create(builder => builder.AddProvider(log));
        await using var engine = new RecEngine(_fixture.Environment, loggerFactory: loggers);

        Assert.True((await engine.LoadManifestAsync(current, currentId, _fixture.Store)).Applied);
        File.Move(_fixture.Store.GetBlobPath(next.Artifacts.Pipeline), Path.Combine(_fixture.StoreRoot, "aside"));
        Assert.Equal(ManifestLoadStatus.Rejected, (await engine.LoadManifestAsync(next, nextId, _fixture.Store)).Status);
        File.Move(Path.Combine(_fixture.StoreRoot, "aside"), _fixture.Store.GetBlobPath(next.Artifacts.Pipeline));
        Assert.Equal(ManifestLoadStatus.Stale, (await engine.LoadManifestAsync(current, currentId, _fixture.Store)).Status);
        Assert.Equal(ManifestLoadStatus.Incompatible, (await engine.LoadManifestAsync(incompatible, incompatibleId, _fixture.Store)).Status);
        Assert.Equal(ManifestLoadStatus.Revoked, (await engine.LoadManifestAsync(next, nextId, _fixture.Store)).Status);

        Assert.Equal(new[] { "applied", "artifacts_failed", "stale", "incompatible", "revoked" }, outcomes);
        var events = log.Entries.Where(e => e.Category == typeof(RecEngine).FullName).Select(e => (e.EventId.Id, e.Level)).ToList();
        Assert.Equal(new[] { (1001, LogLevel.Information), (1008, LogLevel.Error), (1004, LogLevel.Information), (1006, LogLevel.Error), (1007, LogLevel.Warning) }, events);
        var incompatibleEvent = log.Entries.Single(e => e.EventId.Id == 1006).Message;
        Assert.Equal($"Manifest '{incompatibleId}' (sequence 3) for pipeline '{Pipeline}' is incompatible: this binary lacks Missing@1; the current plan stays", incompatibleEvent);
    }

    [Fact]
    public async Task ADisposedEngine_TakesNoManifest()
    {
        var (manifest, id) = await _fixture.Pipeline("p").BuildAsync("p", seq: 1);
        var engine = new RecEngine(_fixture.Environment);
        await engine.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.LoadManifestAsync(manifest, id, _fixture.Store));
    }
}
