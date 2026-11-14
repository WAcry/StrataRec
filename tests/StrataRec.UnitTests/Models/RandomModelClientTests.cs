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
        Assert.All(signature.Inputs.Concat(signature.Outputs), s => Assert.True(ScorerSignature.IsRowTensor(s), s.ToString()));
        Assert.Equal(signature, await client.GetSignatureAsync("ctr_model", null, CancellationToken.None));

        // What ModelScorer@1 checks at load for the sample's ctr_ranker node: the stand-in passes as the real model would.
        var inputs = new ScorerSignature.MappedInput[]
        {
            new("smoothed_ctr", "col.item.smoothed_ctr", ColumnType.Float32), new("log_followers", "col.author.log_followers", ColumnType.Float32),
        };
        Assert.Empty(ScorerSignature.Check("Model 'ctr_model' version 'v13'", inputs, new Dictionary<string, string> { ["p_click"] = "score.ctr" }, signature));
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
        var outputs = new PreallocatedOutputs(rows, "p_click", "p_like");
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
