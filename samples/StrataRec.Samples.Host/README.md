# 宿主示例：在 ASP.NET Core 服务里嵌入 StrataRec

一个最小的 Web 服务，演示两件事：

1. 怎样把引擎放进服务：`AddStrataRec` 加载本目录下的工作区 `config/`，`POST /recommend/{pipeline}` 把 HTTP 请求转成 `RecRequest`，把 `RecResult` 写回 JSON，错误映射成状态码。
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

请求体的三个成员都可选：`inputs`（按流水线的 `request_schema`）、`request_id`（缺省用 HTTP 请求的 TraceIdentifier）、`assignment`（实验平台给出的臂，`experiment/arm`）。示例请求带了 `exp_local_boost/strong`，本地新闻的加权从 1.5 变成 2.0。

状态码：流水线不存在 404；请求体或输入不符合 `request_schema` 400，`pointer` 指出位置；引擎执行失败 500，`detail` 是引擎的错误信息。错误都是 RFC 9457 problem details。

截止时间由服务决定（`StrataRec:DeadlineMs`，默认 1000 ms），不由调用方传入。冷进程的第一个请求包含 JIT 编译，所以默认值放得宽；生产服务按延迟目标设置，并在报告就绪之前预热。

## 校验工作区

官方 CLI `stratarec` 不认识这里的组件，对这个工作区运行 `stratarec validate` 会报未知类型。所以这个二进制自己提供校验，用的是和 CLI 相同的 Hosting API、和服务相同的 `PlanEnvironment`：

```sh
dotnet run -- validate                    # 与 stratarec validate 相同的静态检查；有错误时退出码为 1
dotnet run -- schema CategoryBoost@1      # 自定义算子配置的 JSON Schema
```

CI 里原本跑 `stratarec validate` 的地方改跑 `dotnet run --project <宿主项目> -- validate`。静态校验不做 I/O：绑定的文件在服务加载计划时才打开，需要加载才能回答的模型签名也在那时检查；服务启动时加载失败会直接退出。

## 生产服务还要加什么

`Program.cs` 的注释列出了，这里不实现：接实验平台的 `IAssignmentProvider`、解析 `secret://` 的 `ISecretResolver`、订阅 `StrataRec` meter 和 activity source 的指标与追踪、写漏斗日志的 `ILogSink`，以及不重启加载新发布（`RecEngine.LoadAsync`）。

## 测试

`tests/StrataRec.Samples.Host.Tests/HostSampleTests.cs` 用 `WebApplicationFactory` 在进程内启动这个服务：工作区校验和加载都没有错误和警告；推荐结果来自四个自定义组件（分数按数据和权重逐项核对）；404、400、500 三种错误。
