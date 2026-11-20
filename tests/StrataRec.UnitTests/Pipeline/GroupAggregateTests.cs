using StrataRec.Artifacts.Model;
using StrataRec.Core.Values;
using StrataRec.Experiments.Compilation;
using StrataRec.Hosting.Workspace;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// <c>GroupAggregate@1</c>: each statistic of a row's group written back to the row, missing where the row is in no group
/// or its group has no value; rank in the order TopK keeps; and the configurations it refuses, at their place.
/// </summary>
public sealed class GroupAggregateTests
{
    /// <summary>
    /// id → author (null: none), tag, score (null: none), weight. Author 10 has scores 0.9, 0.5 and 0.5 (items 2 and 3
    /// tie); author 20 has one score and one row without; item 6 has no author.
    /// </summary>
    private static readonly (long Id, long? Author, string Tag, double? Score, double Weight)[] Items =
    [
        (1, 10, "a", 0.9, 1),
        (2, 10, "a", 0.5, 2),
        (3, 10, "b", 0.5, 3),
        (4, 20, "b", null, 4),
        (5, 20, "c", 0.1, 5),
        (6, null, "c", 0.7, 6),
    ];

    private const string ByAuthor = """
        { "op": "GroupAggregate@1", "order": 200, "config": { "group_col": "item.author", "outputs": {
            "author.count": { "fn": "count" }, "author.rank": { "fn": "rank", "value_col": "item.score" },
            "author.sum": { "fn": "sum", "value_col": "item.score" }, "author.mean": { "fn": "mean", "value_col": "item.score" },
            "author.min": { "fn": "min", "value_col": "item.score" }, "author.max": { "fn": "max", "value_col": "item.score" },
            "author.std": { "fn": "std", "value_col": "item.score" } } } }
        """;

    [Fact]
    public async Task EachRow_GetsItsGroupsStatistic_MissingWhenItIsInNoGroupOrItsGroupHasNoValue()
    {
        var result = await Run(Compile(ByAuthor));

        var rows = ById(result);
        Assert.Equal([3L, 3, 3, 2, 2], new[] { 1L, 2, 3, 4, 5 }.Select(id => rows[id]["author_count"].AsInt64()));
        Assert.Equal([1L, 2, 3, 2, 1], new[] { 1L, 2, 3, 4, 5 }.Select(id => rows[id]["author_rank"].AsInt64())); // 2 before 3: ties by id; 4 has no score: last
        Assert.Equal(1.9, rows[1]["author_sum"].AsFloat64(), 12);
        Assert.Equal(1.9 / 3, rows[2]["author_mean"].AsFloat64(), 12);
        Assert.Equal(0.5, rows[3]["author_min"].AsFloat64());
        Assert.Equal(0.9, rows[3]["author_max"].AsFloat64());
        var mean = 1.9 / 3;
        Assert.Equal(Math.Sqrt((Math.Pow(0.9 - mean, 2) + (2 * Math.Pow(0.5 - mean, 2))) / 3), rows[1]["author_std"].AsFloat64(), 12);
        Assert.Equal(0.1, rows[4]["author_mean"].AsFloat64()); // the row without a score is left out of its group's values
        Assert.Equal(0.0, rows[5]["author_std"].AsFloat64());
        Assert.All(rows[6].Values, value => Assert.True(value.IsMissing)); // no author: in no group
    }

    [Fact]
    public async Task StringGroups_WholeTable_AndAscendingRank()
    {
        var plan = Compile("""
            { "op": "GroupAggregate@1", "order": 200, "config": { "group_col": "item.tag", "outputs": {
                "tag.count": { "fn": "count" }, "tag.rank": { "fn": "rank", "value_col": "item.weight", "ascending": true } } } }
            """, """
            { "op": "GroupAggregate@1", "order": 300, "config": { "outputs": {
                "all.count": { "fn": "count" }, "all.mean": { "fn": "mean", "value_col": "item.weight" } } } }
            """);

        var rows = ById(await Run(plan));

        Assert.Equal([2L, 2, 2, 2, 2, 2], rows.Keys.Order().Select(id => rows[id]["tag_count"].AsInt64()));
        Assert.Equal([1L, 2, 1, 2, 1, 2], rows.Keys.Order().Select(id => rows[id]["tag_rank"].AsInt64())); // lower weight first
        Assert.All(rows.Values, row => Assert.Equal(6, row["all_count"].AsInt64()));
        Assert.All(rows.Values, row => Assert.Equal(3.5, row["all_mean"].AsFloat64()));
    }

    [Fact]
    public async Task RankThenFilter_KeepsTheBestRowsOfEachGroup()
    {
        var plan = Compile(ByAuthor, """
            { "op": "ExpressionFilter@1", "order": 300, "config": { "predicate": "(col.author.rank ?? 1) <= 1" } }
            """);

        var result = await Run(plan);

        Assert.Equal([1L, 5, 6], ById(result).Keys.Order()); // the best of author 10 and of author 20; item 6 has no author
    }

    /// <summary>
    /// A node knows the columns the nodes before it on the timeline declare, whatever their ids: compiled by id, a reader
    /// named before its writer did not know the column ('a_filter' reading what 'z_rank' writes; 'a_boost' reading
    /// <c>recall.*</c>).
    /// </summary>
    [Fact]
    public async Task AReaderNamedBeforeItsWriter_KnowsTheColumnsItDeclares()
    {
        var plan = Compile(
            ("z_rank", """{ "op": "GroupAggregate@1", "order": 200, "config": { "group_col": "item.author", "outputs": { "author.rank": { "fn": "rank", "value_col": "item.weight" } } } }"""),
            ("a_filter", """{ "op": "ExpressionFilter@1", "order": 300, "config": { "predicate": "(col.author.rank ?? 1) <= 1 && col.recall.best_rank >= 0" } }"""));

        Assert.Equal([3L, 5, 6], ById(await Run(plan)).Keys.Order()); // the lowest weight of each author, and item 6
    }

    /// <summary>A group column with an error is the only error: the outputs' readers do not report them unknown.</summary>
    [Fact]
    public void AnErrorInTheGroupColumn_IsReportedOnce()
    {
        var compilation = TryCompile(
            ("agg", """{ "op": "GroupAggregate@1", "order": 200, "config": { "group_col": "item.auhtor", "outputs": { "author.rank": { "fn": "rank", "value_col": "item.weight" } } } }"""),
            ("cap", """{ "op": "ExpressionFilter@1", "order": 300, "config": { "predicate": "(col.author.rank ?? 1) <= 1" } }"""));

        var error = Assert.Single(compilation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(("COLUMN_UNKNOWN", "/nodes/agg/config/group_col", "did you mean 'item.author'?"), (error.Code, error.Pointer, error.Hint));
    }

    [Theory]
    [InlineData("""{ "outputs": { "x.c": { "fn": "cuont" } } }""", "/outputs/x.c/fn", "GROUP_AGGREGATE_FN", "did you mean 'count'?")]
    [InlineData("""{ "outputs": { "x.c": { "fn": "count", "value_col": "item.score" } } }""", "/outputs/x.c/value_col", "GROUP_AGGREGATE_VALUE", null)]
    [InlineData("""{ "outputs": { "x.c": { "fn": "sum" } } }""", "/outputs/x.c", "GROUP_AGGREGATE_VALUE", null)]
    [InlineData("""{ "outputs": { "x.c": { "fn": "sum", "value_col": "item.weight", "ascending": true } } }""", "/outputs/x.c/ascending", "GROUP_AGGREGATE_VALUE", null)]
    [InlineData("""{ "outputs": { "x.c": { "fn": "sum", "value_col": "item.tag" } } }""", "/outputs/x.c/value_col", "COLUMN_TYPE_MISMATCH", null)]
    [InlineData("""{ "outputs": {} }""", "/outputs", "GROUP_AGGREGATE_NO_OUTPUT", null)]
    [InlineData("""{ "group_col": "item.weight", "outputs": { "x.c": { "fn": "count" } } }""", "/group_col", "COLUMN_TYPE_MISMATCH", null)]
    [InlineData("""{ "group_col": "item.author", "outputs": { "declared.count": { "fn": "count" } } }""", "/outputs/declared.count", "COLUMN_TYPE_MISMATCH",
        "declare it as int32?, and read it with '??' or has()")]
    [InlineData("""{ "outputs": { "declared.mean": { "fn": "mean", "value_col": "item.score" } } }""", "/outputs/declared.mean", "COLUMN_TYPE_MISMATCH",
        "declare it as float32?, and read it with '??' or has()")]
    [InlineData("""{ "outputs": { "declared.mean": { "fn": "count" } } }""", "/outputs/declared.mean", "COLUMN_TYPE_MISMATCH", "declare it as int64")]
    public void Refused_AtItsPlace(string config, string pointer, string code, string? hint)
    {
        var compilation = TryCompile($$"""{ "op": "GroupAggregate@1", "order": 200, "config": {{config}} }""");

        Assert.False(compilation.Succeeded);
        var error = Assert.Single(compilation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(code, error.Code);
        Assert.Equal("/nodes/agg/config" + pointer, error.Pointer);
        if (hint is not null)
        {
            Assert.Equal(hint, error.Hint);
        }
    }

    [Fact]
    public void DeclaredColumns_OfTheRightKind_AreWritten_IntegersAndFloatsOfEitherWidth()
    {
        var compilation = TryCompile("""
            { "op": "GroupAggregate@1", "order": 200, "config": { "outputs": {
                "declared.count": { "fn": "count" }, "declared.mean": { "fn": "mean", "value_col": "item.weight" } } } }
            """);

        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
    }

    [Theory]
    [InlineData(10000000000000004.0, 17)] // review 44: the mean from the sum was one ulp off, and the deviations from it a std of 2
    [InlineData(0.1, 17)]                  // the same at the scale of a score: a z-score of ±1 for equal scores
    [InlineData(1e308, 2)]                 // the mean of values whose sum overflows
    public async Task EqualValues_HaveTheirValueAsMean_AndAStdOfZero(double value, int count)
    {
        var items = Enumerable.Range(1, count).Select(i => ((long)i, (long?)1, "a", (double?)value, 0.0)).ToArray();
        var plan = TryCompileOver(items, ("agg", """
            { "op": "GroupAggregate@1", "order": 200, "config": { "outputs": {
                "all.mean": { "fn": "mean", "value_col": "item.score" }, "author.std": { "fn": "std", "value_col": "item.score" } } } }
            """)).Plan!;

        var rows = ById(await Run(plan));

        Assert.All(rows.Values, row => Assert.Equal((value, 0.0), (row["all_mean"].AsFloat64(), row["author_std"].AsFloat64())));
    }

    [Theory]
    [InlineData(1e308, -1e308)] // review 45: x − mean overflowed, where the mean is 0
    [InlineData(-1e308, 1e308)]
    public async Task TheMeanOfLargeValuesOfOppositeSigns_IsFinite(double first, double second)
    {
        (long, long?, string, double?, double)[] items = [(1, 1, "a", first, 0), (2, 1, "a", second, 0)];
        var plan = TryCompileOver(items, ("agg", """
            { "op": "GroupAggregate@1", "order": 200, "config": { "outputs": { "all.mean": { "fn": "mean", "value_col": "item.score" } } } }
            """)).Plan!;

        var rows = ById(await Run(plan));

        Assert.All(rows.Values, row => Assert.Equal(0.0, row["all_mean"].AsFloat64()));
    }

    [Fact]
    public async Task Std_OfValuesAtLargeAndSmallScales_IsThePopulationStd()
    {
        double[] values = [1e9 + 1, 1e9 + 2, 1e9 + 3, 1e9 + 4, -0.5];
        var items = values.Select((v, i) => ((long)(i + 1), (long?)1, "a", (double?)v, 0.0)).ToArray();
        var plan = TryCompileOver(items, ("agg", """
            { "op": "GroupAggregate@1", "order": 200, "config": { "outputs": {
                "all.mean": { "fn": "mean", "value_col": "item.score" }, "author.std": { "fn": "std", "value_col": "item.score" } } } }
            """)).Plan!;

        var row = ById(await Run(plan))[1];

        var mean = values.Average();
        Assert.Equal(mean, row["all_mean"].AsFloat64(), 6);
        Assert.Equal(Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Length), row["author_std"].AsFloat64(), 6);
    }

    /// <summary>
    /// Pre-release assessment: the std is right at every scale a float64 holds. Summing squared deviations made ±1e200
    /// infinite (the node failed) and ±1e-200 zero; a group near the largest float64 and one of subnormals are covered too.
    /// </summary>
    [Theory]
    [InlineData(1e200, -1e200, 1e200)]
    [InlineData(1e-200, -1e-200, 1e-200)]
    [InlineData(1.7e308, -1.7e308, 1.7e308)] // the deviations overflow unless halved
    [InlineData(1e308, 1e308, 0.0)]
    [InlineData(4.9406564584124654E-324, -4.9406564584124654E-324, 4.9406564584124654E-324)] // the smallest subnormal
    [InlineData(3.0, 1.0, 1.0)]
    public async Task Std_AtTheEdgesOfFloat64_IsThePopulationStd(double first, double second, double std)
    {
        (long, long?, string, double?, double)[] items = [(1, 1, "a", first, 0), (2, 1, "a", second, 0)];
        var plan = TryCompileOver(items, ("agg", """
            { "op": "GroupAggregate@1", "order": 200, "config": { "outputs": { "author.std": { "fn": "std", "value_col": "item.score" } } } }
            """)).Plan!;

        var result = await Run(plan);

        Assert.Null(result.Error);
        Assert.All(ById(result).Values, row => Assert.Equal(std, row["author_std"].AsFloat64(), 1e-12 * std));
    }

    /// <summary>
    /// The node's buffers are scratch, given back once its outputs are written: outputs computed one after another over
    /// the same reused buffers each start clean (a sum, a std and a count after a rank, as when computed alone).
    /// </summary>
    [Fact]
    public async Task OutputsComputedOverReusedBuffers_EachStartClean()
    {
        var plan = Compile("""
            { "op": "GroupAggregate@1", "order": 200, "config": { "group_col": "item.author", "outputs": {
                "author.rank": { "fn": "rank", "value_col": "item.score" }, "author.sum": { "fn": "sum", "value_col": "item.score" },
                "author.std": { "fn": "std", "value_col": "item.score" }, "author.count": { "fn": "count" } } } }
            """);
        var alone = Compile("""
            { "op": "GroupAggregate@1", "order": 200, "config": { "group_col": "item.author", "outputs": {
                "author.std": { "fn": "std", "value_col": "item.score" } } } }
            """, """
            { "op": "GroupAggregate@1", "order": 210, "config": { "group_col": "item.author", "outputs": {
                "author.sum": { "fn": "sum", "value_col": "item.score" }, "author.count": { "fn": "count" } } } }
            """);

        var together = ById(await Run(plan));
        var separate = ById(await Run(alone));

        Assert.Equal(6, separate.Count);
        foreach (var (id, row) in separate)
        {
            foreach (var column in new[] { "author_sum", "author_std", "author_count" })
            {
                Assert.Equal(row[column], together[id][column]);
            }
        }
    }

    /// <summary>
    /// Review 71: both grouping operators group by a string entity_id. A string column groups by string equality (equal
    /// values that are other instances are one group), numbered as they first appear; a missing value is in no group.
    /// </summary>
    [Fact]
    public void StringGroups_AreEqualStrings_InTheOrderTheyFirstAppear_AndAMissingOneIsInNone()
    {
        var memory = new StrataRec.Core.Memory.RequestMemory();
        var text = new StrataRec.Core.Columns.StringColumn(StrataRec.Core.Types.ColumnType.String.AsNullable(), 6, memory);
        string?[] values = ["b", "a", "b", null, "c", "a"];
        for (var row = 0; row < values.Length; row++)
        {
            if (values[row] is { } value)
            {
                text.Set(row, new string(value)); // equal strings, not the same instances
                text.SetValid(row);
            }
            else
            {
                text.SetMissing(row);
            }
        }

        var groupOf = new int[6];
        var groups = StrataRec.Pipeline.Operators.Standard.RowGroups.Assign(text, [0, 1, 2, 3, 4, 5], groupOf, memory);

        Assert.Equal(3, groups);
        Assert.Equal([0, 1, 0, -1, 2, 1], groupOf);
    }

    [Theory]
    [InlineData("""{ "op": "GroupAggregate@1", "order": 200, "config": { "group_col": "entity_id", "outputs": { "g.count": { "fn": "count" } } } }""")]
    [InlineData("""{ "op": "SlidingWindowQuota@1", "order": 200, "config": { "group_col": "entity_id", "window": 3, "max_per_window": 1 } }""")]
    public async Task AStringEntityId_GroupsRows(string node)
    {
        var root = Directory.CreateTempSubdirectory("stratarec-text-groups-").FullName;
        Write(root, "catalog/c.jsonc", """{ "catalog_id": "c" }""");
        Write(root, "bindings/local.jsonc", """
            { "environment": "local", "candidate_sources": { "r": { "client": "memory_list@1", "items": ["a", "b", "c"] } } }
            """);
        Write(root, "pipelines/p.jsonc", $$"""
            { "pipeline_id": "p", "entity_key_type": "string",
              "param_schema": { "q": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "q": { "r": 3 }, "k": 3 },
              "nodes": {
                "recall": { "op": "MultiSourceRecall@1", "order": 100, "config": { "quotas_param": "q" } },
                "group": {{node}} },
              "output": { "limit_param": "k", "columns": {} } }
            """);
        var diagnostics = new DiagnosticBag();
        var request = ConfigWorkspace.Load(root, diagnostics).CreateCompileRequest("p", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        var compilation = PlanCompiler.Compile(request!, new PlanEnvironment { BaseDirectory = root });
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));

        var result = await Run(compilation.Plan!);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(["a", "b", "c"], result.Items.Select(item => item.Id.StringValue).Order());
    }

    private static Dictionary<long, IReadOnlyDictionary<string, ScalarValue>> ById(PipelineResult result)
    {
        Assert.True(result.Succeeded, result.Error);
        return result.Items.ToDictionary(item => item.Id.Int64Value, item => item.Columns);
    }

    private static ValueTask<PipelineResult> Run(ExecutionPlan plan) => new PipelineExecutor().ExecuteAsync(plan, new PipelineRequest
    {
        RequestId = "r",
        Inputs = new RequestInputs(),
        Deadline = TimeSpan.FromSeconds(30),
    });

    private static ExecutionPlan Compile(params string[] nodes) => Compile(Named(nodes));

    private static ExecutionPlan Compile(params (string Name, string Node)[] nodes)
    {
        var compilation = TryCompile(nodes);
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        return compilation.Plan!;
    }

    private static PlanCompilation TryCompile(params string[] nodes) => TryCompile(Named(nodes));

    /// <summary>The first node is <c>agg</c>, then <c>n1</c>, <c>n2</c>…</summary>
    private static (string, string)[] Named(string[] nodes) => nodes.Select((node, i) => (i == 0 ? "agg" : "n" + i, node)).ToArray();

    /// <summary>
    /// A workspace of one pipeline: recall of the six items, then <paramref name="nodes"/>, and every column the tests read
    /// in the output.
    /// </summary>
    private static PlanCompilation TryCompile(params (string Name, string Node)[] nodes) => TryCompileOver(Items, nodes);

    private static PlanCompilation TryCompileOver((long Id, long? Author, string Tag, double? Score, double Weight)[] items, params (string Name, string Node)[] nodes)
    {
        var root = Directory.CreateTempSubdirectory("stratarec-group-aggregate-").FullName;
        Write(root, "catalog/c.jsonc", """
            { "catalog_id": "c",
              "entities": { "item": { "key": "col.entity_id" } },
              "features": {
                "item.author": { "type": "int64",   "source": "items", "field": "author" },
                "item.tag":    { "type": "string",  "source": "items", "field": "tag", "default": "" },
                "item.score":  { "type": "float64", "source": "items", "field": "score" },
                "item.weight": { "type": "float64", "source": "items", "field": "weight", "default": 0.0 } } }
            """);
        Write(root, "bindings/local.jsonc", """
            { "environment": "local",
              "sources": { "items": { "connector": "memory@1", "data_file": "fixtures/items.json", "timeout_ms": 30000 } },
              "candidate_sources": { "s": { "client": "memory_list@1", "data_file": "fixtures/ids.json" } } }
            """);
        var named = nodes.Select(n => $"\"{n.Name}\": {n.Node}");

        string[] outputs =
        [
            "author.count", "author.rank", "author.sum", "author.mean", "author.min", "author.max", "author.std", "tag.count", "tag.rank",
            "all.count", "all.mean",
        ];
        var produced = string.Join(" ", nodes.Select(n => n.Node));
        var columns = outputs.Where(name => produced.Contains($"\"{name}\"", StringComparison.Ordinal))
            .Select(name => $"\"{name.Replace('.', '_')}\": \"col.{name}\"");
        Write(root, "pipelines/p.jsonc", $$"""
            { "pipeline_id": "p", "entity_key_type": "int64",
              "columns": { "declared.count": { "type": "int32", "default": 0 }, "declared.mean": { "type": "float32", "default": 0.0 } },
              "param_schema": { "quotas": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "quotas": { "s": 100 }, "k": 100 },
              "nodes": {
                "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "quotas" } },
                {{string.Join(",\n", named)}} },
              "output": { "limit_param": "k", "columns": { {{string.Join(", ", columns)}} } } }
            """);
        Write(root, "fixtures/ids.json", "{ \"items\": [" + string.Join(", ", items.Select(i => i.Id)) + "] }");
        Write(root, "fixtures/items.json", "{ " + string.Join(", ", items.Select(i =>
            $"\"{i.Id}\": {{ \"author\": {Json(i.Author)}, \"tag\": \"{i.Tag}\", \"score\": {Json(i.Score)}, \"weight\": {Json(i.Weight)} }}")) + " }");

        var diagnostics = new DiagnosticBag();
        var workspace = ConfigWorkspace.Load(root, diagnostics);
        var request = workspace.CreateCompileRequest("p", "local", diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Items));
        return PlanCompiler.Compile(request!, new PlanEnvironment { BaseDirectory = root });
    }

    private static string Json(object? value) =>
        value is null ? "null" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!;

    private static void Write(string root, string path, string content)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
