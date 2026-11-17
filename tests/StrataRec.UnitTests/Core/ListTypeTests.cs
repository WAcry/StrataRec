using System.Runtime.CompilerServices;
using StrataRec.Core.Columns;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.UnitTests.Core;

/// <summary><c>list&lt;E&gt;</c> as a type constructor (design 4.3): every element type, one representation per layer.</summary>
public class ListTypeTests
{
    [Theory]
    [InlineData("list<bool>", StorageKind.Bool)]
    [InlineData("list<int32>", StorageKind.Int32)]
    [InlineData("list<int64>", StorageKind.Int64)]
    [InlineData("list<float32>", StorageKind.Float32)]
    [InlineData("list<float64>", StorageKind.Float64)]
    [InlineData("list<string>", StorageKind.String)]
    public void EveryElementType_ParsesPrintsAndPromotes(string text, StorageKind element)
    {
        var type = ColumnType.Parse(text + "?");

        Assert.Equal(StorageKind.List, type.Kind);
        Assert.Equal(element, type.Element);
        Assert.True(type.Nullable);
        Assert.Equal(text + "?", type.ToString());
        Assert.Equal(new ColumnType(element), type.ElementType);
        Assert.Equal(ColumnType.List(new ColumnType(element).Promoted().Kind, nullable: true), type.Promoted());
    }

    [Theory]
    [InlineData("list<text>", "write 'list<string>'")]
    [InlineData("list<int64?>", "'list<int64>?'")]
    [InlineData("list<list<int64>>", "lists do not nest")]
    [InlineData("list<vec<float32,4>>", "lists do not nest")]
    [InlineData("list<double>", "bool, int32, int64, float32, float64 or string")]
    public void ElementTypesAListCannotHold_AreRejected_WithTheReason(string text, string reason)
    {
        Assert.False(ColumnType.TryParse(text, out _, out var error));
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("list<float32,64>", StorageKind.Float32, 64, false)]
    [InlineData("list<float32,64>?", StorageKind.Float32, 64, true)]
    [InlineData("list<float64,1>", StorageKind.Float64, 1, false)]
    [InlineData("list<int32,8>", StorageKind.Int32, 8, false)]
    [InlineData("list<bool,3>?", StorageKind.Bool, 3, true)]
    [InlineData("list<string,2>", StorageKind.String, 2, false)]
    [InlineData("list<int64,2147483647>", StorageKind.Int64, int.MaxValue, false)]
    public void FixedLength_ParsesPrintsAndKeepsItsLength(string text, StorageKind element, int length, bool nullable)
    {
        var type = ColumnType.Parse(text);

        Assert.Equal(StorageKind.List, type.Kind);
        Assert.Equal(element, type.Element);
        Assert.Equal(length, type.FixedLength);
        Assert.True(type.IsFixedLength);
        Assert.Equal(nullable, type.Nullable);
        Assert.Equal(text, type.ToString());
        Assert.Equal(type, ColumnType.Parse(text.Replace(",", " , ", StringComparison.Ordinal)));
        Assert.Equal(length, type.AsNullable().FixedLength);
        Assert.Equal(length, type.WithNullable(false).FixedLength);
        Assert.Equal(length, type.Promoted().FixedLength);
        Assert.NotEqual(ColumnType.List(element, nullable), type);
        Assert.NotEqual(ColumnType.List(element, nullable).GetHashCode(), type.GetHashCode());
        Assert.False(ColumnType.List(element, nullable).IsFixedLength);
    }

    [Theory]
    [InlineData("list<float32,0>", "a whole number of at least 1, got '0'")]
    [InlineData("list<float32,-1>", "a whole number of at least 1, got '-1'")]
    [InlineData("list<float32,N>", "a whole number of at least 1, got 'N'")]
    [InlineData("list<float32,2147483648>", "a whole number of at least 1")]
    [InlineData("list<float32,>", "a list of any length is written list<float32>")]
    [InlineData("list<text,4>", "write 'list<string,4>'")]
    [InlineData("vec<float32,64>", "'list<float32,64>'")]
    [InlineData("vec<float32,64>?", "'list<float32,64>?'")]
    [InlineData("vec<float64,8>", "'list<float64,8>'")]
    [InlineData("vec<float32>", "'list<float32,N>'")]
    [InlineData("vector", "'list<float32,N>'")]
    public void FixedLengthMistakes_AndTheRemovedVectorType_AreRejected_WithTheFix(string text, string fix)
    {
        Assert.False(ColumnType.TryParse(text, out _, out var error));
        Assert.Contains(fix, error, StringComparison.Ordinal);
    }

    /// <summary>There is one string type: <c>text</c>, wherever it is written, is an error that names the spelling meant.</summary>
    [Theory]
    [InlineData("text", "string")]
    [InlineData("text?", "string?")]
    [InlineData(" text ? ", "string?")]
    [InlineData("list<text>", "list<string>")]
    [InlineData("list<text>?", "list<string>?")]
    [InlineData("list<text?>", "list<string>?")] // a nullable element means a list that may be missing
    [InlineData("list<text,4>", "list<string,4>")]
    public void TheRemovedTextType_IsRejected_WithTheStringSpelling(string written, string meant)
    {
        Assert.False(ColumnType.TryParse(written, out _, out var error));

        Assert.Equal($"Unknown type '{written}': there is one string type, string, for strings of any length (a category, a title, a prompt); "
                     + $"write '{meant}'.", error);
        Assert.Equal(meant, ColumnType.TextAsString(written));
        Assert.Null(ColumnType.TextAsString("string"));
        Assert.Null(ColumnType.TextAsString("list<string>"));
        Assert.Null(ColumnType.TextAsString("texts"));
    }

    [Fact]
    public void FixedLength_Constructor_RejectsANonPositiveLengthAndALengthOutsideAList()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ColumnType.List(StorageKind.Float32, fixedLength: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ColumnType(StorageKind.Float32, fixedLength: 4));
    }

    [Fact]
    public void FixedLength_ColumnsStoreAnyListOfTheirLength_AndRefuseAnotherAsOutOfRange()
    {
        var memory = new RequestMemory();
        var type = ColumnType.List(StorageKind.Float32, nullable: true, fixedLength: 3);
        var column = ColumnBuffer.Create(type, 3, memory);
        Assert.IsType<ListColumn<float>>(column);

        var three = ScalarValue.FromFloat32List([1, 2, 3]);
        var two = ScalarValue.FromFloat32List([1, 2]);
        Assert.True(ColumnValues.FitsStorage(three, type));
        Assert.False(ColumnValues.FitsStorage(two, type));
        Assert.True(ColumnValues.FitsStorage(two, ColumnType.List(StorageKind.Float32)));
        Assert.Equal("a list of 2 elements, not 3", ColumnValues.Misfit(two, type));

        Assert.True(ColumnValues.TrySet(column, 0, three));
        Assert.False(ColumnValues.TrySet(column, 1, two));
        Assert.Equal(three, ColumnValues.Get(column, 0));
        var thrown = Assert.Throws<OverflowException>(() => ColumnValues.Set(column, 2, two));
        Assert.Contains("a list of 2 elements, not 3", thrown.Message, StringComparison.Ordinal);

        // Converting a value to the type checks the length as well.
        Assert.True(three.TryConvert(type, out _));
        Assert.False(two.TryConvert(type, out _));
        Assert.True(ScalarValue.FromInt64List([1, 2, 3]).TryConvert(type, out var converted));
        Assert.Equal(3, converted.ListLength);
    }

    [Fact]
    public void ColumnType_IsEightBytes()
    {
        // Passed by value wherever types are compared; the one-byte members sit together so no padding is added.
        Assert.Equal(8, Unsafe.SizeOf<ColumnType>());
    }

    [Fact]
    public void Constructor_RejectsAnElementKindOutsideALIst()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ColumnType(StorageKind.List, element: StorageKind.List));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ColumnType(StorageKind.List));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ColumnType(StorageKind.Int64, element: StorageKind.Int64));
    }

    [Theory]
    [InlineData("list<bool>", typeof(ListColumn<bool>))]
    [InlineData("list<int32>", typeof(ListColumn<int>))]
    [InlineData("list<int64>", typeof(ListColumn<long>))]
    [InlineData("list<float32>", typeof(ListColumn<float>))]
    [InlineData("list<float64>", typeof(ListColumn<double>))]
    [InlineData("list<string>", typeof(ListColumn<string>))]
    public void Columns_StoreTheElementTypeNatively(string type, Type column)
    {
        Assert.IsType(column, ColumnBuffer.Create(ColumnType.Parse(type), 4, new RequestMemory()));
    }

    [Fact]
    public void Values_RoundTripThroughColumns_ForEveryElementType()
    {
        var memory = new RequestMemory();
        (string Type, ScalarValue Value)[] cases =
        [
            ("list<bool>", ScalarValue.FromBoolList([true, false, true])),
            ("list<int32>", ScalarValue.FromInt64List([1, -2, int.MaxValue])),
            ("list<int64>", ScalarValue.FromInt64List([long.MinValue, 0])),
            ("list<float32>", ScalarValue.FromFloat64List([0.5, -2.25])),
            ("list<float64>", ScalarValue.FromFloat64List([0.1, 1e300])),
            ("list<string>", ScalarValue.FromStringList(["a", ""])),
        ];
        foreach (var (type, value) in cases)
        {
            var column = ColumnBuffer.Create(ColumnType.Parse(type), 2, memory);
            ColumnValues.Set(column, 0, value);
            ColumnValues.Set(column, 1, ScalarValue.Missing);

            Assert.Equal(value, ColumnValues.Get(column, 0));
            Assert.Equal(value.GetHashCode(), ColumnValues.Get(column, 0).GetHashCode());
            Assert.True(ColumnValues.Get(column, 1).IsMissing);
            Assert.Equal(value.ListLength, ((ListColumn)column).LengthOf(0));
        }
    }

    [Fact]
    public void Float64ElementsBeyondFloat32_BecomeInfinities_AndTheListIsReplacedAsAWhole()
    {
        var memory = new RequestMemory();
        var column = ColumnBuffer.Create(ColumnType.List(StorageKind.Float32, nullable: true), 3, memory);
        ColumnValues.Set(column, 0, ScalarValue.FromFloat64List([1, 1e300]));
        ColumnValues.Set(column, 1, ScalarValue.FromFloat64List([1, 2]));
        ColumnValues.Set(column, 2, ScalarValue.FromFloat64List([double.NaN]));

        Assert.True(ColumnValues.IsFloat(column));
        Assert.Equal(2, ColumnValues.SanitizeNonFinite(column, [0, 1, 2], ScalarValue.Missing));
        Assert.True(ColumnValues.Get(column, 0).IsMissing);
        Assert.Equal(ScalarValue.FromFloat64List([1, 2]), ColumnValues.Get(column, 1));
        Assert.True(ColumnValues.Get(column, 2).IsMissing);

        Assert.True(ColumnValues.IsNonFinite(ScalarValue.FromFloat64List([0, double.PositiveInfinity])));
        Assert.False(ColumnValues.IsNonFinite(ScalarValue.FromFloat64List([0, 1])));
    }

    [Fact]
    public void Conversion_WorksElementByElement_AsForScalars()
    {
        Assert.True(ScalarValue.FromInt64List([1, 2]).TryConvert(ColumnType.List(StorageKind.Float32), out var floats));
        Assert.Equal(ScalarValue.FromFloat64List([1, 2]), floats);

        Assert.True(ScalarValue.FromFloat64List([3, -4]).TryConvert(ColumnType.List(StorageKind.Int32), out var ints));
        Assert.Equal(ScalarValue.FromInt64List([3, -4]), ints);
        Assert.False(ScalarValue.FromFloat64List([0.5]).TryConvert(ColumnType.List(StorageKind.Int64), out _));
        Assert.False(ScalarValue.FromFloat64List([3e9]).TryConvert(ColumnType.List(StorageKind.Int32), out _));
        Assert.False(ScalarValue.FromBoolList([true]).TryConvert(ColumnType.List(StorageKind.Int64), out _));
        Assert.False(ScalarValue.FromInt64List([1]).TryConvert(ColumnType.Int64, out _));
        Assert.False(ScalarValue.FromInt64(1).TryConvert(ColumnType.List(StorageKind.Int64), out _));

        // Values of different element types are different values, even when empty.
        Assert.NotEqual(ScalarValue.FromInt64List([]), ScalarValue.FromFloat64List([]));
    }

    [Fact]
    public void Text_PrintsEachElementAsItsType()
    {
        Assert.Equal("[true, false]", ScalarValue.FromBoolList([true, false]).ToString());
        Assert.Equal("[0.1, 2]", ScalarValue.FromFloat64List([0.1, 2]).ToString());
        Assert.Equal("[0.1]", ScalarValue.FromFloat32List([0.1f]).ToString());
        Assert.Equal("['a', 'b']", ScalarValue.FromStringList(["a", "b"]).ToString());
    }
}
