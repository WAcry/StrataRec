using System.Text.Json;
using StrataRec.Core.Bindings;
using StrataRec.Core.Memory;

namespace StrataRec.UnitTests.Core;

/// <summary>
/// What a factory gets for a binding (design 6.4): local file settings resolved by one rule, credentials only by
/// reference, and secret values that never show in a message.
/// </summary>
public sealed class BindingContextTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "stratarec-config");

    [Theory]
    [InlineData("models/ctr.bin", "models/ctr.bin")]
    [InlineData("./models/../models/ctr.bin", "models/ctr.bin")]
    public void ARelativePath_ResolvesAgainstTheConfigRoot(string uri, string expected)
    {
        var context = Context($$"""{ "uri": "{{uri}}" }""");

        Assert.Equal(Path.GetFullPath(Path.Combine(Root, expected)), context.LocalFile("uri", "model file"));
    }

    [Fact]
    public void AFileUriAndAnAbsolutePath_NameTheSameFile()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "elsewhere", "ctr.bin");
        var context = Context($$"""{ "uri": {{JsonSerializer.Serialize(new Uri(absolute).AbsoluteUri)}}, "path": {{JsonSerializer.Serialize(absolute)}} }""");

        Assert.Equal(absolute, context.LocalFile("uri", "model file"));
        Assert.Equal(absolute, context.LocalFile("path", "model file"));
    }

    [Theory]
    [InlineData("""{ "uri": "s3://bucket/ctr.bin" }""", "is not a local path")]
    [InlineData("""{ "uri": 7 }""", "'uri' is required")]
    [InlineData("""{ }""", "'uri' is required")]
    public void AFileSettingThatIsNotLocal_IsAnErrorAtTheSetting_WithAHint(string settings, string reason)
    {
        var error = Assert.Throws<BindingException>(() => Context(settings).LocalFile("uri", "model file"));

        Assert.Equal("/uri", error.Pointer);
        Assert.Contains(reason, error.Reason, StringComparison.Ordinal);
        Assert.Contains("file://", error.Hint, StringComparison.Ordinal);
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
        var error = Assert.Throws<BindingException>(() => Context("""{ "a/b": "s3://x" }""").LocalFile("a/b", "file"));

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
        var reference = Context("""{ "credentials": "secret://ctr-token" }""", store).SecretReference("credentials")!;

        Assert.Equal(("ctr-token", "/credentials", "secret://ctr-token"), (reference.Name, reference.Pointer, reference.ToString()));
        Assert.Empty(store.Asked); // a reference is read at create time; the store is asked at load

        var secret = await reference.ResolveAsync(CancellationToken.None);

        Assert.Equal("s3cr3t-value", secret.Reveal());
        Assert.Equal("[secret]", secret.ToString());
        Assert.Equal("[secret]", $"{secret}");
        Assert.Equal(["ctr-token"], store.Asked);
    }

    [Fact]
    public void ACredentialWrittenAsAValue_IsAnErrorThatDoesNotQuoteIt()
    {
        var error = Assert.Throws<BindingException>(() => Context("""{ "credentials": "AKIA-pasted-by-mistake" }""").SecretReference("credentials"));

        Assert.Equal("/credentials", error.Pointer);
        Assert.Contains("secret://", error.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("AKIA", error.Message + error.Reason + error.Hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "credentials": "secret://" }""")]
    [InlineData("""{ "credentials": { "user": "u" } }""")]
    public void AReferenceWithoutAName_OrNotAString_IsAnError(string settings)
    {
        var error = Assert.Throws<BindingException>(() => Context(settings).SecretReference("credentials"));

        Assert.Equal("/credentials", error.Pointer);
    }

    [Fact]
    public void ABindingWithoutTheSetting_HasNoReference()
    {
        Assert.Null(Context("{}").SecretReference("credentials"));
    }

    [Fact]
    public async Task AMissingSecret_OrAMissingStore_IsAnErrorAtTheSetting_WithAHint()
    {
        var missing = await Assert.ThrowsAsync<BindingException>(() =>
            Context("""{ "credentials": "secret://nope" }""", new Store()).SecretReference("credentials")!.ResolveAsync(CancellationToken.None).AsTask());
        var noStore = await Assert.ThrowsAsync<BindingException>(() =>
            Context("""{ "credentials": "secret://nope" }""").SecretReference("credentials")!.ResolveAsync(CancellationToken.None).AsTask());

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

        Assert.Equal(1, memory.Release()); // the request returned
        Assert.Equal(0, observer.Ended);

        Assert.Equal(0, memory.Release()); // the call returned
        Assert.Equal((1, true), (observer.Ended, observer.ReturnedWhenTold));
    }

    [Fact]
    public void AnObserverIsToldEvenWhenThePoolThrows()
    {
        var observer = new Observer();
        var memory = new RequestMemory(new ThrowingPool(), observer);
        _ = memory.Rent<float>(16);

        Assert.Equal(0, memory.Release()); // the pool's failure is reported, never thrown (RequestMemoryEventTests)
        Assert.Equal(1, observer.Ended);
    }

    private static BindingContext Context(string settings, ISecretResolver? secrets = null) => new("ctr_model", Settings(settings), Root, secrets);

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
