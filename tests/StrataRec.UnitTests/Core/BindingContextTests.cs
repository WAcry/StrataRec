using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Memory;

namespace StrataRec.UnitTests.Core;

public sealed class BindingContextTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "stratarec-config");

    [Theory]
    [InlineData("models/ctr.bin", "models/ctr.bin")]
    [InlineData("./models/../models/ctr.bin", "models/ctr.bin")]
    public void ARelativePath_ResolvesAgainstTheConfigRoot(string uri, string expected)
    {
        var file = Files($$"""{ "uri": "{{uri}}" }""").Uri!;

        Assert.Equal((Path.GetFullPath(Path.Combine(Root, expected)), "/uri"), (file.Path, file.Pointer));
    }

    [Fact]
    public void AFileUriAndAnAbsolutePath_NameTheSameFile()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "elsewhere", "ctr.bin");
        var files = Files($$"""{ "uri": {{JsonSerializer.Serialize(new Uri(absolute).AbsoluteUri)}}, "path": {{JsonSerializer.Serialize(absolute)}} }""");

        Assert.Equal(absolute, files.Uri!.Path);
        Assert.Equal(absolute, files.Path!.Path);
    }

    [Theory]
    [InlineData("""{ "uri": "s3://bucket/ctr.bin" }""", "is not a local path", "local directory")]
    [InlineData("""{ "uri": 7 }""", "'uri' must be a local path or a file:// URI, got 7.", "file://")]
    [InlineData("""{ "uri": "" }""", "'uri' must be a local path or a file:// URI, got \"\".", "file://")]
    [InlineData("""{ "uri": { "path": "a" } }""", "'uri' must be a local path", "file://")]
    public void AFileSettingThatIsNotLocal_IsAnErrorAtTheSetting_WithAHint(string settings, string reason, string hint)
    {
        var error = Assert.Throws<BindingException>(() => Files(settings));

        Assert.Equal("/uri", error.Pointer);
        Assert.Contains(reason, error.Reason, StringComparison.Ordinal);
        Assert.Contains(hint, error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void ARequiredFileSetting_ThatIsMissing_IsAnErrorWithWhatItMeans()
    {
        var error = Assert.Throws<BindingException>(() => BindingSettings.Bind<RequiredFile>(Context("{}"), "files@1"));

        Assert.Equal(string.Empty, error.Pointer);
        Assert.Contains("'uri' is required", error.Reason, StringComparison.Ordinal);
        Assert.Contains("'uri':", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalPaths_IsTheRuleEveryFileSettingUses()
    {
        Assert.True(LocalPaths.TryResolve("a/b.json", Root, out var path, out var error));
        Assert.Equal((Path.Combine(Root, "a", "b.json"), (string?)null), (path, error));
        Assert.False(LocalPaths.TryResolve("https://example.com/b.json", Root, out _, out error));
        Assert.Equal("'https://example.com/b.json' is not a local path.", error);
    }

    [Fact]
    public void ThePointerOfASettingIsEscaped()
    {
        var error = Assert.Throws<BindingException>(() => BindingSettings.Bind<Dictionary<string, LocalFile>>(Context("""{ "a/b": "s3://x" }"""), "files@1"));

        Assert.Equal("/a~1b", error.Pointer);
    }

    [Fact]
    public void ABaseDirectoryMustBePinned()
    {
        Assert.Throws<ArgumentException>(() => new BindingContext("m", Settings("{}"), "relative/dir", null));
        Assert.Equal(Directory.GetCurrentDirectory(), BindingContext.PinBaseDirectory(null));
        Assert.Equal(Path.GetFullPath("relative/dir"), BindingContext.PinBaseDirectory("relative/dir"));
    }

    [Fact]
    public async Task ASecretReference_IsResolvedByTheHostsStore_AndItsValueNeverShows()
    {
        var store = new Store(("ctr-token", "s3cr3t-value"));
        var reference = Secrets("""{ "credentials": "secret://ctr-token" }""", store).Credentials!;

        Assert.Equal(("ctr-token", "/credentials", "secret://ctr-token"), (reference.Name, reference.Pointer, reference.ToString()));
        Assert.Empty(store.Asked);

        var secret = await reference.ResolveAsync(CancellationToken.None);

        Assert.Equal("s3cr3t-value", secret.Reveal());
        Assert.Equal("[secret]", secret.ToString());
        Assert.Equal("[secret]", $"{secret}");
        Assert.Equal(["ctr-token"], store.Asked);
    }

    [Fact]
    public void ACredentialWrittenAsAValue_IsAnErrorThatDoesNotQuoteIt()
    {
        var error = Assert.Throws<BindingException>(() => Secrets("""{ "credentials": "AKIA-pasted-by-mistake" }"""));

        Assert.Equal("/credentials", error.Pointer);
        Assert.Contains("secret://", error.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("AKIA", error.Message + error.Reason + error.Hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "credentials": "secret://" }""")]
    [InlineData("""{ "credentials": { "user": "u" } }""")]
    [InlineData("""{ "credentials": 7 }""")]
    public void AReferenceWithoutAName_OrNotAString_IsAnError(string settings)
    {
        var error = Assert.Throws<BindingException>(() => Secrets(settings));

        Assert.Equal("/credentials", error.Pointer);
        Assert.Contains("secret://", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void ACredentialInANestedSetting_IsAnErrorAtItsOwnPointer_ThatDoesNotQuoteIt()
    {
        var error = Assert.Throws<BindingException>(() =>
            BindingSettings.Bind<Dictionary<string, List<SecretReference>>>(Context("""{ "a": [], "b/c": ["secret://ok", "hunter2"] }"""), "secrets@1"));

        Assert.Equal("/b~1c/1", error.Pointer);
        Assert.DoesNotContain("hunter2", error.Message + error.Reason + error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void ABindingWithoutTheSetting_HasNoReference()
    {
        Assert.Null(Secrets("{}").Credentials);
        Assert.Null(Secrets("""{ "credentials": null }""").Credentials);
    }

    [Fact]
    public async Task AMissingSecret_OrAMissingStore_IsAnErrorAtTheSetting_WithAHint()
    {
        var missing = await Assert.ThrowsAsync<BindingException>(() =>
            Secrets("""{ "credentials": "secret://nope" }""", new Store()).Credentials!.ResolveAsync(CancellationToken.None).AsTask());
        var noStore = await Assert.ThrowsAsync<BindingException>(() =>
            Secrets("""{ "credentials": "secret://nope" }""").Credentials!.ResolveAsync(CancellationToken.None).AsTask());

        Assert.Equal(("/credentials", "/credentials"), (missing.Pointer, noStore.Pointer));
        Assert.Contains("no secret 'nope'", missing.Reason, StringComparison.Ordinal);
        Assert.Contains("add the secret 'nope'", missing.Hint, StringComparison.Ordinal);
        Assert.Contains("no secret store", noStore.Reason, StringComparison.Ordinal);
        Assert.Contains("ISecretResolver", noStore.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLastReleaseOfARequestsMemory_TellsItsObserverOnce_AfterTheBuffersAreBack()
    {
        var observer = new Observer();
        var memory = new RequestMemory(ended: observer);
        observer.Memory = memory;
        _ = memory.Rent<float>(16);
        memory.Hold(); // a call the request abandoned

        Assert.Equal(1, memory.Release());
        Assert.Equal(0, observer.Ended);

        Assert.Equal(0, memory.Release());
        Assert.Equal((1, true), (observer.Ended, observer.ReturnedWhenTold));
    }

    [Fact]
    public void AnObserverIsToldEvenWhenThePoolThrows()
    {
        var observer = new Observer();
        var memory = new RequestMemory(new ThrowingPool(), observer);
        _ = memory.Rent<float>(16);

        Assert.Equal(0, memory.Release());
        Assert.Equal(1, observer.Ended);
    }

    private static BindingContext Context(string settings, ISecretResolver? secrets = null) => new("ctr_model", Settings(settings), Root, secrets);

    private static FileSettings Files(string settings) => BindingSettings.Bind<FileSettings>(Context(settings), "files@1");

    private static SecretSettings Secrets(string settings, ISecretResolver? secrets = null) =>
        BindingSettings.Bind<SecretSettings>(Context(settings, secrets), "secrets@1");

    private sealed class FileSettings
    {
        public LocalFile? Uri { get; init; }

        public LocalFile? Path { get; init; }
    }

    private sealed class RequiredFile
    {
        /// <summary>The model file.</summary>
        public required LocalFile Uri { get; init; }
    }

    private sealed class SecretSettings
    {
        public SecretReference? Credentials { get; init; }
    }

    private static Dictionary<string, JsonElement> Settings(string json)
    {
        var settings = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in JsonDocument.Parse(json).RootElement.EnumerateObject())
        {
            settings[property.Name] = property.Value.Clone();
        }

        return settings;
    }

    private sealed class Store(params (string Name, string Value)[] secrets) : ISecretResolver
    {
        public List<string> Asked { get; } = new();

        public ValueTask<string?> ResolveAsync(string name, CancellationToken cancellationToken)
        {
            Asked.Add(name);
            foreach (var (n, v) in secrets)
            {
                if (n == name)
                {
                    return ValueTask.FromResult<string?>(v);
                }
            }

            return ValueTask.FromResult<string?>(null);
        }
    }

    private sealed class Observer : IRequestEndObserver
    {
        public RequestMemory? Memory { get; set; }

        public int Ended { get; private set; }

        public bool ReturnedWhenTold { get; private set; }

        public void RequestEnded()
        {
            Ended++;
            ReturnedWhenTold = Memory?.IsReturned == true;
        }
    }

    private sealed class ThrowingPool : IBufferPool
    {
        public T[] Rent<T>(int minimumLength) => new T[minimumLength];

        public void Return<T>(T[] array, bool clearArray) => throw new InvalidOperationException("pool failed");
    }
}
