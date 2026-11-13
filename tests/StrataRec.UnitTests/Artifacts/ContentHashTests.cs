using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;

namespace StrataRec.UnitTests.Artifacts;

public class ContentHashTests
{
    private const string EmptyObjectId = "sha256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a";

    [Fact]
    public void Compute_HashesCanonicalBytes()
    {
        Assert.Equal(EmptyObjectId, ContentHash.Compute(new JsonObject()));
        Assert.Equal(EmptyObjectId, ContentHash.Compute(Jsonc.Parse("{ /* empty */ }")));
    }

    [Fact]
    public void FromCanonicalBytes_IsSha256OfBytes()
    {
        Assert.Equal(
            "sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            ContentHash.FromCanonicalBytes("abc"u8));
    }

    [Fact]
    public void Compute_IsIndependentOfFormattingAndKeyOrder()
    {
        var a = Jsonc.Parse("""{"nodes": {"b": {"order": 2}, "a": {"order": 1.0}}}""");
        var b = Jsonc.Parse("""
            {
              // same content
              "nodes": { "a": { "order": 1 }, "b": { "order": 2, }, },
            }
            """);

        Assert.Equal(ContentHash.Compute(a), ContentHash.Compute(b));
        Assert.NotEqual(ContentHash.Compute(a), ContentHash.Compute(Jsonc.Parse("""{"nodes": {"a": {"order": 1}}}""")));
    }

    [Fact]
    public void Compute_ResultIsValidLowercaseId()
    {
        var id = ContentHash.Compute(JsonValue.Create("x"));

        Assert.True(ContentHash.IsValid(id));
        Assert.Equal(ContentHash.ContentIdLength, id.Length);
        Assert.Equal(id.ToLowerInvariant(), id);
    }

    [Fact]
    public void TryGetHexDigest_ReturnsDigest()
    {
        Assert.True(ContentHash.TryGetHexDigest(EmptyObjectId, out var hex));
        Assert.Equal(EmptyObjectId["sha256:".Length..], hex);
        Assert.Equal(hex, ContentHash.GetHexDigest(EmptyObjectId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a")]
    [InlineData("SHA256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a")]
    [InlineData("sha256:44136FA355B3678A1146AD16F7E8649E94FB4FC21FE77E8310C060F61CAAFF8A")]
    [InlineData("sha256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8")]
    [InlineData("sha256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a0")]
    [InlineData("sha256:g4136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a")]
    [InlineData("sha256:../../../../etc/passwd")]
    [InlineData("sha1:44136fa355b3678a1146ad16f7e8649e94fb4fc2")]
    public void InvalidIds_AreRejected(string id)
    {
        Assert.False(ContentHash.IsValid(id));
        Assert.False(ContentHash.TryGetHexDigest(id, out var hex));
        Assert.Null(hex);
        var ex = Assert.Throws<FormatException>(() => ContentHash.GetHexDigest(id));
        Assert.Contains("64 lowercase hexadecimal digits", ex.Message);
    }

    [Fact]
    public void Null_IsInvalid()
    {
        Assert.False(ContentHash.IsValid(null));
        Assert.False(ContentHash.TryGetHexDigest(null, out _));
        Assert.Throws<ArgumentNullException>(() => ContentHash.GetHexDigest(null!));
    }

    [Fact]
    public void Matches_ComparesHashOfBytes()
    {
        Assert.True(ContentHash.Matches(EmptyObjectId, "{}"u8));
        Assert.False(ContentHash.Matches(EmptyObjectId, "{ }"u8));
        Assert.Throws<FormatException>(() => ContentHash.Matches("sha256:nope", "{}"u8));
    }
}
