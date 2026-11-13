# AGENTS.md — working on StrataRec

StrataRec is an in-process recommendation execution engine for .NET 8+: a columnar candidate table, a
compile-once pipeline of operators, a CEL-style expression language (`ColumnExpr`), and multi-layer experiments
expressed as RFC 7396 merge patches. The design document is `docs/design.md` (Chinese); section numbers
in code comments (e.g. "design 4.6") refer to it.

## Build, test, run

```sh
dotnet build StrataRec.sln              # warnings are errors
dotnet test StrataRec.sln               # unit + integration tests
dotnet run --project src/StrataRec.Cli -- --help
```

Everything must pass locally before a change is done. Prefer adding a test next to the code you touch:
`tests/StrataRec.UnitTests/<Area>/...` for components, `tests/StrataRec.IntegrationTests` for end-to-end runs
over `samples/`.

## Layout

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

Dependencies point downward in that table; never add a reference that creates a cycle.

## Conventions

- C# 12, file-scoped namespaces, nullable enabled, `TreatWarningsAsErrors`. Private fields are `_camelCase`.
- Public types get XML doc comments explaining *why*, not restating the name.
- **No LINQ in `src/`.** Requests can carry many candidates and the engine is columnar (SoA) precisely to avoid
  allocations; LINQ allocates enumerators, closures and intermediate collections. Use explicit loops, arrays,
  `List<T>`, `Dictionary`/`HashSet`, `Array.Sort`/`List.Sort` with ordinal comparers. Enforced at build time by
  `BannedSymbols.txt` (RS0030) and by removing `System.Linq` from implicit usings (`Directory.Build.targets`).
  Tests and benchmarks may use LINQ.
- Hot paths (anything per row or per request) must not allocate per row. Use `RequestMemory` for buffers that
  live for the request; operators never return buffers themselves.
- Configuration uses only standard syntax: JSON(C), RFC 7396 merge patch, RFC 6901 pointers, RFC 8785
  canonicalization, JSON Schema 2020-12, CEL-style expressions, Mustache. Do not invent DSL constructs.
- Errors in configuration must say *where* (JSON pointer / expression offset) and *how to fix* (hint).
- Missing values are explicit: nullable types (`T?`), `??`, `has()`. Never silently turn missing into 0.
- Determinism: ties break by `entity_id` ascending; randomness only through `hash01` / `StableHash`.
