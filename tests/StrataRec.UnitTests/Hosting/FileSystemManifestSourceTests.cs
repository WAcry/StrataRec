using StrataRec.Hosting.Manifests;

namespace StrataRec.UnitTests.Hosting;

/// <summary>The pointer files <c>stratarec publish</c> writes and a node reads: the format, atomic replacement, strictness.</summary>
public sealed class FileSystemManifestSourceTests : IDisposable
{
    private const string IdA = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string IdB = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly string _root = Directory.CreateTempSubdirectory("stratarec-pointers-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task NoPointer_IsNull_EvenWithoutTheDirectory()
    {
        var source = new FileSystemManifestSource(Path.Combine(_root, "nothing-here"));

        Assert.Null(await source.GetManifestIdAsync("home_feed", "prod", CancellationToken.None));
    }

    [Fact]
    public async Task APointer_IsWrittenWhereNodesLook_AndReplacedWholly()
    {
        var source = new FileSystemManifestSource(_root);

        await source.WritePointerAsync("home_feed", "prod", IdA, TestContext.Current.CancellationToken);
        var path = Path.Combine(_root, "pointers", "prod", "home_feed.json");
        Assert.Equal(path, source.GetPointerPath("home_feed", "prod"));
        Assert.Equal($"{{ \"manifest\": \"{IdA}\" }}\n", File.ReadAllText(path));
        Assert.Equal(IdA, await source.GetManifestIdAsync("home_feed", "prod", CancellationToken.None));
        Assert.Null(await source.GetManifestIdAsync("home_feed", "staging", CancellationToken.None));

        await source.WritePointerAsync("home_feed", "prod", IdB, TestContext.Current.CancellationToken);
        Assert.Equal(IdB, await source.GetManifestIdAsync("home_feed", "prod", CancellationToken.None));
        Assert.Equal(new[] { "home_feed.json" }, Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName)); // no temporary left
    }

    [Fact]
    public async Task OtherMembers_AreIgnored()
    {
        var source = new FileSystemManifestSource(_root);
        Write(source, $$"""{ "published_by": "ci", "manifest": "{{IdA}}", "at": 1 }""");

        Assert.Equal(IdA, await source.GetManifestIdAsync("p", "e", CancellationToken.None));
    }

    [Theory]
    [InlineData("", "is not valid: ")]
    [InlineData("{ \"manifest\": \"" + IdA + "\", }", "is not valid: ")] // strict JSON: no trailing comma
    [InlineData("// a comment\n{ \"manifest\": \"" + IdA + "\" }", "is not valid: ")]
    [InlineData("[\"" + IdA + "\"]", "it is a JSON array, not an object")]
    [InlineData("{ \"id\": \"" + IdA + "\" }", "it has no string member 'manifest'")]
    [InlineData("{ \"manifest\": 42 }", "it has no string member 'manifest'")]
    [InlineData("{ \"manifest\": \"sha256:ABC\" }", "'sha256:ABC' is not a manifest content id")]
    public async Task AMalformedPointer_IsAnError_NamingTheFile(string text, string reason)
    {
        var source = new FileSystemManifestSource(_root);
        var path = Write(source, text);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => source.GetManifestIdAsync("p", "e", CancellationToken.None).AsTask());

        Assert.StartsWith($"The manifest pointer {path} is not valid: ", error.Message, StringComparison.Ordinal);
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
        Assert.EndsWith("as 'stratarec publish' writes it.", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../escape", "e")]
    [InlineData("a/b", "e")]
    [InlineData("a\\b", "e")]
    [InlineData("p", "..")]
    [InlineData("p", ".")]
    [InlineData("", "e")]
    public void NamesThatAreNotPlainFileNames_AreRefused(string pipelineId, string environment)
    {
        var source = new FileSystemManifestSource(_root);

        Assert.ThrowsAny<ArgumentException>(() => source.GetPointerPath(pipelineId, environment));
    }

    [Fact]
    public async Task OnlyContentIds_ArePointedAt()
    {
        var source = new FileSystemManifestSource(_root);

        await Assert.ThrowsAsync<ArgumentException>(() => source.WritePointerAsync("p", "e", "sha256:nope", TestContext.Current.CancellationToken));
        Assert.Null(await source.GetManifestIdAsync("p", "e", CancellationToken.None));
    }

    private static string Write(FileSystemManifestSource source, string text)
    {
        var path = source.GetPointerPath("p", "e");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }
}
