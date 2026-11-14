using StrataRec.Core.Columns;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Expressions;
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
    [InlineData("vec<float32,3>", "vec<float32,4>")]
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
