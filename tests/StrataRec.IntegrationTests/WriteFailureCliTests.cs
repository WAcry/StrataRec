using StrataRec.CommandLine;
using StrataRec.Hosting.Manifests;

namespace StrataRec.IntegrationTests;

/// <summary>
/// What the command line cannot write is a diagnostic at the option that names it, with what the system said and a hint,
/// not an exception and its stack (review 43): the store of build, publish and revoke, the files of build --manifest and
/// run --record; and a store whose pointers cannot be listed is not one that publishes nothing.
/// </summary>
public sealed class WriteFailureCliTests
{
    [Fact]
    public async Task Build_Publish_AndRevoke_SayTheStoreCannotBeWritten()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return; // permissions
        }

        await StoreUnwritableAsync();
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static async Task StoreUnwritableAsync()
    {

        var root = ManifestStores.Workspace();
        var store = ManifestStores.NewStore();
        var (first, _) = await ManifestStores.BuildAsync(root, store);
        Assert.Equal(0, (await Cli("publish", "-s", store, first)).Code);

        // A newer manifest to publish, written outside the store; then a configuration the store has no artifacts for.
        var elsewhere = Directory.CreateTempSubdirectory("stratarec-out-").FullName;
        var next = Path.Combine(elsewhere, "next.json");
        SetFinalTopK(root, 7);
        Assert.Equal(0, (await Cli("build", "-c", root, "-s", store, "-m", next)).Code);
        SetFinalTopK(root, 6);

        SetWritable(store, false);
        try
        {
            string[][] commands =
            [
                ["build", "-c", root, "-s", store, "-m", Path.Combine(elsewhere, "other.json")],
                ["publish", "-s", store, next],
                ["revoke", "-s", store, "-p", "home_feed", "exp_mmr_diversity"],
            ];
            foreach (var command in commands)
            {
                var (code, _, errors) = await Cli(command);
                Assert.True(code == 1, command[0] + ": " + errors);
                Assert.Contains("error STORE_UNWRITABLE --store: ", errors);
                Assert.Contains("hint: check that this process may write the store", errors);
                Assert.DoesNotContain("Unhandled", errors);
                Assert.DoesNotContain("   at ", errors);
            }
        }
        finally
        {
            SetWritable(store, true);
        }

        Assert.Equal(first, await new FileSystemManifestSource(store).GetManifestIdAsync("home_feed", "local", CancellationToken.None));
    }

    [Fact]
    public async Task Build_AndRun_CreateTheDirectoryOfTheirFile_AndSayWhenItCannotBeWritten()
    {
        var root = ManifestStores.Workspace();
        var store = ManifestStores.NewStore();
        var scratch = Directory.CreateTempSubdirectory("stratarec-out-").FullName;

        // A directory that does not exist yet is created, as run --record, init and schema --out do.
        var nested = Path.Combine(scratch, "releases", "local", "home_feed.json");
        var (code, _, errors) = await Cli("build", "-c", root, "-s", store, "-m", nested);
        Assert.True(code == 0, errors);
        Assert.True(File.Exists(nested));

        // A file where its directory belongs: said at the option, and run prints the result of the request all the same.
        var blocker = Path.Combine(scratch, "blocker");
        File.WriteAllText(blocker, "x");
        (code, _, errors) = await Cli("build", "-c", root, "-s", store, "-m", Path.Combine(blocker, "m.json"));
        Assert.Equal(1, code);
        Assert.Contains("error OUTPUT_UNWRITABLE --manifest: Cannot write '", errors);
        Assert.Contains("hint: check that this process may write there, or pass another --manifest", errors);
        Assert.DoesNotContain("Unhandled", errors);

        var request = Path.Combine(root, "requests", "user_1001.json");
        (code, var output, errors) = await Cli("run", "-c", root, "-r", request, "--deadline-ms",
            HomeFeedTestHost.UntimedMs.ToString(System.Globalization.CultureInfo.InvariantCulture), "--record", Path.Combine(blocker, "b.json"));
        Assert.Equal(1, code);
        Assert.Contains("error OUTPUT_UNWRITABLE --record: Cannot write '", errors);
        Assert.DoesNotContain("Unhandled", errors);
        Assert.Contains("\"items\"", output);

        var bundle = Path.Combine(scratch, "bundles", "b.json");
        (code, _, errors) = await Cli("run", "-c", root, "-r", request, "--deadline-ms",
            HomeFeedTestHost.UntimedMs.ToString(System.Globalization.CultureInfo.InvariantCulture), "--record", bundle);
        Assert.True(code == 0, errors);
        Assert.True(File.Exists(bundle));
    }

    [Fact]
    public async Task Status_SaysAFileStandsWhereTheDirectoryOfPointersBelongs()
    {
        var store = ManifestStores.NewStore();
        File.WriteAllText(Path.Combine(store, "pointers"), "x");
        string[][] filters = [[], ["-e", "local"], ["-p", "home_feed"], ["-e", "local", "-p", "home_feed"]];
        foreach (var filter in filters)
        {
            var (code, output, errors) = await Cli(["status", "-s", store, .. filter]);
            Assert.True(code == 1, string.Join(' ', filter) + ": " + output + errors);
            Assert.Contains("error POINTER_UNREADABLE", errors);
            Assert.Contains($"'{Path.Combine(store, "pointers")}' is a file where a directory of pointers belongs", errors);
            Assert.DoesNotContain("nothing is published", output);
        }

        // An environment directory that is a file, when it is asked for or listed.
        File.Delete(Path.Combine(store, "pointers"));
        Directory.CreateDirectory(Path.Combine(store, "pointers"));
        File.WriteAllText(Path.Combine(store, "pointers", "local"), "x");
        foreach (var filter in new[] { Array.Empty<string>(), ["-e", "local"] })
        {
            var (code, _, errors) = await Cli(["status", "-s", store, .. filter]);
            Assert.True(code == 1 || filter.Length == 0, errors); // listed, a file is no environment: skipped
            if (filter.Length > 0)
            {
                Assert.Contains("is a file where a directory of pointers belongs", errors);
            }
        }
    }

    private static void SetFinalTopK(string root, int value)
    {
        var path = Path.Combine(root, "pipelines", "home_feed.jsonc");
        var text = File.ReadAllText(path);
        var edited = System.Text.RegularExpressions.Regex.Replace(text, "\"final_top_k\": \\d+,", $"\"final_top_k\": {value},");
        Assert.NotEqual(text, edited);
        File.WriteAllText(path, edited);
    }

    /// <summary>Every directory and file under <paramref name="root"/> made read-only, or writable again.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void SetWritable(string root, bool writable)
    {
        const UnixFileMode Directory = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        const UnixFileMode File = UnixFileMode.UserRead;
        var write = writable ? UnixFileMode.UserWrite : UnixFileMode.None;
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            entry.UnixFileMode = (entry is DirectoryInfo ? Directory : File) | write;
        }

        new DirectoryInfo(root).UnixFileMode = Directory | write;
    }

    private static async Task<(int Code, string Output, string Errors)> Cli(params string[] arguments)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var code = await StrataRecCommandLine.InvokeAsync(arguments, Tool.Environment, output, errors);
        return (code, output.ToString(), errors.ToString());
    }
}
