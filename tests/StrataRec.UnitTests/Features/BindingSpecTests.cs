using System.Text.Json;
using StrataRec.Features.Bindings;

namespace StrataRec.UnitTests.Features;

/// <summary>The one rule for sharing an instance across plans (design 6.4): what is part of a binding's identity.</summary>
public sealed class BindingSpecTests
{
    private static readonly object Factory = new();
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "stratarec-config");

    [Fact]
    public void TheIdentityIsCanonical_SoMemberOrderAndSpacingDoNotMatter()
    {
        var a = Spec("""{ "uri": "m.onnx", "intra_op_threads": 2, "versions": { "1": "a.onnx", "2": "b.onnx" } }""");
        var b = Spec("""{"versions":{"2":"b.onnx","1":"a.onnx"},"intra_op_threads":2.0,"uri":"m.onnx"}""");

        Assert.Equal(a.Identity, b.Identity);
    }

    [Theory]
    [InlineData("""{ "uri": "other.onnx" }""")] // any setting
    [InlineData("""{ "uri": "m.onnx", "added_later": true }""")] // a setting nobody listed
    public void EverySettingIsPartOfTheIdentity(string settings)
    {
        Assert.NotEqual(Spec("""{ "uri": "m.onnx" }""").Identity, Spec(settings).Identity);
    }

    [Fact]
    public void TheSectionNameTypeAndBaseDirectory_ArePartOfTheIdentity()
    {
        var spec = Spec("""{ "uri": "m.onnx" }""");

        Assert.NotEqual(spec.Identity, Spec("""{ "uri": "m.onnx" }""", section: BindingSection.CandidateSources).Identity);
        Assert.NotEqual(spec.Identity, Spec("""{ "uri": "m.onnx" }""", name: "ctr_other").Identity);
        Assert.NotEqual(spec.Identity, Spec("""{ "uri": "m.onnx" }""", type: "local_onnx@2").Identity);
        Assert.NotEqual(spec.Identity, Spec("""{ "uri": "m.onnx" }""", baseDirectory: Path.Combine(Root, "other")).Identity);
    }

    [Fact]
    public void ARelativeBaseDirectoryIsPinned()
    {
        var spec = Spec("{}", baseDirectory: "relative");

        Assert.Equal(Path.GetFullPath("relative"), spec.BaseDirectory);
        Assert.Equal(spec.Identity, Spec("{}", baseDirectory: Path.GetFullPath("relative")).Identity);
    }

    [Theory]
    [InlineData(BindingSection.Models, "/models/ctr~1model", "model 'ctr/model' (local_onnx@1)")]
    [InlineData(BindingSection.CandidateSources, "/candidate_sources/ctr~1model", "candidate source 'ctr/model' (local_onnx@1)")]
    [InlineData(BindingSection.Sources, "/sources/ctr~1model", "feature source 'ctr/model' (local_onnx@1)")]
    public void ThePointerAndLabelNameTheBinding(BindingSection section, string pointer, string label)
    {
        var spec = Spec("{}", section: section, name: "ctr/model");

        Assert.Equal((pointer, label), (spec.Pointer, spec.Label));
    }

    private static BindingSpec Spec(
        string settings, BindingSection section = BindingSection.Models, string name = "ctr_model", string type = "local_onnx@1", string? baseDirectory = null)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in JsonDocument.Parse(settings).RootElement.EnumerateObject())
        {
            values[property.Name] = property.Value.Clone();
        }

        return BindingSpec.Create(section, name, type, values, baseDirectory ?? Root, Factory, "bindings:test");
    }
}
