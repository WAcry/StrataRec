using StrataRec.Expressions.Runtime;

namespace StrataRec.UnitTests.Expressions;

/// <summary>
/// The transcendental kernels must give every lane the same bits as the scalar entry point, whatever its neighbours
/// hold: dead rows contain garbage, the row domain depends on the live ratio, and hoisted or constant-folded values must
/// match per-row ones (design 5.7, 7.7).
/// </summary>
public class MathKernelTests
{
    private static readonly double[] Specials =
    {
        double.NaN, double.PositiveInfinity, double.NegativeInfinity, 1e300, -1e300, 750, -750, 709.5, -709.5, 705, -705,
        0.0, -0.0, 1e-310, -1.0, -2.0,
    };

    public static TheoryData<string> Kernels() => new() { "exp", "sigmoid", "log", "log1p", "pow" };

    [Theory]
    [MemberData(nameof(Kernels))]
    public void SpanKernels_AreLaneIndependent_AndMatchTheScalarEntryPoint(string kernel)
    {
        var random = new Random(kernel.Length);
        foreach (var n in new[] { 1, 7, 8, 9, 16, 33, 100 })
        {
            for (var trial = 0; trial < 50; trial++)
            {
                var x = new double[n];
                var y = new double[n];
                for (var i = 0; i < n; i++)
                {
                    x[i] = random.Next(4) == 0 ? Specials[random.Next(Specials.Length)] : (random.NextDouble() * 40) - 20;
                    y[i] = random.Next(6) == 0 ? Specials[random.Next(Specials.Length)] : (random.NextDouble() * 6) - 3;
                }

                var actual = new double[n];
                switch (kernel)
                {
                    case "exp": MathKernels.Exp(x, actual); break;
                    case "sigmoid": MathKernels.Sigmoid(x, actual); break;
                    case "log": MathKernels.Log(x, actual); break;
                    case "log1p": MathKernels.Log1p(x, actual); break;
                    default: MathKernels.Pow(x, y, actual); break;
                }

                for (var i = 0; i < n; i++)
                {
                    var expected = kernel switch
                    {
                        "exp" => MathKernels.Exp(x[i]),
                        "sigmoid" => MathKernels.Sigmoid(x[i]),
                        "log" => MathKernels.Log(x[i]),
                        "log1p" => MathKernels.Log1p(x[i]),
                        _ => MathKernels.Pow(x[i], y[i]),
                    };
                    Assert.True(
                        BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual[i]) || (double.IsNaN(expected) && double.IsNaN(actual[i])),
                        $"{kernel}({x[i]:R}) lane {i} of {n}: scalar {expected:R}, span {actual[i]:R}");
                }
            }
        }
    }

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(1.0, Math.E)]
    [InlineData(-745.2, 0.0)]
    [InlineData(709.0, 8.218407461554972E+307)]
    [InlineData(710.0, double.PositiveInfinity)]
    public void Exp_IsAccurate(double x, double expected)
    {
        var actual = MathKernels.Exp(x);
        Assert.True(expected == actual || Math.Abs(expected - actual) <= 4e-16 * Math.Abs(expected), $"exp({x}) = {actual:R}");
    }

    [Fact]
    public void Log1p_IsAccurateForTinyArguments()
    {
        foreach (var x in new[] { 1e-300, 1e-20, 1e-10, -1e-10, 1e-5, 0.5, -0.5, 10.0 })
        {
            var expected = x < 1e-4 && x > -1e-4 ? x - (x * x / 2) + (x * x * x / 3) : Math.Log(1 + x);
            Assert.True(Math.Abs(MathKernels.Log1p(x) - expected) <= 2e-16 * Math.Abs(expected), $"log1p({x:R}) = {MathKernels.Log1p(x):R}, expected {expected:R}");
        }

        Assert.Equal(-0.0, MathKernels.Log1p(-0.0));
        Assert.True(double.IsNegative(MathKernels.Log1p(-0.0)));
        Assert.Equal(double.PositiveInfinity, MathKernels.Log1p(double.PositiveInfinity));
        Assert.True(double.IsNaN(MathKernels.Log1p(double.NegativeInfinity)));
    }
}
