using StrataRec.Core.Keys;
using StrataRec.Features.Connectors;
using StrataRec.Pipeline.Replay;

namespace StrataRec.UnitTests.Pipeline;

public class ReplayKeysTests
{
    private static readonly EntityKeyLayout ItemCountry = new(
    [
        new EntityKeyPart("col.entity_id", EntityKeyType.Int64, false),
        new EntityKeyPart("ctx.request.country", EntityKeyType.String, true),
    ]);

    [Fact]
    public void KeysOfSeveralParts_AreWrittenAsArrays_AndReadBackAsTuples()
    {
        var batch = EntityKeyBatch.Create(ItemCountry, 3, new[] { EntityKey.FromInt64(7), EntityKey.FromInt64(-1), EntityKey.FromInt64(8) }, new[] { EntityKey.FromString("US") });

        var json = ReplayJson.Keys(batch);
        var keys = ReplayJson.KeyTuples(json, "/runs/0/feature_calls/0/keys", out var parts);

        Assert.Equal("""[[7,"US"],[-1,"US"],[8,"US"]]""", json.GetRawText());
        Assert.Equal(2, parts);
        Assert.Equal(
            new[] { EntityKey.FromInt64(7), EntityKey.FromString("US"), EntityKey.FromInt64(-1), EntityKey.FromString("US"), EntityKey.FromInt64(8), EntityKey.FromString("US") },
            keys);
    }

    [Fact]
    public void OnePartKeys_AreTheirValues_AsBeforeKeysHadParts()
    {
        var json = ReplayJson.Keys(new EntityKeyBatch(new[] { EntityKey.FromString("a"), EntityKey.FromString("b") }));

        Assert.Equal("""["a","b"]""", json.GetRawText());
        Assert.Equal(new[] { EntityKey.FromString("a"), EntityKey.FromString("b") }, ReplayJson.KeyTuples(json, "/keys", out var parts));
        Assert.Equal(1, parts);
    }

    [Theory]
    [InlineData("""[[1,"US"],[2]]""", "/keys/1")]
    [InlineData("""[[1,"US"],3]""", "/keys/1")]
    [InlineData("""[[1,"US"],[2,true]]""", "/keys/1/1")]
    [InlineData("""[[1,2,3,4,5,6,7,8,9]]""", "/keys/0")]
    [InlineData("""[[]]""", "/keys/0")]
    public void AKeyOfAnotherShape_IsAnErrorAtItsPointer(string keys, string pointer)
    {
        using var document = System.Text.Json.JsonDocument.Parse(keys);

        var error = Assert.Throws<ReplayBundleException>(() => ReplayJson.KeyTuples(document.RootElement, "/keys", out _));

        Assert.Equal(pointer, error.Pointer);
    }
}
