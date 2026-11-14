using System.Text.Json;
using StrataRec.Core.Bindings;
using StrataRec.Core.Hashing;
using StrataRec.Core.Types;
using StrataRec.Models;
using StrataRec.Pipeline.Operators.Standard;

namespace StrataRec.UnitTests.Models;

/// <summary>
/// <c>random@1</c>, the stand-in a dev or test environment binds a real model's name to: it declares the signature its
/// settings give, and scores each row from its own inputs and the seed alone, so batching, order and replay cannot change
/// a score while a new request seed does.
/// </summary>
public sealed class RandomModelClientTests
{
    private const string SampleSettings = """{ "inputs": ["smoothed_ctr", "log_followers"], "outputs": ["p_click"] }""";

    [Fact]
    public async Task TheSignature_IsTheDeclaredNames_AsFloat32Rows_ForAnyVersion_AnsweredAtOnce()
    {
        var client = Create("""{ "inputs": ["smoothed_ctr", "log_followers"], "outputs": ["p_click", "p_like"] }""");

        var lookup = client.GetSignatureAsync("ctr_model", "v13", CancellationToken.None);

        Assert.True(lookup.IsCompletedSuccessfully);
        var signature = await lookup;
        Assert.Equal(new[] { "log_followers", "smoothed_ctr" }, signature.Inputs.Select(s => s.Name));
        Assert.Equal(new[] { "p_click", "p_like" }, signature.Outputs.Select(s => s.Name));
        Assert.All(signature.Inputs.Concat(signature.Outputs), s => Assert.True(ModelTensorRule.AcceptsOutput(TensorLayout.Rows, s), s.ToString()));
        Assert.Equal(signature, await client.GetSignatureAsync("ctr_model", null, CancellationToken.None));

        // What ModelScorer@1 checks at load for the sample's ctr_ranker node: the stand-in passes as the real model would.
        var inputs = new ModelTensorRule.MappedInput[]
        {
            new("smoothed_ctr", "col.item.smoothed_ctr", ColumnType.Float32), new("log_followers", "col.author.log_followers", ColumnType.Float32),
        };
        Assert.Empty(ModelTensorRule.Check("Model 'ctr_model' version 'v13'", TensorLayout.Rows, inputs, new ModelTensorRule.MappedOutput[] { new("p_click", "/outputs/p_click") }, signature));
        await Assert.ThrowsAsync<ArgumentException>(async () => await client.GetSignatureAsync("other_model", null, CancellationToken.None));
    }

    [Fact]
    public void Scores_AreUniformInTheUnitInterval()
    {
        var rows = Rows(20_000);

        var scores = Invoke(Create(SampleSettings), rows, seed: 7)["p_click"];

        Assert.All(scores, s => Assert.InRange(s, 0f, MathF.BitDecrement(1f)));
        Assert.InRange(scores.Average(), 0.49, 0.51);
        var deciles = new int[10];
        foreach (var s in scores)
        {
            deciles[(int)(s * 10)]++;
        }

        Assert.All(deciles, count => Assert.InRange(count, 1_800, 2_200));
        Assert.True(scores.Distinct().Count() > 19_900, "distinct rows get distinct scores but for rare 24-bit collisions");
    }

    [Fact]
    public void ARowsScore_DoesNotDependOnItsPositionTheBatchOrTheInputOrder()
    {
        var client = Create(SampleSettings);
        var rows = Rows(500);
        var whole = Invoke(client, rows, seed: 11)["p_click"];

        // Reversed, and split into batches of 7 like ModelScorer@1's batch_size would.
        var reversed = rows.Reverse().ToArray();
        var reversedScores = Invoke(client, reversed, seed: 11)["p_click"];
        var batched = rows.Chunk(7).SelectMany(batch => Invoke(client, batch, seed: 11)["p_click"]).ToArray();
        var swapped = Invoke(client, rows, seed: 11, inputOrder: new[] { "smoothed_ctr", "log_followers" })["p_click"];

        Assert.Equal(whole, reversedScores.Reverse());
        Assert.Equal(whole, batched);
        Assert.Equal(whole, swapped);
        Assert.Equal(whole, Invoke(Create("""{ "inputs": ["log_followers", "smoothed_ctr"], "outputs": ["p_click"] }"""), rows, seed: 11)["p_click"]);

        // Equal inputs, equal scores: the score is a function of the row, as from a real model.
        var duplicated = Invoke(client, new[] { rows[3], rows[9], rows[3] }, seed: 11)["p_click"];
        Assert.Equal((whole[3], whole[9], whole[3]), (duplicated[0], duplicated[1], duplicated[2]));
    }

    [Fact]
    public void Scores_ChangeWithTheSeedTheOutputAndTheModel()
    {
        var rows = Rows(1_000);
        var client = Create("""{ "inputs": ["smoothed_ctr", "log_followers"], "outputs": ["p_click", "p_like"] }""");
        var first = Invoke(client, rows, seed: 1);

        Assert.Equal(first["p_click"], Invoke(client, rows, seed: 1)["p_click"]);
        Assert.True(Differing(first["p_click"], Invoke(client, rows, seed: 2)["p_click"]) > 990);
        Assert.True(Differing(first["p_click"], first["p_like"]) > 990);
        Assert.True(Differing(first["p_click"], Invoke(Create(SampleSettings, "cvr_model"), rows, seed: 1)["p_click"]) > 990);
        Assert.True(Differing(first["p_click"], Invoke(client, rows.Select(r => (r.SmoothedCtr, MathF.BitIncrement(r.LogFollowers))).ToArray(), seed: 1)["p_click"]) > 990,
            "the exact bits count: the next float32 is another row");
    }

    [Fact]
    public void AFixedSeed_OverridesTheCallsSeed_AndServesCallsWithoutOne()
    {
        var rows = Rows(200);
        var fixedSeed = Create("""{ "inputs": ["smoothed_ctr", "log_followers"], "outputs": ["p_click"], "seed": -42 }""");

        var withRequestSeed = Invoke(fixedSeed, rows, seed: 1)["p_click"];

        Assert.Equal(withRequestSeed, Invoke(fixedSeed, rows, seed: 2)["p_click"]);
        Assert.Equal(withRequestSeed, Invoke(fixedSeed, rows, seed: null)["p_click"]);
        Assert.Equal(withRequestSeed, Invoke(Create(SampleSettings), rows, seed: -42)["p_click"]);
    }

    [Fact]
    public void ACallWithoutASeed_Fails_WhenTheBindingFixesNone()
    {
        var (status, _) = TryInvoke(Create(SampleSettings), Rows(3), seed: null);

        Assert.False(status.Succeeded);
        Assert.Contains("without a seed", status.Error);
        Assert.Contains("\"seed\" in the binding", status.Error);
    }

    [Fact]
    public async Task AMissingOrMalformedInput_FailsTheCall()
    {
        var client = Create(SampleSettings);
        var missing = new ModelCall
        {
            Model = "ctr_model",
            BatchSize = 2,
            Inputs = new[] { NamedInput.FromFloat32("smoothed_ctr", new[] { 2 }, new float[2]) },
            Options = new ModelCallOptions(Seed: 1),
        };
        var shortInput = new ModelCall
        {
            Model = "ctr_model",
            BatchSize = 3,
            Inputs = new[] { NamedInput.FromFloat32("smoothed_ctr", new[] { 3 }, new float[3]), NamedInput.FromFloat32("log_followers", new[] { 2 }, new float[2]) },
            Options = new ModelCallOptions(Seed: 1),
        };
        var int64Input = new ModelCall
        {
            Model = "ctr_model",
            BatchSize = 1,
            Inputs = new[] { NamedInput.FromFloat32("smoothed_ctr", new[] { 1 }, new float[1]), NamedInput.FromInt64("log_followers", new[] { 1 }, new long[1]) },
            Options = new ModelCallOptions(Seed: 1),
        };
        var vectorInput = new ModelCall // what ModelScorer@1 sends for a vec<float32,2> column: enough floats, but two per row
        {
            Model = "ctr_model",
            BatchSize = 1,
            Inputs = new[] { NamedInput.FromFloat32("smoothed_ctr", new[] { 1 }, new float[1]), NamedInput.FromFloat32("log_followers", new[] { 1, 2 }, new float[2]) },
            Options = new ModelCallOptions(Seed: 1),
        };

        Assert.Equal(ModelStatus.Failed("Missing input 'log_followers'."), await client.InvokeAsync(missing, new Outputs(), CancellationToken.None));
        Assert.Equal(ModelStatus.Failed("Input 'log_followers' must be float32 with one value per row (3)."), await client.InvokeAsync(shortInput, new Outputs(), CancellationToken.None));
        Assert.Equal(ModelStatus.Failed("Input 'log_followers' must be float32 with one value per row (1)."), await client.InvokeAsync(int64Input, new Outputs(), CancellationToken.None));
        Assert.Equal(ModelStatus.Failed("Input 'log_followers' must be float32 with one value per row (1)."), await client.InvokeAsync(vectorInput, new Outputs(), CancellationToken.None));
    }

    [Fact]
    public async Task AnInputWhoseShapeIsNotTheBatch_FailsTheCall_AndALongerBackingBufferIsAllowed()
    {
        var client = Create(SampleSettings);

        ModelCall Call(int batch, int rows, int floats) => new()
        {
            Model = "ctr_model",
            BatchSize = batch,
            Inputs = new[] { NamedInput.FromFloat32("log_followers", new[] { rows }, new float[floats]), NamedInput.FromFloat32("smoothed_ctr", new[] { rows }, new float[floats]) },
            Options = new ModelCallOptions(Seed: 1),
        };

        Assert.Equal(ModelStatus.Failed("Input 'log_followers' must be float32 with one value per row (2)."),
            await client.InvokeAsync(Call(batch: 2, rows: 3, floats: 3), new Outputs(), CancellationToken.None)); // enough data, but three rows
        Assert.Equal(ModelStatus.Failed("Input 'log_followers' must be float32 with one value per row (2)."),
            await client.InvokeAsync(Call(batch: 2, rows: 1, floats: 2), new Outputs(), CancellationToken.None));
        Assert.Equal(ModelStatus.Failed("The call's batch size is -1; it must not be negative."),
            await client.InvokeAsync(Call(batch: -1, rows: -1, floats: 1), new Outputs(), CancellationToken.None));
        var outputs = new Outputs();
        Assert.Equal(ModelStatus.Ok, await client.InvokeAsync(Call(batch: 2, rows: 2, floats: 5), outputs, CancellationToken.None));
        Assert.Equal(2, outputs.Values["p_click"].Length);
    }

    /// <summary>
    /// The definition, so a score can be recomputed anywhere the canonical encoding can (any language): the top 24 bits of
    /// the stable hash of (hash of model and output name, seed, inputs in ordinal name order as float64). Changing it
    /// changes every stand-in's scores, which this pins.
    /// </summary>
    [Fact]
    public void AScore_IsTheStableHashOfSaltSeedAndInputs_InCanonicalEncoding()
    {
        var rows = new[] { (0.25f, 3.5f), (-0f, float.NaN) };
        var scores = Invoke(Create(SampleSettings), rows, seed: 99)["p_click"];

        var salt = new StableHash.Builder();
        salt.Add("ctr_model");
        salt.Add("p_click");
        var saltValue = unchecked((long)salt.Finish());
        salt.Dispose();
        for (var i = 0; i < rows.Length; i++)
        {
            var hash = new StableHash.Builder();
            hash.Add(saltValue);
            hash.Add(99L);
            hash.Add((double)rows[i].Item2); // log_followers sorts first
            hash.Add((double)rows[i].Item1);
            Assert.Equal((hash.Finish() >> 40) / (float)(1 << 24), scores[i]);
            hash.Dispose();
        }

        // −0.0 and 0.0, and every NaN, are one value to the canonical encoding.
        Assert.Equal(scores[1], Invoke(Create(SampleSettings), new[] { (0f, BitConverter.Int32BitsToSingle(unchecked((int)0xFFC00001))) }, seed: 99)["p_click"][0]);
    }

    [Theory]
    [InlineData("""{ "outputs": ["p_click"] }""", "/inputs", "'inputs' is required")]
    [InlineData("""{ "inputs": ["smoothed_ctr"] }""", "/outputs", "'outputs' is required")]
    [InlineData("""{ "inputs": "smoothed_ctr", "outputs": ["p_click"] }""", "/inputs", "must be an array of names")]
    [InlineData("""{ "inputs": [], "outputs": ["p_click"] }""", "/inputs", "every row would score the same")]
    [InlineData("""{ "inputs": ["smoothed_ctr"], "outputs": [] }""", "/outputs", "'outputs' is empty")]
    [InlineData("""{ "inputs": ["smoothed_ctr", 3], "outputs": ["p_click"] }""", "/inputs/1", "must be a non-empty name, got 3")]
    [InlineData("""{ "inputs": ["smoothed_ctr", ""], "outputs": ["p_click"] }""", "/inputs/1", "must be a non-empty name")]
    [InlineData("""{ "inputs": ["smoothed_ctr"], "outputs": ["p_click", "p_like", "p_click"] }""", "/outputs/2", "'p_click' is listed twice")]
    [InlineData("""{ "inputs": ["smoothed_ctr"], "outputs": ["p_click"], "seed": 1.5 }""", "/seed", "'seed' must be an integer, got 1.5")]
    [InlineData("""{ "inputs": ["smoothed_ctr"], "outputs": ["p_click"], "seed": "42" }""", "/seed", "must be an integer")]
    [InlineData("""{ "inputs": ["smoothed_ctr"], "outputs": ["p_click"], "seed": 1e30 }""", "/seed", "must be an integer")]
    [InlineData("""{ "inputs": ["smoothed_ctr"], "outputs": ["p_click"], "versions": ["v1"] }""", "/versions", "Unknown setting 'versions'")]
    [InlineData("""{ "inputs": ["smoothed_ctr"], "outputs": ["p_click"], "a/b~c": 1 }""", "/a~1b~0c", "Unknown setting 'a/b~c'")]
    public void AWrongSetting_IsABindingError_AtItsPointer_WithAHint(string settings, string pointer, string reason)
    {
        var error = Assert.Throws<BindingException>(() => Create(settings));

        Assert.Equal(pointer, error.Pointer);
        Assert.Contains(reason, error.Reason);
        Assert.False(string.IsNullOrEmpty(error.Hint));
    }

    // The full form: each tensor as a real model's signature declares it, so a pairwise or listwise model can be mimicked.

    private const string PointSettings = """
        { "inputs": { "item_id": { "type": "int64", "shape": [-1] }, "emb": { "type": "float32", "shape": [-1, 3] }, "ctr": { "type": "float32", "shape": [-1] } },
          "outputs": ["p_click"] }
        """;

    /// <summary>As <c>PairwiseRanker@1</c> calls it: columns as <c>[pairs, 2]</c>, a context value as <c>[pairs]</c>.</summary>
    private const string PairSettings = """
        { "inputs": { "final": { "type": "float32", "shape": [-1, 2] }, "item_id": { "type": "int64", "shape": [-1, 2] }, "quality": { "type": "float32", "shape": [-1] } },
          "outputs": { "p_first": { "shape": [-1] } } }
        """;

    /// <summary>As <c>ListwiseReranker@1</c> calls it: columns as <c>[1, n]</c> / <c>[1, n, N]</c>, context values as <c>[1]</c> / <c>[1, N]</c>.</summary>
    private const string ListSettings = """
        { "inputs": { "final": { "type": "float32", "shape": [1, -1] }, "emb": { "type": "float32", "shape": [1, -1, 2] },
                      "seed": { "type": "int64", "shape": [1] }, "user": { "type": "float32", "shape": [1, 3] } },
          "outputs": { "scores": { "type": "float32", "shape": [1, -1] } } }
        """;

    [Fact]
    public async Task TheFullForm_DeclaresEachTensorAsGiven_SoEachModelOperatorsLoadTimeCheckPasses()
    {
        var point = await Create(PointSettings).GetSignatureAsync("ctr_model", "v1", CancellationToken.None);
        Assert.Equal(new[] { "ctr float32 [-1]", "emb float32 [-1, 3]", "item_id int64 [-1]" }, point.Inputs.Select(Describe));
        Assert.Equal(new[] { "p_click float32 [-1]" }, point.Outputs.Select(Describe));
        Assert.Empty(ModelTensorRule.Check("m", TensorLayout.Rows,
            new ModelTensorRule.MappedInput[] { new("item_id", "col.entity_id", ColumnType.Int64), new("emb", "col.item.emb", ColumnType.Vector(3)), new("ctr", "col.item.ctr", ColumnType.Float32) },
            new ModelTensorRule.MappedOutput[] { new("p_click", "/outputs/p_click") }, point));

        var pair = await Create(PairSettings).GetSignatureAsync("ctr_model", null, CancellationToken.None);
        Assert.Equal(new[] { "final float32 [-1, 2]", "item_id int64 [-1, 2]", "quality float32 [-1]" }, pair.Inputs.Select(Describe));
        Assert.Equal(new[] { "p_first float32 [-1]" }, pair.Outputs.Select(Describe));
        Assert.Empty(ModelTensorRule.Check("m", TensorLayout.Pairs,
            new ModelTensorRule.MappedInput[]
            {
                new("final", "col.score.final", ColumnType.Float32), new("item_id", "col.entity_id", ColumnType.Int64), new("quality", "param.quality_weight", ColumnType.Float64, Broadcast: true),
            },
            new ModelTensorRule.MappedOutput[] { new("p_first", "/output") }, pair));

        var list = await Create(ListSettings).GetSignatureAsync("ctr_model", null, CancellationToken.None);
        Assert.Equal(new[] { "emb float32 [1, -1, 2]", "final float32 [1, -1]", "seed int64 [1]", "user float32 [1, 3]" }, list.Inputs.Select(Describe));
        Assert.Equal(new[] { "scores float32 [1, -1]" }, list.Outputs.Select(Describe));
        Assert.Empty(ModelTensorRule.Check("m", TensorLayout.List,
            new ModelTensorRule.MappedInput[]
            {
                new("final", "col.score.final", ColumnType.Float32), new("emb", "col.item.emb", ColumnType.Vector(2)),
                new("seed", "ctx.request.seed", ColumnType.Int64, Broadcast: true), new("user", "ctx.request.user_vec", ColumnType.Vector(3), Broadcast: true),
            },
            new ModelTensorRule.MappedOutput[] { new("scores", "/output") }, list));
    }

    [Fact]
    public void TheFullForm_OfFloat32Rows_ScoresAsTheShortForm_SinceNamesTypesAndShapesAreNotHashed()
    {
        var rows = Rows(300);
        var full = Create("""
            { "inputs": { "smoothed_ctr": { "type": "float32", "shape": [-1] }, "log_followers": { "type": "float32", "shape": [-1] } },
              "outputs": { "p_click": { "shape": [-1] } } }
            """);
        var mixed = Create("""{ "inputs": ["smoothed_ctr", "log_followers"], "outputs": { "p_click": { "type": "float32", "shape": [-1] } } }""");

        var expected = Invoke(Create(SampleSettings), rows, seed: 4)["p_click"];

        Assert.Equal(expected, Invoke(full, rows, seed: 4)["p_click"]);
        Assert.Equal(expected, Invoke(mixed, rows, seed: 4)["p_click"]);
    }

    [Fact]
    public void Pointwise_Int64AndVectorInputs_ScoreEachRowFromItsOwnValues_ExactlyAndInAnyBatch()
    {
        var client = Create(PointSettings);
        const int N = 40;
        var random = new Random(8);
        var ids = Enumerable.Range(0, N).Select(i => (1L << 40) + (i * 7919L)).ToArray(); // beyond float32's exact integers
        var emb = Enumerable.Range(0, 3 * N).Select(_ => (float)random.NextDouble()).ToArray();
        var ctr = Enumerable.Range(0, N).Select(_ => (float)random.NextDouble() * 0.2f).ToArray();

        Dictionary<string, float[]> Score(int first, int count, long[] idValues) => Call(client, count, 9,
            L("item_id", new[] { count }, idValues[first..(first + count)]), F("ctr", new[] { count }, ctr[first..(first + count)]),
            F("emb", new[] { count, 3 }, emb[(3 * first)..(3 * (first + count))]));

        var scores = Score(0, N, ids)["p_click"];

        Assert.Equal(N, scores.Length);
        for (var i = 0; i < N; i++)
        {
            // ctr, emb, item_id: ordinal name order; each row's vector whole, its int64 exactly.
            Assert.Equal(Expected("p_click", 9, ctr[i], emb[3 * i], emb[(3 * i) + 1], emb[(3 * i) + 2], ids[i]), scores[i]);
        }

        Assert.Equal(scores[10..17], Score(10, 7, ids)["p_click"]);
        var nextIds = ids.ToArray();
        nextIds[5]++; // the same float32, another int64
        var next = Score(0, N, nextIds)["p_click"];
        Assert.NotEqual(scores[5], next[5]);
        Assert.Equal(scores.Where((_, i) => i != 5), next.Where((_, i) => i != 5));
    }

    [Fact]
    public void Pairwise_APairsScore_DependsOnBothRowsAndTheirOrder_AndRebatchingKeepsIt()
    {
        var client = Create(PairSettings);
        var random = new Random(3);
        var final = Enumerable.Range(0, 8).Select(_ => (float)random.NextDouble()).ToArray();
        var ids = Enumerable.Range(0, 8).Select(i => 100L + i).ToArray();
        var pairs = (from i in Enumerable.Range(0, 8) from j in Enumerable.Range(i + 1, 7 - i) select (A: i, B: j)).ToArray(); // as PairwiseRanker@1 sends them

        float[] Score(IReadOnlyList<(int A, int B)> batch, long seed = 5, float quality = 0.3f) => Call(client, batch.Count, seed,
            F("final", new[] { batch.Count, 2 }, batch.SelectMany(p => new[] { final[p.A], final[p.B] }).ToArray()),
            L("item_id", new[] { batch.Count, 2 }, batch.SelectMany(p => new[] { ids[p.A], ids[p.B] }).ToArray()),
            F("quality", new[] { batch.Count }, Enumerable.Repeat(quality, batch.Count).ToArray()))["p_first"];

        var scores = Score(pairs);

        Assert.Equal(28, scores.Length);
        for (var k = 0; k < pairs.Length; k++)
        {
            var (a, b) = pairs[k];
            Assert.Equal(Expected("p_first", 5, final[a], final[b], ids[a], ids[b], 0.3f), scores[k]);
        }

        Assert.All(scores, s => Assert.InRange(s, 0f, MathF.BitDecrement(1f))); // a preference PairwiseRanker@1 accepts
        Assert.Equal(scores, pairs.Chunk(4).SelectMany(batch => Score(batch)).ToArray());
        Assert.Equal(scores.Reverse(), Score(pairs.Reverse().ToArray()));
        Assert.Equal(28, Differing(scores, Score(pairs.Select(p => (p.B, p.A)).ToArray()))); // the order within a pair counts
        Assert.Equal(28, Differing(scores, Score(pairs, seed: 6)));
        Assert.Equal(28, Differing(scores, Score(pairs, quality: 0.4f)));

        // A pair's score depends on both of its rows and on nothing else.
        final[7] += 0.5f;
        var changed = Score(pairs);
        Assert.All(Enumerable.Range(0, 28), k => Assert.Equal(pairs[k].B == 7, changed[k] != scores[k]));
    }

    [Fact]
    public void Listwise_EachRowsScore_DependsOnItsSliceAndTheSharedInputs_AndTheListsLengthComesFromTheInputs()
    {
        var client = Create(ListSettings);
        var random = new Random(4);
        var final = Enumerable.Range(0, 6).Select(_ => (float)random.NextDouble()).ToArray();
        var emb = Enumerable.Range(0, 12).Select(_ => (float)random.NextDouble()).ToArray();

        float[] Score(int[] list, long seedInput = 77, float lastUser = 0.3f, long seed = 3) => Call(client, 1, seed,
            F("final", new[] { 1, list.Length }, list.Select(r => final[r]).ToArray()),
            F("emb", new[] { 1, list.Length, 2 }, list.SelectMany(r => new[] { emb[2 * r], emb[(2 * r) + 1] }).ToArray()),
            L("seed", new[] { 1 }, new[] { seedInput }),
            F("user", new[] { 1, 3 }, new[] { 0.1f, 0.2f, lastUser }))["scores"];

        var all = new[] { 0, 1, 2, 3, 4, 5 };
        var scores = Score(all);

        Assert.Equal(6, scores.Length);
        for (var r = 0; r < 6; r++)
        {
            // emb, final, seed, user: a row's slices, then the shared inputs whole.
            Assert.Equal(Expected("scores", 3, emb[2 * r], emb[(2 * r) + 1], final[r], 77L, 0.1f, 0.2f, 0.3f), scores[r]);
        }

        var permuted = new[] { 5, 3, 1, 0, 2, 4 };
        Assert.Equal(permuted.Select(r => scores[r]), Score(permuted));
        Assert.Equal(scores[..3], Score(new[] { 0, 1, 2 }));
        Assert.Empty(Score(Array.Empty<int>()));
        Assert.Equal(6, Differing(scores, Score(all, seedInput: 78)));
        Assert.Equal(6, Differing(scores, Score(all, lastUser: 0.4f)));
        Assert.Equal(6, Differing(scores, Score(all, seed: 4)));
    }

    [Fact]
    public void ACallThatDoesNotMatchTheDeclaredTensors_Fails_WithoutReadingOutOfRange()
    {
        var pair = Create(PairSettings);
        NamedInput Final(int[] shape, int values) => F("final", shape, new float[values]);
        NamedInput Ids(int pairs) => L("item_id", new[] { pairs, 2 }, new long[2 * pairs]);
        NamedInput Quality(int pairs) => F("quality", new[] { pairs }, new float[pairs]);
        ModelStatus Pair(int batch, params NamedInput[] inputs) => TryCall(pair, batch, 1, inputs).Status;

        Assert.True(Pair(3, Final(new[] { 3, 2 }, 6), Ids(3), Quality(3)).Succeeded);
        Assert.True(Pair(3, Final(new[] { 3, 2 }, 7), Ids(3), Quality(3)).Succeeded); // a longer backing buffer
        Assert.Empty(TryCall(pair, 0, 1, Final(new[] { 0, 2 }, 0), Ids(0), Quality(0)).Outputs["p_first"]);
        Assert.Equal(ModelStatus.Failed("Missing input 'item_id'."), Pair(3, Final(new[] { 3, 2 }, 6), Quality(3)));
        Assert.Equal(ModelStatus.Failed("Input 'final' must be float32 [-1, 2] with the call's batch size (3) as its first dimension; the call sends int64 [3, 2] with 6 values."),
            Pair(3, L("final", new[] { 3, 2 }, new long[6]), Ids(3), Quality(3)));
        Assert.Equal(ModelStatus.Failed("Input 'final' must be float32 [-1, 2] with the call's batch size (3) as its first dimension; the call sends float32 [6] with 6 values."),
            Pair(3, Final(new[] { 6 }, 6), Ids(3), Quality(3)));
        Assert.Equal(ModelStatus.Failed("Input 'final' must be float32 [-1, 2] with the call's batch size (3) as its first dimension; the call sends float32 [3, 3] with 9 values."),
            Pair(3, Final(new[] { 3, 3 }, 9), Ids(3), Quality(3)));
        Assert.Equal(ModelStatus.Failed("Input 'final' must be float32 [-1, 2] with the call's batch size (3) as its first dimension; the call sends float32 [2, 2] with 4 values."),
            Pair(3, Final(new[] { 2, 2 }, 4), Ids(3), Quality(3)));
        Assert.Equal(ModelStatus.Failed("Input 'final' must be float32 [-1, 2] with the call's batch size (3) as its first dimension; the call sends float32 [3, 2] with 5 values."),
            Pair(3, Final(new[] { 3, 2 }, 5), Ids(3), Quality(3)));
        Assert.Equal(ModelStatus.Failed("Input 'final' must be float32 [-1, 2] with the call's batch size (3) as its first dimension; the call sends float32 [3, -2] with 6 values."),
            Pair(3, Final(new[] { 3, -2 }, 6), Ids(3), Quality(3)));
        Assert.Equal(ModelStatus.Failed("Input 'quality' must be float32 with one value per row (3)."), Pair(3, Final(new[] { 3, 2 }, 6), Ids(3), Quality(2)));

        var list = Create(ListSettings);
        ModelStatus List(int batch, int rows, int embRows, int[]? user = null, int userValues = 3) => TryCall(list, batch, 1,
            F("final", new[] { 1, rows }, new float[rows]), F("emb", new[] { 1, embRows, 2 }, new float[2 * embRows]), L("seed", new[] { 1 }, new long[1]),
            F("user", user ?? new[] { 1, 3 }, new float[userValues])).Status;

        Assert.True(List(1, 4, 4).Succeeded);
        Assert.Equal(ModelStatus.Failed("Inputs 'emb' and 'final' give output 'scores' (float32 [1, -1]) different shapes, [1, 3] and [1, 4]: the inputs scored per element " +
            "of an output must agree on its dimensions."), List(1, 4, 3));
        Assert.Equal(ModelStatus.Failed("Input 'emb' must be float32 [1, -1, 2] with the call's batch size (2) as its first dimension; the call sends float32 [1, 4, 2] with 8 values."),
            List(2, 4, 4));
        Assert.Equal(ModelStatus.Failed("Input 'user' must be float32 [1, 3]; the call sends float32 [1, 2] with 3 values."), List(1, 4, 4, user: new[] { 1, 2 }));
        Assert.Equal(ModelStatus.Failed("Input 'user' must be float32 [1, 3]; the call sends float32 [1, 3] with 2 values."), List(1, 4, 4, userValues: 2));
        Assert.Equal(ModelStatus.Failed("Input 'id' must be int64 with one value per row (2)."),
            TryCall(Create("""{ "inputs": { "id": { "type": "int64", "shape": [-1] } }, "outputs": ["p"] }"""), 2, 1, L("id", new[] { 1 }, new long[1])).Status);
    }

    [Theory]
    [InlineData("""{ "inputs": 3, "outputs": ["p"] }""", "/inputs", "must be an array of names or an object mapping each name to its tensor spec, got 3")]
    [InlineData("""{ "inputs": {}, "outputs": ["p"] }""", "/inputs", "every row would score the same")]
    [InlineData("""{ "inputs": ["x"], "outputs": {} }""", "/outputs", "'outputs' is empty")]
    [InlineData("""{ "inputs": ["x"], "outputs": "p" }""", "/outputs", "must be an array of names or an object")]
    [InlineData("""{ "inputs": { "": { "type": "float32", "shape": [-1] } }, "outputs": ["p"] }""", "/inputs/", "'inputs' maps an empty name")]
    [InlineData("""{ "inputs": { "x": { "type": "float32", "shape": [-1] }, "x": { "type": "int64", "shape": [-1] } }, "outputs": ["p"] }""", "/inputs/x", "'x' is listed twice")]
    [InlineData("""{ "inputs": { "a/b": 3 }, "outputs": ["p"] }""", "/inputs/a~1b", "Input 'a/b' must be a tensor spec object, got 3")]
    [InlineData("""{ "inputs": { "x": { "shape": [-1] } }, "outputs": ["p"] }""", "/inputs/x/type", "Input 'x' needs a 'type'")]
    [InlineData("""{ "inputs": { "x": { "type": "float32" } }, "outputs": ["p"] }""", "/inputs/x/shape", "Input 'x' needs a 'shape'")]
    [InlineData("""{ "inputs": { "x": { "type": "float64", "shape": [-1] } }, "outputs": ["p"] }""", "/inputs/x/type", "has type \"float64\", but the model operators send float32 and int64")]
    [InlineData("""{ "inputs": { "x": { "type": "text", "shape": [-1] } }, "outputs": ["p"] }""", "/inputs/x/type", "has type \"text\"")]
    [InlineData("""{ "inputs": { "x": { "type": 1, "shape": [-1] } }, "outputs": ["p"] }""", "/inputs/x/type", "has type 1")]
    [InlineData("""{ "inputs": { "x": { "type": "float32", "shape": [-1], "dtype": "f" } }, "outputs": ["p"] }""", "/inputs/x/dtype", "Unknown key 'dtype' in the spec of input 'x'")]
    [InlineData("""{ "inputs": { "x": { "type": "float32", "shape": [] } }, "outputs": ["p"] }""", "/inputs/x/shape", "The shape of input 'x' must be an array of at least one dimension, got []")]
    [InlineData("""{ "inputs": { "x": { "type": "float32", "shape": -1 } }, "outputs": ["p"] }""", "/inputs/x/shape", "at least one dimension, got -1")]
    [InlineData("""{ "inputs": { "x": { "type": "float32", "shape": [-1, 0] } }, "outputs": ["p"] }""", "/inputs/x/shape/1", "Dimension 1 of input 'x' must be -1 (dynamic) or a positive size, got 0")]
    [InlineData("""{ "inputs": { "x": { "type": "float32", "shape": [-2] } }, "outputs": ["p"] }""", "/inputs/x/shape/0", "got -2")]
    [InlineData("""{ "inputs": { "x": { "type": "float32", "shape": [-1, 1.5] } }, "outputs": ["p"] }""", "/inputs/x/shape/1", "got 1.5")]
    [InlineData("""{ "inputs": { "x": { "type": "float32", "shape": [3000000000] } }, "outputs": ["p"] }""", "/inputs/x/shape/0", "got 3000000000")]
    [InlineData("""{ "inputs": { "x": { "type": "float32", "shape": ["-1"] } }, "outputs": ["p"] }""", "/inputs/x/shape/0", "got \"-1\"")]
    [InlineData("""{ "inputs": ["x"], "outputs": { "p": [-1] } }""", "/outputs/p", "Output 'p' must be a tensor spec object, got [-1]")]
    [InlineData("""{ "inputs": ["x"], "outputs": { "p": {} } }""", "/outputs/p/shape", "Output 'p' needs a 'shape'")]
    [InlineData("""{ "inputs": ["x"], "outputs": { "p": { "type": "int64", "shape": [-1] } } }""", "/outputs/p/type", "the model operators read float32 outputs only")]
    [InlineData("""{ "inputs": ["x"], "outputs": { "p": { "shape": [-1], "activation": "sigmoid" } } }""", "/outputs/p/activation", "Unknown key 'activation' in the spec of output 'p'")]
    [InlineData("""{ "inputs": { "u": { "type": "float32", "shape": [8] } }, "outputs": { "p": { "shape": [-1] } } }""", "/outputs/p/shape",
        "Output 'p' ([-1]) has no input whose shape starts with [-1]")]
    [InlineData("""{ "inputs": { "u": { "type": "float32", "shape": [1, -1] } }, "outputs": ["p"] }""", "/outputs/0", "Output 'p' ([-1]) has no input")]
    [InlineData("""{ "inputs": { "x": { "type": "float32", "shape": [-1, -1] } }, "outputs": { "s": { "shape": [1, -1] } } }""", "/outputs/s/shape",
        "Output 's' ([1, -1]) has no input whose shape starts with [1, -1]")]
    [InlineData("""{ "inputs": ["x"], "outputs": { "p": { "shape": [-1] }, "s": { "shape": [1, -1] } } }""", "/outputs/s/shape", "Output 's' ([1, -1])")]
    public void AWrongTensorSpec_IsABindingError_AtItsPointer_WithAHint(string settings, string pointer, string reason)
    {
        var error = Assert.Throws<BindingException>(() => Create(settings));

        Assert.Equal(pointer, error.Pointer);
        Assert.Contains(reason, error.Reason);
        Assert.False(string.IsNullOrEmpty(error.Hint));
    }

    [Fact]
    public void TheDefaultRegistry_HasIt()
    {
        Assert.True(StrataRec.Pipeline.Planning.ModelClientRegistry.CreateDefault().TryGet("random@1", out var factory));
        Assert.Equal("random@1", factory.Create(Context(SampleSettings, "ctr_model")).ClientType);
    }

    internal static IModelClient Create(string settings, string model = "ctr_model") => new RandomModelClientFactory().Create(Context(settings, model));

    internal static (float SmoothedCtr, float LogFollowers)[] Rows(int count)
    {
        var random = new Random(5);
        var rows = new (float, float)[count];
        for (var i = 0; i < count; i++)
        {
            rows[i] = ((float)random.NextDouble() * 0.2f, MathF.Round((float)random.NextDouble() * 12f, 2));
        }

        return rows;
    }

    private static BindingContext Context(string settings, string model) =>
        new(model, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(settings)!, AppContext.BaseDirectory, null);

    private static int Differing(float[] a, float[] b) => a.Zip(b).Count(p => p.First != p.Second);

    private static string Describe(TensorSpec spec) => $"{spec.Name} {spec.ElementType.ToString().ToLowerInvariant()} [{string.Join(", ", spec.Shape)}]";

    private static NamedInput F(string name, int[] shape, float[] data) => NamedInput.FromFloat32(name, shape, data);

    private static NamedInput L(string name, int[] shape, long[] data) => NamedInput.FromInt64(name, shape, data);

    /// <summary>
    /// The definition, as in <see cref="AScore_IsTheStableHashOfSaltSeedAndInputs_InCanonicalEncoding"/>: the top 24 bits of
    /// the stable hash of (hash of model and output name, seed, values), float32 values as float64 and int64 values as int64.
    /// </summary>
    private static float Expected(string output, long seed, params object[] values)
    {
        var salt = new StableHash.Builder();
        salt.Add("ctr_model");
        salt.Add(output);
        var saltValue = unchecked((long)salt.Finish());
        salt.Dispose();
        var hash = new StableHash.Builder();
        hash.Add(saltValue);
        hash.Add(seed);
        foreach (var value in values)
        {
            switch (value)
            {
                case float f:
                    hash.Add((double)f);
                    break;
                case long l:
                    hash.Add(l);
                    break;
                default:
                    throw new ArgumentException($"not a tensor value: {value}");
            }
        }

        var score = (hash.Finish() >> 40) / (float)(1 << 24);
        hash.Dispose();
        return score;
    }

    private static Dictionary<string, float[]> Call(IModelClient client, int batch, long? seed, params NamedInput[] inputs)
    {
        var (status, outputs) = TryCall(client, batch, seed, inputs);
        Assert.True(status.Succeeded, status.Error);
        return outputs;
    }

    private static (ModelStatus Status, Dictionary<string, float[]> Outputs) TryCall(IModelClient client, int batch, long? seed, params NamedInput[] inputs)
    {
        var outputs = new Outputs();
        var call = new ModelCall { Model = "ctr_model", BatchSize = batch, Inputs = inputs, Options = new ModelCallOptions(Seed: seed) };
        var pending = client.InvokeAsync(call, outputs, CancellationToken.None);
        Assert.True(pending.IsCompletedSuccessfully, "random@1 answers synchronously");
        return (pending.Result, outputs.Values);
    }

    private static Dictionary<string, float[]> Invoke(IModelClient client, (float SmoothedCtr, float LogFollowers)[] rows, long? seed, string[]? inputOrder = null)
    {
        var (status, outputs) = TryInvoke(client, rows, seed, inputOrder);
        Assert.True(status.Succeeded, status.Error);
        return outputs;
    }

    private static (ModelStatus Status, Dictionary<string, float[]> Outputs) TryInvoke(
        IModelClient client, (float SmoothedCtr, float LogFollowers)[] rows, long? seed, string[]? inputOrder = null)
    {
        var columns = new Dictionary<string, float[]>
        {
            ["smoothed_ctr"] = rows.Select(r => r.SmoothedCtr).ToArray(),
            ["log_followers"] = rows.Select(r => r.LogFollowers).ToArray(),
        };
        var order = inputOrder ?? new[] { "log_followers", "smoothed_ctr" }; // ModelScorer@1 sends them in ordinal order
        var call = new ModelCall
        {
            Model = "ctr_model",
            BatchSize = rows.Length,
            Inputs = order.Select(name => NamedInput.FromFloat32(name, new[] { rows.Length }, columns[name])).ToArray(),
            Options = new ModelCallOptions(Seed: seed),
        };
        var outputs = new Outputs();
        var pending = client.InvokeAsync(call, outputs, CancellationToken.None);
        Assert.True(pending.IsCompletedSuccessfully, "random@1 answers synchronously");
        return (pending.Result, outputs.Values);
    }

    private sealed class Outputs : IModelOutputWriter
    {
        public Dictionary<string, float[]> Values { get; } = new();

        public Memory<float> GetFloat32(string name, int elementCount) => Values[name] = new float[elementCount];

        public void SetText(string name, string text) => throw new NotSupportedException();
    }
}

/// <summary>What a call of <c>random@1</c> allocates: nothing, per call or per row, and so never more than <c>linear@1</c>.</summary>
[Collection(AllocationMeasurement.Name)]
public sealed class RandomModelClientAllocationTests
{
    [Fact]
    public void ACall_AllocatesNothing_AndNoMoreThanLinear()
    {
        var random = RandomModelClientTests.Create("""{ "inputs": ["smoothed_ctr", "log_followers"], "outputs": ["p_click", "p_like"] }""");
        const string LinearSettings = """
            { "outputs": { "p_click": { "weights": { "smoothed_ctr": 8.0, "log_followers": 0.05 }, "activation": "sigmoid" }, "p_like": { "weights": { "smoothed_ctr": 2.0 } } } }
            """;
        var linear = new LinearModelClientFactory().Create(
            new BindingContext("ctr_model", JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(LinearSettings)!, AppContext.BaseDirectory, null));

        var randomBytes = BytesPerCall(random, rows: 512, calls: 200);
        var linearBytes = BytesPerCall(linear, rows: 512, calls: 200);

        Assert.Equal(0, randomBytes);
        Assert.True(randomBytes <= linearBytes, $"random@1 {randomBytes} B per call, linear@1 {linearBytes} B");
    }

    /// <summary>
    /// Pairs and lists of vectors and int64 values, and a model with more inputs than the stack holds (their bookkeeping
    /// is rented from the pool): no call allocates either.
    /// </summary>
    [Fact]
    public void APairwiseOrListwiseCall_AndOneWithManyInputs_AllocatesNothing()
    {
        var pair = RandomModelClientTests.Create("""
            { "inputs": { "final": { "type": "float32", "shape": [-1, 2] }, "item_id": { "type": "int64", "shape": [-1, 2] }, "quality": { "type": "float32", "shape": [-1] } },
              "outputs": { "p_first": { "shape": [-1] } } }
            """);
        var pairCall = new ModelCall
        {
            Model = "ctr_model",
            BatchSize = 256,
            Inputs = new[]
            {
                NamedInput.FromFloat32("final", new[] { 256, 2 }, Enumerable.Range(0, 512).Select(i => i * 0.01f).ToArray()),
                NamedInput.FromInt64("item_id", new[] { 256, 2 }, Enumerable.Range(0, 512).Select(i => (long)i).ToArray()),
                NamedInput.FromFloat32("quality", new[] { 256 }, new float[256]),
            },
            Options = new ModelCallOptions(Seed: 3),
        };
        var list = RandomModelClientTests.Create("""
            { "inputs": { "final": { "type": "float32", "shape": [1, -1] }, "emb": { "type": "float32", "shape": [1, -1, 4] }, "seed": { "type": "int64", "shape": [1] } },
              "outputs": { "scores": { "shape": [1, -1] } } }
            """);
        var listCall = new ModelCall
        {
            Model = "ctr_model",
            BatchSize = 1,
            Inputs = new[]
            {
                NamedInput.FromFloat32("final", new[] { 1, 50 }, Enumerable.Range(0, 50).Select(i => i * 0.01f).ToArray()),
                NamedInput.FromFloat32("emb", new[] { 1, 50, 4 }, Enumerable.Range(0, 200).Select(i => i * 0.01f).ToArray()),
                NamedInput.FromInt64("seed", new[] { 1 }, new[] { 7L }),
            },
            Options = new ModelCallOptions(Seed: 3),
        };
        var names = Enumerable.Range(0, 40).Select(i => $"x{i:D2}").ToArray();
        var wide = RandomModelClientTests.Create($$"""{ "inputs": [{{string.Join(", ", names.Select(n => $"\"{n}\""))}}], "outputs": ["p_click"] }""");
        var wideCall = new ModelCall
        {
            Model = "ctr_model",
            BatchSize = 64,
            Inputs = names.Select(n => NamedInput.FromFloat32(n, new[] { 64 }, new float[64])).ToArray(),
            Options = new ModelCallOptions(Seed: 3),
        };

        Assert.Equal(0, BytesPerCall(pair, pairCall, new PreallocatedOutputs(256, "p_first"), calls: 100));
        Assert.Equal(0, BytesPerCall(list, listCall, new PreallocatedOutputs(50, "scores"), calls: 100));
        Assert.Equal(0, BytesPerCall(wide, wideCall, new PreallocatedOutputs(64, "p_click"), calls: 100));
    }

    /// <summary>The average bytes one call allocates on this thread, after warming up; the call and its buffers are reused.</summary>
    private static double BytesPerCall(IModelClient client, int rows, int calls)
    {
        var data = RandomModelClientTests.Rows(rows);
        var call = new ModelCall
        {
            Model = "ctr_model",
            BatchSize = rows,
            Inputs = new[]
            {
                NamedInput.FromFloat32("log_followers", new[] { rows }, data.Select(r => r.LogFollowers).ToArray()),
                NamedInput.FromFloat32("smoothed_ctr", new[] { rows }, data.Select(r => r.SmoothedCtr).ToArray()),
            },
            Options = new ModelCallOptions(Seed: 3),
        };
        return BytesPerCall(client, call, new PreallocatedOutputs(rows, "p_click", "p_like"), calls);
    }

    private static double BytesPerCall(IModelClient client, ModelCall call, IModelOutputWriter outputs, int calls)
    {
        for (var i = 0; i < 20; i++)
        {
            Consume(client.InvokeAsync(call, outputs, CancellationToken.None));
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < calls; i++)
        {
            Consume(client.InvokeAsync(call, outputs, CancellationToken.None));
        }

        return (double)(GC.GetAllocatedBytesForCurrentThread() - before) / calls;
    }

    private static void Consume(ValueTask<ModelStatus> pending)
    {
        if (!pending.IsCompletedSuccessfully || !pending.Result.Succeeded)
        {
            throw new InvalidOperationException("the call did not succeed synchronously");
        }
    }

    private sealed class PreallocatedOutputs : IModelOutputWriter
    {
        private readonly Dictionary<string, float[]> _buffers = new(StringComparer.Ordinal);

        public PreallocatedOutputs(int rows, params string[] names)
        {
            foreach (var name in names)
            {
                _buffers[name] = new float[rows];
            }
        }

        public Memory<float> GetFloat32(string name, int elementCount) => _buffers[name].AsMemory(0, elementCount);

        public void SetText(string name, string text) => throw new NotSupportedException();
    }
}
