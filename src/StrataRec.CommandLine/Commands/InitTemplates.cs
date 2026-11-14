namespace StrataRec.CommandLine.Commands;

/// <summary>One file <c>stratarec init</c> writes.</summary>
/// <param name="Path">Relative to the workspace root, with <c>/</c> separators.</param>
/// <param name="Description">What the file is, for the list init prints.</param>
/// <param name="Content">The file's text.</param>
internal sealed record InitFile(string Path, string Description, string Content);

/// <summary>
/// The workspace <c>stratarec init</c> writes: the smallest one that shows every part a user edits and still validates
/// without warnings and runs offline. The files are C# raw strings here rather than embedded resources, so that the one
/// substitution (the pipeline id, in <c>{{pipeline}}</c> holes) is checked by the compiler, and the files, their
/// descriptions and the names the printed next steps use (<see cref="ExperimentId"/>, the arms, the request) are kept
/// together in one file.
/// </summary>
/// <remarks>
/// Every file, fixtures and request too, is JSONC that starts with a comment saying what it is and what to change first
/// (every reader of these files skips comments). The pipeline id is checked before it is substituted
/// (<see cref="InitCommand"/>), so it needs no JSON escaping. Adjacent braces would start a hole in a <c>$$</c> raw
/// string, so the JSON keeps a space between nested braces.
/// </remarks>
internal static class InitTemplates
{
    public const string DefaultPipeline = "feed";

    public const string ExperimentId = "exp_freshness";

    /// <summary>The arm the request file's assignment names.</summary>
    public const string AssignedArm = "boost";

    public const string ControlArm = "control";

    public const string RequestPath = "requests/example.jsonc";

    public static string PipelinePath(string pipeline) => $"pipelines/{pipeline}.jsonc";

    /// <summary>The files, in the order init lists them.</summary>
    public static InitFile[] Files(string pipeline) =>
    [
        new("policy.jsonc", "what experiments may change", Policy),
        new($"catalog/{pipeline}.jsonc", "entities and features", Catalog(pipeline)),
        new("bindings/local.jsonc", "the local environment: sources, model and recall bound to fixtures", Bindings),
        new(PipelinePath(pipeline), "the pipeline: parameters, nodes, output", Pipeline(pipeline)),
        new($"experiments/{ExperimentId}.jsonc", "an experiment: two arms, each a patch of the pipeline", Experiment(pipeline)),
        new($"releases/{pipeline}.local.jsonc", "the local release: catalog, bindings, experiment layers", Release(pipeline)),
        new("fixtures/items.jsonc", "feature values of the items", Items),
        new("fixtures/recall.jsonc", "the candidates recall returns", Recall),
        new(RequestPath, $"a request, assigned to {ExperimentId}/{AssignedArm}", Request(pipeline)),
    ];

    private const string Policy = """
        // What experiments may change: RFC 6901 path prefixes into the pipeline. These are the defaults: narrow them to protect
        // what experiments must not change, and add "owners" (path prefix → teams) to route reviews.
        {
          "experiment_patchable": ["/params", "/nodes", "/log_taps"],
          "experiment_add_only": ["/columns", "/param_schema", "/features", "/derived_features", "/funnel_groups"]
        }

        """;

    private static string Catalog(string pipeline) => $$"""
        // The features pipelines read, by entity (here one: the candidate item, keyed by its id). Replace the item features
        // with your own: "source" is a source bound in bindings/<env>.jsonc and "field" its field there. A feature with a
        // "default" is never missing; one without (age_hours) is nullable, and expressions say what missing means
        // (col.item.age_hours ?? 168.0).
        {
          "catalog_id": "{{pipeline}}",
          "entities": {
            "item": { "scope": "candidate", "key": "col.entity_id", "key_type": "int64" }
          },
          "features": {
            "item.clicks_7d":      { "type": "float32", "source": "item_store", "field": "clicks_7d", "default": 0.0 },
            "item.impressions_7d": { "type": "float32", "source": "item_store", "field": "impressions_7d", "default": 0.0 },
            "item.age_hours":      { "type": "float32", "source": "item_store", "field": "age_hours" }
          },
          // Computed from other features. Models read them as plain columns, so training and serving share the formula.
          "derived_features": {
            "item.smoothed_ctr": { "type": "float32", "expr": "(col.item.clicks_7d + 1.0) / (col.item.impressions_7d + 20.0)" }
          }
        }

        """;

    private const string Bindings = """
        // The "local" environment: what each source, model and candidate source the catalog and pipeline name is here. Point
        // them at your data: here they read fixtures/ or run in process, so the workspace runs offline. Another environment
        // binds the same names in bindings/<env>.jsonc, used by releases/<pipeline>.<env>.jsonc.
        {
          "environment": "local",
          "sources": {
            // A fixture answers at once; the timeout only allows for the first request of a process, which compiles code (JIT).
            "item_store": { "connector": "memory@1", "data_file": "fixtures/items.jsonc", "timeout_ms": 1000 }
          },
          "models": {
            // A stand-in for your model: p_click = sigmoid(bias + weight · smoothed_ctr).
            "ctr_model": {
              "client": "linear@1",
              "outputs": { "p_click": { "bias": -2.5, "weights": { "smoothed_ctr": 12.0 }, "activation": "sigmoid" } }
            }
          },
          "candidate_sources": {
            "popular": { "client": "memory_list@1", "data_file": "fixtures/recall.jsonc" }
          }
        }

        """;

    private static string Pipeline(string pipeline) => $$"""
        // The pipeline: what a request carries, the columns its nodes write, the parameters experiments tune, the nodes (run
        // by "order") and the output. Start with the nodes: 'stratarec operators' lists the operators and
        // 'stratarec schema operator TopK@1' shows the settings of one.
        {
          "pipeline_id": "{{pipeline}}",
          // ctx.request.user_id in expressions. To read user features, add a "user" entity keyed by it to the catalog.
          "request_schema": {
            "user_id": { "type": "int64?" }
          },
          "columns": {
            "score.p_click": { "type": "float32", "default": 0.0 },
            "score.final":   { "type": "float32", "default": 0.0 }
          },
          "param_schema": {
            "recall_quotas":    { "type": "map<string,int64>", "min": 0, "max": 1000 },
            "freshness_weight": { "type": "float64", "min": 0, "max": 1 },
            "top_k":            { "type": "int64", "min": 1, "max": 100 }
          },
          "params": {
            "recall_quotas": { "popular": 100 },
            "freshness_weight": 0.05,
            "top_k": 10
          },
          "nodes": {
            "recall": {
              "order": 100, "op": "MultiSourceRecall@1",
              "config": { "quotas_param": "recall_quotas" }
            },
            "ctr_ranker": {
              "order": 200, "op": "ModelScorer@1",
              "config": {
                "model": "ctr_model",
                "inputs": { "smoothed_ctr": "col.item.smoothed_ctr" },
                "outputs": { "p_click": "score.p_click" }
              }
            },
            // An item of unknown age counts as a week old.
            "final_score": {
              "order": 300, "op": "ExpressionScorer@1",
              "config": {
                "output_col": "score.final",
                "expr": "col.score.p_click + param.freshness_weight / (1.0 + (col.item.age_hours ?? 168.0) / 24.0)"
              }
            },
            "rank": {
              "order": 400, "op": "TopK@1",
              "config": { "score_col": "score.final", "k_param": "top_k" }
            }
          },
          "output": {
            "limit_param": "top_k",
            "columns": { "score": "col.score.final", "p_click": "col.score.p_click", "age_hours": "col.item.age_hours" }
          }
        }

        """;

    private static string Experiment(string pipeline) => $$"""
        // An experiment: each arm is an RFC 7396 merge patch on the pipeline, here of one parameter ({} changes nothing). The
        // experiment platform decides which arm a request is in (a request's "assignment"); the release lists the experiment
        // and its layer. To start another one, copy this file: the file name is the experiment_id.
        {
          "experiment_id": "{{ExperimentId}}",
          "pipeline_id": "{{pipeline}}",
          "owner": "your-team",
          "description": "A stronger freshness weight in the final score.",
          "expires_at": "2099-12-31",
          "arms": {
            "{{ControlArm}}": {},
            "{{AssignedArm}}": { "params": { "freshness_weight": 0.2 } }
          }
        }

        """;

    private static string Release(string pipeline) => $$"""
        // The release of pipeline "{{pipeline}}" in environment "local": the catalog and bindings it uses, and each experiment
        // in flight with its layer (a request is in at most one arm per layer). An experiment runs only once it is listed here.
        {
          "pipeline_id": "{{pipeline}}",
          "environment": "local",
          "catalog": "{{pipeline}}",
          "bindings": "local",
          "experiments": {
            "{{ExperimentId}}": { "layer": "ranking" }
          }
        }

        """;

    private const string Items = """
        // Item features for the memory@1 source item_store (bindings/local.jsonc): item id → the fields the catalog reads. Add
        // the fields of your features here. A missing field or item is a missing value: item 7 has no age_hours, and item 12
        // has no entry, so its features take their defaults.
        {
          "1":  { "clicks_7d": 300, "impressions_7d": 2000, "age_hours": 72 },
          "2":  { "clicks_7d": 90,  "impressions_7d": 1200, "age_hours": 2 },
          "3":  { "clicks_7d": 150, "impressions_7d": 1400, "age_hours": 30 },
          "4":  { "clicks_7d": 40,  "impressions_7d": 900,  "age_hours": 6 },
          "5":  { "clicks_7d": 210, "impressions_7d": 1800, "age_hours": 150 },
          "6":  { "clicks_7d": 12,  "impressions_7d": 400,  "age_hours": 1 },
          "7":  { "clicks_7d": 60,  "impressions_7d": 700 },
          "8":  { "clicks_7d": 5,   "impressions_7d": 300,  "age_hours": 200 },
          "9":  { "clicks_7d": 110, "impressions_7d": 1000, "age_hours": 12 },
          "10": { "clicks_7d": 25,  "impressions_7d": 600,  "age_hours": 48 },
          "11": { "clicks_7d": 180, "impressions_7d": 1300, "age_hours": 400 }
        }

        """;

    private const string Recall = """
        // The candidates the memory_list@1 candidate source "popular" returns, best first (bindings/local.jsonc).
        {
          "items": [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]
        }

        """;

    private static string Request(string pipeline) => $$"""
        // A request for 'stratarec run'. Change it to try others: "inputs" are the pipeline's request_schema fields, and
        // "assignment" the arms the experiment platform put the request in (experiment/arm).
        {
          "pipeline_id": "{{pipeline}}",
          "request_id": "example",
          "inputs": { "user_id": 42 },
          "assignment": ["{{ExperimentId}}/{{AssignedArm}}"]
        }

        """;
}
