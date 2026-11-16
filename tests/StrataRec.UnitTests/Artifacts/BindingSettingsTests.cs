using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;

namespace StrataRec.UnitTests.Artifacts;

/// <summary>
/// A binding's own settings bind to the type its factory declares (design 6.4) as strictly, and with the same words, as
/// an operator's config: every mistake is a <see cref="BindingException"/> at its pointer within the binding, with a hint;
/// local files and secret references are typed members that know their pointer; and the schema of the settings is
/// generated from the same type.
/// </summary>
public sealed class BindingSettingsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "stratarec-settings-root");

    [Theory]
    [InlineData("""{ "endpoint": "e", "retires": 3 }""", "/retires", "Unknown key 'retires' in the settings of test@1.", "did you mean 'retries'?")]
    [InlineData("""{ "endpoint": "e", "x": 3 }""", "/x", "Unknown key 'x' in the settings of test@1.", "allowed keys: ")]
    [InlineData("""{ "endpoint": "e", "routes": [{ "nmae": "a" }] }""", "/routes/0/nmae", "Unknown key 'nmae'", "did you mean 'name'?")]
    [InlineData("""{ "endpoint": "e", "retries": "3" }""", "/retries", "'retries' must be an integer, got the string \"3\".", "'stratarec schema bindings'")]
    [InlineData("""{ "endpoint": 7 }""", "/endpoint", "'endpoint' must be a string, got the number 7.", "'stratarec schema bindings'")]
    [InlineData("""{ "retries": 3 }""", "", "'endpoint' is required in the settings of test@1.", "'endpoint': The server to call, host:port.")]
    [InlineData("""{ "endpoint": "e", "routes": [{}] }""", "/routes/0", "'name' is required", "'name': ")]
    [InlineData("""{ "endpoint": "e", "retries": 1e400 }""", "/retries", "The number 1e400 is outside the double-precision range.", "use a smaller magnitude")]
    public void AMistake_IsAnErrorAtItsPointer_WithAHint(string settings, string pointer, string reason, string hint)
    {
        var error = Assert.Throws<BindingException>(() => BindingSettings.Bind<ServerSettings>(Context(settings), "test@1"));

        Assert.Equal(pointer, error.Pointer);
        Assert.Contains(reason, error.Reason, StringComparison.Ordinal);
        Assert.Contains(hint, error.Hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "endpoint": null }""", "/endpoint")]
    [InlineData("""{ "endpoint": "e", "routes": [{ "name": "a" }, null] }""", "/routes/1")]
    public void NullWhereAValueIsRequired_IsAnErrorAtIt(string settings, string pointer)
    {
        var error = Assert.Throws<BindingException>(() => BindingSettings.Bind<ServerSettings>(Context(settings), "test@1"));

        Assert.Equal(pointer, error.Pointer);
        Assert.EndsWith("got null.", error.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NullForARequiredFile_IsAnErrorAtIt()
    {
        var error = Assert.Throws<BindingException>(() => BindingSettings.Bind<Outer>(Context("""{ "nested": { "name": "n" }, "file": null }"""), "test@1"));

        // The converter never sees the null; the serializer's own words named a .NET type.
        Assert.Equal(("/file", "'file' must not be null."), (error.Pointer, error.Reason));
        Assert.Contains("stratarec schema bindings", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidSettings_Bind_WithTheirDefaults_AndNullAsAbsent()
    {
        var settings = BindingSettings.Bind<ServerSettings>(Context("""{ "endpoint": "e", "password": null, "routes": [{ "name": "a" }] }"""), "test@1");

        Assert.Equal(("e", 2, null), (settings.Endpoint, settings.Retries, settings.Password));
        Assert.Equal("a", Assert.Single(settings.Routes!).Name);
    }

    [Fact]
    public void LocalFilesAndSecretReferences_AnywhereInTheSettings_KnowTheirPointers()
    {
        var settings = BindingSettings.Bind<ServerSettings>(
            Context("""{ "endpoint": "e", "password": "secret://db", "routes": [{ "name": "a" }, { "name": "b/c", "file": "routes/b.json" }] }"""), "test@1");

        Assert.Equal(("db", "/password"), (settings.Password!.Name, settings.Password.Pointer));
        Assert.Null(settings.Routes![0].File);
        Assert.Equal((Path.Combine(Root, "routes", "b.json"), "/routes/1/file"), (settings.Routes[1].File!.Path, settings.Routes[1].File!.Pointer));
    }

    [Theory]
    [InlineData("""{ "endpoint": "e", "routes": [{ "name": "a" }, { "name": "b", "file": "s3://bucket/b.json" }] }""", "/routes/1/file", "is not a local path")]
    [InlineData("""{ "endpoint": "e", "password": "hunter2" }""", "/password", "secret://")]
    public void AWrongFileOrSecret_IsAnErrorAtItsPointer(string settings, string pointer, string text)
    {
        var error = Assert.Throws<BindingException>(() => BindingSettings.Bind<ServerSettings>(Context(settings), "test@1"));

        Assert.Equal(pointer, error.Pointer);
        Assert.Contains(text, error.Reason + " " + error.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", error.Message + error.Reason + error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void AValueBoundOnItsOwn_ReportsBelowItsPointer()
    {
        // A member taken in more than one form is declared as a JsonElement and bound once its form is known.
        var context = Context("""{ "routes": [{ "name": "a" }, { "name": "b", "file": "s3://x" }] }""");
        var routes = context.Settings["routes"];

        var error = Assert.Throws<BindingException>(() => BindingSettings.Bind<List<Route>>(context, routes, "/routes", "'routes' of test@1"));
        var unknown = Assert.Throws<BindingException>(() =>
            BindingSettings.Bind<Route>(context, JsonSerializer.SerializeToElement(new { name = "a", flie = "x" }), "/routes/3", "a route of test@1"));

        Assert.Equal("/routes/1/file", error.Pointer);
        Assert.Equal(("/routes/3/flie", "Unknown key 'flie' in a route of test@1."), (unknown.Pointer, unknown.Reason));
    }

    [Fact]
    public void AKeyTwiceInSettingsBuiltInCode_IsAnErrorAtTheKey()
    {
        var context = Context("""{ "endpoint": "e", "routes": [{ "name": "a", "name": "b" }] }""");

        var error = Assert.Throws<BindingException>(() => BindingSettings.Bind<ServerSettings>(context, "test@1"));

        Assert.Equal(("/routes/0/name", "remove or merge one of the definitions"), (error.Pointer, error.Hint));
    }

    [Fact]
    public void NoSettings_TakesNoKeys()
    {
        BindingSettings.Bind<NoSettings>(Context("{}"), "plain@1");

        var error = Assert.Throws<BindingException>(() => BindingSettings.Bind<NoSettings>(Context("""{ "endpoint": "e" }"""), "plain@1"));

        Assert.Equal(("/endpoint", "it takes no keys"), (error.Pointer, error.Hint));
    }

    [Fact]
    public void AFileOrSecret_ReadOutsideABinding_IsAnError_AndABindingThatFailed_LeavesNoneInProgress()
    {
        Assert.Throws<BindingException>(() => BindingSettings.Bind<ServerSettings>(Context("""{ "endpoint": "e", "password": "x" }"""), "test@1"));

        var error = Assert.Throws<InvalidOperationException>(() => JsonSerializer.Deserialize<Route>("""{ "name": "a", "file": "f" }""", BindingSettings.Options));

        Assert.Contains("BindingSettings.Bind", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FilesResolveAgainstTheirOwnBinding_WhenOneIsBoundInsideAnother()
    {
        // A converter binds a part with another binding's context mid-way; the outer bind's files after it are still its own.
        var outer = Context("""{ "nested": { "name": "n", "file": "b.json" }, "file": "a.json" }""", Path.Combine(Root, "outer"));

        var settings = BindingSettings.Bind<Outer>(outer, "outer@1");

        Assert.Equal((Path.Combine(Root, "inner", "b.json"), "/x/file"), (settings.Nested.Route.File!.Path, settings.Nested.Route.File.Pointer));
        Assert.Equal((Path.Combine(Root, "outer", "a.json"), "/file"), (settings.File.Path, settings.File.Pointer));
    }

    [Theory]
    [InlineData("""{ "a": 1 }""", 7, "/a")]
    [InlineData("""{ "a/b": [10, { "c~d": [7] }] }""", 23, "/a~1b/1/c~0d")]
    [InlineData("""{ "a/b": [10, { "c~d": [7] }] }""", 24, "/a~1b/1/c~0d/0")]
    [InlineData("""{ "a": 1 }""", 0, "")]
    [InlineData("""[1, 2, [3]]""", 8, "/2/0")]
    [InlineData("""{ "a": {}, "b": 2 }""", 16, "/b")]
    public void PointerAt_NamesTheValueAtAnOffset(string json, int offset, string pointer)
    {
        Assert.Equal(pointer, BindingSettings.PointerAt(System.Text.Encoding.UTF8.GetBytes(json), offset));
    }

    [Fact]
    public void TheSchemaOfASettingsType_IsWhatBinds()
    {
        var schema = ArtifactSchemas.Artifact("bindings", [], [new BindingSchemaSource(BindingKind.Model, "test@1", typeof(ServerSettings))]);
        var then = schema["properties"]!["models"]!["additionalProperties"]!["allOf"]![0]!["then"]!;

        Assert.Equal("test@1", schema["properties"]!["models"]!["additionalProperties"]!["allOf"]![0]!["if"]!["properties"]!["client"]!["const"]!.GetValue<string>());
        Assert.False(then["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(["endpoint"], then["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("The server to call, host:port.", then["properties"]!["endpoint"]!["description"]!.GetValue<string>());
        Assert.Equal("^secret://.", then["properties"]!["password"]!["pattern"]!.GetValue<string>());
        Assert.Equal(JsonValueKind.Object, then["properties"]!["client"]!.GetValueKind()); // the framework's members stay allowed
        Assert.NotNull(then["properties"]!["timeout_ms"]);
    }

    [Fact]
    public void ASettingNamedLikeAFrameworkMember_IsRejectedWhenTheSchemaIsMade()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ArtifactSchemas.Artifact("bindings", [], [new BindingSchemaSource(BindingKind.Model, "bad@1", typeof(Clashing))]));

        Assert.Contains("timeout_ms", error.Message, StringComparison.Ordinal);
    }

    private static BindingContext Context(string settings, string? directory = null) =>
        new("b", JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(settings)!, directory ?? Root, secrets: null);

    private sealed class ServerSettings
    {
        [Description("The server to call, host:port.")]
        public required string Endpoint { get; init; }

        public int Retries { get; init; } = 2;

        public SecretReference? Password { get; init; }

        public List<Route>? Routes { get; init; }
    }

    private sealed class Route
    {
        [Description("The route's name.")]
        public required string Name { get; init; }

        public LocalFile? File { get; init; }
    }

    private sealed class Outer
    {
        public required Nested Nested { get; init; }

        public required LocalFile File { get; init; }
    }

    [JsonConverter(typeof(NestedConverter))]
    private sealed class Nested
    {
        public required Route Route { get; init; }
    }

    /// <summary>Binds its value as a route of another binding, whose base directory is <c>inner</c>.</summary>
    private sealed class NestedConverter : JsonConverter<Nested>
    {
        public override Nested Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var inner = Context("{}", Path.Combine(Root, "inner"));
            return new Nested { Route = BindingSettings.Bind<Route>(inner, JsonElement.ParseValue(ref reader), "/x", "the inner binding") };
        }

        public override void Write(Utf8JsonWriter writer, Nested value, JsonSerializerOptions options) => throw new NotSupportedException();
    }

    private sealed class Clashing
    {
        public int TimeoutMs { get; init; }
    }
}
