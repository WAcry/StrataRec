using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Features.Local;

namespace StrataRec.UnitTests.Features.Local;

/// <summary>Tests that change the process's current directory: they run alone, and put it back.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CurrentDirectoryCollection
{
    public const string Name = "Current directory";
}

/// <summary>
/// The base directory of a local table (design 6.6) is pinned when its spec is created: the identity names the
/// directory the source really reads, whatever the current directory is later.
/// </summary>
[Collection(CurrentDirectoryCollection.Name)]
public class LocalTableBaseDirectoryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("dir")]
    public async Task ANullOrRelativeBaseDirectory_IsPinned_ForTheIdentityAndForTheRead(string? baseDirectory)
    {
        // Two directories with the same layout and different data: x/dir/snap holds 10, y/dir/snap holds 99. The specs
        // are created with the current directory in x and then in y (x/dir and y/dir for a null base directory).
        var root = Directory.CreateTempSubdirectory("stratarec-base-").FullName;
        var x = Path.Combine(root, "x");
        var y = Path.Combine(root, "y");
        WriteSnapshot(Path.Combine(x, "dir", "snap"), 10);
        WriteSnapshot(Path.Combine(y, "dir", "snap"), 99);
        var current = baseDirectory is null ? "dir" : string.Empty;
        var original = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(Path.Combine(x, current));
            var first = Spec(baseDirectory);
            Directory.SetCurrentDirectory(Path.Combine(y, current));
            var second = Spec(baseDirectory);

            Assert.Equal(Path.Combine(x, "dir"), first.BaseDirectory);
            Assert.Equal(Path.Combine(y, "dir"), second.BaseDirectory);
            Assert.NotEqual(first.Identity, second.Identity); // so a host never serves one with the other's table
            Assert.Contains(first.BaseDirectory, first.Identity, StringComparison.Ordinal);

            // Read with the current directory in y: each source reads the directory its identity names.
            Assert.Equal(10.0, await Load(first));
            Assert.Equal(99.0, await Load(second));
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
        }
    }

    [Fact]
    public void ASourceCreatedDirectly_KeepsAnAbsolutePath()
    {
        var factory = new SnapshotFilesUpdateSourceFactory();
        var settings = ArtifactSerializer.Deserialize<FeatureSourceBinding>("""{ "bootstrap": { "source": "snapshot_files@1", "uri": "snap" } }""").Bootstrap!.Settings;

        var source = (SnapshotFilesUpdateSource)factory.Create("item_store", settings, baseDirectory: null);

        Assert.True(Path.IsPathFullyQualified(source.ManifestPath));
        Assert.Equal(Path.Combine(Directory.GetCurrentDirectory(), "snap", "snapshot.json"), source.ManifestPath);
        Assert.True(Path.IsPathFullyQualified(new SnapshotFilesUpdateSource("relative/snapshot.json").ManifestPath));
    }

    private static LocalTableSpec Spec(string? baseDirectory)
    {
        var entity = new StrataRec.Features.Catalog.EntityDefinition
        {
            Name = "item", Kind = StrataRec.Features.Catalog.EntityKind.Candidate, CandidateKeyColumn = "entity_id", CandidateKeyType = EntityKeyType.Int64,
        };
        var schema = new LocalTableSchema("item_store", entity.Name, EntityKeyType.Int64, LocalTableMode.Local, new[] { new LocalTableField("score", ColumnType.Float64) });
        var binding = ArtifactSerializer.Deserialize<FeatureSourceBinding>(
            """{ "mode": "local", "max_bytes": "1MiB", "bootstrap": { "source": "snapshot_files@1", "uri": "snap" } }""");
        return LocalTableSpec.Create(
            schema, new LocalFeatureTableOptions { MaxBytes = 1 << 20 }, binding, new SnapshotFilesUpdateSourceFactory(), "item_store", "bindings:test",
            "/sources/item_store", baseDirectory);
    }

    private static async Task<double> Load(LocalTableSpec spec)
    {
        var table = new LocalFeatureTable(spec.Schema, spec.Options);
        await spec.Bootstrap.RunAsync(UpdatePosition.Empty, table.Writer, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        var version = table.Pin();
        return version.Read(0, version.Find(EntityKey.FromInt64(1))).AsFloat64();
    }

    private static void WriteSnapshot(string directory, double score)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "part-0000.jsonl"), $"{{\"key\": 1, \"version\": 1, \"fields\": {{\"score\": {score:F1}}}}}\n");
        File.WriteAllText(Path.Combine(directory, "snapshot.json"), """{ "format": "jsonl", "files": ["part-0000.jsonl"] }""");
    }
}
