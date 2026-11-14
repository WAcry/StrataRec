using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using StrataRec.Core.Keys;
using StrataRec.Core.Values;
using StrataRec.Experiments.Assignment;
using StrataRec.Hosting;
using StrataRec.Pipeline.Execution;

namespace StrataRec.UnitTests.Hosting;

/// <summary>
/// <see cref="RecResultJson"/>: the one document of a result, for every host and the CLI. Missing is null, never 0; what
/// JSON cannot represent is null too; ids and integers are exact; floats are not rounded.
/// </summary>
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
            ErrorPointer = "/inputs/x", Items = Array.Empty<RankedItem>(), Experiments = result.Experiments, ManifestId = string.Empty, ServedBy = "home_feed",
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
            ["text"] = ScalarValue.FromText("line\nbreak"),
            ["vector"] = ScalarValue.FromVector(new[] { 0.5f, float.NaN, float.PositiveInfinity }),
            ["ints"] = ScalarValue.FromInt64List(new[] { long.MinValue, 2L }),
            ["strings"] = ScalarValue.FromStringList(new[] { "x", string.Empty }),
            ["empty"] = ScalarValue.FromInt64List(Array.Empty<long>()),
        };

        var json = Write(Result(new RankedItem(EntityKey.FromString("item-\"1\""), 1, columns)), relaxed: true);

        using var document = JsonDocument.Parse(json);
        var item = document.RootElement.GetProperty("items")[0];
        Assert.Equal("item-\"1\"", item.GetProperty("id").GetString()); // a string id is a string
        var written = item.GetProperty("columns");
        Assert.Equal(JsonValueKind.Null, written.GetProperty("missing").ValueKind); // never 0
        Assert.Equal(JsonValueKind.Null, written.GetProperty("nan").ValueKind);
        Assert.Equal(JsonValueKind.Null, written.GetProperty("inf").ValueKind);
        Assert.Equal(JsonValueKind.False, written.GetProperty("bool").ValueKind);
        Assert.Contains("\"int\":9007199254740993,", json, StringComparison.Ordinal); // exact above 2^53
        Assert.Contains("\"float\":0.1,", json, StringComparison.Ordinal); // the shortest form that reads back as the double
        Assert.Equal(1e-300, written.GetProperty("tiny").GetDouble()); // not rounded
        Assert.Equal("a \"quoted\" é", written.GetProperty("string").GetString());
        Assert.Equal("line\nbreak", written.GetProperty("text").GetString());
        Assert.Contains("\"vector\":[0.5,null,null]", json, StringComparison.Ordinal);
        Assert.Contains("\"ints\":[-9223372036854775808,2]", json, StringComparison.Ordinal);
        Assert.Contains("\"strings\":[\"x\",\"\"]", json, StringComparison.Ordinal);
        Assert.Contains("\"empty\":[]", json, StringComparison.Ordinal);
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
            RequestId = result.RequestId, Succeeded = true, Items = result.Items, Experiments = result.Experiments, ManifestId = result.ManifestId,
            ServedBy = result.ServedBy, ColumnProducers = new Dictionary<string, string> { ["score"] = "rank" },
        };

        Assert.DoesNotContain("column_producers", Write(withProducers), StringComparison.Ordinal);
        Assert.Contains("\"column_producers\":{\"score\":\"rank\"}", Write(withProducers, options: new RecResultJsonOptions { IncludeColumnProducers = true }), StringComparison.Ordinal);
    }

    private static RecResult Result(params RankedItem[] items) => new()
    {
        RequestId = "req-1",
        Succeeded = true,
        Items = items,
        Experiments = new ExperimentRecord(new[] { "exp/a" }, new[] { "exp" }, new[] { new IgnoredArm("exp_gone/b", IgnoredArmReason.Unknown) }),
        ManifestId = "m1",
        ServedBy = "home_feed",
        ElapsedMs = 1.23456,
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
