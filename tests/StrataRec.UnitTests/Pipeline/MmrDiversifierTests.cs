using StrataRec.Core.Columns;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Pipeline.Operators.Standard;

namespace StrataRec.UnitTests.Pipeline;

public sealed class MmrDiversifierTests
{
    [Fact]
    public void AnOppositeVector_IsANegativeSimilarity_NotZero()
    {
        double[] scores = [1.0, 0.7, 0.8];
        float[]?[] vectors = [[1, 0], [-1, 0], [0, 1]];

        Assert.Equal(new[] { 0, 1, 2 }, Order(scores, vectors, 0.5));
        Assert.Equal(new[] { 0, 1, 2 }, Reference(scores, vectors, 0.5));
    }

    [Fact]
    public void RowsWithoutAScore_ComeAfterEveryRowWithOne_EvenWithoutWeightOnTheScore()
    {
        double[] scores = [1.0, 0.9, double.NegativeInfinity];
        float[]?[] vectors = [[1, 0], [1, 0], [-1, 0]];

        Assert.Equal(new[] { 0, 1, 2 }, Order(scores, vectors, 0));
        Assert.Equal(new[] { 0, 1, 2 }, Order(scores, vectors, 0.5));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    public void RowsWithoutAScore_ComeLast_EvenWhenSimilaritiesAreNaN(double lambda)
    {
        double[] scores = [1.0, double.NegativeInfinity, 0.9];
        float[]?[] vectors = [[2e38f, 2e38f], [2e38f, 2e38f], [2e38f, 2e38f]];

        Assert.Equal(new[] { 0, 2, 1 }, Order(scores, vectors, lambda));
    }

    [Fact]
    public void TheFirstPick_IsTheBestScore_EvenWithoutWeightOnTheScore()
    {
        double[] scores = [0.2, 1.0, 0.5];
        float[]?[] vectors = [[1, 0], [0, 1], [1, 1]];

        Assert.Equal(1, Order(scores, vectors, 0)[0]);
    }

    [Fact]
    public void WithNoWeightOnSimilarity_TheOrderIsTheScores_WhateverTheVectors()
    {
        double[] scores = [1.0, 0.7, 0.9];
        float[]?[] vectors = [[2e38f, 2e38f], [2e38f, 2e38f], [2e38f, 2e38f]];

        Assert.Equal(new[] { 0, 2, 1 }, Order(scores, vectors, 1));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.3)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Orders_AsTheFormula_OnRandomWindows(double lambda)
    {
        var random = new Random(17);
        for (var trial = 0; trial < 300; trial++)
        {
            var n = random.Next(1, 12);
            var scores = new double[n];
            var vectors = new float[]?[n];
            for (var i = 0; i < n; i++)
            {
                scores[i] = random.Next(10) == 0 ? double.NegativeInfinity : random.Next(-3, 4) / 2.0;
                vectors[i] = random.Next(6) switch
                {
                    0 => null,
                    1 => [0, 0],
                    _ => [random.Next(-2, 3), random.Next(-2, 3)],
                };
            }

            Assert.Equal(Reference(scores, vectors, lambda), Order(scores, vectors, lambda));
            Assert.Equal(Reference(scores, vectors, lambda, float64: true), Order(scores, vectors, lambda, float64: true));
        }
    }

    [Fact]
    public void ARowOfAnotherLength_HasNoSimilarity_AsARowWithoutAVector()
    {
        // The column's writers keep every row at its declared length; were one not, it is never paired element by element.
        double[] scores = [1.0, 0.9, 0.8];
        float[]?[] vectors = [[1, 0], [1, 0, 0], [1, 0]];

        Assert.Equal(Reference(scores, [[1, 0], null, [1, 0]], 0.5), Order(scores, vectors, 0.5));
    }

    private static int[] Order(double[] scores, float[]?[] vectors, double lambda, bool float64 = false)
    {
        var memory = new RequestMemory();
        try
        {
            var type = ColumnType.List(float64 ? StorageKind.Float64 : StorageKind.Float32, nullable: true, fixedLength: 2);
            var column = (ListColumn)ColumnBuffer.Create(type, scores.Length, memory);
            for (var i = 0; i < vectors.Length; i++)
            {
                if (vectors[i] is not { } vector)
                {
                    column.SetMissing(i);
                }
                else if (column is ListColumn<double> doubles)
                {
                    doubles.Set(i, vector.Select(x => (double)x).ToArray());
                }
                else
                {
                    ((ListColumn<float>)column).Set(i, vector);
                }
            }

            var order = new int[scores.Length];
            MmrDiversifierFactory.Order(Enumerable.Range(0, scores.Length).ToArray(), scores, column, lambda, order, memory);
            return order;
        }
        finally
        {
            memory.Release();
        }
    }

    private static int[] Reference(double[] scores, float[]?[] vectors, double lambda, bool float64 = false)
    {
        var scored = Enumerable.Range(0, scores.Length).Where(i => !double.IsNegativeInfinity(scores[i])).ToList();
        var picked = new List<int>();
        while (picked.Count < scored.Count)
        {
            var best = -1;
            var bestValue = double.NegativeInfinity;
            foreach (var i in scored.Where(i => !picked.Contains(i)))
            {
                var value = picked.Count == 0 ? scores[i] : (lambda * scores[i]) - ((1 - lambda) * picked.Max(p => float64 ? Cosine64(vectors[i], vectors[p]) : Cosine(vectors[i], vectors[p])));
                if (best < 0 || value > bestValue)
                {
                    best = i;
                    bestValue = value;
                }
            }

            picked.Add(best);
        }

        return picked.Concat(Enumerable.Range(0, scores.Length).Where(i => double.IsNegativeInfinity(scores[i]))).ToArray();
    }

    /// <summary>The cosine of a <c>list&lt;float32,N&gt;</c> column, as the expressions' <c>cosine</c> computes it: the dot
    /// product and the norms in float32, the cosine from them in float64.</summary>
    private static double Cosine(float[]? a, float[]? b)
    {
        if (a is null || b is null)
        {
            return 0;
        }

        double dot = (a[0] * b[0]) + (a[1] * b[1]);
        double normA = MathF.Sqrt((a[0] * a[0]) + (a[1] * a[1]));
        double normB = MathF.Sqrt((b[0] * b[0]) + (b[1] * b[1]));
        return normA == 0 || normB == 0 ? 0 : dot / (normA * normB);
    }

    private static double Cosine64(float[]? a, float[]? b)
    {
        if (a is null || b is null)
        {
            return 0;
        }

        var norms = Math.Sqrt(((double)a[0] * a[0]) + ((double)a[1] * a[1])) * Math.Sqrt(((double)b[0] * b[0]) + ((double)b[1] * b[1]));
        return norms == 0 ? 0 : (((double)a[0] * b[0]) + ((double)a[1] * b[1])) / norms;
    }
}
