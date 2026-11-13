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
| 5.7 规范编码 | `float64` 除了 −0.0 按 0.0 编码，所有 NaN（任意符号和载荷）一律按静默 NaN `0x7FF8000000000000` 编码。离线任务复算哈希时也要这样规范化 | IEEE 754 不规定定义域错误（`sqrt(-1)`、`0/0`）产生的 NaN 的符号和载荷，优化器把取负移进不变量、SIMD 交换操作数都会改变它，`hash01` 的结果不能随之变化 | `Core/Hashing/StableHash.cs` `CanonicalFloat64Bits` |
| 9.x 输出 | 输出段在所有节点之后编译 | 算子会声明隐式列（例如 `recall.primary_source`），输出需要能读到它们 | `PlanCompiler.cs` `Run` |
| 6.9 模型输入 | 模型输入必须是列或特征的直接引用。需要变换的输入先声明成衍生特征（示例里的 `author.log_followers`） | 这是设计 6.9 训练与服务一致性的要求。实现中给出了带修复提示的错误 `MODEL_INPUT_NOT_REFERENCE` | `samples/home_feed/catalog/home_feed.jsonc` |

## 2. 尚未实现的部分

当前是阶段性的框架，下面这些在设计中有描述，代码里还没有。产物 schema 是严格的，未知字段在加载时就被拒绝；未实现的 `mode` 会报出带修复提示的错误，不会被静默忽略。

| 设计章节 | 内容 | 现状 |
| :--- | :--- | :--- |
| 6.6 | `cache`、`local` 两种供给方式和本地特征表（快照加更新流、开放寻址索引） | 只支持 `fetch`；其他 `mode` 报“not available in this build yet” |
| 4.7 | 父子层级表 | 未实现 |
| 13.1 / 13.2 | 指标（`System.Diagnostics.Metrics`）和分布式追踪（`ActivitySource`、OpenTelemetry） | 未接入。目前的可观测性来自结果中的节点结果、trace 和非有限值计数 |
| 13.4 / 10.8 | 回放包与回放对比 | 未实现。确定性已经具备：同一请求和计划的结果逐字节一致，端到端测试有覆盖 |
| 10.7 | 合并前的草稿预览 | 未实现 |
| 5.9 | 成对、列表级、生成式等扩展算子示例（`PairwiseRanker@1` 等） | 内置算子见 `stratarec operators`，共 8 个 |
| 6.4 | 网络连接器（gRPC、Redis 等）和真实模型客户端 | 只有用于本地运行和测试的内存实现（`memory@1`、`memory_list@1`、`linear@1`）。接口（`IFeatureConnector`、`ICandidateSource`、`IModelClient`）已经确定 |
| 15 | 端到端合成基准的完整规模（6 路召回、50 列、25 个基线节点、10 层 × 10 桶），以及 CI 中的回归阈值 | 已有 `PipelineBenchmarks`（示例流水线，500 和 5,000 候选）和 `ExpressionBenchmarks`。CI 只做 dry run，共享 runner 的噪声太大，不适合设阈值 |

## 3. 评审记录

每个阶段结束后都请 Codex 做了独立评审，评审结论没有照单全收，而是逐条判断后再修复：

- 评审 2（特征运行时、JSON 标准实现）：10 条，全部修复（`a38e427`）。
- 评审 3（实验、触发站点、宿主、CLI）：14 条，全部修复（`d3b2788`），回归测试见
  `tests/StrataRec.IntegrationTests/ReviewRegressionTests.cs`。
