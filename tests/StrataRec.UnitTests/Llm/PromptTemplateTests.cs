using StrataRec.Llm;
using StrataRec.Llm.Templates;
using StrataRec.Models;
using StrataRec.Pipeline.Operators;
using static StrataRec.UnitTests.Llm.LlmFixture;

namespace StrataRec.UnitTests.Llm;

/// <summary>
/// Prompt templates bound to StrataRec (design 5.9 rule 1), through <c>LlmReranker@1</c> and a model that captures its
/// prompts: names resolved at load and fetched before the node, how every kind of value renders, the sections, and every
/// load error at the template's pointer with the tag's offset and a hint.
/// </summary>
public sealed class PromptTemplateTests
{
    private const string Pointer = "/nodes/rank/config/prompts/user";

    /// <summary>Every kind of value and section: context, parameters, a nullable guard each way, lists, and the window's rows.</summary>
    internal const string Everything = """
        Query: {{ctx.request.query}} (budget {{ctx.request.budget}}, vip {{ctx.request.vip}}, tier {{ctx.user.tier}}, tone {{param.tone}}, boost {{ param.boost }})
        {{#ctx.user.name}}Hello {{ctx.user.name}}!{{/ctx.user.name}}{{^ctx.request.nickname}}No nickname.{{/ctx.request.nickname}}
        Interests:{{#ctx.request.interests}} {{.}}{{/ctx.request.interests}}; ids:{{#ctx.request.ids}} {{.}}{{/ctx.request.ids}}
        {{! one line per row; the standalone section tags leave no blank lines }}
        {{#rows}}
        {{rank}}. {{col.item.title}} | {{col.item.price}} | {{col.item.score}} | {{col.item.stock}} | {{col.item.fresh}} | {{#col.item.blurb}}{{col.item.blurb}}{{/col.item.blurb}}{{^col.item.blurb}}no blurb{{/col.item.blurb}} | {{#col.item.tags}}#{{.}}{{/col.item.tags}}{{^col.item.tags}}untagged{{/col.item.tags}} | {{#col.item.codes}}{{.}};{{/col.item.codes}} | {{#col.item.rating}}{{.}}★{{/col.item.rating}}{{#col.item.fresh}} NEW{{/col.item.fresh}}
        {{/rows}}
        Answer with the ranks.
        """;

    [Fact]
    public async Task ThePrompt_RendersEveryKindOfValue_AsSpecified()
    {
        var models = new TextModels("[2, 1]");
        var plan = Compile(models, Rerank(Everything, window: 4));

        var result = await Run(plan);

        Assert.True(result.Succeeded, result.Error);
        var call = Assert.Single(models.Calls);
        Assert.Equal(
            "Query: gifts for \"dad\" (budget 0.1, vip true, tier 2, tone friendly, boost 1.5)\n"
            + "Hello Ada!No nickname.\n"
            + "Interests: tea lamps; ids: 3 -4\n"
            + "1. Green\nscarf | 1E-07 | 3.4028235E+38 | -12 | true | Warm | #green | 1; | 0.1★ NEW\n"
            + "2. Red \"shoes\" | 19.99 | 0.1 | 3 | true | Bright & bold | #red#shoe | 7;-9007199254740993; |  NEW\n"
            + "3. Mug | 0.30000000000000004 | -0.5 | 1 | true |  | untagged | 4; | -1.25★ NEW\n"
            + "4. Blue hat | 5 | 0.25 | 0 | false | no blurb | untagged |  | 4.5★\n"
            + "Answer with the ranks.",
            call.Prompts["user"]);
    }

    [Fact]
    public async Task EveryPrompt_IsRenderedOnce_WithTheFirstWindowRows_AndSentInNameOrder()
    {
        var models = new TextModels(_ => ("[30]", ModelStatus.Ok), _ => TextModels.Text("system", "user"));
        const string Config = """
            { "model": "llm", "output": "answer", "answer": "entity_id", "window": 2,
              "prompts": { "user": "{{#rows}}{{col.entity_id}}={{col.item.title}};{{/rows}}", "system": "You rank {{param.tone}}ly." } }
            """;

        await Run(Compile(models, Config));

        var call = Assert.Single(models.Calls);
        Assert.Equal(new[] { "system", "user" }, call.Prompts.Keys);
        Assert.Equal("You rank friendlyly.", call.Prompts["system"]);
        Assert.Equal("30=Green\nscarf;10=Red \"shoes\";", call.Prompts["user"]);
    }

    /// <summary>The features a template names are the node's reads: fetched before it runs, though nothing else reads them.</summary>
    [Fact]
    public async Task AFeatureOnlyTheTemplateNames_IsFetchedBeforeTheNode()
    {
        var models = new TextModels("[1]");

        await Run(Compile(models, Rerank("{{ctx.user.tier}}:{{#rows}}{{col.item.extra}},{{/rows}}", window: 7)));

        Assert.Equal("2:x30,x10,x50,x20,x40,x70,x60,", Assert.Single(models.Calls).Prompts["user"]);
    }

    [Theory]
    [InlineData("Hi {{ctx.request.qeury}}", 3, "MUSTACHE_UNKNOWN_NAME", "did you mean 'ctx.request.query'?")]
    [InlineData("Hi {{title}}", 3, "MUSTACHE_UNKNOWN_NAME", "a template names col.* (inside {{#rows}}), ctx.* and param.* references, rows and rank")]
    [InlineData("Top: {{col.item.title}}", 5, "MUSTACHE_ROW_OUTSIDE_ROWS", "move it inside {{#rows}}…{{/rows}}, or render a ctx.* value")]
    [InlineData("{{#col.item.tags}}{{.}}{{/col.item.tags}}", 0, "MUSTACHE_ROW_OUTSIDE_ROWS", "move it inside")]
    [InlineData("#{{rank}}", 1, "MUSTACHE_ROW_OUTSIDE_ROWS", "move it inside {{#rows}}…{{/rows}}")]
    [InlineData("{{rows}}", 0, "MUSTACHE_ROWS", "write {{#rows}}…{{/rows}}")]
    [InlineData("{{#rows}}{{#rows}}x{{/rows}}{{/rows}}", 9, "MUSTACHE_ROWS", "close the outer section")]
    [InlineData("{{^rows}}none{{/rows}}", 0, "MUSTACHE_SECTION_TYPE", "remove the inverted section")]
    [InlineData("{{#rows}}{{#rank}}x{{/rank}}{{/rows}}", 9, "MUSTACHE_SECTION_TYPE", "write {{rank}}")]
    [InlineData("{{.}}", 0, "MUSTACHE_IMPLICIT", "use {{.}} inside a section over a list")]
    [InlineData("{{#rows}} {{.}}{{/rows}}", 10, "MUSTACHE_IMPLICIT", "name the value to show")]
    [InlineData("{{#ctx.request.interests}}{{#.}}x{{/.}}{{/ctx.request.interests}}", 26, "MUSTACHE_SECTION_TYPE", "iterate a list by its name")]
    [InlineData("{{#rows}}{{col.item.emb}}{{/rows}}", 9, "MUSTACHE_UNRENDERABLE", "render a derived feature")]
    [InlineData("{{#rows}}{{#col.item.emb}}x{{/col.item.emb}}{{/rows}}", 9, "MUSTACHE_UNRENDERABLE", "render a derived feature")]
    [InlineData("I like {{ctx.request.interests}}", 7, "MUSTACHE_UNRENDERABLE", "iterate it: {{#ctx.request.interests}}{{.}}, {{/ctx.request.interests}}")]
    [InlineData("{{#ctx.request.query}}q{{/ctx.request.query}}", 0, "MUSTACHE_SECTION_TYPE", "show the value with {{ctx.request.query}}")]
    [InlineData("{{^param.boost}}none{{/param.boost}}", 0, "MUSTACHE_SECTION_TYPE", "a section takes a list")]
    [InlineData("Hi {{> greeting}}", 3, "MUSTACHE_PARTIAL", "write the text in the template itself")]
    [InlineData("{{=<% %>=}}", 0, "MUSTACHE_DELIMITER", "use the standard {{ }} delimiters")]
    [InlineData("{{#rows}}x", 0, "MUSTACHE_UNCLOSED_SECTION", "close it with {{/rows}}")]
    public void AnInvalidTemplate_IsALoadError_AtItsPointer_WithTheOffset_AndAHint(string template, int offset, string code, string hint)
    {
        var error = Error(TryCompile(new TextModels("[1]"), Rerank(template)));

        Assert.Equal((Pointer, code), (error.Pointer, error.Code));
        Assert.EndsWith($"(at offset {offset} of the template)", error.Message);
        Assert.Contains(hint, error.Hint);
    }

    /// <summary>Missing is explicit: a nullable value renders only inside a section on the same name, which tests it.</summary>
    [Theory]
    [InlineData("{{ctx.user.name}}", 0)]
    [InlineData("{{^ctx.user.name}}{{ctx.user.name}}{{/ctx.user.name}}", 18)]
    [InlineData("{{#ctx.request.vip}}{{ctx.user.name}}{{/ctx.request.vip}}", 20)]
    [InlineData("{{#rows}}{{col.item.rating}}{{/rows}}", 9)]
    [InlineData("{{#rows}}{{#col.item.rating}}{{/col.item.rating}}{{col.item.rating}}{{/rows}}", 49)]
    public void ANullableValue_OutsideASectionOnItsName_IsALoadError(string template, int offset)
    {
        var error = Error(TryCompile(new TextModels("[1]"), Rerank(template)));

        Assert.Equal((Pointer, "MUSTACHE_NULLABLE"), (error.Pointer, error.Code));
        Assert.Contains("may be missing", error.Message);
        Assert.EndsWith($"(at offset {offset} of the template)", error.Message);
        var name = error.Message[(error.Message.IndexOf('\'') + 1)..];
        name = name[..name.IndexOf('\'')];
        Assert.StartsWith($"guard it: {{{{#{name}}}}}{{{{{name}}}}}{{{{/{name}}}}}{{{{^{name}}}}}unknown{{{{/{name}}}}}, or render a non-nullable derived feature", error.Hint);
    }

    [Fact]
    public void EveryError_OfEveryPrompt_IsReported()
    {
        var models = new TextModels(_ => ("[1]", ModelStatus.Ok), _ => TextModels.Text("system", "user"));
        const string Config = """
            { "model": "llm", "output": "answer", "answer": "rank",
              "prompts": { "user": "{{ctx.nope}} {{col.item.title}}", "system": "{{ctx.user.name}}", "extra~/": "" } }
            """;

        var compilation = TryCompile(models, Config);

        Assert.False(compilation.Succeeded);
        Assert.Equal(
            new[]
            {
                ("/nodes/rank/config/prompts/extra~0~1", "MUSTACHE_EMPTY"), ("/nodes/rank/config/prompts/system", "MUSTACHE_NULLABLE"),
                (Pointer, "MUSTACHE_UNKNOWN_NAME"), (Pointer, "MUSTACHE_ROW_OUTSIDE_ROWS"),
            },
            compilation.Diagnostics.Where(d => d.IsError).Select(d => (d.Pointer, d.Code)));
    }
}

/// <summary>
/// What rendering a prompt allocates: the prompt string and nothing else, however many rows, lists and sections the
/// template has (design 5.9; AGENTS.md: no per-row allocation on the request path).
/// </summary>
[Collection(AllocationMeasurement.Name)]
public sealed class PromptTemplateAllocationTests
{
    [Fact]
    public async Task RenderingAPrompt_AllocatesOnlyThePromptString()
    {
        var probe = new PromptProbeFactory();
        var plan = Compile(new TextModels("[1]"), $$"""{ "template": {{System.Text.Json.JsonSerializer.Serialize(PromptTemplateTests.Everything)}} }""",
            operators: OperatorRegistry.CreateDefault().AddLlm().Add(probe), op: "PromptProbe@1");

        var result = await Run(plan);

        Assert.True(result.Succeeded, result.Error);
        Assert.StartsWith("Query: gifts for \"dad\"", probe.Prompt);
        Assert.Contains("6. Desk | 120 | 7 | 1 | true | Oak | #wood | 6; | 5★ NEW\n7. Lamp | 30 | 2 | 2 | false | Glows | #light | 5; | 2★\n", probe.Prompt);
        Assert.True(probe.StringBytes > 0);
        Assert.Equal(probe.StringBytes, probe.RenderBytes);
    }

    private sealed class PromptProbeConfig
    {
        public required string Template { get; init; }
    }

    /// <summary>
    /// <c>PromptProbe@1</c>: renders its template with every live row many times, and measures what each render allocates
    /// against what allocating a string of the prompt's length costs, on the request's thread.
    /// </summary>
    private sealed class PromptProbeFactory : IOperatorFactory
    {
        private const int Renders = 200;

        public OperatorDescriptor Descriptor { get; } = new("PromptProbe", 1, Effects.RowOrder, typeof(PromptProbeConfig), "Measures prompt rendering.");

        public string Prompt { get; private set; } = string.Empty;

        public double RenderBytes { get; private set; }

        public double StringBytes { get; private set; }

        public Operator? Compile(OperatorCompileContext context)
        {
            var config = context.BindConfig<PromptProbeConfig>();
            var template = config is null ? null : PromptTemplate.Compile(context, config.Template, "/template");
            return template is null ? null : new Probe(this, template);
        }

        private sealed class Probe(PromptProbeFactory factory, PromptTemplate template) : Operator
        {
            public override ValueTask ExecuteAsync(OperatorContext context)
            {
                var rows = context.LiveRows.ToArray();
                var prompt = string.Empty;
                for (var i = 0; i < 5; i++)
                {
                    prompt = template.Render(context, rows);
                }

                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < Renders; i++)
                {
                    prompt = template.Render(context, rows);
                }

                var rendered = GC.GetAllocatedBytesForCurrentThread() - before;
                before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < Renders; i++)
                {
                    GC.KeepAlive(new string(' ', prompt.Length));
                }

                var strings = GC.GetAllocatedBytesForCurrentThread() - before;
                (factory.Prompt, factory.RenderBytes, factory.StringBytes) = (prompt, (double)rendered / Renders, (double)strings / Renders);
                return ValueTask.CompletedTask;
            }
        }
    }
}
