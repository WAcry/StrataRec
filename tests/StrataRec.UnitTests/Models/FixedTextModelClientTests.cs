using System.Text.Json;
using StrataRec.Core.Bindings;
using StrataRec.Llm;
using StrataRec.Models;
using StrataRec.Core.Services;

namespace StrataRec.UnitTests.Models;

public sealed class FixedTextModelClientTests
{
    private const string Settings = """{ "inputs": ["system", "user"], "outputs": { "answer": "[3, 1, 2]", "note": "" } }""";

    [Fact]
    public async Task TheSignature_IsTheDeclaredNames_AsText_ForAnyVersion_AnsweredAtOnce()
    {
        var client = Create(Settings);

        var lookup = client.DescribeAsync("llm", "v7", CancellationToken.None);

        Assert.True(lookup.IsCompletedSuccessfully);
        var signature = await lookup;
        Assert.Equal(new[] { "system text []", "user text []" }, signature.Inputs.Select(Describe));
        Assert.Equal(new[] { "answer text []", "note text []" }, signature.Outputs.Select(Describe));
        Assert.Equal(signature, await client.DescribeAsync("llm", null, CancellationToken.None));

        Assert.Empty(TextSignature.Check("Model 'llm' version 'v7'", "LlmReranker@1", [("system", "/prompts/system"), ("user", "/prompts/user")], [("answer", "/output")], signature));
        await Assert.ThrowsAsync<ArgumentException>(async () => await client.DescribeAsync("other", null, CancellationToken.None));
    }

    [Fact]
    public void EveryCall_AnswersTheFixedTexts_WithoutTokenCounts_WhateverThePromptsAndSeed()
    {
        var client = Create(Settings);

        var first = Invoke(client, seed: 1, ("user", "rank these"), ("system", "be brief"));
        var second = Invoke(client, seed: 2, ("system", "other"), ("user", "prompts"), ("extra", "ignored"));

        Assert.Equal((first.Status, first.Texts), (second.Status, second.Texts), (a, b) => a.Status == b.Status && a.Texts.SequenceEqual(b.Texts));
        Assert.Equal(new Dictionary<string, string> { ["answer"] = "[3, 1, 2]", ["note"] = "" }, first.Texts);
        Assert.Equal(ServiceStatus.Ok, first.Status);
        Assert.Null(first.Status.OutputTokens);
        Assert.False(first.Status.Truncated);
    }

    [Fact]
    public async Task ACallWithoutADeclaredInput_OrWithANonTextOne_Fails_AnsweringNothing()
    {
        var client = Create(Settings);

        var missing = Invoke(client, seed: 1, ("user", "x"));
        var outputs = new Outputs();
        var call = new ServiceCall
        {
            Service = "llm",
            Inputs = new[] { NamedInput.FromText("user", "x"), NamedInput.FromFloat32("system", new[] { 1 }, new[] { 1f }) },
            BatchSize = 1,
        };
        var wrongType = await client.CallAsync(call, outputs, CancellationToken.None);

        Assert.Equal((ServiceStatus.Failed("Missing input 'system'."), 0), (missing.Status, missing.Texts.Count));
        Assert.Equal((ServiceStatus.Failed("Input 'system' must be text."), 0), (wrongType, outputs.Texts.Count));
    }

    [Fact]
    public void ItIsInTheDefaultRegistry()
    {
        Assert.True(StrataRec.Pipeline.Planning.PlanEnvironment.DefaultBindings().TryGet<IServiceClient>("fixed_text@1", out var factory));
        Assert.IsType<FixedTextModelClient>(factory.Create(Context(Settings)));
    }

    [Theory]
    [InlineData("""{ "outputs": { "answer": "x" } }""", "", "'inputs' is required in the settings of fixed_text@1.")]
    [InlineData("""{ "inputs": ["user"] }""", "", "'outputs' is required in the settings of fixed_text@1.")]
    [InlineData("""{ "inputs": ["user"], "outputs": { "answer": "x" }, "seed": 1 }""", "/seed", "Unknown key 'seed' in the settings of fixed_text@1.")]
    [InlineData("""{ "inputs": "user", "outputs": { "answer": "x" } }""", "/inputs", "'inputs' must be an array, got the string \"user\".")]
    [InlineData("""{ "inputs": [], "outputs": { "answer": "x" } }""", "/inputs", "'inputs' is empty")]
    [InlineData("""{ "inputs": ["user", ""], "outputs": { "answer": "x" } }""", "/inputs/1", "'inputs' item 1 must be a non-empty name")]
    [InlineData("""{ "inputs": ["user", "user"], "outputs": { "answer": "x" } }""", "/inputs/1", "'user' is listed twice in 'inputs'.")]
    [InlineData("""{ "inputs": ["user"], "outputs": ["answer"] }""", "/outputs", "'outputs' must be an object")]
    [InlineData("""{ "inputs": ["user"], "outputs": { } }""", "/outputs", "'outputs' is empty")]
    [InlineData("""{ "inputs": ["user"], "outputs": { "answer": [3, 1] } }""", "/outputs/answer", "Entry 'answer' of 'outputs' must be a string")]
    [InlineData("""{ "inputs": ["user"], "outputs": { "": "x" } }""", "/outputs/", "'outputs' maps an empty name.")]
    [InlineData("""{ "inputs": ["user"], "outputs": { "a/b": "x", "a/b": "y" } }""", "/outputs/a~1b", "Duplicate key 'a/b'")]
    public void BadSettings_AreBindingErrors_AtTheirPointer_WithAHint(string settings, string pointer, string message)
    {
        var error = Assert.Throws<BindingException>(() => Create(settings));

        Assert.Equal(pointer, error.Pointer);
        Assert.StartsWith(message, error.Reason);
        Assert.False(string.IsNullOrWhiteSpace(error.Hint));
    }

    private static IServiceClient Create(string settings) => new FixedTextModelClientFactory().Create(Context(settings));

    private static BindingContext Context(string settings) =>
        new("llm", ParseSettings(settings), AppContext.BaseDirectory, null);

    private static IReadOnlyDictionary<string, JsonElement> ParseSettings(string settings)
    {
        var members = new Dictionary<string, JsonElement>();
        foreach (var property in JsonDocument.Parse(settings).RootElement.EnumerateObject())
        {
            members[property.Name] = property.Value.Clone();
        }

        return members;
    }

    private static (ServiceStatus Status, Dictionary<string, string> Texts) Invoke(IServiceClient client, long seed, params (string Name, string Text)[] prompts)
    {
        var outputs = new Outputs();
        var call = new ServiceCall
        {
            Service = "llm",
            Inputs = prompts.Select(p => NamedInput.FromText(p.Name, p.Text)).ToArray(),
            BatchSize = 1,
            Options = new ServiceCallOptions(Seed: seed, MaxOutputTokens: 16),
        };
        var pending = client.CallAsync(call, outputs, CancellationToken.None);
        Assert.True(pending.IsCompletedSuccessfully);
        return (pending.Result, outputs.Texts);
    }

    private static string Describe(TensorSpec spec) => $"{spec.Name} {spec.ElementType.ToString().ToLowerInvariant()} [{string.Join(", ", spec.Shape)}]";

    private sealed class Outputs : IServiceResponse
    {
        public void AddItem(long entityId, double score) => throw new NotSupportedException("a model answers with outputs");

        public void AddItem(string entityId, double score) => throw new NotSupportedException("a model answers with outputs");

        public Dictionary<string, string> Texts { get; } = new();

        public Memory<T> GetValues<T>(string name, int elementCount) => throw new InvalidOperationException("a text model writes no tensor output");

        public void SetText(string name, string text) => Texts[name] = text;
    }
}
