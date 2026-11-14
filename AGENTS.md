# AGENTS.md：在 StrataRec 上工作

StrataRec 是面向 .NET 10+ 的进程内推荐执行引擎：列式候选表、编译一次的算子流水线、CEL 风格表达式 `ColumnExpr`，以及用 RFC 7396 合并补丁表达的多层实验。

- `docs/design.md`：整体设计，代码注释中的 “design 4.6” 指它的章节号。
- `docs/decisions.md`：实现中的决定和理由。改变设计或做出取舍时在这里补一条。

文档用中文，代码、标识符和代码注释用英文。文档只记录读代码看不出来的东西：决定、取舍、理由和整体架构。

## 构建、测试、运行

```sh
dotnet build StrataRec.sln              # 警告视为错误
dotnet test StrataRec.sln               # 单元测试 + 集成测试
dotnet run --project src/StrataRec.Cli -- --help
```

改动完成之前必须全部通过。测试放在被改动代码旁边：组件测试放 `tests/StrataRec.UnitTests/<Area>/`，基于 `samples/` 的端到端测试放 `tests/StrataRec.IntegrationTests`。

## 项目结构

| 项目 | 职责 |
| --- | --- |
| `StrataRec.Core` | 类型系统、`CandidateTable`、`RowSelection`、列缓冲、位图、`OutputBindings`、`RequestMemory`、稳定哈希 |
| `StrataRec.Artifacts` | JSON 标准（JSONC、RFC 8785、RFC 7396、RFC 6901）、产物模型、内容寻址存储 |
| `StrataRec.Expressions` | `ColumnExpr`：解析、类型与可空性检查、IR、向量化后端 |
| `StrataRec.Features` | 特征目录、拉取规划、连接器、暂存与回填 |
| `StrataRec.Models` | `IModelClient` 契约与内置客户端 |
| `StrataRec.Logging` | 漏斗组、探针、协调采样、异步日志管道 |
| `StrataRec.Pipeline` | 节点模型、效果、算子、执行器 |
| `StrataRec.Experiments` | 外部分配的映射、补丁校验、足迹、联合计划、触发 |
| `StrataRec.Hosting` | `IRecEngine`、计划加载与切换、DI |
| `StrataRec.Redis` | `redis@1`：`cache` 供给方式的分布式层（StackExchange.Redis），核心包不依赖它 |
| `StrataRec.Llm` | 生成式与大模型类算子（`LlmReranker@1`）、Mustache 提示词；依赖 `StrataRec.Pipeline`，核心包不依赖它 |
| `StrataRec.Cli` | `stratarec` 命令行工具 |

依赖关系按表格自上而下，不允许引入循环引用。

## 约定

- C# 14、文件级命名空间、启用 nullable、`TreatWarningsAsErrors`。私有字段用 `_camelCase`。
- 公开类型写 XML 文档注释，说明为什么这样设计，不要复述名字。
- **`src/` 中禁止 LINQ。** 请求可能携带大量候选，引擎采用列式（SoA）正是为了减少分配；LINQ 会分配枚举器、闭包和中间集合。用显式循环、数组、`List<T>`、`Dictionary`/`HashSet`、带序数比较器的 `Array.Sort`/`List.Sort`。构建时由 `BannedSymbols.txt`（RS0030）和移除隐式 `System.Linq` using（`Directory.Build.targets`）强制。测试和基准可以用 LINQ。
- 热路径（按行或按请求执行的代码）不能按行分配。请求生命周期内的缓冲从 `RequestMemory` 租借；算子不自己归还缓冲。
- 配置只使用标准语法：JSON(C)、RFC 7396 合并补丁、RFC 6901 指针、RFC 8785 规范化、JSON Schema 2020-12、CEL 风格表达式、Mustache。不要发明 DSL。
- 配置错误必须说明位置（JSON 指针或表达式偏移）和修复方法（hint）。
- 缺失值是显式的：可空类型（`T?`）、`??`、`has()`。绝不把缺失静默变成 0。
- 确定性：平分时按 `entity_id` 升序；随机性只来自 `hash01` / `StableHash`。
- 框架不做个人数据过滤（见 `docs/decisions.md`）。
- CI 只用 Linux（`.github/workflows/ci.yml`），用 SDK 10 构建，测试跑在 .NET 10 运行时上。
