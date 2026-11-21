using System.Text.Json;
using StrataRec.Core.Columns;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Logging;

namespace StrataRec.UnitTests.Logging;

/// <summary>
/// A log column holds the declared type's storage: an int32 column is an <c>int[]</c>, a float32 one a <c>float[]</c>,
/// lists of them their elements in one such array, copied from the buffer for the selected rows without widening. It
/// logged every column at the expression's promoted type (int64, float64), so a float32 embedding doubled in size.
/// </summary>
public sealed class LogColumnTests
{
    [Fact]
    public void Capture_CopiesInt32AndFloat32Storage_AtItsWidth_ForTheSelectedRows()
    {
        var memory = new RequestMemory();
        var ints = (ScalarColumn<int>)ColumnBuffer.Create(ColumnType.Parse("int32?"), 4, memory);
        var floats = (ScalarColumn<float>)ColumnBuffer.Create(ColumnType.Float32, 4, memory);
        for (var row = 0; row < 4; row++)
        {
            ints.Set(row, int.MaxValue - row);
            floats.Set(row, 0.1f * row);
        }

        ints.SetMissing(2);
        floats.Set(3, float.PositiveInfinity);

        var intColumn = LogColumn.Capture("clicks", ColumnType.Parse("int32?"), ints, [3, 2, 0]);
        var floatColumn = LogColumn.Capture("ctr", ColumnType.Float32, floats, [3, 1]);

        Assert.Equal([int.MaxValue - 3, 0, int.MaxValue], Assert.IsType<int[]>(intColumn.Values));
        Assert.Equal(new[] { false, true, false }, new[] { intColumn.IsMissing(0), intColumn.IsMissing(1), intColumn.IsMissing(2) });
        Assert.Equal(ScalarValue.FromInt64(int.MaxValue), intColumn.Get(2)); // read back promoted, as every value is
        Assert.Equal([0f, 0.1f], Assert.IsType<float[]>(floatColumn.Values));
        Assert.True(floatColumn.IsMissing(0)); // logs carry no non-finite values, float32 ones included
        Assert.True(floatColumn.Get(1).IsFloat32);
        Assert.Equal("float32", floatColumn.Type.ToString());
    }

    [Fact]
    public void Capture_CopiesAFloat32Embedding_AsFloats_AndAListWithANonFiniteElementIsMissing()
    {
        var memory = new RequestMemory();
        var type = ColumnType.Parse("list<float32,2>?");
        var embedding = (ListColumn<float>)ColumnBuffer.Create(type, 3, memory);
        embedding.Set(0, [0.5f, 0.25f]);
        embedding.Set(1, [1f, float.NaN]);
        embedding.Set(2, [0.1f, -2f]);
        var tags = (ListColumn<int>)ColumnBuffer.Create(ColumnType.List(StorageKind.Int32), 2, memory);
        tags.Set(0, [int.MinValue, 7]);
        tags.Set(1, []);

        var column = LogColumn.Capture("emb", type, embedding, [2, 1, 0]);
        var tagColumn = LogColumn.Capture("tags", ColumnType.List(StorageKind.Int32), tags, [0, 1]);

        Assert.Equal([0.1f, -2f, 0.5f, 0.25f], Assert.IsType<float[]>(column.Values)); // 4 bytes an element, not 8
        Assert.Equal([0, 2, 2, 4], column.Offsets!);
        Assert.True(column.IsMissing(1));
        Assert.True(column.Get(0).IsFloat32List);
        Assert.Equal([int.MinValue, 7], Assert.IsType<int[]>(tagColumn.Values));
        Assert.Equal(ScalarValue.FromInt64List([int.MinValue, 7]), tagColumn.Get(0));
    }

    [Fact]
    public void Capture_RejectsABufferThatDoesNotStoreTheType()
    {
        var buffer = ColumnBuffer.Create(ColumnType.Float64, 1, new RequestMemory());

        Assert.Throws<ArgumentException>(() => LogColumn.Capture("x", ColumnType.Float32, buffer, [0]));
    }

    [Fact]
    public void FromValues_NarrowsPromotedValuesToTheTypesStorage()
    {
        var ints = LogColumn.FromValues("n", ColumnType.Parse("int32?"), [ScalarValue.FromInt64(-5), ScalarValue.Missing]);
        var floats = LogColumn.FromValues("f", ColumnType.Float32, [ScalarValue.FromFloat64(0.1), ScalarValue.FromFloat64(1e300)]);
        var lists = LogColumn.FromValues("l", ColumnType.List(StorageKind.Float32),
            [ScalarValue.FromFloat64List([1e300, 1]), ScalarValue.FromFloat64List([0.5, 0.25]), ScalarValue.FromFloat64List([])]);

        Assert.Equal([-5, 0], Assert.IsType<int[]>(ints.Values));
        Assert.True(ints.IsMissing(1));
        Assert.Equal([0.1f, 0f], Assert.IsType<float[]>(floats.Values));
        Assert.True(floats.IsMissing(1)); // beyond the float32 range: an infinity in float32 storage
        Assert.Equal([0.5f, 0.25f], Assert.IsType<float[]>(lists.Values)); // the missing row adds no elements, no spare capacity
        Assert.Equal([0, 0, 2, 2], lists.Offsets!);
        Assert.True(lists.IsMissing(0));
        Assert.Throws<InvalidOperationException>(() => LogColumn.FromValues("n", ColumnType.Int32, [ScalarValue.FromInt64(1L << 40)]));
    }

    [Theory]
    [InlineData("int32", typeof(int[]))]
    [InlineData("float32?", typeof(float[]))]
    [InlineData("list<int32>", typeof(int[]))]
    [InlineData("list<float32,64>", typeof(float[]))]
    [InlineData("float64", typeof(double[]))]
    public void AllMissing_HasTheTypesStorage(string type, Type values)
    {
        var column = LogColumn.AllMissing("x", ColumnType.Parse(type), 3);

        Assert.IsType(values, column.Values);
        Assert.True(column.IsMissing(0) && column.IsMissing(2));
    }

    [Fact]
    public void Json_WritesInt32AsIntegers_AndFloat32AsTheShortestTextOfTheFloat_InColumnsAndContext()
    {
        var schema = new LogSchema("sha256:s", "t",
            [new LogField("n", ColumnType.Int32, "col.n"), new LogField("f", ColumnType.Float32, "col.f"), new LogField("e", ColumnType.Parse("list<float32,2>"), "col.e")],
            [new LogField("ctr", ColumnType.Float32, "ctx.user.ctr"), new LogField("w", ColumnType.Float64, "ctx.w"), new LogField("v", ColumnType.Parse("list<float32>"), "ctx.v")]);
        var batch = new LogBatch
        {
            TapId = "t", RequestId = "r", PipelineId = "p", ManifestId = "m", Schema = schema, TimeMs = 0, Delivery = LogDelivery.BestEffort,
            RequestProbability = 1, Int64EntityIds = [1], Ranks = [1], RowProbability = 1,
            Columns =
            [
                LogColumn.FromValues("n", ColumnType.Int32, [ScalarValue.FromInt64(int.MinValue)]),
                LogColumn.FromValues("f", ColumnType.Float32, [ScalarValue.FromFloat32(0.1f)]),
                LogColumn.FromValues("e", ColumnType.Parse("list<float32,2>"), [ScalarValue.FromFloat32List([0.1f, 0.2f])]),
            ],
            Context =
            [
                ("ctr", ScalarValue.FromFloat64(0.1)), // a float32 input holds the double it was given: written as the float
                ("w", ScalarValue.FromFloat32(0.1f)), // a float64 field is written as the double, whatever the value's origin
                ("v", ScalarValue.FromFloat64List([0.1, 0.5])),
            ],
        };

        var json = LogBatchJson.Encode(batch, indented: false);

        Assert.Contains("\"int_cols\":{\"n\":[-2147483648]}", json, StringComparison.Ordinal);
        Assert.Contains("\"float_cols\":{\"f\":[0.1]}", json, StringComparison.Ordinal);
        Assert.Contains("\"nested_cols\":{\"e\":[[0.1,0.2]]}", json, StringComparison.Ordinal);
        Assert.Contains("\"context\":{\"ctr\":0.1,\"w\":0.10000000149011612,\"v\":[0.1,0.5]}", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.True(LogBatchJson.TryReadValue(document.RootElement.GetProperty("float_cols").GetProperty("f")[0], ColumnType.Float32, out var read, out _));
        Assert.Equal(ScalarValue.FromFloat32(0.1f), read); // reads back as the float logged
    }

    [Fact]
    public void Json_RejectsABatchWhoseContextIsNotItsSchemas()
    {
        var batch = new LogBatch
        {
            TapId = "t", RequestId = "r", PipelineId = "p", ManifestId = "m", Schema = new LogSchema("sha256:s", "t", [], [new LogField("a", ColumnType.Int64, "ctx.a")]),
            TimeMs = 0, Delivery = LogDelivery.BestEffort, RequestProbability = 1, Int64EntityIds = [], Ranks = [], RowProbability = 1, Columns = [],
            Context = [("b", ScalarValue.FromInt64(1))],
        };

        Assert.Throws<ArgumentException>(() => LogBatchJson.Encode(batch));
    }

    [Fact]
    public void ApproximateBytes_CountsInt32AndFloat32ValuesAtFourBytes()
    {
        LogBatch Of(LogColumn column) => new()
        {
            TapId = "t", RequestId = "r", PipelineId = "p", ManifestId = "m", Schema = new LogSchema("sha256:s", "t", [], []), TimeMs = 0,
            Delivery = LogDelivery.BestEffort, RequestProbability = 1, Int64EntityIds = new long[1000], Ranks = new int[1000], RowProbability = 1,
            Columns = [column], Context = [],
        };

        var wide = Of(LogColumn.AllMissing("x", ColumnType.Parse("float64?"), 1000)).ApproximateBytes;

        Assert.Equal(wide - 4000, Of(LogColumn.AllMissing("x", ColumnType.Parse("float32?"), 1000)).ApproximateBytes);
        Assert.Equal(wide - 4000, Of(LogColumn.AllMissing("x", ColumnType.Parse("int32?"), 1000)).ApproximateBytes);
    }
}
