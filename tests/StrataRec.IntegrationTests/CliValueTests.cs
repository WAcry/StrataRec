using StrataRec.CommandLine.Commands;
using StrataRec.Core.Values;

namespace StrataRec.IntegrationTests;

public sealed class CliValueTests
{
    public static TheoryData<ScalarValue, string> Values() => new()
    {
        { ScalarValue.FromFloat64(1e-7), "1E-07" },
        { ScalarValue.FromFloat64(0.12345678901234), "0.12345678901234" },
        { ScalarValue.FromFloat32(0.3291833f), "0.3291833" },
        { ScalarValue.FromFloat64(0.3291833f), "0.32918331027030945" },
        { ScalarValue.FromFloat32(float.NaN), "\"NaN\"" },
        { ScalarValue.FromFloat64(double.NegativeInfinity), "\"-Infinity\"" },
        { ScalarValue.FromBool(true), "true" },
        { ScalarValue.Missing, "null" },
    };

    [Theory]
    [MemberData(nameof(Values))]
    public void AValue_IsWrittenExactly(ScalarValue value, string json) =>
        Assert.Equal(json, Output.ToJson(writer => Output.WriteValue(writer, value)));
}
