# 宿主示例：在 ASP.NET Core 服务里嵌入 StrataRec

一个最小的 Web 服务，演示两件事：

1. 怎样把引擎放进服务：`AddStrataRec` 在宿主启动时加载本目录下的工作区 `config/`，`POST /recommend/{pipeline}` 只做传输：请求体用 `RecRequestJson` 按流水线的 `RequestSchema` 读出，宿主补上截止时间组成 `RecRequest`，`RecResult` 用 `RecResultJson` 写回，按 `RecResult.ErrorKind` 映射状态码。请求文档和结果文档都是引擎的，换一个宿主也是同样的文档。
2. 怎样写并注册自己的组件：三个扩展点有八个小实现（外部服务的召回、模型和过滤三种用法，调用另一条流水线的算子，以及处理列表列的算子），在 `SampleEnvironment.cs` 里和内置组件一起注册，工作区按类型名引用它们。

| 组件 | 类型名 | 契约 | 演示什么 |
| --- | --- | --- | --- |
| `Components/KeyValueFeatureConnector.cs` | `kv_store@1` | `IFeatureSourceConnector` | `Create` 只读设置、`ILoadable` 里做 I/O；按列解码，取数时不按行分配；缺失值不写，类型不符就让这次拉取失败 |
| `Components/TrendingService.cs` | `trending@1` | `IServiceClient` | 召回节点调用的服务：创建时排好序（平分按 id 升序）；按调用的输入 `region` 选列表（召回节点的 `queries` 把 `ctx.request.region` 送给它；服务只看到调用送的值），值缺失时用总榜；同步返回。绑定声明了 `cache`，每个地区的回答在进程内缓存 30 秒 |
| `Components/LogisticModelClient.cs` | `logistic@1` | `IServiceClient` | 模型算子调用的服务：自己的签名（加载期校验节点的输入输出）；不会看到缺失值；输入不对返回失败状态，由节点的 `on_failure` 兜底 |
| `Components/CategoryBoost.cs` | `CategoryBoost@1` | `IOperatorFactory` | 配置类即 JSON Schema；只声明 `ColumnWriter`；检查都在编译期；执行时从 `RequestMemory` 租缓冲，不按行分配 |
| `Components/StockService.cs` | `stock@1` | `IServiceClient` | 过滤节点调用的业务服务：回答关于物品的调用，每项一个 bool（`in_stock`）；一项的回答只取决于这一项，所以引擎可以随意分批、逐项缓存（绑定声明了 `cache`）和逐项回放；回答不了的调用返回失败状态并说明它回答什么 |
| `Components/StockFilter.cs` | `StockFilter@1` | `IOperatorFactory` | 调用外部服务的自定义算子：编译期按绑定名取服务（`OperatorCompileContext.Service`），执行时把存活行作为调用的物品经 `OperatorContext.CallAsync` 发出（有界等待、缓存、录制和回放都由引擎做），按回答删行；调用失败交给节点的 `on_failure`（示例里 `skip`，保留全部候选），回答用不了也算失败，从不猜；改了流水线之后回放，没有录制过的候选是回放报告里的缺口：录制里这个服务（同一版本，哪个节点调用的都算）有过这个输出时按 bool 缺口的值 false 回答，所以被删掉，看缺口才能和真没库存的区分；录制里这个服务根本没有这个输出（录制时没有调用它）时调用失败，按 `on_failure` 保留全部候选。它还演示算子写请求列：删掉了几个候选写进 `req.stock.dropped`（描述符带 `RequestWriter`，编译期 `TryWriteRequestColumn`，执行时 `SetRequestValue`，节点提交时才发布），流水线用 `output.values` 把它作为 `out_of_stock` 返回给调用方 |
| `Components/SlotBlend.cs` | `SlotBlend@1` | `IOperatorFactory` | 调用另外两条流水线的自定义算子：编译期声明每次调用和它需要的东西（`TryCallPipeline`、`CallAppendsRows`、`TryCallColumn`），在工作区里对着被调用流水线的接口检查；执行时两次调用先后发起、再一起等（`OperatorContext.CallPipelineAsync`），所以两条流水线并发运行；调用失败是一个结果，没有主列表就让节点失败，没有赞助内容只是降级，可有可无的那次调用可以有自己的时限（`sponsored_timeout_ms`）；它和别的行源一样只追加请求没见过的键；怎么把赞助行放进列表是它自己的规则 |
| `Components/DistinctHeads.cs` | `DistinctHeads@1` | `IOperatorFactory` | 处理列表列的自定义算子：一行的列表是这一行的值（`ListColumn<T>`），读的是节点之前的版本，写的是自己的版本（列声明 `"flow": true`）；从 id 决定一次顺序，逐个元素对齐的几个列表按同一组位置取（`ListColumn.SetTaken`），不管元素类型；规则本身（上面的栏目没有在开头展示过的物品排到这个栏目的开头，不够时其余的照原来的顺序跟上）是这个页面自己的 |

每个文件的注释写了契约的规则（取消、预算、分配、缺失值）以及这个实现为什么这样写。

## 一个页面，几条流水线

`config/pipelines/home.jsonc` 是首页：它只有一个节点 `SlotBlend@1`，为同一个请求调用 `trending_feed` 和 `sponsored` 两条流水线，把赞助行每隔几行放进主列表。宿主只请求 `home`，另外两条由引擎运行：同一个请求 id、同一次实验分配（各条流水线运行自己的臂，结果的 `experiments` 里是全部），时间用的是这个节点剩下的预算。三条流水线各自有节点、参数、实验和日志，也都可以单独请求。

页面怎么组成是这个节点的配置，所以和别的配置一样能做实验：`experiments/exp_home_slots.jsonc` 把赞助行从每三行一个改成每两行一个；换一条被调用的流水线，或者换掉这个节点，也只是补丁。

```sh
curl -s -X POST localhost:5000/recommend/home \
     -H 'content-type: application/json' -d @config/requests/home_1001.json
```

用 `stratarec run -t` 运行同一个请求（请求文件里加上 `"pipeline_id": "home"`）时，追踪里有每条被调用流水线的节点。

## 一个页面，几个栏目

`config/pipelines/shelves.jsonc` 是另一种页面：它的每一行是一个栏目，栏目里的物品是这一行的列表。栏目从一个列表召回（`shelf_list`，真实系统里是为用户挑栏目的服务），`PipelineColumns@1` 按行的键为每个栏目调用它自己的流水线（`trending_feed`、`editors_picks`、`sponsored`），同时发出，把返回的 id 和分数写进这一行的 `items`、`item_scores`；只有赞助栏目的流水线有 `track_id`，所以 `track_ids` 只在那一行有值。没有内容的栏目被过滤掉（`archive` 没有流水线）。栏目是普通的行：要给栏目打分、排序、做实验，用的就是每条流水线都有的节点。

- 同一个物品可以在几个栏目里（物品 3 既在热门里也在编辑精选里）：每一行有自己的列表。
- 跨栏目的规则是页面自己的算子 `DistinctHeads@1`：上面的栏目没有在开头展示过的物品，排到这个栏目的开头；这样的物品不够时，其余的照原来的顺序跟上（不丢物品，所以开头仍然可能有重复）。实验 `experiments/exp_shelf_heads.jsonc` 的 `as_ranked` 臂把这条规则关掉。
- 页面展示了什么由这条流水线的探针 `shown` 记录（每个栏目一行，带它的列表）；各栏目流水线照常写自己的日志。

```sh
curl -s -X POST localhost:5000/recommend/shelves \
     -H 'content-type: application/json' -d @config/requests/shelves_1001.json
```

结果的每个条目是一个栏目：`{ "id": "sponsored", "columns": { "items": [24, 2, 25, 26], "item_scores": […], "track_ids": ["spring-24", …] } }`。

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

## 服务发布（manifest）

上面的服务在启动时读一次工作区。生产节点服务的是发布出来的 manifest：设置 `StrataRec:Store` 为一个 artifact store 目录，服务就从那里读每条流水线（`StrataRec:Pipelines`，默认是示例的全部五条）的指针，启动前加载好，之后按 `StrataRec:ReleasePollMs`（示例默认 2 秒，生产用引擎默认的 10 秒）轮询新的发布。`config/` 这时只是绑定里相对路径的基准目录。在本目录里走一遍完整流程：

```sh
for p in trending_feed sponsored home editors_picks shelves; do   # 每条流水线各自发布；服务的每一条、它们调用的每一条都要有
  dotnet run -- stratarec build -c config -s store -p $p  # 校验、上传产物，打印 publish 命令
done
dotnet run -- stratarec publish -s store <manifest id>    # 每个 manifest 一次
dotnet run --StrataRec:Store=store                        # 服务它；响应里的 manifest 是它的 id

# 另一个终端：紧急停止一个实验。一次轮询之内服务换成撤销后的 manifest，这个实验的臂此后被忽略（experiments.ignored）
dotnet run -- stratarec revoke -s store -p trending_feed exp_local_boost
dotnet run -- stratarec status -s store                   # 发布了什么、哪些停止正在生效
```

之后 `stratarec build` 不会把被停止的实验原样带回来：先从 `config/releases/` 里去掉它，或者修改它（owner 的修复）再发布。

## 校验工作区

官方 CLI `stratarec` 不认识这里的组件，对这个工作区运行 `stratarec validate` 会报未知类型。所以这个二进制带上了全部 `stratarec` 命令（`StrataRec.CommandLine` 库的 `StrataRecCommandLine.InvokeAsync`），用和服务相同的 `SampleEnvironment`：

```sh
dotnet run -- stratarec validate -c config               # 与 stratarec validate 相同的静态检查；有错误时退出码为 1
dotnet run -- stratarec schema operator CategoryBoost@1  # 自定义算子配置的 JSON Schema
dotnet run -- stratarec operators                        # 内置和本二进制的组件
```

`config/schemas/` 是这个二进制写出的 JSON Schema，`config/` 下每个文件用第一个成员 `"$schema"` 指向自己那一类（引擎忽略它，也不计入内容 id）。在 VS Code 里打开就有补全、悬停说明和即时报错，包括自定义算子 `CategoryBoost@1` 的配置项。官方 CLI 写出的 schema 不含自定义组件，所以要用宿主自己的二进制生成；加了组件或升级 StrataRec 之后重新生成并提交，否则 `validate` 会给出 `SCHEMAS_STALE` 警告（CI 用 `--warnings-as-errors` 时即失败）：

```sh
dotnet run -- stratarec schema --out config/schemas       # 每类文件一个 <kind>.schema.json，同一个二进制总是写出相同的字节
```

`config/requests/user_1001.json` 是给 curl 用的 HTTP 请求体，不是 `stratarec run` 的请求文件，所以不带 `"$schema"`：端点是严格的，会把它当未知成员拒绝。

命令、选项、输出和退出码与官方 CLI 完全相同。CI 里原本跑 `stratarec validate` 的地方改跑 `dotnet run --project <宿主项目> -- stratarec validate -c <工作区>`；`dotnet run` 在项目目录运行（`RunWorkingDirectory`），相对路径从那里算，本示例就是 `-c config`。`stratarec run` 的请求文件比 HTTP 请求体多一个 `pipeline_id`，因为 HTTP 由路由给出流水线。

静态校验不做 I/O：绑定的文件在服务加载计划时才打开，需要加载才能回答的模型签名也在那时检查。服务启动时（`AddStrataRec` 注册的托管服务，在开始监听之前）加载失败会抛出列出全部错误的 `ConfigException`，服务不会启动；警告写入日志（事件 1003）。

## 生产服务还要加什么

`Program.cs` 的注释列出了，这里不实现：接实验平台的 `IAssignmentProvider`、解析 `secret://` 的 `ISecretResolver`、订阅 `StrataRec` meter 和 activity source 的指标与追踪、写漏斗日志的 `ILogSink`，以及不重启加载新发布（`RecEngine.LoadAsync`）。

## 测试

`tests/StrataRec.Samples.Host.Tests/HostSampleTests.cs` 用 `WebApplicationFactory` 在进程内启动这个服务：工作区校验和加载都没有错误和警告；`stratarec` 命令带着自定义组件运行；`config/schemas/` 与这个二进制写出的完全相同；推荐结果来自四个自定义组件（分数按数据和权重逐项核对），与 `stratarec run` 的输出一致；400（含重复成员和无效转义）、404、500 的响应，以及每种 `ErrorKind` 对应的状态码；配置有错时服务启动失败。
