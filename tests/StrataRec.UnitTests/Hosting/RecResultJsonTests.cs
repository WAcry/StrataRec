using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Experiments.Assignment;
using StrataRec.Hosting;
using StrataRec.Pipeline.Execution;

namespace StrataRec.UnitTests.Hosting;

public sealed class RecResultJsonTests
{
    [Fact]
    public void ASuccess_IsTheItemsAndTheRecord_WithoutErrorMembers()
    {
        var json = Write(Result(new RankedItem(EntityKey.FromInt64(7), 1, new Dictionary<string, ScalarValue> { ["score"] = ScalarValue.FromFloat64(0.5) })));

        Assert.Equal(
            """{"request_id":"req-1","succeeded":true,"served_by":"home_feed","manifest":"m1","experiments":{"assigned":["exp/a"],"triggered":["exp"],"ignored":[{"id":"exp_gone/b","reason":"unknown"}]},"items":[{"rank":1,"id":7,"columns":{"score":0.5}}],"elapsed_ms":1.235}""",
            json);
    }

    [Fact]
    public void AFailure_HasItsKind_Error_AndPointer()
    {
        var result = Result();
        var failed = new RecResult
        {
            RequestId = "req-2", Succeeded = false, ErrorKind = RecErrorKind.InvalidRequest, Error = "Input 'x' is not declared.",
            ErrorPointer = "/inputs/x", Columns = ResultColumns.Empty, Experiments = result.Experiments, ManifestId = string.Empty, ServedBy = "home_feed",
        };

        using var document = JsonDocument.Parse(Write(failed));
        var root = document.RootElement;

        Assert.Equal(
            new[] { "request_id", "succeeded", "error_kind", "error", "error_pointer", "served_by", "manifest", "experiments", "items", "elapsed_ms" },
            root.EnumerateObject().Select(m => m.Name));
        Assert.Equal(("invalid_request", "Input 'x' is not declared.", "/inputs/x"),
            (root.GetProperty("error_kind").GetString(), root.GetProperty("error").GetString(), root.GetProperty("error_pointer").GetString()));
    }

    [Theory]
    [InlineData(RecErrorKind.None, "none")]
    [InlineData(RecErrorKind.UnknownPipeline, "unknown_pipeline")]
    [InlineData(RecErrorKind.InvalidRequest, "invalid_request")]
    [InlineData(RecErrorKind.DeadlineExceeded, "deadline_exceeded")]
    [InlineData(RecErrorKind.Failed, "failed")]
    public void EveryKind_HasAJsonName(RecErrorKind kind, string name) => Assert.Equal(name, RecResultJson.Name(kind));

    [Fact]
    public void AFloat32Value_IsWrittenAsTheFloat_AndADoubleAsTheDouble()
    {
        var json = Write(Result(new RankedItem(EntityKey.FromInt64(7), 1, new Dictionary<string, ScalarValue>
        {
            ["p_click"] = ScalarValue.FromFloat32(0.1f),
            ["widened"] = ScalarValue.FromFloat64(0.1f),
            ["nan"] = ScalarValue.FromFloat32(float.NaN),
        })));

        Assert.Contains("""
            "columns":{"p_click":0.1,"widened":0.10000000149011612,"nan":null}
            """, json, StringComparison.Ordinal);
        Assert.Equal(0.1f, (float)JsonDocument.Parse(json).RootElement.GetProperty("items")[0].GetProperty("columns").GetProperty("p_click").GetDouble());
    }

    [Fact]
    public void AColumnsDeclaredType_DecidesHowItsFloatsPrint_NotWhereTheValueCameFrom()
    {
        var json = Write(Result(
            [("p", ColumnType.Float32), ("d", ColumnType.Float64), ("v", ColumnType.Parse("list<float32,2>"))],
            new RankedItem(EntityKey.FromInt64(7), 1, new Dictionary<string, ScalarValue>
            {
                ["p"] = ScalarValue.FromFloat64(0.1f), // a float32 value held as its double, e.g. a float32 request input
                ["d"] = ScalarValue.FromFloat32(0.1f),
                ["v"] = ScalarValue.FromFloat64List([0.1f, 2f]),
            })));

        Assert.Contains("""
            "columns":{"p":0.1,"d":0.10000000149011612,"v":[0.1,2]}
            """, json, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryKindOfValue_IsWrittenAsItsJson_MissingAndNonFiniteAsNull()
    {
        var columns = new Dictionary<string, ScalarValue>
        {
            ["missing"] = ScalarValue.Missing,
            ["bool"] = ScalarValue.FromBool(false),
            ["int"] = ScalarValue.FromInt64(9007199254740993),
            ["float"] = ScalarValue.FromFloat64(0.1),
            ["tiny"] = ScalarValue.FromFloat64(1e-300),
            ["nan"] = ScalarValue.FromFloat64(double.NaN),
            ["inf"] = ScalarValue.FromFloat64(double.NegativeInfinity),
            ["string"] = ScalarValue.FromString("a \"quoted\" é"),
            ["text"] = ScalarValue.FromString("line\nbreak"),
            ["vector"] = ScalarValue.FromFloat32List(new[] { 0.5f, float.NaN, float.PositiveInfinity }),
            ["ints"] = ScalarValue.FromInt64List(new[] { long.MinValue, 2L }),
            ["strings"] = ScalarValue.FromStringList(new[] { "x", string.Empty }),
            ["empty"] = ScalarValue.FromInt64List(Array.Empty<long>()),
            ["flags"] = ScalarValue.FromBoolList(new[] { true, false }),
            ["weights"] = ScalarValue.FromFloat64List(new[] { 0.1, double.NaN, 1e-300 }),
            ["weights32"] = ScalarValue.FromFloat32List(new[] { 0.1f, float.PositiveInfinity }),
        };

        var json = Write(Result(new RankedItem(EntityKey.FromString("item-\"1\""), 1, columns)), relaxed: true);

        using var document = JsonDocument.Parse(json);
        var item = document.RootElement.GetProperty("items")[0];
        Assert.Equal("item-\"1\"", item.GetProperty("id").GetString());
        var written = item.GetProperty("columns");
        Assert.Equal(JsonValueKind.Null, written.GetProperty("missing").ValueKind);
        Assert.Equal(JsonValueKind.Null, written.GetProperty("nan").ValueKind);
        Assert.Equal(JsonValueKind.Null, written.GetProperty("inf").ValueKind);
        Assert.Equal(JsonValueKind.False, written.GetProperty("bool").ValueKind);
        Assert.Contains("\"int\":9007199254740993,", json, StringComparison.Ordinal); // exact above 2^53
        Assert.Contains("\"float\":0.1,", json, StringComparison.Ordinal);
        Assert.Equal(1e-300, written.GetProperty("tiny").GetDouble());
        Assert.Equal("a \"quoted\" é", written.GetProperty("string").GetString());
        Assert.Equal("line\nbreak", written.GetProperty("text").GetString());
        Assert.Contains("\"vector\":[0.5,null,null]", json, StringComparison.Ordinal);
        Assert.Contains("\"ints\":[-9223372036854775808,2]", json, StringComparison.Ordinal);
        Assert.Contains("\"strings\":[\"x\",\"\"]", json, StringComparison.Ordinal);
        Assert.Contains("\"empty\":[]", json, StringComparison.Ordinal);
        Assert.Contains("\"flags\":[true,false]", json, StringComparison.Ordinal);
        Assert.Contains("\"weights\":[0.1,null,1E-300]", json, StringComparison.Ordinal);
        Assert.Contains("\"weights32\":[0.1,null]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AColumnNamedLikeAnItemMember_StaysInColumns()
    {
        var columns = new Dictionary<string, ScalarValue> { ["id"] = ScalarValue.FromString("other"), ["rank"] = ScalarValue.FromInt64(99) };

        using var document = JsonDocument.Parse(Write(Result(new RankedItem(EntityKey.FromInt64(1), 1, columns))));
        var item = document.RootElement.GetProperty("items")[0];

        Assert.Equal((1L, 1), (item.GetProperty("id").GetInt64(), item.GetProperty("rank").GetInt32()));
        Assert.Equal(("other", 99L), (item.GetProperty("columns").GetProperty("id").GetString(), item.GetProperty("columns").GetProperty("rank").GetInt64()));
    }

    [Fact]
    public void ColumnProducers_AreWrittenOnlyWhenAskedFor()
    {
        var result = Result();
        var withProducers = new RecResult
        {
            RequestId = result.RequestId, Succeeded = true, Columns = result.Columns, Experiments = result.Experiments, ManifestId = result.ManifestId,
            ServedBy = result.ServedBy, ColumnProducers = new Dictionary<string, string> { ["score"] = "rank" },
        };

        Assert.DoesNotContain("column_producers", Write(withProducers), StringComparison.Ordinal);
        Assert.Contains("\"column_producers\":{\"score\":\"rank\"}", Write(withProducers, options: new RecResultJsonOptions { IncludeColumnProducers = true }), StringComparison.Ordinal);
    }

    private static RecResult Result(params RankedItem[] items) =>
        Result(items.Length == 0 ? [] : items[0].Columns.Select(c => (c.Key, TypeOf(c.Value))).ToArray(), items);

    private static RecResult Result((string, ColumnType)[] columns, params RankedItem[] items) => new()
    {
        RequestId = "req-1",
        Succeeded = true,
        Columns = ResultColumns.FromItems(columns, items),
        Experiments = new ExperimentRecord(new[] { "exp/a" }, new[] { "exp" }, new[] { new IgnoredArm("exp_gone/b", IgnoredArmReason.Unknown) }),
        ManifestId = "m1",
        ServedBy = "home_feed",
        ElapsedMs = 1.23456,
    };

    private static ColumnType TypeOf(ScalarValue value) => value switch
    {
        { IsMissing: true } => ColumnType.Float64.AsNullable(),
        { IsFloat32: true } => ColumnType.Float32,
        { IsFloat32List: true } => ColumnType.List(StorageKind.Float32),
        { Kind: StorageKind.List } => ColumnType.List(value.ListElement),
        _ => new ColumnType(value.Kind),
    };

    private static string Write(RecResult result, RecResultJsonOptions? options = null, bool relaxed = false)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = relaxed ? JavaScriptEncoder.UnsafeRelaxedJsonEscaping : null }))
        {
            RecResultJson.Write(json, result, options);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
