# 术语

一个概念只有一个词，文档、配置、公开 API 和内部类型用同一个。左边是规范的写法（中文文档里的词和代码里的词），右边是不要用来指它的词，或者容易混的另一个概念。新增公开类型、配置键和文档时照这张表；`scripts/gate.py` 拒绝公开类型名里出现标了“禁”的词。

| 术语（文档 / 代码） | 在这里指什么 | 不要写成 / 区别于 |
| --- | --- | --- |
| 流水线 / pipeline | 一份配置（`pipelines/*.jsonc`）描述的一次推荐流程：节点、列、参数、输出 | 禁：workflow。不叫 flow、DAG：它是按 `order` 排的一条线，不是图 |
| 计划 / plan（`ExecutionPlan`） | 一条流水线连同它在这次发布里的所有实验，编译一次得到的不可变结果，所有请求共用 | 区别于流水线（配置）；不叫 graph、program |
| 节点 / node | 流水线里的一步：一个算子、它的配置、`when`、`timeout_ms`、`on_failure` | 禁：stage。不叫 step、task（解释里的一步 `ExplainStep` 是另一回事） |
| 算子 / operator（`Name@N`） | 节点做的事的实现，由二进制提供，配置按 id 引用；`@N` 是配置契约的版本 | 禁：plugin。不叫 component、handler。配置键 `op` 是它的缩写，仅此一处 |
| 候选表 / candidate table（`CandidateTable`） | 一个请求一张按列存的表，一个键一行 | 行（row）是表里的一行；item 只指返回结果里的一项（`RankedItem`）和使用方自己的实体名 |
| 列 / column（`col.x`） | 候选表的一个逻辑列；每个写它的节点提交它的一个版本 | 字段（field）只指数据源里的一个字段；不叫 attribute |
| 特征 / feature（`col.item.x`、`ctx.user.x`） | 目录里声明、由数据源供给的值：候选的特征是列，上下文的特征是请求级的值 | 不叫 signal、attribute |
| 请求列 / request column（`req.x`） | 节点在请求里算出的请求级的值 | 区别于请求输入（`ctx.request.x`，宿主给的）和参数 |
| 参数 / param（`param.x`） | 流水线声明、实验可以覆盖的值 | 不叫 setting、flag、config。config 只指节点的 `config` 和算子的配置类 |
| 实验、臂、层 / experiment、arm、layer | 臂是基线上的一个合并补丁；一层里一个请求至多在一个臂 | 禁：bucket、treatment。中文不写“桶”。variant 另有所指（下一行） |
| 变体 / variant | 一个节点在某些臂的组合下的编译结果（统一计划里同一个位置的几种定义） | 不用来指臂 |
| 尝试 / attempt | 一个节点的一次执行；失败时候选表回到尝试之前 | 区别于运行和调用 |
| 运行 / run | 一条流水线对一个请求的一次执行：请求本身、它的后备流水线、被调用的流水线各是一次 | 不叫 execution、invocation |
| 调用 / call | 一次对外部服务的调用，或者一个算子对另一条流水线的一次调用 | 请求（request）只指宿主交给引擎的那一个 |
| 服务 / service（`IServiceClient`） | 流水线调用的外部依赖：模型、召回、任何别的服务，同一个契约 | 不再分 model client、candidate source |
| 连接器 / connector（`IFeatureSourceConnector`） | 一个特征数据源的实现 | 数据源（source）是绑定里的一个名字；连接器是它背后的实现 |
| 绑定 / binding（`bindings/<环境>.jsonc`） | 一个环境里，名字到实现和设置的映射：服务、数据源、表 | 不叫 wiring、registry |
| 工作区 / workspace | 一个配置目录：目录、绑定、流水线、实验、发布文件 | 不叫 project、repo |
| 发布、manifest / release、manifest | 一条流水线在一个环境里要服务的内容：产物的内容地址和实验所在的层 | 产物（artifact）是其中一个文件的规范内容；不叫 bundle（回放包才是 bundle） |
| 探针 / log tap（`log_taps`） | 流水线上一个记录行和列的位置，挂在节点或输出上 | 不叫 probe、hook、logger |
| 漏斗组 / funnel group | 几个探针共用的采样决定，使同一个请求在各探针上要么都记要么都不记 | — |
| 录制、回放包 / recording、replay bundle | 一个请求的输入和它所有外部回答的记录；回放用它在同一条执行路径上重跑 | 不叫 snapshot、trace。trace 只指结果里各节点的执行情况 |
| 后备 / fallback | 节点失败时代替它运行的节点（`fallback`），或流水线失败时宿主改为服务的流水线（`fallback_pipeline`） | 不叫 backup、retry：引擎在一个请求里不重试 |
| 缺失 / missing | 一个值不存在；是值的一部分，由表达式显式处理（`??`、`has`） | 不等于 0、空串或默认值；不叫 null（null 只是 JSON 里的写法） |
