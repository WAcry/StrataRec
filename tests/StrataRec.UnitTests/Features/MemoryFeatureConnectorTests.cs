using System.Text.Json;
using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features;

/// <summary>
/// <c>memory@1</c> looks int64 keys up in a <c>long</c> index built at load time; it must find exactly the records a
/// lookup by the key's decimal string finds, and string keys keep matching property names verbatim.
/// </summary>
public class MemoryFeatureConnectorTests
{
    private static readonly CompiledFieldBinding[] Fields = { new("x", 0, ColumnType.Float64) };

    private static double?[] Fetch(string json, EntityKeyBatch batch)
    {
        using var document = JsonDocument.Parse(json);
        var connector = new MemoryFeatureConnector(document.RootElement);
        var staging = new StagingBuffer(Fields, batch.KeyCount, new RequestMemory());
        var status = connector.FetchAsync(batch, Fields, staging, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        Assert.Equal(FetchOutcome.Ok, status.Outcome);
        var values = (ScalarColumn<double>)staging.Field(0);
        return Enumerable.Range(0, batch.KeyCount).Select(i => staging.HasValue(0, i) ? values[i] : (double?)null).ToArray();
    }

    private static EntityKeyBatch Many(params EntityKey[] keys) => new() { Shape = KeyShape.Many, CandidateKeys = keys };

    [Fact]
    public void Many_Int64KeysMatchCanonicalDecimalNamesOnly_StringKeysMatchVerbatim()
    {
        const string Data = """
            { "7": { "x": 1.5 }, "007": { "x": 2.5 }, "+8": { "x": 3.5 }, "-3": { "x": 4.5 }, "abc": { "x": 5.5 },
              "9223372036854775807": { "x": 6.5 }, "-9223372036854775808": { "x": 7.5 }, "-0": { "x": 8.5 }, " 9": { "x": 9.5 } }
            """;

        var values = Fetch(Data, Many(
            EntityKey.FromInt64(7), EntityKey.FromInt64(8), EntityKey.FromInt64(-3), EntityKey.FromInt64(long.MaxValue),
            EntityKey.FromInt64(long.MinValue), EntityKey.FromInt64(0), EntityKey.FromInt64(9),
            EntityKey.FromString("007"), EntityKey.FromString("+8"), EntityKey.FromString("abc"), EntityKey.FromString("7"),
            EntityKey.FromString("-0"), EntityKey.FromString("8")));

        Assert.Equal(new double?[] { 1.5, null, 4.5, 6.5, 7.5, null, null, 2.5, 3.5, 5.5, 1.5, 8.5, null }, values);
    }

    [Fact]
    public void Single_ByInt64AndStringContextKey()
    {
        const string Data = """{ "1001": { "x": 1.0 }, "anon": { "x": 2.0 } }""";

        Assert.Equal(new double?[] { 1.0 }, Fetch(Data, new EntityKeyBatch { Shape = KeyShape.Single, ContextKey = EntityKey.FromInt64(1001) }));
        Assert.Equal(new double?[] { 2.0 }, Fetch(Data, new EntityKeyBatch { Shape = KeyShape.Single, ContextKey = EntityKey.FromString("anon") }));
        Assert.Equal(new double?[] { null }, Fetch(Data, new EntityKeyBatch { Shape = KeyShape.Single, ContextKey = EntityKey.FromInt64(1002) }));
    }

    [Fact]
    public void Cross_CandidateKeysOfEitherType()
    {
        const string Data = """
            { "1001": { "7": { "x": 1.0 }, "07": { "x": 2.0 }, "k": { "x": 3.0 }, "8": 42 },
              "u":    { "7": { "x": 4.0 } } }
            """;
        EntityKey[] candidates = { EntityKey.FromInt64(7), EntityKey.FromInt64(70), EntityKey.FromString("07"), EntityKey.FromString("k"), EntityKey.FromInt64(8) };

        var byUser = Fetch(Data, new EntityKeyBatch { Shape = KeyShape.Cross, ContextKey = EntityKey.FromInt64(1001), CandidateKeys = candidates });
        var byName = Fetch(Data, new EntityKeyBatch { Shape = KeyShape.Cross, ContextKey = EntityKey.FromString("u"), CandidateKeys = candidates });

        Assert.Equal(new double?[] { 1.0, null, 2.0, 3.0, null }, byUser); // "8" is not a record: missing
        Assert.Equal(new double?[] { 4.0, null, null, null, null }, byName);
    }

    [Fact]
    public void DuplicateNames_TheLastOneWins_LikeJsonElement()
    {
        const string Data = """{ "7": { "x": 1.0 }, "7": { "x": 2.0 }, "1": { "5": { "x": 3.0 }, "5": { "x": 4.0 } } }""";

        Assert.Equal(new double?[] { 2.0, 2.0 }, Fetch(Data, Many(EntityKey.FromInt64(7), EntityKey.FromString("7"))));
        Assert.Equal(new double?[] { 4.0, 4.0 }, Fetch(Data, new EntityKeyBatch
        {
            Shape = KeyShape.Cross, ContextKey = EntityKey.FromInt64(1), CandidateKeys = new[] { EntityKey.FromInt64(5), EntityKey.FromString("5") },
        }));
    }
}
