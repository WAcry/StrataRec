# 决策记录

这里只记读代码看不出来的东西：为什么这样做、考虑过而没有选的方案、代价，以及刻意不做的事。系统现在怎么做，以代码和测试为准；这里和代码不一致时，是这里过时了，改这里。判断的标准在 [`../development.md`](../development.md)。

## 怎么用

- 改一个领域之前，先看它的文件有哪些决定：`grep -n '^- \*\*' docs/decisions/<文件>.md` 列出每一条的标题，再读相关的那几条。按类型名、配置键或错误码找：`grep -rn 'TopK@1' docs/decisions/`。
- 有人提出一个能力或者改法时，先看 [`scope.md`](scope.md)：不做的、暂时不做的和理由在那里。
- 做出新的决定时怎么记、记在哪，见 `.agents/skills/record-decision/SKILL.md`。

## 文件

| 文件 | 管什么 |
| --- | --- |
| [`experiments.md`](experiments.md) | 实验、臂的补丁、层、统一计划的编译和校验、触发记录（`src/StrataRec.Experiments`） |
| [`execution.md`](execution.md) | 节点的一次尝试、候选表、失败与回滚、预算、取消、对扩展的调用（`src/StrataRec.Pipeline/Execution`、`src/StrataRec.Core/Tables`） |
| [`operators.md`](operators.md) | 内置算子、模型调用和张量（`src/StrataRec.Pipeline/Operators`、`src/StrataRec.Llm`） |
| [`hosting.md`](hosting.md) | 嵌入宿主、发布和撤销、命令行、请求和结果的文档（`src/StrataRec.Hosting`、`src/StrataRec.CommandLine`） |
| [`request-columns.md`](request-columns.md) | 请求级的值 `req.*` |
| [`pipeline-calls.md`](pipeline-calls.md) | 算子调用另一条流水线：`PipelineSource@1`、`PipelineColumns@1` |
| [`features.md`](features.md) | 特征目录、拉取规划、预取、暂存与回填（`src/StrataRec.Features`） |
| [`local-tables.md`](local-tables.md) | 进程内的特征表和缓存表（`src/StrataRec.Features/Local`、`src/StrataRec.Features/Cache`） |
| [`bindings.md`](bindings.md) | 绑定的实例：创建、共用、加载、释放（`src/StrataRec.Hosting/Bindings`） |
| [`external-calls.md`](external-calls.md) | 外部依赖：服务契约、有界等待、服务缓存、实验分配的调用 |
| [`logging.md`](logging.md) | 漏斗日志、探针、采样、日志管道（`src/StrataRec.Logging`） |
| [`expressions.md`](expressions.md) | 表达式语言和类型（`src/StrataRec.Expressions`） |
| [`observability.md`](observability.md) | 指标、span、事件日志、解释 |
| [`replay.md`](replay.md) | 录制和回放 |
| [`config-errors.md`](config-errors.md) | 配置的绑定和错误信息 |
| [`plan-tools.md`](plan-tools.md) | `explain-plan`、`diff`、`compare` 等计划工具 |
| [`engineering.md`](engineering.md) | 构建、测试、分配预算、目标运行时、格式版本 |
| [`scope.md`](scope.md) | 不做的和暂时不做的 |
