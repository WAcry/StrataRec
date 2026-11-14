using System.Text.Json;
using StrataRec.Core.Bindings;
using StrataRec.Llm;
using StrataRec.Models;

namespace StrataRec.UnitTests.Models;

/// <summary>
/// <c>fixed_text@1</c>, the stand-in for a generative model: it declares the text signature its settings give, and every
/// call answers the same fixed texts.
/// </summary>
public sealed class FixedTextModelClientTests
{
    private const string Settings = """{ "inputs": ["system", "user"], "outputs": { "answer": "[3, 1, 2]", "note": "" } }""";

    [Fact]
    public async Task TheSignature_IsTheDeclaredNames_AsText_ForAnyVersion_AnsweredAtOnce()
    {
        var client = Create(Settings);

        var lookup = client.GetSignatureAsync("llm", "v7", CancellationToken.None);

        Assert.True(lookup.IsCompletedSuccessfully);
        var signature = await lookup;
        Assert.Equal(new[] { "system text []", "user text []" }, signature.Inputs.Select(Describe));
        Assert.Equal(new[] { "answer text []", "note text []" }, signature.Outputs.Select(Describe));
        Assert.Equal(signature, await client.GetSignatureAsync("llm", null, CancellationToken.None));
        Assert.Equal("fixed_text@1", client.ClientType);

        // What LlmReranker@1 checks at load: the stand-in passes as the real model would.
        Assert.Empty(TextSignature.Check("Model 'llm' version 'v7'", "LlmReranker@1", [("system", "/prompts/system"), ("user", "/prompts/user")], [("answer", "/output")], signature));
        await Assert.ThrowsAsync<ArgumentException>(async () => await client.GetSignatureAsync("other", null, CancellationToken.None));
    }

    [Fact]
    public void EveryCall_AnswersTheFixedTexts_WithoutTokenCounts_WhateverThePromptsAndSeed()
    {
        var client = Create(Settings);

        var first = Invoke(client, seed: 1, ("user", "rank these"), ("system", "be brief"));
        var second = Invoke(client, seed: 2, ("system", "other"), ("user", "prompts"), ("extra", "ignored"));

        Assert.Equal((first.Status, first.Texts), (second.Status, second.Texts), (a, b) => a.Status == b.Status && a.Texts.SequenceEqual(b.Texts));
        Assert.Equal(new Dictionary<string, string> { ["answer"] = "[3, 1, 2]", ["note"] = "" }, first.Texts);
        Assert.Equal(ModelStatus.Ok, first.Status);
        Assert.Null(first.Status.OutputTokens);
        Assert.False(first.Status.Truncated);
    }

    [Fact]
    public async Task ACallWithoutADeclaredInput_OrWithANonTextOne_Fails_AnsweringNothing()
    {
        var client = Create(Settings);

        var missing = Invoke(client, seed: 1, ("user", "x"));
        var outputs = new Outputs();
        var call = new ModelCall
        {
            Model = "llm",
            Inputs = new[] { NamedInput.FromText("user", "x"), NamedInput.FromFloat32("system", new[] { 1 }, new[] { 1f }) },
            BatchSize = 1,
        };
        var wrongType = await client.InvokeAsync(call, outputs, CancellationToken.None);

        Assert.Equal((ModelStatus.Failed("Missing input 'system'."), 0), (missing.Status, missing.Texts.Count));
        Assert.Equal((ModelStatus.Failed("Input 'system' must be text."), 0), (wrongType, outputs.Texts.Count));
    }

    [Fact]
    public void ItIsInTheDefaultRegistry()
    {
        Assert.True(StrataRec.Pipeline.Planning.ModelClientRegistry.CreateDefault().TryGet("fixed_text@1", out var factory));
        Assert.IsType<FixedTextModelClient>(factory.Create(Context(Settings)));
    }

    [Theory]
    [InlineData("""{ "outputs": { "answer": "x" } }""", "", "fixed_text@1 needs 'inputs'")]
    [InlineData("""{ "inputs": ["user"] }""", "", "fixed_text@1 needs 'outputs'")]
    [InlineData("""{ "inputs": ["user"], "outputs": { "answer": "x" }, "seed": 1 }""", "/seed", "Unknown setting 'seed'.")]
    [InlineData("""{ "inputs": "user", "outputs": { "answer": "x" } }""", "/inputs", "'inputs' must be an array of names")]
    [InlineData("""{ "inputs": [], "outputs": { "answer": "x" } }""", "/inputs", "'inputs' is empty")]
    [InlineData("""{ "inputs": ["user", ""], "outputs": { "answer": "x" } }""", "/inputs/1", "'inputs' item 1 must be a non-empty name")]
    [InlineData("""{ "inputs": ["user", "user"], "outputs": { "answer": "x" } }""", "/inputs/1", "'user' is listed twice in 'inputs'.")]
    [InlineData("""{ "inputs": ["user"], "outputs": ["answer"] }""", "/outputs", "'outputs' must be an object")]
    [InlineData("""{ "inputs": ["user"], "outputs": { } }""", "/outputs", "'outputs' is empty")]
    [InlineData("""{ "inputs": ["user"], "outputs": { "answer": [3, 1] } }""", "/outputs/answer", "Output 'answer' must be a string")]
    [InlineData("""{ "inputs": ["user"], "outputs": { "": "x" } }""", "/outputs/", "'outputs' maps an empty name.")]
    [InlineData("""{ "inputs": ["user"], "outputs": { "a/b": "x", "a/b": "y" } }""", "/outputs/a~1b", "'a/b' is listed twice in 'outputs'.")]
    public void BadSettings_AreBindingErrors_AtTheirPointer_WithAHint(string settings, string pointer, string message)
    {
        var error = Assert.Throws<BindingException>(() => Create(settings));

        Assert.Equal(pointer, error.Pointer);
        Assert.StartsWith(message, error.Reason);
        Assert.False(string.IsNullOrWhiteSpace(error.Hint));
    }

    private static IModelClient Create(string settings) => new FixedTextModelClientFactory().Create(Context(settings));

    private static BindingContext Context(string settings) =>
        new("llm", ParseSettings(settings), AppContext.BaseDirectory, null);

    /// <summary>The settings' members in order, repeats included (a dictionary would keep only one).</summary>
    private static IReadOnlyDictionary<string, JsonElement> ParseSettings(string settings)
    {
        var members = new Dictionary<string, JsonElement>();
        foreach (var property in JsonDocument.Parse(settings).RootElement.EnumerateObject())
        {
            members[property.Name] = property.Value.Clone();
        }

        return members;
    }

    private static (ModelStatus Status, Dictionary<string, string> Texts) Invoke(IModelClient client, long seed, params (string Name, string Text)[] prompts)
    {
        var outputs = new Outputs();
        var call = new ModelCall
        {
            Model = "llm",
            Inputs = prompts.Select(p => NamedInput.FromText(p.Name, p.Text)).ToArray(),
            BatchSize = 1,
            Options = new ModelCallOptions(Seed: seed, MaxOutputTokens: 16),
        };
        var pending = client.InvokeAsync(call, outputs, CancellationToken.None);
        Assert.True(pending.IsCompletedSuccessfully);
        return (pending.Result, outputs.Texts);
    }

    private static string Describe(TensorSpec spec) => $"{spec.Name} {spec.ElementType.ToString().ToLowerInvariant()} [{string.Join(", ", spec.Shape)}]";

    private sealed class Outputs : IModelOutputWriter
    {
        public Dictionary<string, string> Texts { get; } = new();

        public Memory<float> GetFloat32(string name, int elementCount) => throw new InvalidOperationException("a text model writes no float32 output");

        public void SetText(string name, string text) => Texts[name] = text;
    }
}
