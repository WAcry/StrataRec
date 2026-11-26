# 架构：一页图和不变量

每一处怎么做，读代码；这一页只给要读很多文件才拼得出来的东西：一次请求经过哪些部分，每个项目管什么，以及全局成立、改动时不能打破的不变量和它们的所有者。反引号里的名字都在仓库里（`scripts/gate.py` 检查拼写，不检查这句话说得对不对），对不上就是这一页过时了，改这一页。

## 从配置到一次请求

1. **配置 → 计划（加载期，每次发布一次）。** `ConfigWorkspace` 读工作区；`PlanCompiler` 把基线和每个臂的补丁编译成一份统一的 `ExecutionPlan`，节点、列和表达式由 `PlanBuilder` 编译。名字解析、类型检查、数据流检查和所有可能同时生效的臂组合的校验都在这一步。生产节点读发布出来的 manifest（`ManifestBuilder`），得到的是同一种编译请求。
2. **请求 → 运行。** 宿主调用 `IRecEngine`。`RecEngine` 取当前计划，向 `IAssignmentProvider` 要这个请求的臂，交给 `PipelineExecutor`。
3. **一次运行。** 节点按 `order` 一个接一个执行，没有并行的节点。每个节点：按请求的臂选出变体 → `when` → 取齐它读的特征（`FeatureFetcher`；预取在它之前已经发出）→ 一次尝试（检查点、算子、提交；失败就回滚）→ 按 `on_failure` 处理 → 探针。
4. **结果。** 按 `output` 取行和输出列，构造 `RecResult`；探针的批次交给 `LogPipeline`；要求录制时带上 `ReplayBundle`，回放用它在同一个执行器上重跑（`RunReplay`）。

## 项目和依赖方向

依赖只能指向表里更靠上的项目；`scripts/gate.py` 按这张表检查每个 `.csproj`。核心（前九个）不引用扩展包。

| 项目 | 管什么 | 引用 |
| --- | --- | --- |
| `StrataRec.Core` | 类型系统、`CandidateTable`、列缓冲、`RequestMemory`、`StableHash`、有界等待 `BoundedCall`、服务契约 `IServiceClient` | 无 |
| `StrataRec.Artifacts` | JSON 的严格读取和规范化（`StrictJson`、`Jsonc`、`JsonPointer`）、产物模型、内容寻址存储 | Core |
| `StrataRec.Expressions` | `ColumnExpr`：解析、绑定和类型检查、IR、向量化后端、参考解释器 | Core |
| `StrataRec.Features` | 特征目录、拉取规划、连接器契约、本地表和缓存 | Core、Expressions、Artifacts |
| `StrataRec.Models` | 内置的服务客户端，给测试、示例和本地开发 | Core、Artifacts |
| `StrataRec.Logging` | 漏斗组、探针批次、协调采样、异步日志管道 | Core、Expressions |
| `StrataRec.Pipeline` | 节点模型、计划、算子契约和内置算子、执行器、回放 | 以上全部 |
| `StrataRec.Experiments` | 臂的补丁、统一计划的编译和校验、外部分配的契约 | Pipeline、Artifacts |
| `StrataRec.Hosting` | `IRecEngine`、工作区、manifest、绑定实例的生命周期、请求和结果的 JSON、DI | Experiments、Pipeline |
| `StrataRec.CommandLine` | `stratarec` 的各个命令，做成库，宿主用自己的组件调用 | Hosting |
| `StrataRec.Redis` | `redis@1`：缓存的分布式层 | Features |
| `StrataRec.Llm` | 大模型类算子和提示词模板 | Pipeline |
| `StrataRec.Cli` | 可执行文件：内置组件加两个扩展包 | CommandLine、Redis、Llm |

## 扩展点

使用方接入自己的系统只通过这些契约；每个契约的规则写在它自己的文档注释里，只写一处。由绑定按名字选用的实现（服务、连接器、更新来源、缓存的存储）用 `BindingFactory` 注册，设置就有了 Schema 和校验。

| 契约 | 接什么 |
| --- | --- |
| `IOperatorFactory` | 自定义算子：编译期用 `OperatorCompileContext` 声明读写，请求期用 `OperatorContext` |
| `IServiceClient` | 流水线调用的服务：模型、召回、其他 |
| `IFeatureSourceConnector` | 特征数据源 |
| `IFeatureUpdateSource` | 本地表的更新来源 |
| `ICacheStore` | 缓存的分布式层 |
| `IAssignmentProvider` | 实验平台：一个请求在哪些臂 |
| `ILogSink` | 日志的去处 |
| `ISecretResolver` | 绑定里 `secret://` 的解析 |
| `IManifestSource`、`IArtifactStore` | 发布：当前的 manifest 和它指向的产物从哪里读 |
| `IReleaseStatusReporter` | 每个节点加载了哪次发布，报给谁 |
| `IExplainableOperator` | 自定义算子怎么解释它对一行做了什么（`--explain`） |

## 不变量

改动可以自由选择实现，但这些要一直成立。每一条有一个所有者（规则只在那里实现）和守住它的测试；理由在 [`development.md`](development.md) 和 `decisions/`。每一条说到哪里为止也写在里面：括号里的例外和前提是契约的一部分，不是待补的缺口。

| # | 不变量 | 所有者 | 测试 |
| --- | --- | --- | --- |
| 1 | 一行在一个列的版本里没有被写过就是缺失（表保证）；一个键至多一个存活行（行源在追加之前自己跳过存活的键：`OperatorContext.AppendRows` 的契约，表不去重） | `CandidateTable`、行源 | `UnwrittenRowsTests`、`CompactionTests` |
| 2 | 失败的尝试不留痕迹：它追加的行、它写的列的版本、请求列都回到尝试之前；预算用完之后才到的结果不采用。前提是算子只写自己声明的输出和自己追加的行，输入是只读的 | `CandidateTable.Rollback`、`PipelineExecutor` | `RowSourceFallbackTests`、`NodeRunsAgainTests` |
| 3 | 提交时，不可空的列在每个存活行上都有值；`default` 不替没写的行补值 | `PipelineExecutor` | `UnwrittenAndUncommittedDataTests` |
| 4 | 缺失、非有限值、存不下的值不静默变成一个看似合法的数（表达式里 `int64` 的加减乘溢出回绕，是指南里写明的例外） | `ColumnValues`、表达式后端 | `NarrowValueTests`、`SemanticsTests` |
| 5 | 编译期接受的，运行时就能执行；统一计划接受的每个臂组合，单独编译它也接受、得到同样的计划（统一计划可以多拒绝：跨组合的冲突，检查超出上限） | `PlanCompiler` | `GeneratedExperimentTests` |
| 6 | 计划所有请求共用，公开面只读：没有 setter，没有声明成可变集合或 JSON 树的成员（把只读接口强转回去改，和用反射改一样，不在保证之内）。节点的表达式、列和参数在请求期不解析、不按名字查找（请求输入按名字绑定一次；带配置覆盖的调试请求在请求内编译） | `ExecutionPlan` | `PlanImmutabilityTests` |
| 7 | 确定性：计划、请求（输入、id、种子）、时间上的判定（时钟读数，哪一次等待先到了上界）、外部回答和臂相同，结果就相同；回放复用录制下来的这些判定。随机只来自请求种子的 `StableHash` | `PipelineExecutor` | `ReplayTests` |
| 8 | 请求路径上框架发起的每一次外部等待（服务、连接器、缓存的存储、臂的分配）都有上界，只有一份实现，不依赖对方响应取消。不在其内：自定义算子里同步执行的代码；启动、发布状态上报和日志 sink，它们收到取消信号，不保证限时返回 | `BoundedCall` | `BoundedCallTests`、`TimerFaultTests` |
| 9 | 向外交付的值（给服务的张量、日志、结果）是声明的类型和宽度，不做隐式转换。进来的请求输入和流水线调用的输入只按一张固定的表放宽（`int32`→`int64`、整数→浮点） | `ColumnType`、各边界的写入器、`RequestSchema` | `DeclaredWidthEndToEndTests`、`RequestSchemaTests` |
| 10 | 表达式的向量化后端和参考解释器结果相同 | `ReferenceInterpreter` | `DifferentialTests` |
| 11 | 请求的分配不随候选数增长（列缓冲随请求租借和归还；交给调用方的结果随返回的行数增长）；`src/` 里不用 `BannedSymbols.txt` 列出的 API（LINQ） | `RequestMemory`、`BannedSymbols.txt` | `RequestAllocationTests`、`BufferPoisoningTests` |
| 12 | 回放、解释、计划对比读的是请求实际执行的编译结果，不另写一份逻辑 | `RunReplay` | `ReplayTests`、`ExplainEndToEndTests` |
| 13 | 配置严格绑定；每个错误有精确的位置和一句修法，只报一次 | `StrictJson`、`OperatorCompileContext` | `DiagnosticLocationCliTests` |
| 14 | JSON Schema 从配置类型生成；示例里的副本和生成结果相同 | `ArtifactSchemas` | `EditorSchemaTests` |
