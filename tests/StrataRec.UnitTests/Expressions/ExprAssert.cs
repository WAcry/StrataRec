using StrataRec.Core.Columns;
using StrataRec.Core.Values;
using StrataRec.Expressions;

namespace StrataRec.UnitTests.Expressions;

internal static class ExprAssert
{
    /// <summary>
    /// Evaluates <paramref name="source"/> over the first <c>expected.Length</c> rows three ways — vectorized over all
    /// rows (full-range path), vectorized one row at a time (gather path) and with the reference interpreter — and
    /// checks each against <paramref name="expected"/> (null = missing; double NaN matches NaN).
    /// </summary>
    public static void Rows(ExprFixture f, string source, params object?[] expected)
    {
        var compiled = f.Compile(source);
        var type = compiled.ResultType.AsNullable();
        var rows = new int[expected.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = i;
        }

        Assert.True(f.Table.PhysicalRowCount >= 3, "gather path needs at least 3 physical rows");
        var full = ColumnBuffer.Create(type, f.Capacity, f.Memory);
        if (rows.Length * 2 >= f.Table.PhysicalRowCount)
        {
            compiled.Evaluate(f.Context, rows, full);
        }
        else
        {
            compiled.Evaluate(f.Context, f.Table.Rows.Order, full);
        }

        for (var r = 0; r < expected.Length; r++)
        {
            var single = ColumnBuffer.Create(type, f.Capacity, f.Memory);
            compiled.Evaluate(f.Context, new[] { r }, single);
            Same(expected[r], ExprFixture.Read(full, r), $"{source} @row {r} (full range)");
            Same(expected[r], ExprFixture.Read(single, r), $"{source} @row {r} (gather)");
            Same(expected[r], f.Reference(compiled, r), $"{source} @row {r} (reference)");
        }
    }

    /// <summary>Request-level evaluation equals <paramref name="expected"/>.</summary>
    public static void Scalar(ExprFixture f, string source, object? expected) =>
        Same(expected, f.Compile(source).EvaluateScalar(f.Context), source);

    public static void Same(object? expected, ScalarValue actual, string what)
    {
        switch (expected)
        {
            case null:
                Assert.True(actual.IsMissing, $"{what}: expected missing, got {actual}");
                break;
            case double d:
                Assert.False(actual.IsMissing, $"{what}: expected {d}, got missing");
                var a = actual.AsFloat64();
                Assert.True(d.Equals(a) || Math.Abs(d - a) <= 1e-12 * Math.Max(1, Math.Abs(d)), $"{what}: expected {d:R}, got {a:R}");
                break;
            case int i:
                Assert.Equal(ScalarValue.FromInt64(i), actual);
                break;
            case long l:
                Assert.Equal(ScalarValue.FromInt64(l), actual);
                break;
            case bool b:
                Assert.True(ScalarValue.FromBool(b) == actual, $"{what}: expected {b}, got {actual}");
                break;
            case string s:
                Assert.False(actual.IsMissing, $"{what}: expected '{s}', got missing");
                Assert.Equal(s, actual.AsString());
                break;
            case long[] list:
                Assert.Equal(list, actual.AsInt64List());
                break;
            case string[] list:
                Assert.Equal(list, actual.AsStringList());
                break;
            case float[] vector:
                Assert.Equal(vector, actual.AsVector());
                break;
            default:
                throw new ArgumentException($"Unsupported expected value {expected}");
        }
    }
}
