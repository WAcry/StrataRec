using StrataRec.Core.Columns;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Expressions;
using StrataRec.Pipeline.Operators.Standard;
using StrataRec.Core.Services;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// The input type the model operators share gathers a column only from a buffer of exactly its declared type, at its
/// width: a buffer of another type would send a tensor other than the one the signature check saw. Not reachable through
/// a config (a slot takes its column's type); a defensive invariant.
/// </summary>
public sealed class ModelInputTests
{
    [Theory]
    [InlineData("int32", "int64")]
    [InlineData("int64", "int32")]
    [InlineData("float32", "float64")]
    [InlineData("float64", "float32")]
    [InlineData("float32", "bool")]
    [InlineData("bool", "float32")]
    [InlineData("list<float32,3>", "list<float32,4>")]
    [InlineData("list<float32,3>", "list<float64,3>")]
    [InlineData("list<bool,2>", "list<int64,2>")]
    [InlineData("float32", "float32?")]
    public void ABufferOfAnotherType_IsRejected_BeforeAnyValueIsRead(string declared, string stored)
    {
        var (input, memory) = Input(declared);
        var buffer = ColumnBuffer.Create(ColumnType.Parse(stored), 4, memory);

        var error = Assert.Throws<InvalidOperationException>(() => input.GatherFrom(buffer, new[] { 0, 1 }, memory));

        Assert.Equal($"Column 'col.x' is declared {declared} but its buffer holds {stored}.", error.Message);
    }

    [Fact]
    public void ABufferOfTheDeclaredType_IsGathered_InTheOrderOfTheRows_AtItsWidth()
    {
        var (input, memory) = Input("int32");
        var buffer = (ScalarColumn<int>)ColumnBuffer.Create(ColumnType.Int32, 4, memory);
        for (var row = 0; row < 4; row++)
        {
            buffer.Set(row, int.MinValue + row);
        }

        var gathered = input.GatherFrom(buffer, new[] { 3, 0, 3 }, memory);

        Assert.Equal(TensorElementType.Int32, input.Tensor.ElementType);
        Assert.Equal(new[] { int.MinValue + 3, int.MinValue, int.MinValue + 3 }, ((int[])gathered.Values)[..3]);
    }

    [Fact]
    public void ABoolColumn_IsABoolTensor()
    {
        var (input, memory) = Input("bool");
        var buffer = (ScalarColumn<bool>)ColumnBuffer.Create(ColumnType.Bool, 4, memory);
        buffer.Set(0, true);
        buffer.Set(1, false);

        var gathered = input.GatherFrom(buffer, new[] { 1, 0, 0 }, memory);

        Assert.Equal(TensorElementType.Bool, input.Tensor.ElementType);
        Assert.Equal(new[] { false, true, true }, ((bool[])gathered.Values)[..3]);
    }

    [Theory]
    [InlineData("int64", TensorElementType.Int64)]
    [InlineData("float32", TensorElementType.Float32)]
    [InlineData("float64", TensorElementType.Float64)]
    [InlineData("string", TensorElementType.String)]
    public void EveryScalarType_IsATensorOfThatType(string declared, TensorElementType tensor)
    {
        var (input, _) = Input(declared);

        Assert.Equal((tensor, 0), (input.Tensor.ElementType, input.Tensor.Dimension));
    }

    [Fact]
    public void AFloat64Column_IsSentExactly_NotRoundedToFloat32()
    {
        var (input, memory) = Input("float64");
        var buffer = (ScalarColumn<double>)ColumnBuffer.Create(ColumnType.Float64, 2, memory);
        buffer.Set(0, 0.1);
        buffer.Set(1, 1e300);

        var gathered = input.GatherFrom(buffer, new[] { 1, 0 }, memory);

        Assert.Equal(new[] { 1e300, 0.1 }, ((double[])gathered.Values)[..2]);
    }

    [Fact]
    public void AStringColumn_IsAStringTensor_OfItsInstances()
    {
        var (input, memory) = Input("string");
        var buffer = (StringColumn)ColumnBuffer.Create(ColumnType.String, 2, memory);
        var sports = "sports";
        buffer.Set(0, sports);
        buffer.Set(1, "news");

        var gathered = (string[])input.GatherFrom(buffer, new[] { 1, 0 }, memory).Values;

        Assert.Equal(new[] { "news", "sports" }, gathered[..2]);
        Assert.Same(sports, gathered[1]);
    }

    [Fact]
    public void FixedLengthLists_AreGatheredNPerRow_RowMajor_AtTheirElementsWidth()
    {
        var (bools, memory) = Input("list<bool,2>");
        var flags = (ListColumn<bool>)ColumnBuffer.Create(ColumnType.Parse("list<bool,2>"), 4, memory);
        flags.Set(0, [true, false]);
        flags.Set(1, [false, true]);
        var gathered = bools.GatherFrom(flags, new[] { 1, 0 }, memory);
        Assert.Equal((TensorElementType.Bool, 2), (bools.Tensor.ElementType, bools.Tensor.Dimension));
        Assert.Equal(new[] { false, true, true, false }, ((bool[])gathered.Values)[..4]);

        var (ints, _) = Input("list<int32,3>");
        var ids = (ListColumn<int>)ColumnBuffer.Create(ColumnType.Parse("list<int32,3>"), 4, memory);
        ids.Set(2, [int.MinValue, 0, int.MaxValue]);
        Assert.Equal(new[] { int.MinValue, 0, int.MaxValue }, ((int[])ints.GatherFrom(ids, new[] { 2 }, memory).Values)[..3]);

        var (doubles, _) = Input("list<float64,2>");
        var weights = (ListColumn<double>)ColumnBuffer.Create(ColumnType.Parse("list<float64,2>"), 4, memory);
        weights.Set(3, [0.1, -2.5]);
        weights.Set(0, [1e300, 0]);
        var values = doubles.GatherFrom(weights, new[] { 3, 0 }, memory);
        Assert.Equal((TensorElementType.Float64, 2), (doubles.Tensor.ElementType, doubles.Tensor.Dimension));
        Assert.Equal(new[] { 0.1, -2.5, 1e300, 0 }, ((double[])values.Values)[..4]);

        var (tags, _) = Input("list<string,2>");
        var words = (ListColumn<string>)ColumnBuffer.Create(ColumnType.Parse("list<string,2>"), 4, memory);
        words.Set(1, ["a", "b"]);
        Assert.Equal(new[] { "a", "b" }, ((string[])tags.GatherFrom(words, new[] { 1 }, memory).Values)[..2]);
    }

    [Fact]
    public void ARowOfAnotherLength_IsAnEngineDefect_NotATensor()
    {
        // The column's writers keep every row at the declared length; a row that is not would shift every
        // later row of the tensor.
        var (input, memory) = Input("list<float32,2>");
        var buffer = (ListColumn<float>)ColumnBuffer.Create(ColumnType.Parse("list<float32,2>"), 4, memory);
        buffer.Set(0, [1, 2, 3]);

        Assert.Throws<InvalidOperationException>(() => input.GatherFrom(buffer, new[] { 0 }, memory));
    }

    private static (ModelInput Input, RequestMemory Memory) Input(string declared)
    {
        var type = ColumnType.Parse(declared);
        var symbol = new Symbol(SymbolKind.Column, "col.x", type, 0, 1);
        Assert.True(ExpressionCompiler.TryCompile("col.x", new DictionaryScope().Add(symbol), CompileOptions.Any, out var expression, out var diagnostics),
            string.Join("\n", diagnostics));
        Assert.True(ModelTensorRule.TryGetInputTensor(type, out var tensor));
        return (new ModelInput("x", expression, symbol, type, tensor), new RequestMemory());
    }
}
