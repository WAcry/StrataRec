using StrataRec.Core.Columns;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Expressions;
using StrataRec.Models;
using StrataRec.Pipeline.Operators.Standard;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// The input type the model operators share gathers a column only from a buffer of exactly its declared type: one of
/// the same family (int64 for int32, float64 for float32, bool for float32) would otherwise be read as if it were the
/// declared type. Not reachable through a config (a slot takes its column's type); a defensive invariant.
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
    public void ABufferOfTheDeclaredType_IsGathered_InTheOrderOfTheRows()
    {
        var (input, memory) = Input("int32");
        var buffer = (ScalarColumn<int>)ColumnBuffer.Create(ColumnType.Int32, 4, memory);
        for (var row = 0; row < 4; row++)
        {
            buffer.Set(row, int.MinValue + row);
        }

        var gathered = input.GatherFrom(buffer, new[] { 3, 0, 3 }, memory);

        Assert.Null(gathered.Float32);
        Assert.Equal(new long[] { int.MinValue + 3, int.MinValue, int.MinValue + 3 }, gathered.Int64![..3]);
    }

    [Fact]
    public void ABoolColumn_IsGatheredAsInt64ZeroOrOne()
    {
        var (input, memory) = Input("bool");
        var buffer = (ScalarColumn<bool>)ColumnBuffer.Create(ColumnType.Bool, 4, memory);
        buffer.Set(0, true);
        buffer.Set(1, false);

        var gathered = input.GatherFrom(buffer, new[] { 1, 0, 0 }, memory);

        Assert.Equal(TensorElementType.Int64, input.Tensor.ElementType);
        Assert.Null(gathered.Float32);
        Assert.Equal(new long[] { 0, 1, 1 }, gathered.Int64![..3]);
    }

    [Fact]
    public void FixedLengthLists_AreGatheredNPerRow_RowMajor_BoolAndIntElementsAsInt64_FloatsAsFloat32()
    {
        var (bools, memory) = Input("list<bool,2>");
        var flags = (ListColumn<bool>)ColumnBuffer.Create(ColumnType.Parse("list<bool,2>"), 4, memory);
        flags.Set(0, [true, false]);
        flags.Set(1, [false, true]);
        var gathered = bools.GatherFrom(flags, new[] { 1, 0 }, memory);
        Assert.Equal((TensorElementType.Int64, 2), (bools.Tensor.ElementType, bools.Tensor.Dimension));
        Assert.Null(gathered.Float32);
        Assert.Equal(new long[] { 0, 1, 1, 0 }, gathered.Int64![..4]);

        var (ints, _) = Input("list<int32,3>");
        var ids = (ListColumn<int>)ColumnBuffer.Create(ColumnType.Parse("list<int32,3>"), 4, memory);
        ids.Set(2, [int.MinValue, 0, int.MaxValue]);
        Assert.Equal(new long[] { int.MinValue, 0, int.MaxValue }, ints.GatherFrom(ids, new[] { 2 }, memory).Int64![..3]);

        var (doubles, _) = Input("list<float64,2>");
        var weights = (ListColumn<double>)ColumnBuffer.Create(ColumnType.Parse("list<float64,2>"), 4, memory);
        weights.Set(3, [0.1, -2.5]);
        weights.Set(0, [1e300, 0]);
        var floats = doubles.GatherFrom(weights, new[] { 3, 0 }, memory);
        Assert.Equal((TensorElementType.Float32, 2), (doubles.Tensor.ElementType, doubles.Tensor.Dimension));
        Assert.Null(floats.Int64);
        Assert.Equal(new[] { 0.1f, -2.5f, float.PositiveInfinity, 0f }, floats.Float32![..4]);
    }

    [Fact]
    public void ARowOfAnotherLength_IsAnEngineDefect_NotATensor()
    {
        // The column's writers keep every row at the declared length (design 4.3); a row that is not would shift every
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
        return (new ModelInput("x", expression, symbol, tensor), new RequestMemory());
    }
}
