using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Local;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features.Local;

/// <summary>
/// Binding a feature source with mode <c>local</c> (design 6.6): the settings each mode takes, the diagnostics of a
/// wrong binding (pointer and hint), and the identity that decides when a plan can reuse a loaded table.
/// </summary>
public class LocalBindingTests
{
    /// <summary>A value of every declared binding setting; a setting added to the binding must be added here.</summary>
    private static readonly Dictionary<string, string> SettingSamples = new(StringComparer.Ordinal)
    {
        ["connector"] = "\"memory@1\"",
        ["timeout_ms"] = "10",
        ["max_batch"] = "10",
        ["lookup"] = "\"context\"",
        ["max_bytes"] = "\"1MiB\"",
        ["bootstrap"] = """{ "source": "snapshot_files@1", "uri": "x" }""",
        ["cache"] = """{ "ttl_ms": 1000 }""",
    };

    [Fact]
    public void ModeSettings_ListEveryDeclaredBindingSetting_AndDetectEachOne()
    {
        // The resolver rejects a setting of the other mode only if it knows the setting. A member added to the binding
        // without a mode would be silently accepted by a mode that ignores it.
        var declared = ArtifactSerializer.Options.GetTypeInfo(typeof(FeatureSourceBinding)).Properties
            .Select(p => p.Name).Where(n => n is not "mode" and not "settings").Order(StringComparer.Ordinal).ToArray();
        var listed = FeatureSourceResolver.ModeSettings.Select(s => s.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(declared, listed);
        Assert.Equal(declared, SettingSamples.Keys.Order(StringComparer.Ordinal));

        foreach (var (name, sample) in SettingSamples)
        {
            var binding = ArtifactSerializer.Deserialize<FeatureSourceBinding>($$"""{ "{{name}}": {{sample}} }""");
            var set = FeatureSourceResolver.ModeSettings.Where(s => s.IsSet(binding)).Select(s => s.Name);
            Assert.Equal(new[] { name }, set);
            Assert.Empty(binding.Settings);
        }
    }

    [Fact]
    public void EveryMode_HasTheNameTheBindingParsesBack_AndTheSchemaAllowsExactlyThoseNames()
    {
        // The code handles a mode through switches over every SupplyMode value (a new value fails to compile there);
        // what the compiler cannot see is the binding's spelling of a mode, and the schema's list of them.
        var names = Enum.GetValues<SupplyMode>().Select(FeatureSourceResolver.ModeName).ToArray();
        foreach (var mode in Enum.GetValues<SupplyMode>())
        {
            Assert.True(FeatureSourceResolver.TryParseMode(FeatureSourceResolver.ModeName(mode), out var parsed));
            Assert.Equal(mode, parsed);
        }

        Assert.True(FeatureSourceResolver.TryParseMode(null, out var unset));
        Assert.Equal(SupplyMode.Fetch, unset);
        Assert.False(FeatureSourceResolver.TryParseMode("Local", out _));
        var allowed = typeof(FeatureSourceBinding).GetProperty(nameof(FeatureSourceBinding.Mode))!
            .GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.AllowedValuesAttribute), false)
            .Cast<System.ComponentModel.DataAnnotations.AllowedValuesAttribute>().Single().Values;
        Assert.Equal(names.Order(StringComparer.Ordinal), allowed.Cast<string>().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void LocalBinding_WithoutItsRequiredSettings_ReportsBothAtTheBinding()
    {
        var diagnostics = Build(new FeatureSourceBinding { Mode = "local" });

        var items = Errors(diagnostics);
        Assert.Equal(new[] { ("LOCAL_MAX_BYTES", "/sources/item_store"), ("LOCAL_BOOTSTRAP_MISSING", "/sources/item_store") },
            items.Select(d => (d.Code, d.Pointer)));
        Assert.All(items, d => Assert.Equal("bindings:test", d.Artifact));
        Assert.Contains("\"max_bytes\": \"256MiB\"", items[0].Hint);
        Assert.Contains(SnapshotFilesUpdateSource.Type, items[1].Hint);
    }

    [Fact]
    public void SettingsOfTheOtherMode_AreErrors_AtEachSetting()
    {
        var local = Build(Binding("""
            { "mode": "local", "max_bytes": "1MiB", "bootstrap": { "source": "test_updates@1" },
              "connector": "memory@1", "timeout_ms": 10, "data_file": "items.json" }
            """));
        Assert.Equal(
            new[] { "/sources/item_store/connector", "/sources/item_store/timeout_ms", "/sources/item_store/data_file" },
            Errors(local).Select(d => d.Pointer));
        Assert.All(Errors(local), d => Assert.Equal("SOURCE_SETTING_WRONG_MODE", d.Code));
        Assert.Contains("\"mode\": \"fetch\"", Errors(local)[0].Hint);
        Assert.Contains("inside 'bootstrap'", Errors(local)[2].Hint);

        var fetch = Build(Binding("""{ "connector": "memory@1", "data": {}, "max_bytes": "1MiB", "bootstrap": { "source": "snapshot_files@1" } }"""));
        Assert.Equal(new[] { "/sources/item_store/max_bytes", "/sources/item_store/bootstrap" }, Errors(fetch).Select(d => d.Pointer));
        Assert.All(Errors(fetch), d => Assert.Contains("\"mode\": \"local\"", d.Hint));
    }

    [Theory]
    [InlineData("12MB")]
    [InlineData("0B")]
    [InlineData("1.5GiB")]
    [InlineData("99999999999TiB")]
    public void MaxBytes_ThatIsNotAPositiveSize_IsAnErrorAtTheSetting(string maxBytes)
    {
        var diagnostics = Build(Binding($$"""{ "mode": "local", "max_bytes": "{{maxBytes}}", "bootstrap": { "source": "test_updates@1" } }"""));

        var error = Assert.Single(Errors(diagnostics));
        Assert.Equal(("LOCAL_MAX_BYTES", "/sources/item_store/max_bytes"), (error.Code, error.Pointer));
        Assert.Contains("256MiB", error.Hint);
    }

    [Fact]
    public void MaxBytes_BelowAnEmptyTable_IsAnErrorAtTheSetting()
    {
        var diagnostics = Build(Binding("""{ "mode": "local", "max_bytes": "16B", "bootstrap": { "source": "test_updates@1" } }"""));

        var error = Assert.Single(Errors(diagnostics));
        Assert.Equal(("LOCAL_MAX_BYTES", "/sources/item_store/max_bytes"), (error.Code, error.Pointer));
        Assert.DoesNotContain("Parameter", error.Message);
    }

    [Fact]
    public void Bootstrap_OfAnUnknownType_OrWithBadSettings_IsAnErrorAtTheSetting()
    {
        var unknown = Assert.Single(Errors(Build(Binding("""{ "mode": "local", "max_bytes": "1MiB", "bootstrap": { "source": "snapshot_file@1", "uri": "x" } }"""))));
        Assert.Equal(("UPDATE_SOURCE_UNKNOWN", "/sources/item_store/bootstrap/source"), (unknown.Code, unknown.Pointer));
        Assert.Contains(SnapshotFilesUpdateSource.Type, unknown.Hint);

        var unnamed = Assert.Single(Errors(Build(Binding("""{ "mode": "local", "max_bytes": "1MiB", "bootstrap": { "uri": "x" } }"""))));
        Assert.Equal(("UPDATE_SOURCE_UNKNOWN", "/sources/item_store/bootstrap"), (unnamed.Code, unnamed.Pointer));

        var noUri = Assert.Single(Errors(Build(Binding("""{ "mode": "local", "max_bytes": "1MiB", "bootstrap": { "source": "snapshot_files@1" } }"""))));
        Assert.Equal(("UPDATE_SOURCE_CONFIG", "/sources/item_store/bootstrap"), (noUri.Code, noUri.Pointer));
        Assert.Contains("uri", noUri.Hint);

        // Reported with the binding's other problems, not instead of them.
        var several = Errors(Build(Binding("""{ "mode": "local", "max_bytes": "lots", "bootstrap": { "source": "snapshot_files@1", "url": "x" } }""")));
        Assert.Equal(new[] { ("LOCAL_MAX_BYTES", "/sources/item_store/max_bytes"), ("UPDATE_SOURCE_CONFIG", "/sources/item_store/bootstrap/url") },
            several.Select(d => (d.Code, d.Pointer)));
        Assert.Contains("'uri'", several[1].Hint);
    }

    [Theory]
    [InlineData("file://%")]
    [InlineData("file://[broken")]
    [InlineData("snap\u0000shot")]
    public void Bootstrap_WithAMalformedUri_IsAnErrorAtTheUri_WithAHint(string uri)
    {
        var json = JsonSerializer.Serialize(uri);
        var error = Assert.Single(Errors(Build(Binding($$"""{ "mode": "local", "max_bytes": "1MiB", "bootstrap": { "source": "snapshot_files@1", "uri": {{json}} } }"""))));

        Assert.Equal(("UPDATE_SOURCE_CONFIG", "/sources/item_store/bootstrap/uri"), (error.Code, error.Pointer));
        Assert.Contains("file://", error.Hint);
        Assert.Contains("local path", error.Hint);
    }

    [Fact]
    public void UnknownMode_IsAnErrorAtTheMode()
    {
        var error = Assert.Single(Errors(Build(Binding("""{ "mode": "stream", "max_bytes": "1MiB" }"""))));

        Assert.Equal(("SOURCE_MODE_UNSUPPORTED", "/sources/item_store/mode"), (error.Code, error.Pointer));
        Assert.Contains("'local'", error.Hint);
    }

    [Fact]
    public void LocalTable_ForFeaturesThatDisagreeOnAFieldsType_IsAnErrorAtTheFeature()
    {
        var rig = new FeatureTestRig().LocalSource("item_store", null);
        rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float32, "item_store");
        rig.Physical("item.clicks_exact", FeatureTestRig.Item, ColumnType.Int64, "item_store", field: "clicks");

        var error = Assert.Single(Errors(rig.TryBuild()));

        Assert.Equal(("LOCAL_FIELD_TYPE_CONFLICT", "catalog", "/features/item.clicks_exact/type"), (error.Code, error.Artifact, error.Pointer));
        Assert.NotNull(error.Hint);
    }

    [Fact]
    public void Identity_IgnoresHowFeaturesReadTheTable_AndEverythingElseInTheConfiguration()
    {
        var baseline = Identity(rig =>
        {
            rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float32, "item_store", ScalarValue.FromFloat64(0));
            rig.Physical("item.age", FeatureTestRig.Item, ColumnType.Float32, "item_store");
        });

        // Renamed features, another default, another declaration order, and another source bound differently.
        var same = Identity(rig =>
        {
            rig.Physical("item.age_hours", FeatureTestRig.Item, ColumnType.Float32, "item_store", field: "age");
            rig.Physical("item.clicks_7d", FeatureTestRig.Item, ColumnType.Float32, "item_store", ScalarValue.FromFloat64(5), field: "clicks");
            rig.MemorySource("user_store", "{}", timeoutMs: 7);
            rig.Physical("user.ctr", FeatureTestRig.User, ColumnType.Float32, "user_store");
        });

        Assert.Equal(baseline, same);
        Assert.Matches("\"fields\":\\[\\[\"age\",\"[^\"]+\"\\],\\[\"clicks\",\"[^\"]+\"\\]\\]", baseline); // physical fields, by name
    }

    [Fact]
    public void Identity_ChangesWithTheFields_TheBinding_AndTheBaseDirectory()
    {
        void Features(FeatureTestRig rig) => rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float32, "item_store");
        var baseline = Identity(Features);

        var changed = new[]
        {
            Identity(rig => rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store")),
            Identity(rig =>
            {
                Features(rig);
                rig.Physical("item.age", FeatureTestRig.Item, ColumnType.Float32, "item_store");
            }),
            Identity(Features, maxBytes: "2MiB"),
            Identity(Features, bootstrap: """{ "source": "test_updates@1", "uri": "other" }"""),
            Identity(Features, baseDirectory: "/srv/config-2"),
        };

        Assert.All(changed, identity => Assert.NotEqual(baseline, identity));
        Assert.Equal(changed.Length, changed.Distinct().Count());
    }

    [Theory]
    [InlineData("0B", 0L)]
    [InlineData("512B", 512L)]
    [InlineData("1KiB", 1024L)]
    [InlineData("16MiB", 16L << 20)]
    [InlineData("3GiB", 3L << 30)]
    [InlineData("2TiB", 2L << 40)]
    [InlineData("8388607TiB", 8388607L << 40)]
    public void ByteSize_ReadsAnIntegerAndAnIecUnit(string text, long bytes)
    {
        Assert.True(ByteSize.TryParse(text, out var parsed));
        Assert.Equal(bytes, parsed);
        Assert.Matches(FeatureSourceBinding.ByteSizePattern, text); // the schema pattern accepts what the parser accepts
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("MiB")]
    [InlineData("16")]
    [InlineData("16MB")]
    [InlineData("16 MiB")]
    [InlineData("-1KiB")]
    [InlineData("+1KiB")]
    [InlineData("1.5GiB")]
    [InlineData("16mib")]
    [InlineData("8388608TiB")]
    public void ByteSize_RejectsAnythingElse(string? text)
    {
        Assert.False(ByteSize.TryParse(text, out _));
    }

    private static DiagnosticBag Build(FeatureSourceBinding binding)
    {
        var rig = new FeatureTestRig().Bind("item_store", binding);
        rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float32, "item_store");
        return rig.TryBuild();
    }

    private static FeatureSourceBinding Binding(string json) => ArtifactSerializer.Deserialize<FeatureSourceBinding>(json);

    private static List<ConfigDiagnostic> Errors(DiagnosticBag diagnostics) => diagnostics.Items.Where(d => d.IsError).ToList();

    private static string Identity(
        Action<FeatureTestRig> features, string maxBytes = "1MiB", string bootstrap = """{ "source": "test_updates@1", "uri": "data" }""",
        string baseDirectory = "/srv/config")
    {
        var rig = new FeatureTestRig().Bind("item_store", Binding($$"""{ "mode": "local", "max_bytes": "{{maxBytes}}", "bootstrap": {{bootstrap}} }"""));
        features(rig);
        var diagnostics = rig.TryBuild(baseDirectory);
        Assert.False(diagnostics.HasErrors, string.Join("; ", diagnostics.Items));
        return rig.Plan.Groups.Single(g => g.SourceName == "item_store").Supply.Table!.Spec.Identity;
    }
}
