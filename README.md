# StrataRec

面向 .NET 8+ 的进程内推荐执行引擎：列式候选表、编译一次的算子流水线、CEL 风格的表达式语言 `ColumnExpr`、声明式漏斗日志，以及用 RFC 7396 合并补丁表达的多层实验。

流水线的全部行为都是标准格式的配置（JSONC、JSON Merge Patch、JSON Pointer、JSON Schema、CEL 风格表达式），并且在发布之前就能在本地完整校验：类型、数据流、实验冲突，以及所有可能同时分配的臂组合。

> 状态：阶段性框架。整个链路在自带的 `home_feed` 示例上端到端可用，公开 API 尚未稳定。

## 快速开始

需要 .NET 8 SDK 或更新版本。

```sh
dotnet build StrataRec.sln          # 警告视为错误
dotnet test StrataRec.sln           # 单元测试 + 集成测试

dotnet run --project src/StrataRec.Cli -- validate     -c samples/home_feed
dotnet run --project src/StrataRec.Cli -- explain-plan -c samples/home_feed
dotnet run --project src/StrataRec.Cli -- run          -c samples/home_feed -r samples/home_feed/requests/user_1001.json --trace --logs
dotnet run --project src/StrataRec.Cli -- schema pipeline          # 各类产物的 JSON Schema
dotnet run --project src/StrataRec.Cli -- operators                # 算子及其声明的效果
```

`validate` 对示例会报一个预期内的警告：MMR 实验读取的分数会被另一层的校准实验修改，而这两个臂可能同时分配。

## 示例工作区

```text
samples/home_feed/
  policy.jsonc                      实验可以修改哪些路径
  catalog/home_feed.jsonc           实体与特征（物理特征和衍生特征）
  bindings/local.jsonc              各环境的连接器、模型和召回源
  pipelines/home_feed.jsonc         参数、列、节点、漏斗组、探针、输出
  experiments/*.jsonc               每个实验一个文件，每个臂是一个合并补丁
  allocation/home_feed.local.jsonc  桶、域和层（流量分配）
  fixtures/、requests/              本地运行用的内存数据和示例请求
```

## 基准

```sh
dotnet run -c Release --project tests/StrataRec.Benchmarks -- --filter '*ExpressionBenchmarks*'
dotnet run -c Release --project tests/StrataRec.Benchmarks -- --filter '*PipelineBenchmarks*'
```

`PipelineBenchmarks` 端到端执行 `home_feed` 请求，候选数为 500 和 5,000。

## 文档

- [`docs/design.md`](docs/design.md)：整体设计。代码注释中的“design 4.6”指这里的章节号。
- [`docs/decisions.md`](docs/decisions.md)：实现中的决定、取舍和尚未实现的部分。
- [`AGENTS.md`](AGENTS.md)：项目结构与编码约定。
