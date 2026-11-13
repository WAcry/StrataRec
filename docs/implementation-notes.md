# 实现说明：与设计文档的对应、细化与尚未实现的部分

本文记录 `docs/design.md` 在实现阶段被细化或调整的地方，以及尚未实现的部分。设计文档本身保持原样。每一条都标注了
对应的设计章节和代码位置，评审时可以对照阅读。

## 1. 对设计的细化与调整

| 设计章节 | 实现中的决定 | 理由 | 代码 |
| :--- | :--- | :--- | :--- |
| 11.5 / 12.3 组合校验 | 组合空间不超过 `MaxCombinationsChecked`（默认 4096）时全量枚举。超过时退回到单个实验、成对组合和按列枚举，并报**错误** `COMBINATION_CHECK_LIMIT`，不是警告 | 设计假设“涉及的层很少，直接枚举即可”。层数一多，组合会指数增长。超限时如果只给警告就发布，等于在没有证明安全的情况下上线（评审 3 #1） | `Experiments/Compilation/PlanCompiler.Validation.cs` |
| 11.4 可补丁视图 | `columns`、`param_schema`、`features`、`derived_features`、`funnel_groups` 这几个 schema 段**始终只增**，与 policy 的写法无关 | 实验修改或删除基线的 schema，会让其他实验和基线的类型检查失效 | `Experiments/Compilation/ArmPatch.cs` |
| 11.5 基线独立性 | 基线必须单独编译通过。实验新增的特征或列不能“修好”基线的引用 | 实验下线后，基线必须仍然可用（评审 3 #2） | `PlanCompiler.Validation.cs` `ValidateBaselineAlone` |
| 12.6 触发记录 | 参数站点在**调用算子之前**标记触发，节点之后失败也算触发；后备节点站点在被调用时触发 | 按结果决定是否记为触发会让分析产生偏差：失败的请求会从实验组里消失（评审 3 #3） | `Pipeline/Execution/PipelineExecutor.cs` `MarkParamTriggers` |
| 11.1 域 | 跨域的臂组合不可能被同时分配，编译时直接跳过，`ArmsFromIds` 也拒绝这类组合 | 避免对不可能出现的组合报出冲突误报（评审 3 #6） | `PlanCompiler.cs` `CoOccur`、`ExecutionPlan.ArmsFromIds` |
| 5.6 `fallback_pipeline` | 宿主自动执行后备流水线：不带实验，只用**剩余**的端到端时间预算，结果的 `ServedBy` 标明由谁返回 | 设计只说“宿主可以选择返回它的结果”。实现选择统一的行为，并且不给后备流水线重新计时，以免整体超出请求的截止时间（评审 3 #12） | `Hosting/RecEngine.cs` |
| 10.3 计划加载 | 每条流水线的计划带单调递增的 `Sequence`，较旧的加载结果不会覆盖较新的计划（`PLAN_STALE`）。工作区组装出错时整体放弃，不做部分加载 | 后台编译可能乱序完成（评审 3 #5） | `Hosting/RecEngine.cs`、`Hosting/Workspace/ConfigWorkspace.cs` |
| 8.7 血缘 | 探针列读取 `col.x@node` 时，`column_producers` 记录被固定的那个节点，不是 `x` 的当前产出者 | 否则血缘会指向错误的版本 | `Pipeline/Planning/PlanBuilder.cs` `CompileTap`、`TapCapture.cs` |
| 8.6 热路径捕获 | 没有日志管道且请求没有设置 `CaptureLogs` 时，跳过漏斗采样和探针捕获 | 捕获出来的批次没有任何消费者，只会白白占用 CPU 和内存 | `PipelineExecutor.cs` |
| 6.7 缺失与失败 | 衍生特征的表达式抛出异常（数据层面的错误本应变成缺失值，所以这只会是缺陷）时，只让这一个特征失败，按数据源失败处理，读取它的节点也随之失败 | 异常不应越过特征拉取路径，也不应影响同一请求里的其他特征 | `Features/Runtime/FeatureFetcher.cs` |
| 5.7 `hash01` | 表达式后端用栈上缓冲区自行写规范编码，没有复用 `StableHash.Builder`。两份实现由一致性测试锁定为逐字节相同 | 标量路径因此完全不经过数组池 | `Expressions/Runtime/Hash01.cs` |
| 8.2 漏斗组 | 组的 `key` 只能读取请求输入（`ctx.request.*`），不能读取需要拉取的特征（`FUNNEL_KEY_FETCHED`）。`rate` 为 0 的组不计算 key | 组决定在请求开始时做出。如果 key 要等一次拉取，每个请求都会在召回之前阻塞在日志采样上（评审 4 #8） | `Pipeline/Planning/PlanBuilder.cs` `DefineFunnelGroups` |
| 8.5 被动探针 | 上下文特征与候选特征规则相同：探针只能记录服务链路本来就会拉取的上下文特征（`TAP_FEATURE_NOT_FETCHED`），只被探针读取的上下文特征不会进入预取 | 探针不应给每个请求都增加一次拉取，无论这个请求是否被采样（评审 4 #8）。`fetch_missing` 尚未实现 | `PlanBuilder.cs` `Build` |
| 8.3 `columns_from` | 展开的是当前臂组合下实际生效的那个模型节点变体；修改该模型节点的层也会参与探针的变体划分。探针 schema id 包含每一列解析后的表达式 | 训练日志必须记录实际服务时的输入（设计 6.9），同一个 schema id 不能对应两种列定义（评审 4 #1、#7） | `PlanCompiler.cs` `CompileTapVariants`、`PlanBuilder.CompileTap` |
| 8.9 隐私 | `personal` 沿衍生特征传递：由 personal 特征算出的衍生特征同样是 personal。`text` 类型的节点输出，只要产出者在任一变体中读取了 personal 数据，也视为 personal。数值输出（例如模型分数）不受影响 | 别名或变换不能绕开 purpose 与 durable 的要求。设计只把这条规则扩展到 text 列；如果把所有分数都标成 personal，普通的打分日志就无法记录（评审 4 #2） | `PlanBuilder.cs` `IsPersonalFeature`、`IsPersonalText` |
| 8.6 durable 投递 | `Submit` 返回投递结果（Queued、Spilled、Dropped、Closed）。内存队列满时调用 `DurableOverflow` 钩子落盘，批次保留原有的 `seq`；没有钩子时计入 `DurableOverflow`。sink 可以用 `LogBatchRejectedException` 声明某些批次永久不可写，这些批次不再重试，也不会阻塞后续批次 | 设计中的本地磁盘缓冲以钩子的形式提供，由宿主实现。其余情况下丢失必须可见，不能静默丢弃（评审 4 #3、#4） | `Logging/LogPipeline.cs` |
| 12.6 / 8.8 取消 | 调用方取消或请求异常中止时，仍然发布 `$experiments` 记录和已经捕获的探针批次 | 慢请求更容易被取消，而慢往往与臂相关；丢掉这些记录会让实验分析产生偏差（评审 4 #6） | `PipelineExecutor.cs` |
| 5.x 节点 id | 节点 id 只能是标识符（字母、数字、`_`） | 版本引用 `col.x@node` 必须能写出每一个节点；如果 id 中含 `.` 或 `-`，会被解析成字段访问或减法（评审 4 #14） | `ArmPatch.cs` `IsValidNodeId` |
| 5.7 规范编码 | `float64` 除了 −0.0 按 0.0 编码，所有 NaN（任意符号和载荷）一律按静默 NaN `0x7FF8000000000000` 编码。离线任务复算哈希时也要这样规范化 | IEEE 754 不规定定义域错误（`sqrt(-1)`、`0/0`）产生的 NaN 的符号和载荷，优化器把取负移进不变量、SIMD 交换操作数都会改变它，`hash01` 的结果不能随之变化 | `Core/Hashing/StableHash.cs` `CanonicalFloat64Bits` |
| 7.4 缺失值提升 | 带显式默认值的 `lookup(table, key, default)` 在 key 缺失时返回 `default`，不返回缺失；其他内置函数只要有参数缺失，结果就是缺失 | 第三个参数本身就是调用方给出的缺失处理，作用相当于 `lookup(table, key) ?? default`；如果改成缺失，现有打分表达式会变成可空类型而无法编译 | `Expressions/Vectorized/Instructions.cs` `LookupInstr` |
| 9.x 输出 | 输出段在所有节点之后编译 | 算子会声明隐式列（例如 `recall.primary_source`），输出需要能读到它们 | `PlanCompiler.cs` `Run` |
| 6.9 模型输入 | 模型输入必须是列或特征的直接引用。需要变换的输入先声明成衍生特征（示例里的 `author.log_followers`） | 这是设计 6.9 训练与服务一致性的要求。实现中给出了带修复提示的错误 `MODEL_INPUT_NOT_REFERENCE` | `samples/home_feed/catalog/home_feed.jsonc` |

## 2. 尚未实现的部分

当前是阶段性的框架，下面这些在设计中有描述，代码里还没有。产物 schema 是严格的，未知字段在加载时就被拒绝；未实现的 `mode` 会报出带修复提示的错误，不会被静默忽略。

| 设计章节 | 内容 | 现状 |
| :--- | :--- | :--- |
| 6.6 | `cache`、`local` 两种供给方式和本地特征表（快照加更新流、开放寻址索引） | 只支持 `fetch`；其他 `mode` 报“not available in this build yet” |
| 4.7 | 父子层级表 | 未实现 |
| 13.1 / 13.2 | 指标（`System.Diagnostics.Metrics`）和分布式追踪（`ActivitySource`、OpenTelemetry） | 未接入。目前的可观测性来自结果中的节点结果、trace 和非有限值计数 |
| 13.4 / 10.8 | 回放包与回放对比 | 未实现。确定性的前提已经具备：计划、请求输入、时间、外部响应和执行决定（超时、跳过、后备）都相同时，排序结果、分数、选中的变体和采样决定都相同，端到端测试比较的就是这些。耗时、`seq`、producer id 这类运行元数据不在保证范围内，回放对比时需要排除 |
| 10.7 | 合并前的草稿预览 | 未实现 |
| 5.9 | 成对、列表级、生成式等扩展算子示例（`PairwiseRanker@1` 等） | 内置算子见 `stratarec operators`，共 8 个 |
| 8.5 | `fetch_missing`：只为被采样的行、在响应返回之后补拉探针需要的特征 | 未实现。目前探针只能记录服务链路已经拉取的数据 |
| 8.6 | 本地磁盘缓冲的具体实现（落盘格式、重启后按序号补发） | 只提供 `DurableOverflow` 钩子 |
| 6.4 | 网络连接器（gRPC、Redis 等）和真实模型客户端 | 只有用于本地运行和测试的内存实现（`memory@1`、`memory_list@1`、`linear@1`）。接口（`IFeatureConnector`、`ICandidateSource`、`IModelClient`）已经确定 |
| 15 | 请求热路径托管分配 ≤ 2 KB 且与候选数无关 | 部分达成。按键的工作（实体键索引、拉取键、召回合并、请求账本）已经不再分配。示例流水线在 5,000 个候选下每个请求分配约 220 KB（原来是 4.2 MB），没有 Gen1/Gen2 回收。剩余部分：每次连接器调用约 420 B（任务、带计时器的取消源、租约、暂存缓冲；随 `候选数 / max_batch` 增长），探针批次的 `ScalarValue[]` 载荷，以及约 30–50 KB 的固定开销。要继续压缩，需要设计 4.8 中的请求状态池和池化的探针批次格式 |
| 15 | 端到端合成基准的完整规模（6 路召回、50 列、25 个基线节点、10 层 × 10 桶），以及 CI 中的回归阈值 | 已有 `PipelineBenchmarks`（示例流水线，500 和 5,000 候选）和 `ExpressionBenchmarks`。CI 只做 dry run，共享 runner 的噪声太大，不适合设阈值 |

## 3. 评审记录

每个阶段结束后都请 Codex 做了独立评审，评审结论没有照单全收，而是逐条判断后再修复：

- 评审 2（特征运行时、JSON 标准实现）：10 条，全部修复（`a38e427`）。
- 评审 3（实验、触发站点、宿主、CLI）：14 条，全部修复（`d3b2788`），回归测试见
  `tests/StrataRec.IntegrationTests/ReviewRegressionTests.cs`。
- 评审 4（日志、评审 3 修复的复核、ColumnExpr、端到端）：16 条，全部接受。修复深度的取舍见上表；回归测试见
  `tests/StrataRec.IntegrationTests/Review4RegressionTests.cs` 和 `tests/StrataRec.UnitTests/Logging/LoggingTests.cs`。
- 评审 5（复核评审 4 的修复、热路径分配优化、文档）：6 条，全部修复。评审 4 的 16 条均确认关闭，分配优化中没有发现缓冲区生命周期问题。
  - 主要问题：banned-API 分析器 5.x 在 .NET 8 SDK 上无法构建，改用 3.3.4，CI 同时在固定的 SDK 8 和 SDK 10 上构建。
  - 评审 4 #6 的修复引入了回归：被取消的 `CaptureLogs` 请求会写入生产日志，已修复。
