using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using StrataRec.Artifacts.Model;
using StrataRec.CommandLine;
using StrataRec.Experiments.Assignment;
using StrataRec.Hosting;
using StrataRec.Samples.Host;
using StrataRec.Testing;

namespace StrataRec.Samples.Host.Tests;

/// <summary>
/// The host sample (samples/StrataRec.Samples.Host) started in-process: its workspace loads clean with its own components,
/// the stratarec commands run with them, <c>POST /recommend</c> serves items that its custom source recalled, its custom
/// connector fed, its custom model scored and its custom operator boosted, and the engine's error kinds map to 404, 400,
/// 504 and 500.
/// </summary>
public sealed class HostSampleTests : IClassFixture<HostSampleTests.SampleHost>, IDisposable
{
    private const string Pipeline = "trending_feed";

    private readonly SampleHost _host;

    private readonly List<string> _copies = new(); // workspace copies this test made, deleted when it ends

    public HostSampleTests(SampleHost host) => _host = host;

    public void Dispose()
    {
        foreach (var copy in _copies)
        {
            Directory.Delete(copy, recursive: true);
        }
    }

    /// <summary>The sample's workspace as the test output holds it.</summary>
    private static string ConfigRoot => Path.Combine(AppContext.BaseDirectory, "samples", "StrataRec.Samples.Host", "config");

    [Fact]
    public async Task TheWorkspace_Validates_AndLoads_WithNoErrorsOrWarnings()
    {
        // What `dotnet run -- stratarec validate -c config` runs: the stock commands with this binary's components.
        var (code, validated, _) = await Stratarec("validate", "-c", ConfigRoot, "--format", "json");
        Assert.True(code == 0, validated.ToJsonString());
        Assert.Equal((0, 0), (validated["errors"]!.GetValue<int>(), validated["warnings"]!.GetValue<int>()));

        // What the service does at startup: compile, load every binding (kv_store@1 reads its files), check the model's signature.
        await using var engine = new RecEngine(SampleEnvironment.Create() with { BaseDirectory = ConfigRoot });
        var loaded = await ServiceCollectionExtensions.LoadWorkspaceAsync(engine, ConfigRoot, "local");
        Assert.True(Clean(loaded.Items), string.Join("\n", loaded.Items));
        Assert.Equal(new[] { Pipeline }, engine.PipelineIds());
    }

    [Fact]
    public async Task Validate_ReportsMistakesInTheCustomComponentsSettings_AtTheirPointers()
    {
        var root = CopyOfTheWorkspace();
        Edit(Path.Combine(root, "pipelines", "trending_feed.jsonc"), "\"local_news\": 1.5", "\"local_news\": 0");
        Edit(Path.Combine(root, "bindings", "local.jsonc"), "\"data_file\": \"data/users.jsonl\"", "\"data_fle\": \"data/users.jsonl\"");
        Edit(Path.Combine(root, "bindings", "local.jsonc"), "\"version\": \"v3\"", "\"version\": \"v4\"");

        var (code, report, _) = await Stratarec("validate", "-c", root, "--format", "json");

        Assert.Equal(1, code);
        var errors = report["diagnostics"]!.AsArray()
            .Where(d => d!["severity"]!.GetValue<string>() == "error")
            .Select(d => (d!["code"]!.GetValue<string>(), d["artifact"]!.GetValue<string>() + d["pointer"]!.GetValue<string>()))
            .ToList();
        Assert.Contains(("BOOST_INVALID", "pipeline:trending_feed/nodes/editorial_boost/config/boosts/local_news"), errors);
        Assert.Contains(("CONNECTOR_CONFIG", "bindings:local/sources/user_store/data_fle"), errors);
        Assert.Contains(("MODEL_SIGNATURE", "pipeline:trending_feed/nodes/engagement/config/model"), errors); // the node calls v3; the binding serves v4
        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public async Task TheCustomOperator_HasTheJsonSchemaOfItsConfigClass()
    {
        var (code, schema, _) = await Stratarec("schema", "operator", "CategoryBoost@1");

        Assert.Equal(0, code);
        Assert.Equal("CategoryBoost@1 config", schema["title"]!.GetValue<string>());
        Assert.Equal(new[] { "score_col", "category_col", "output_col", "boosts" }, schema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        Assert.Contains("keeps its score", schema["properties"]!["category_col"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);

        var unknown = await StratarecText("schema", "operator", "CategoryBoost@2");
        Assert.Equal(2, unknown.Code);
        Assert.Contains("Unknown operator 'CategoryBoost@2'", unknown.Error, StringComparison.Ordinal);
        Assert.Contains("CategoryBoost@1", unknown.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Operators_ListTheCustomComponents_NextToTheBuiltIns()
    {
        var (code, listed, _) = await Stratarec("operators", "--format", "json");

        Assert.Equal(0, code);
        var operators = listed["operators"]!.AsArray().Select(o => o!["id"]!.GetValue<string>()).ToList();
        Assert.Contains("CategoryBoost@1", operators);
        Assert.Contains("TopK@1", operators);
        var capabilities = listed["capabilities"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();
        Assert.Contains("kv_store@1", capabilities);
        Assert.Contains("logistic@1", capabilities);
        Assert.Contains("trending@1", capabilities);
    }

    [Fact]
    public async Task Run_ServesTheRequestFile_WithTheCustomComponents_AsTheServiceAnswersIt()
    {
        // The HTTP body names the pipeline in the route; a request file for `stratarec run` names it in pipeline_id.
        var body = await File.ReadAllTextAsync(Path.Combine(ConfigRoot, "requests", "user_1001.json"));
        var file = JsonNode.Parse(body)!.AsObject();
        file["pipeline_id"] = Pipeline;
        var request = Path.Combine(CopyOfTheWorkspace(), "requests", "user_1001.cli.json");
        await File.WriteAllTextAsync(request, file.ToJsonString());
        var (code, local, error) = await Stratarec("run", "-c", ConfigRoot, "-r", request, "--deadline-ms", SampleBudgets.UntimedMs.ToString(CultureInfo.InvariantCulture));
        Assert.True(code == 0, error);

        var served = await Post(_host.CreateClient(), Pipeline, body);

        // One document (RecResultJson): the same items, ranks, ids and columns, and the same record of experiments.
        Assert.Equal(Items(served.Json), Items(local));
        Assert.Equal(served.Json["experiments"]!.ToJsonString(), local["experiments"]!.ToJsonString());
        Assert.Equal("req-1001", local["request_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Recommend_RanksWithTheCustomComponents_AndTheAssignedArm()
    {
        var body = await File.ReadAllTextAsync(Path.Combine(ConfigRoot, "requests", "user_1001.json")); // region us, arm strong
        var response = await Post(_host.CreateClient(), Pipeline, body);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        var result = response.Json;
        Assert.Equal("req-1001", result["request_id"]!.GetValue<string>());
        Assert.True(result["succeeded"]!.GetValue<bool>());
        Assert.Equal(new[] { "exp_local_boost/strong" }, Strings(result["experiments"]!["assigned"]));
        Assert.Equal(new[] { "exp_local_boost" }, Strings(result["experiments"]!["triggered"]));
        var items = Items(result);
        Assert.Equal(8, items.Count); // final_top_k
        Assert.Equal(Enumerable.Range(1, items.Count), items.Select(i => i.Rank));

        var data = ItemData();
        foreach (var item in items)
        {
            // kv_store@1 fed the features: the category as stored, null where the store has none.
            Assert.Equal(data[item.Id].Category, item.Category);

            // logistic@1 scored the rows (its fallback would have written the raw ctr).
            Assert.Equal(Engagement(data[item.Id], affinity: 0.8), item.Engage, 5);

            // CategoryBoost@1 wrote the boosted score: the strong arm's 2.0 for local news, the engagement otherwise.
            Assert.Equal(item.Engage * (item.Category == "local_news" ? 2.0 : 1.0), item.Score, 5);

            // trending@1 recalled the region's list; the editors' picks come from the built-in memory_list@1.
            Assert.True(item.Source == "editorial" ? new long[] { 21, 22, 3, 23 }.Contains(item.Id) : UsTrending.Contains(item.Id), $"item {item.Id} from {item.Source}");
        }

        // Ranked by the boosted score, which reorders the model's ranking: a boosted item outranks a better-engaging one.
        Assert.Equal(items.Select(i => i.Score).OrderByDescending(s => s), items.Select(i => i.Score));
        Assert.Contains(items, a => items.Any(b => a.Rank < b.Rank && a.Engage < b.Engage));
        Assert.Contains(items, i => i.Source == "trending");
        Assert.Contains(items, i => i.Source == "editorial");
    }

    [Fact]
    public async Task Recommend_WithoutAssignment_RunsTheBaseline_AndAnotherRegion_ReadsItsList()
    {
        var response = await Post(_host.CreateClient(), Pipeline, """{ "inputs": { "user_id": 1002, "region": "de" } }""");

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Empty(Strings(response.Json["experiments"]!["assigned"]));
        var data = ItemData();
        var items = Items(response.Json);
        Assert.NotEmpty(items);
        Assert.All(items.Where(i => i.Source == "trending"), i => Assert.DoesNotContain(i.Id, UsTrending));
        Assert.All(items, i => Assert.Equal(Engagement(data[i.Id], affinity: 0.2) * (i.Category == "local_news" ? 1.5 : 1.0), i.Score, 5));
    }

    [Fact]
    public async Task Recommend_ReportsAnArmThePlanDoesNotHave_AsIgnored()
    {
        var response = await Post(_host.CreateClient(), Pipeline, """{ "assignment": ["exp_gone/treatment"] }""");

        Assert.Equal(HttpStatusCode.OK, response.Status);
        var ignored = Assert.Single(response.Json["experiments"]!["ignored"]!.AsArray());
        Assert.Equal(("exp_gone/treatment", "unknown"), (ignored!["id"]!.GetValue<string>(), ignored["reason"]!.GetValue<string>()));
    }

    [Fact]
    public async Task AnUnknownPipeline_Is404()
    {
        var response = await Post(_host.CreateClient(), "home_feed", "{}");

        Assert.Equal(HttpStatusCode.NotFound, response.Status);
        Assert.Equal("Unknown pipeline 'home_feed'.", response.Json["detail"]!.GetValue<string>());
        Assert.Equal(new[] { Pipeline }, Strings(response.Json["pipelines"]));
    }

    [Theory]
    [InlineData("""{ "inputs": { "user_id": "u-1001" } }""", "/inputs/user_id", "Input 'user_id' must be an integer (int64), got the string \"u-1001\".")]
    [InlineData("""{ "inputs": { "country": "us" } }""", "/inputs/country", "Input 'country' is not declared in the request_schema of pipeline 'trending_feed'.")]
    [InlineData("""{ "inputs": { "user_id": 1001.5 } }""", "/inputs/user_id", "got the number 1001.5")]
    [InlineData("""{ "inputs": [] }""", "/inputs", "'inputs' must be an object")]
    [InlineData("""{ "input": {} }""", "/input", "Unknown member 'input'")]
    [InlineData("""{ "assignment": "exp_local_boost/strong" }""", "/assignment", "must be an array")]
    [InlineData("""{ "assignment": [7] }""", "/assignment/0", "An arm id is a string")]
    [InlineData("""{ "request_id": "" }""", "/request_id", "request_id must be a non-empty string")]
    [InlineData("""{ "inputs": """, "", "not JSON")]
    [InlineData("""[]""", "", "must be a JSON object")]
    // Review 32: a JsonObject throws on a duplicate name only when its members are enumerated, which gave an empty 500.
    [InlineData("""{ "inputs": {}, "inputs": {} }""", "/inputs", "Member 'inputs' is given more than once.")]
    [InlineData("""{ "inputs": { "user_id": 1001, "user_id": 1002 } }""", "/inputs/user_id", "Member 'user_id' is given more than once.")]
    [InlineData("""{ "assignment": [], "inputs": { "region": { "a": 1, "a": 2 } } }""", "/inputs/region/a", "Member 'a' is given more than once.")]
    [InlineData("""{ "request_id": "\uD800" }""", "/request_id", "not valid Unicode")]
    [InlineData("""{ "inputs": { "\uD800": 1 } }""", "/inputs", "not valid Unicode")]
    public async Task ABadRequest_Is400_WithThePointerOfTheProblem(string body, string pointer, string message)
    {
        var response = await Post(_host.CreateClient(), Pipeline, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal(pointer, response.Json["pointer"]!.GetValue<string>());
        Assert.Contains(message, response.Json["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(pointer, response.Json["errors"]![0]!["pointer"]!.GetValue<string>());
    }

    /// <summary>
    /// Review 33: bytes that are not UTF-8 parse, and threw when the name or string was read, which gave a 500. <c>#</c>
    /// stands for a raw <c>0xFF</c>.
    /// </summary>
    [Theory]
    [InlineData("""{ "inputs": { "#": 1 } }""", "/inputs", "The name of member 1 of this object is not valid UTF-8 text.")]
    [InlineData("""{ "request_id": "r-#" }""", "/request_id", "The string is not valid UTF-8 text.")]
    public async Task BytesThatAreNotUtf8_Are400_WithThePointerOfTheProblem(string body, string pointer, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        bytes[Array.IndexOf(bytes, (byte)'#')] = 0xFF;

        var response = await Post(_host.CreateClient(), Pipeline, bytes);

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal(pointer, response.Json["pointer"]!.GetValue<string>());
        Assert.Equal(message, response.Json["detail"]!.GetValue<string>());
        Assert.Equal("send the body as UTF-8 (Content-Type: application/json; charset=utf-8)", response.Json["errors"]![0]!["hint"]!.GetValue<string>());
    }

    [Fact]
    public async Task ABadRequest_ListsEveryInputAtFault()
    {
        var response = await Post(_host.CreateClient(), Pipeline, """{ "inputs": { "regoin": "us", "user_id": true } }""");

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        var errors = response.Json["errors"]!.AsArray();
        Assert.Equal(new[] { "/inputs/regoin", "/inputs/user_id" }, errors.Select(e => e!["pointer"]!.GetValue<string>()));
        Assert.Equal("did you mean 'region'?", errors[0]!["hint"]!.GetValue<string>());
    }

    [Fact]
    public async Task AFailedRequest_Is500_WithTheEnginesMessage()
    {
        // A store whose ctr_7d holds strings: kv_store@1 fails the fetch rather than serve a number it does not have, so the
        // model and its fallback (both read the feature) fail, and with them the request.
        var root = CopyOfTheWorkspace();
        File.WriteAllText(Path.Combine(root, "data", "items.jsonl"), """{"id": 1, "ctr_7d": "high", "age_hours": 2, "category": "sports"}""");
        using var broken = _host.WithWebHostBuilder(builder => builder.UseSetting("StrataRec:ConfigRoot", root));

        var response = await Post(broken.CreateClient(), Pipeline, """{ "inputs": { "user_id": 1001 } }""");

        Assert.Equal(HttpStatusCode.InternalServerError, response.Status);
        Assert.Contains("kv_store@1 field 'ctr_7d' holds string values", response.Json["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The engine's answer decides the status, also when it differs from what the host read: a plan loaded between reading
    /// the inputs and running them (review 32) is the engine's InvalidRequest, a 400 at the input's pointer, not a 500.
    /// </summary>
    [Theory]
    [InlineData(RecErrorKind.InvalidRequest, HttpStatusCode.BadRequest, "/inputs/tenant_id")]
    [InlineData(RecErrorKind.UnknownPipeline, HttpStatusCode.NotFound, null)]
    [InlineData(RecErrorKind.DeadlineExceeded, HttpStatusCode.GatewayTimeout, null)]
    [InlineData(RecErrorKind.Failed, HttpStatusCode.InternalServerError, null)]
    public async Task TheEnginesErrorKind_DecidesTheStatus(RecErrorKind kind, HttpStatusCode status, string? pointer)
    {
        var result = new RecResult
        {
            RequestId = "r",
            Succeeded = false,
            ErrorKind = kind,
            Error = "what the engine said",
            ErrorPointer = pointer,
            Items = Array.Empty<Pipeline.Execution.RankedItem>(),
            Experiments = new ExperimentRecord(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<IgnoredArm>()),
            ManifestId = string.Empty,
            ServedBy = Pipeline,
        };
        _ = _host.CreateClient(); // starts the host

        var http = new DefaultHttpContext { RequestServices = _host.Services };
        http.Response.Body = new MemoryStream();
        await RecommendEndpoint.Answer(result, _host.Services.GetRequiredService<IRecEngine>()).ExecuteAsync(http);

        Assert.Equal((int)status, http.Response.StatusCode);
        var problem = JsonNode.Parse(Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray()))!;
        if (pointer is not null)
        {
            Assert.Equal(pointer, problem["pointer"]!.GetValue<string>());
        }

        Assert.Equal(kind == RecErrorKind.UnknownPipeline ? $"Unknown pipeline '{Pipeline}'." : "what the engine said", problem["detail"]!.GetValue<string>());
    }

    [Fact]
    public void AWorkspaceWithErrors_StopsTheServiceAtStartup()
    {
        var root = CopyOfTheWorkspace();
        Edit(Path.Combine(root, "pipelines", "trending_feed.jsonc"), "\"local_news\": 1.5", "\"local_news\": 0");
        using var broken = _host.WithWebHostBuilder(builder => builder.UseSetting("StrataRec:ConfigRoot", root));

        var thrown = Record.Exception(() => broken.CreateClient());

        var config = Assert.IsType<ConfigException>(thrown is AggregateException { InnerExceptions: [var inner] } ? inner : thrown);
        Assert.Contains(config.Diagnostics, d => d.Code == "BOOST_INVALID");
    }

    [Fact]
    public async Task Healthz_ListsTheServedPipelines()
    {
        var response = await _host.CreateClient().GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("ok", json["status"]!.GetValue<string>());
        Assert.Equal(new[] { Pipeline }, Strings(json["pipelines"]));
    }

    /// <summary>The ids of trending@1's list for region us (bindings/local.jsonc).</summary>
    private static readonly long[] UsTrending = { 1, 2, 3, 5, 8, 9, 11, 13, 15, 17 };

    /// <summary>logistic@1 with the weights of bindings/local.jsonc, over the catalog's features and their defaults.</summary>
    private static double Engagement((double? Ctr, double? AgeHours, string? Category) item, double affinity)
    {
        var freshness = 1.0 / (1.0 + ((item.AgeHours ?? 72.0) / 24.0));
        var logit = -3.0 + (30.0 * (item.Ctr ?? 0.01)) + (1.5 * freshness) + (0.8 * affinity);
        return 1.0 / (1.0 + Math.Exp(-logit));
    }

    /// <summary>The records of data/items.jsonl: what kv_store@1 serves.</summary>
    private static Dictionary<long, (double? Ctr, double? AgeHours, string? Category)> ItemData()
    {
        var items = new Dictionary<long, (double?, double?, string?)>();
        foreach (var line in File.ReadAllLines(Path.Combine(ConfigRoot, "data", "items.jsonl")).Where(l => l.Length > 0))
        {
            var record = JsonNode.Parse(line)!;
            items[record["id"]!.GetValue<long>()] = (record["ctr_7d"]?.GetValue<double>(), record["age_hours"]?.GetValue<double>(), record["category"]?.GetValue<string>());
        }

        return items;
    }

    private static bool Clean(IEnumerable<ConfigDiagnostic> diagnostics) =>
        !diagnostics.Any(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning);

    /// <summary>A stratarec command with the sample's components (what <c>dotnet run -- stratarec …</c> runs); its output as JSON.</summary>
    private static async Task<(int Code, JsonNode Output, string Error)> Stratarec(params string[] args)
    {
        var (code, output, error) = await StratarecText(args);
        return (code, JsonNode.Parse(output) ?? throw new InvalidOperationException(error), error);
    }

    private static async Task<(int Code, string Output, string Error)> StratarecText(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await StrataRecCommandLine.InvokeAsync(args, SampleEnvironment.Create(), output, error);
        return (code, output.ToString(), error.ToString());
    }

    private static async Task<(HttpStatusCode Status, JsonNode Json)> Post(HttpClient client, string pipeline, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/recommend/" + pipeline, content);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    private static async Task<(HttpStatusCode Status, JsonNode Json)> Post(HttpClient client, string pipeline, byte[] body)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var response = await client.PostAsync("/recommend/" + pipeline, content);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    private static string[] Strings(JsonNode? array) => array!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    private static List<Item> Items(JsonNode result) => result["items"]!.AsArray().Select(n =>
    {
        var columns = n!["columns"]!;
        return new Item(n["rank"]!.GetValue<int>(), n["id"]!.GetValue<long>(), columns["score"]!.GetValue<double>(), columns["engage"]!.GetValue<double>(),
            columns["category"]?.GetValue<string>(), columns["source"]!.GetValue<string>());
    }).ToList();

    private string CopyOfTheWorkspace()
    {
        var root = Directory.CreateTempSubdirectory("stratarec-host-sample-").FullName;
        _copies.Add(root);
        CopyDirectory(ConfigRoot, root);
        return root;
    }

    private static void Edit(string path, string from, string to)
    {
        var text = File.ReadAllText(path);
        Assert.Contains(from, text, StringComparison.Ordinal);
        File.WriteAllText(path, text.Replace(from, to, StringComparison.Ordinal));
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var directory in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(from, to, StringComparison.Ordinal));
        }

        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(from, to, StringComparison.Ordinal), overwrite: true);
        }
    }

    private sealed record Item(int Rank, long Id, double Score, double Engage, string? Category, string Source);

    /// <summary>
    /// The sample's Program on a test server, one per test class. Untimed: the first request of the process compiles what it
    /// runs, and no machine should be slow enough to fail it (see <see cref="SampleBudgets.UntimedMs"/>).
    /// </summary>
    public sealed class SampleHost : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseSetting("StrataRec:DeadlineMs", SampleBudgets.UntimedMs.ToString(CultureInfo.InvariantCulture));
    }
}
