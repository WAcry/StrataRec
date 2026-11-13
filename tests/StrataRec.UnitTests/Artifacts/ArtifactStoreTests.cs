using System.Text;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Storage;

namespace StrataRec.UnitTests.Artifacts;

/// <summary>Behaviour every <see cref="IArtifactStore"/> must have; run once per implementation.</summary>
public abstract class ArtifactStoreContractTests
{
    protected const string Pipeline = """
        {
          "pipeline_id": "home_feed",
          "nodes": { "fusion": { "order": 400, "op": "CompositeMultiplierScorer@1" } },
          "params": { "final_top_k": 50 }
        }
        """;

    protected abstract IArtifactStore Store { get; }

    [Fact]
    public async Task Put_ReturnsContentIdAndStoresCanonicalBytes()
    {
        var artifact = Jsonc.Parse(Pipeline)!;

        var id = await Store.PutAsync(artifact);
        var bytes = await Store.TryGetAsync(id);

        Assert.Equal(ContentHash.Compute(artifact), id);
        Assert.Equal(JsonCanonicalizer.Canonicalize(artifact), bytes);
    }

    [Fact]
    public async Task Put_IsIdempotentAcrossFormatting()
    {
        var first = await Store.PutAsync(Jsonc.Parse(Pipeline)!);
        var second = await Store.PutAsync(Jsonc.Parse("""
            // same content, different order and spelling
            { "params": { "final_top_k": 5e1 }, "nodes": { "fusion": { "op": "CompositeMultiplierScorer@1", "order": 400.0 } }, "pipeline_id": "home_feed", }
            """)!);

        Assert.Equal(first, second);
        Assert.Equal(1, CountBlobs());
    }

    [Fact]
    public async Task TryGet_UnknownId_ReturnsNull()
    {
        Assert.Null(await Store.TryGetAsync(ContentHash.Compute(JsonValue.Create("never stored"))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha256:abc")]
    [InlineData("sha256:../../../etc/passwd")]
    [InlineData("SHA256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a")]
    public async Task TryGet_MalformedId_Throws(string id)
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(async () => await Store.TryGetAsync(id));

        Assert.Equal("contentId", ex.ParamName);
    }

    [Fact]
    public async Task Arguments_AreValidated()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await Store.TryGetAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await Store.PutAsync(null!));
    }

    [Fact]
    public async Task Put_NonCanonicalizableArtifact_Throws()
    {
        await Assert.ThrowsAsync<JsonCanonicalizationException>(
            async () => await Store.PutAsync(new JsonObject { ["x"] = double.NaN }));
        Assert.Equal(0, CountBlobs());
    }

    [Fact]
    public async Task Put_DoesNotMutateArtifact()
    {
        var artifact = Jsonc.Parse(Pipeline)!;
        var before = artifact.ToJsonString();

        await Store.PutAsync(artifact);

        Assert.Equal(before, artifact.ToJsonString());
    }

    [Fact]
    public async Task TryGet_ReturnsCallerOwnedArray()
    {
        var id = await Store.PutAsync(Jsonc.Parse(Pipeline)!);
        var first = (await Store.TryGetAsync(id))!;

        first[0] = (byte)'X';
        var second = (await Store.TryGetAsync(id))!;

        Assert.Equal((byte)'{', second[0]);
    }

    [Fact]
    public async Task CanceledToken_IsHonored()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var id = ContentHash.Compute(new JsonObject());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Store.PutAsync(new JsonObject(), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Store.TryGetAsync(id, cts.Token));
    }

    [Fact]
    public async Task ConcurrentPuts_AreSafe()
    {
        var documents = Enumerable.Range(0, 8).Select(i => new JsonObject { ["i"] = i }).ToArray();

        var tasks = Enumerable.Range(0, 64)
            .Select(n => Task.Run(async () => await Store.PutAsync(documents[n % documents.Length].DeepClone())))
            .ToArray();
        var ids = await Task.WhenAll(tasks);

        Assert.Equal(documents.Length, ids.Distinct().Count());
        Assert.Equal(documents.Length, CountBlobs());
        for (var n = 0; n < ids.Length; n++)
        {
            Assert.Equal(ContentHash.Compute(documents[n % documents.Length]), ids[n]);
            var bytes = await Store.TryGetAsync(ids[n]);
            Assert.Equal(JsonCanonicalizer.Canonicalize(documents[n % documents.Length]), bytes);
        }
    }

    protected abstract int CountBlobs();
}

public sealed class InMemoryArtifactStoreTests : ArtifactStoreContractTests
{
    private readonly InMemoryArtifactStore _store = new();

    protected override IArtifactStore Store => _store;

    protected override int CountBlobs() => _store.Count;
}

public sealed class FileSystemArtifactStoreTests : ArtifactStoreContractTests, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stratarec-tests", Guid.NewGuid().ToString("N"));
    private readonly FileSystemArtifactStore _store;

    public FileSystemArtifactStoreTests() => _store = new FileSystemArtifactStore(_root);

    protected override IArtifactStore Store => _store;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    protected override int CountBlobs() =>
        Directory.Exists(_store.BlobDirectory) ? Directory.GetFiles(_store.BlobDirectory).Length : 0;

    [Fact]
    public async Task Put_UsesBlobsSha256Layout_AndLeavesNoTemporaryFiles()
    {
        var id = await _store.PutAsync(Jsonc.Parse(Pipeline)!);

        var expectedPath = Path.Combine(_root, "blobs", "sha256", id["sha256:".Length..]);
        Assert.Equal(expectedPath, _store.GetBlobPath(id));
        Assert.Equal(Path.Combine(_root, "blobs", "sha256"), _store.BlobDirectory);
        Assert.True(File.Exists(expectedPath));
        Assert.Equal(new[] { expectedPath }, Directory.GetFiles(_store.BlobDirectory));
        Assert.Equal(JsonCanonicalizer.Canonicalize(Jsonc.Parse(Pipeline)), await File.ReadAllBytesAsync(expectedPath));
    }

    [Fact]
    public async Task TryGet_CorruptedBlob_ThrowsIntegrityException()
    {
        var id = await _store.PutAsync(Jsonc.Parse(Pipeline)!);
        var path = _store.GetBlobPath(id);
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[^2] ^= 0x01; // same length, different content
        await File.WriteAllBytesAsync(path, bytes);

        var ex = await Assert.ThrowsAsync<ArtifactIntegrityException>(async () => await _store.TryGetAsync(id));

        Assert.Equal(id, ex.ExpectedContentId);
        Assert.Equal(ContentHash.FromCanonicalBytes(bytes), ex.ActualContentId);
        Assert.Equal(path, ex.Location);
        Assert.Contains(id, ex.Message);
    }

    [Fact]
    public async Task Put_RepairsDamagedBlob()
    {
        var artifact = Jsonc.Parse(Pipeline)!;
        var id = await _store.PutAsync(artifact);
        var path = _store.GetBlobPath(id);
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("{\"truncated"));

        Assert.Equal(id, await _store.PutAsync(artifact));

        Assert.Equal(JsonCanonicalizer.Canonicalize(artifact), await _store.TryGetAsync(id));
        Assert.Equal(1, CountBlobs());
    }

    [Fact]
    public async Task StoresSharingARoot_SeeEachOthersBlobs()
    {
        var id = await _store.PutAsync(Jsonc.Parse(Pipeline)!);

        var other = new FileSystemArtifactStore(_root);

        Assert.NotNull(await other.TryGetAsync(id));
    }

    [Fact]
    public async Task TryGet_BeforeAnyPut_ReturnsNull()
    {
        Assert.False(Directory.Exists(_store.BlobDirectory));
        Assert.Null(await _store.TryGetAsync(ContentHash.Compute(new JsonObject())));
    }

    [Fact]
    public void Constructor_ValidatesRoot()
    {
        Assert.Throws<ArgumentNullException>(() => new FileSystemArtifactStore(null!));
        Assert.Throws<ArgumentException>(() => new FileSystemArtifactStore("  "));
        Assert.True(Path.IsPathFullyQualified(new FileSystemArtifactStore("relative/dir").RootDirectory));
    }

    [Fact]
    public void GetBlobPath_RejectsMalformedIds()
    {
        Assert.Throws<ArgumentException>(() => _store.GetBlobPath("sha256:../../x"));
    }
}
