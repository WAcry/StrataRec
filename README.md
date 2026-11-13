# StrataRec

An in-process recommendation execution engine for .NET 8+: a columnar candidate table, a compile-once pipeline of
typed operators, a CEL-style expression language, declarative funnel logging, and multi-layer experiments expressed as
RFC 7396 merge patches over the pipeline.

Everything a pipeline does is configuration in standard formats (JSONC, JSON Merge Patch, JSON Pointer, JSON Schema,
CEL-style expressions), and everything about that configuration is checked locally before it ships: types, data flow,
experiment conflicts, and every combination of arms that can be assigned together.

> Status: preliminary framework. The full stack runs end to end on the bundled `home_feed` sample, but the public
> API is not stable. The design document is [`docs/design.md`](docs/design.md) (Chinese); section numbers in code
> comments such as "design 4.6" refer to it.

## Why

Recommendation serving stacks usually grow three problems at once:

- **Per-candidate object graphs.** Thousands of candidates per request become millions of small objects per second.
  StrataRec keeps candidates in a structure-of-arrays table with pooled column buffers. Expressions are vectorized
  over columns (SIMD via `TensorPrimitives`), and the request hot path avoids per-row allocation.
- **Experiments as code forks.** Each experiment becomes an `if` in the pipeline code, and nobody can say what happens
  when two experiments meet. In StrataRec an experiment arm is a merge patch over a *patchable view* of the pipeline.
  The compiler builds one unified plan for all layers and checks every reachable arm combination for data-flow errors
  and conflicts. It also records which experiments were actually *triggered* (exposed) per request, not just assigned.
- **Configuration only production can check.** Every artifact has a JSON Schema. Every error carries a JSON
  pointer or expression offset plus a fix hint. `stratarec validate` runs the same checks CI and the server run, and
  `stratarec run` executes a request locally against in-memory fixtures.

## Quick start

Requires the .NET 8 SDK or newer.

```sh
dotnet build StrataRec.sln          # warnings are errors
dotnet test StrataRec.sln           # unit + integration tests

# The CLI, against the bundled sample workspace
dotnet run --project src/StrataRec.Cli -- validate     -c samples/home_feed
dotnet run --project src/StrataRec.Cli -- explain-plan -c samples/home_feed
dotnet run --project src/StrataRec.Cli -- run          -c samples/home_feed -r samples/home_feed/requests/user_1001.json --trace
dotnet run --project src/StrataRec.Cli -- run          -c samples/home_feed -r samples/home_feed/requests/user_1001.json \
                                          --arms exp_mmr_diversity/control --logs
```

`validate` on the sample reports one intended warning: an MMR experiment reads a score that a calibration experiment
in another layer changes, and the two can be assigned together. The output looks like this:

```
warning INTERACTION_READ experiment:exp_mmr_diversity/arms/mmr/nodes/mmr_diversifier: Node 'mmr_diversifier' added by
  'exp_mmr_diversity/mmr' reads 'score.final', which 'exp_freshness_calibration/calibrated' changes through
  'freshness_calibration'; the two arms can be assigned together.
    hint: confirm in review that the added node should see the other experiment's values; otherwise put the
    experiments in the same layer
home_feed: ok — 11 nodes, 11 compiled variants, 3 experiments, 6 arms, 4 arm combinations checked
```

## A workspace

```
samples/home_feed/
  policy.jsonc                    what experiments may patch (patchable / add-only paths)
  catalog/home_feed.jsonc         entities and features: physical (source + field) and derived (expression)
  bindings/local.jsonc            per-environment connectors, models and candidate sources
  pipelines/home_feed.jsonc       params, columns, nodes, funnel groups, log taps, output
  experiments/*.jsonc             one experiment per file; each arm is a merge patch
  allocation/home_feed.local.jsonc  buckets, domains and layers (traffic assignment)
  fixtures/, requests/            in-memory data and sample requests for local runs
```

A pipeline node is an operator plus config. Expressions are CEL-style, and missing values are explicit (`T?`, `??`,
`has()`):

```jsonc
"safety_filter": {
  "order": 200, "op": "ExpressionFilter@1",
  "config": { "predicate": "(col.item.is_policy_safe ?? true) && col.item.report_rate < 0.05" }
}
```

An experiment arm is a merge patch. This one deletes a node, adds another, and declares a new parameter:

```jsonc
"mmr": {
  "param_schema": { "mmr_lambda": { "type": "float64", "min": 0, "max": 1 } },
  "params": { "mmr_lambda": 0.7 },
  "nodes": {
    "diversity_cap": null,
    "mmr_diversifier": {
      "order": 550, "op": "MmrDiversifier@1",
      "config": { "score_col": "score.final", "embedding_col": "item.embedding", "lambda_param": "mmr_lambda", "window": 20 }
    }
  }
}
```

`stratarec schema <kind>` prints the JSON Schema of each artifact type. `stratarec schema operator TopK@1` prints an
operator's config schema, and `stratarec operators` lists operators with their declared effects.

## Concepts

| Concept | Where | Summary |
| --- | --- | --- |
| Candidate table | `Core/Tables` | SoA columns with validity bitmaps. A `RowSelection` (live order) makes filtering and reordering O(rows), not O(rows × columns). |
| Output bindings | `Core` | A logical column maps to the physical slot of its current producer. Variants and `col.x@node` pinned reads resolve without copies. |
| Operators | `Pipeline/Operators` | Typed config (JSON Schema exported). Declared effects: `RowSource`, `RowFilter`, `RowOrder`, `ColumnWriter`. Compiled once per plan. |
| Failure policy | `Pipeline` | Per node: `fail_request`, `skip`, or `fallback:<node>`. A failed node's row and column effects are rolled back. A pipeline can also name a `fallback_pipeline` that runs within the remaining deadline. |
| Features | `Features` | Catalog of physical and derived features. Fetches are planned by dependency waves and batched per source with `max_batch`, and only what later nodes read is fetched. Values are staged and then backfilled. |
| ColumnExpr | `Expressions` | Parser, type and nullability checker, optimizer, and vectorized backend, with differential tests against a reference interpreter. |
| Experiments | `Experiments` | Layers and domains, XXH3 bucket assignment, strict patch analysis against the policy, a unified plan with per-arm-combination node variants, trigger sites. |
| Funnel logging | `Logging`, `Pipeline/Execution/TapCapture.cs` | Funnel groups with coordinated hash sampling, plus taps that capture `entered`, `survived`, `dropped` and `output` rows. Rows are sampled bottom-k with recorded inclusion probabilities. Delivery is durable or best-effort through bounded async queues. |
| Hosting | `Hosting` | `IRecEngine` with atomic plan swaps (stale loads rejected), one end-to-end deadline, release manifests, DI registration. |

## Determinism

- Ties break by `entity_id` ascending.
- Randomness only comes from `hash01(...)` / `StableHash`: XXH3 over a canonical encoding, so the same request and
  plan always give byte-identical results, traces and tap output.
- Variant identity is the RFC 8785 canonical hash of the merged configuration.

The integration tests compile each of the eight arm combinations of the sample in two ways and compare the results:
once through the unified multi-layer plan, once as a single merged pipeline (the design 16.2 differential anchor).

## Performance

Benchmarks live in `tests/StrataRec.Benchmarks` (BenchmarkDotNet):

```sh
dotnet run -c Release --project tests/StrataRec.Benchmarks -- --filter '*ExpressionBenchmarks*'
dotnet run -c Release --project tests/StrataRec.Benchmarks -- --filter '*PipelineBenchmarks*'
```

- `ExpressionBenchmarks` evaluates a fusion formula with `exp` and `log1p` over 5,000 live rows. It takes about 37 μs
  over the full range and about 50 μs for 5,000 rows gathered from a 20,000-row table, with no allocation. The
  design budget is ≤ 50 μs.
- `PipelineBenchmarks` runs the whole `home_feed` request (recall → filter → coarse score → top-k → model → fusion
  → sort → diversity) over 500 and 5,000 synthetic candidates. It runs once with no experiments, once with all
  treatment arms, and once with log capture on.

Measured with `--job short --inProcess` on a Xeon 6975P-C, .NET 8:

| `PipelineBenchmarks` | 500 candidates | 5,000 candidates |
| --- | --- | --- |
| Baseline | 230 μs, 43 KB | 2.31 ms, 220 KB |
| All treatment arms | 247 μs, 46 KB | 2.48 ms, 232 KB |
| All arms, with log capture | 282 μs, 57 KB | 2.80 ms, 283 KB |

There are no Gen1 or Gen2 collections. Entity-key indexes, fetch keys, recall merge scratch and the request's buffer
ledger come from pooled request memory, so per-key work allocates nothing. Most of what remains is per connector call:
the sample uses `max_batch: 25`, which means about 400 calls at 5,000 candidates. The rest is a constant per-request
overhead of roughly 30–50 KB. The design budget is about 2 KB; see
[`docs/implementation-notes.md`](docs/implementation-notes.md) for what is left.

## Repository layout

| Project | Responsibility |
| --- | --- |
| `StrataRec.Core` | Type system, `CandidateTable`, `RowSelection`, column buffers, bitmaps, `OutputBindings`, `RequestMemory`, stable hashing |
| `StrataRec.Artifacts` | JSON standards (JSONC, RFC 8785, RFC 7396, RFC 6901), artifact models, content-addressed stores |
| `StrataRec.Expressions` | `ColumnExpr`: parser, type/nullability checker, IR, vectorized backend |
| `StrataRec.Features` | Feature catalog, fetch planning, connectors, staging and backfill |
| `StrataRec.Models` | `IModelClient` contracts and built-in clients |
| `StrataRec.Logging` | Funnel groups, log taps, coordinated sampling, async log pipeline |
| `StrataRec.Pipeline` | Node model, effects, operators, executor |
| `StrataRec.Experiments` | Assignment, patch validation, footprints, unified plan, triggers |
| `StrataRec.Hosting` | `IRecEngine`, plan loading and swapping, DI |
| `StrataRec.Cli` | `stratarec` command-line tool |

Dependencies point downward in that table. Contribution conventions, including the ban on LINQ in `src/` and the
allocation rules for hot paths, are in [`AGENTS.md`](AGENTS.md).

## Embedding

```csharp
services.AddStrataRec(options =>
{
    options.ConfigRoot = "config";
    options.Environment = "prod";
    options.Pipelines.Add("home_feed");
});

// ...
var result = await engine.ExecuteAsync(new RecRequest
{
    PipelineId = "home_feed",
    RequestId = requestId,
    Inputs = new RequestInputs().Set("user_id", userId).Set("surface", "home"),
    Deadline = TimeSpan.FromMilliseconds(80),
});
```

`RecResult` carries the ranked items, the assigned and triggered experiments, the pipeline that served the request
(the requested one or its fallback), and, when `DebugOptions` ask for them, a per-node trace and the captured log
batches.
