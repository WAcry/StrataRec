# StrataRec

面向 .NET 10+ 的进程内推荐执行引擎：列式候选表、编译一次的算子流水线、CEL 风格的表达式语言 `ColumnExpr`、声明式漏斗日志，以及用 RFC 7396 合并补丁表达的多层实验。

流水线的全部行为都是标准格式的配置（JSONC、JSON Merge Patch、JSON Pointer、JSON Schema、CEL 风格表达式），并且在发布之前就能在本地完整校验：类型、数据流、实验冲突，以及所有可能同时分配的臂组合。

> 状态：阶段性框架。整个链路在自带的 `home_feed` 示例上端到端可用，公开 API 尚未稳定。

## 快速开始

需要 .NET 10 SDK 或更新版本。

```sh
dotnet build StrataRec.sln          # 警告视为错误
dotnet test StrataRec.sln           # 单元测试 + 集成测试

dotnet run --project src/StrataRec.Cli -- validate     -c samples/home_feed
dotnet run --project src/StrataRec.Cli -- explain-plan -c samples/home_feed   # --arms exp/arm：这个组合实际执行的节点、定义和每个节点读写的列
dotnet run --project src/StrataRec.Cli -- run          -c samples/home_feed -r samples/home_feed/requests/user_1001.json --trace --logs   # 冷进程第一个请求含 JIT，超时可加 --deadline-ms 5000
dotnet run --project src/StrataRec.Cli -- run          -c samples/home_feed -r samples/home_feed/requests/user_1001.json --record bundles/1001.json
dotnet run --project src/StrataRec.Cli -- run          -c samples/home_feed -r samples/home_feed/requests/user_1001.json --format text --explain 17,1   # 可读的结果；每个 id 落在哪、每个节点之后的状态
dotnet run --project src/StrataRec.Cli -- replay       -c samples/home_feed bundles   # 用录制的 I/O 重现并对比（--explain 同样可用）；compare --baseline 对比两份配置
dotnet run --project src/StrataRec.Cli -- diff         --from <旧工作区或 manifest> --to samples/home_feed   # 按桶组合对比两份配置
dotnet run --project src/StrataRec.Cli -- build        -c samples/home_feed -s store   # 产物按哈希写进 store；加载这份 manifest 的节点录制的包，replay -s store 就在录制时的 manifest 上回放（在工作区上录制的包仍用 -c）
dotnet run --project src/StrataRec.Cli -- publish      -s store <manifest id 或文件>   # 让 store 里的指针指向它（seq 必须更大）；设置了 Releases 的服务节点会加载它
dotnet run --project src/StrataRec.Cli -- revoke       -s store -p home_feed exp_mmr_diversity   # 紧急停止：立即发布去掉这个实验的 manifest，并撤销之前所有的 manifest
dotnet run --project src/StrataRec.Cli -- schema pipeline          # 各类产物的 JSON Schema；schema --out <工作区>/schemas 写进工作区，供编辑器使用
dotnet run --project src/StrataRec.Cli -- operators                # 算子及其声明的效果
```

`validate` 对示例会报一个预期内的警告：MMR 实验读取的分数会被另一层的校准实验修改，而这两个臂可能同时分配。

从零开始写一条流水线时，先生成一份最小的工作区，再在上面改：

```sh
dotnet run --project src/StrataRec.Cli -- init my-feed            # 目录须为空（.git 等隐藏条目除外）；--pipeline 指定流水线 ID
dotnet run --project src/StrataRec.Cli -- validate -c my-feed
dotnet run --project src/StrataRec.Cli -- run      -c my-feed -r my-feed/requests/example.jsonc --format text --trace
dotnet run --project src/StrataRec.Cli -- run      -c my-feed -r my-feed/requests/example.jsonc --format text --explain 8   # 第 8 项为什么没有返回
```

`init` 写出目录、本地绑定、一条从召回到 top-k 的流水线、一个实验、发布文件、夹具和示例请求，每个文件开头的注释说明先改哪里，最后打印接下来可以照抄的命令。`home_feed` 是演示全部功能的完整示例。

工作区的 `schemas/` 里是各类文件的 JSON Schema，每个文件用顶层的 `"$schema"` 引用它，所以在 VS Code 等编辑器里打开就有补全、字段说明和即时报错：节点的 `config` 按它的 `op` 检查，实验臂按补丁的形式检查。Schema 由二进制生成，包含它注册的算子；升级之后 `validate` 会报 `SCHEMAS_STALE`，用 `stratarec schema --out <工作区>/schemas` 重新生成并提交。

## 在服务里嵌入引擎

引擎是一个库，传输层、认证和线程模型由宿主决定。[`samples/StrataRec.Samples.Host`](samples/StrataRec.Samples.Host) 是一个 ASP.NET Core 服务：用 `AddStrataRec` 加载工作区，`POST /recommend/{pipeline}` 返回排好序的结果；并且演示四个扩展点怎么写、怎么注册：

| 扩展点 | 契约 | 示例 |
| --- | --- | --- |
| 特征连接器 | `IFeatureSourceConnector` | `kv_store@1` |
| 召回源 | `ICandidateSource` | `trending@1` |
| 模型客户端 | `IModelClient` | `logistic@1` |
| 算子 | `IOperatorFactory` | `CategoryBoost@1` |

```sh
dotnet run --project samples/StrataRec.Samples.Host                   # http://localhost:5000
curl -s -X POST localhost:5000/recommend/trending_feed -H 'content-type: application/json' \
     -d @samples/StrataRec.Samples.Host/config/requests/user_1001.json
dotnet run --project samples/StrataRec.Samples.Host -- stratarec validate -c config   # 宿主自己的二进制带着自定义组件跑全部 stratarec 命令（-c 相对项目目录）
```

生产环境的节点服务 manifest 而不是工作区：CI 用 `stratarec build --store` 写产物和 manifest（序号默认接着已发布的那个，`stratarec diff --to <id> -s <store>` 看这次改了什么）、`stratarec run -m <id> -s <store>` 像节点一样加载它并跑一个请求、`stratarec publish` 改指针，出了问题用 `stratarec revoke` 立即停掉一个实验（之后的 `build` 不会把它原样带回来）；宿主在 `AddStrataRec` 里设置 `Releases`、列出 `Pipelines`，并注册 `IManifestSource` 和 `IArtifactStore`（本地目录各有现成的实现，其他存储实现这两个接口）。节点启动时加载每条流水线的 manifest，任何一条没有应用成功就不启动；之后按间隔轮询，新的 manifest 在后台编译、原子替换。缺少 manifest 所需能力的节点报 `incompatible`，继续服务当前计划。

## 示例工作区

```text
samples/home_feed/
  policy.jsonc                      实验可以修改哪些路径
  catalog/home_feed.jsonc           实体与特征（物理特征和衍生特征）
  bindings/local.jsonc              各环境的连接器、模型和召回源
  pipelines/home_feed.jsonc         参数、列、节点、漏斗组、探针、输出
  experiments/*.jsonc               每个实验一个文件，每个臂是一个合并补丁
  releases/home_feed.local.jsonc    发布的组成：目录、绑定和每个实验所在的层（流量分配归实验平台）
  fixtures/、requests/              本地运行用的内存数据和示例请求（请求的 assignment 即平台给出的臂）
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
