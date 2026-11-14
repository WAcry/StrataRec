# 宿主示例：在 ASP.NET Core 服务里嵌入 StrataRec

一个最小的 Web 服务，演示两件事：

1. 怎样把引擎放进服务：`AddStrataRec` 在宿主启动时加载本目录下的工作区 `config/`，`POST /recommend/{pipeline}` 只做传输：请求体按流水线的 `RequestSchema` 读成 `RecRequest`，`RecResult` 用 `RecResultJson` 写回，按 `RecResult.ErrorKind` 映射状态码。输入的规则和结果文档都是引擎的，与 `stratarec run` 一致。
2. 怎样写并注册自己的组件：四个扩展点各有一个小实现，在 `SampleEnvironment.cs` 里和内置组件一起注册，工作区按类型名引用它们。

| 组件 | 类型名 | 契约 | 演示什么 |
| --- | --- | --- | --- |
| `Components/KeyValueFeatureConnector.cs` | `kv_store@1` | `IFeatureSourceConnector` | `Create` 只读设置、`ILoadable` 里做 I/O；按列解码，取数时不按行分配；缺失值不写，类型不符就让这次拉取失败 |
| `Components/TrendingCandidateSource.cs` | `trending@1` | `ICandidateSource` | 创建时排好序（平分按 id 升序）；按请求输入 `region` 选列表，没有 region 就用总榜；同步返回 |
| `Components/LogisticModelClient.cs` | `logistic@1` | `IModelClient` | 自己的签名（加载期校验节点的输入输出）；不会看到缺失值；输入不对返回失败状态，由节点的 `on_failure` 兜底 |
| `Components/CategoryBoost.cs` | `CategoryBoost@1` | `IOperatorFactory` | 配置类即 JSON Schema；只声明 `ColumnWriter`；检查都在编译期；执行时从 `RequestMemory` 租缓冲，不按行分配 |

每个文件的注释写了契约的规则（取消、预算、分配、缺失值）以及这个实现为什么这样写。

## 运行

```sh
cd samples/StrataRec.Samples.Host
dotnet run                                    # 默认监听 http://localhost:5000

curl -s localhost:5000/healthz
curl -s -X POST localhost:5000/recommend/trending_feed \
     -H 'content-type: application/json' -d @config/requests/user_1001.json
curl -s -X POST localhost:5000/recommend/trending_feed \
     -H 'content-type: application/json' -d '{ "inputs": { "user_id": 1002, "region": "de" } }'
```

请求体的三个成员都可选：`inputs`（按流水线的 `request_schema`）、`request_id`（缺省用 HTTP 请求的 TraceIdentifier）、`assignment`（实验平台给出的臂，`experiment/arm`）。示例请求带了 `exp_local_boost/strong`，本地新闻的加权从 1.5 变成 2.0。请求体是严格的：未知成员、同一对象里重复的成员（如 `{"inputs":{},"inputs":{}}`）、不成对的代理项转义（`"\uD800"`）都是 400，而不是取其中一个值或在读取时抛异常。

响应是 `RecResultJson` 的文档：`request_id`、`succeeded`、`served_by`、`manifest`、`experiments`、`items`（每项 `rank`、`id`、`columns`）、`elapsed_ms`。int64 的 id 是 JSON 数字；缺失值和非有限浮点数是 `null`。

状态码按引擎的 `RecErrorKind`：

| `ErrorKind` | 状态码 | 何时 |
| --- | --- | --- |
| `UnknownPipeline` | 404 | 引擎不服务这条流水线 |
| `InvalidRequest` | 400 | 请求体或输入不符合 `request_schema`；`pointer` 是第一个问题的 JSON 指针，`errors` 列出全部 |
| `DeadlineExceeded` | 504 | 流水线开始之前截止时间已用完（例如等实验平台） |
| `Failed` | 500 | 流水线运行了但失败（备用流水线也失败），`detail` 是引擎的错误信息 |

错误都是 RFC 9457 problem details。端点先用当前计划的 schema 读输入；引擎执行时再按它实际运行的计划检查一次，两次之间换了发布时以引擎的回答为准（仍然是 400 和指针）。

截止时间由服务决定（`StrataRec:DeadlineMs`，默认 1000 ms），不由调用方传入。冷进程的第一个请求包含 JIT 编译，所以默认值放得宽；生产服务按延迟目标设置，并在报告就绪之前预热。

## 校验工作区

官方 CLI `stratarec` 不认识这里的组件，对这个工作区运行 `stratarec validate` 会报未知类型。所以这个二进制带上了全部 `stratarec` 命令（`StrataRec.CommandLine` 库的 `StrataRecCommandLine.InvokeAsync`），用和服务相同的 `SampleEnvironment`：

```sh
dotnet run -- stratarec validate -c config               # 与 stratarec validate 相同的静态检查；有错误时退出码为 1
dotnet run -- stratarec schema operator CategoryBoost@1  # 自定义算子配置的 JSON Schema
dotnet run -- stratarec operators                        # 内置和本二进制的组件
```

命令、选项、输出和退出码与官方 CLI 完全相同。CI 里原本跑 `stratarec validate` 的地方改跑 `dotnet run --project <宿主项目> -- stratarec validate -c <工作区>`。`stratarec run` 的请求文件比 HTTP 请求体多一个 `pipeline_id`，因为 HTTP 由路由给出流水线。

静态校验不做 I/O：绑定的文件在服务加载计划时才打开，需要加载才能回答的模型签名也在那时检查。服务启动时（`AddStrataRec` 注册的托管服务，在开始监听之前）加载失败会抛出列出全部错误的 `ConfigException`，服务不会启动；警告写入日志（事件 1003）。

## 生产服务还要加什么

`Program.cs` 的注释列出了，这里不实现：接实验平台的 `IAssignmentProvider`、解析 `secret://` 的 `ISecretResolver`、订阅 `StrataRec` meter 和 activity source 的指标与追踪、写漏斗日志的 `ILogSink`，以及不重启加载新发布（`RecEngine.LoadAsync`）。

## 测试

`tests/StrataRec.Samples.Host.Tests/HostSampleTests.cs` 用 `WebApplicationFactory` 在进程内启动这个服务：工作区校验和加载都没有错误和警告；`stratarec` 命令带着自定义组件运行；推荐结果来自四个自定义组件（分数按数据和权重逐项核对），与 `stratarec run` 的输出一致；400（含重复成员和无效转义）、404、500 的响应，以及每种 `ErrorKind` 对应的状态码；配置有错时服务启动失败。
