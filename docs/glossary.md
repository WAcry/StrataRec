# 术语

一个概念只有一个词，文档、配置、公开 API 和内部类型用同一个。每张表左边是规范的写法（中文文档里的词和代码里的词），右边是不要用来指它的词，或者容易混的另一个概念。新增公开类型、配置键和文档时照这些表；`scripts/gate.py` 拒绝公开类型名里出现标了“禁”的词。要给一个东西起名时，先在这里和代码里找它已经叫什么；新概念有了名字并且会在几处用到时，才在这里加一行。

## 配置和发布

| 术语（文档 / 代码） | 在这里指什么 | 不要写成 / 区别于 |
| --- | --- | --- |
| 工作区 / workspace | 放一套配置的文件夹：目录、绑定、流水线、实验、发布文件 | 不叫 project、repo |
| 目录 / catalog（`catalog/*.jsonc`） | 声明实体、物理特征和衍生特征的文件；流水线按名字读里面的特征 | 不叫 registry、feature store。文件系统的目录写“文件夹”或者直接写路径 |
| 绑定 / binding（`bindings/<环境>.jsonc`） | 一个环境里，名字到实现和设置的映射：服务、数据源、表 | 不叫 wiring、registry |
| 环境 / environment | 绑定文件和发布文件上的一个名字（`"environment": "local"`）：同一份目录和流水线，每个环境用自己的绑定 | 不叫 profile、cluster、tier |
| 发布 / release（`releases/*.jsonc`） | 作者写的：一条流水线在一个环境里服务哪些实验、各在哪一层，用的是工作区里的名字 | 区别于 manifest（下一行） |
| manifest（`ReleaseManifest`） | `stratarec build` 从一次发布生成、节点加载的文件：每个产物的内容地址、实验所在的层、发布的序号（`seq`，节点只接受比当前大的） | 不叫 bundle（回放包才是 bundle） |
| 产物、存储 / artifact、store（`IArtifactStore`） | 产物是一个配置文件规范化之后的内容，用内容的哈希称呼（`sha256:…`）；存储按这个地址存取产物，`stratarec build -s <存储>` 往里写 | 不叫 blob、package。区别于缓存的存储（`ICacheStore`，缓存的分布式层） |
| 能力 / capability（manifest 的 `requires`） | 一次发布要求二进制提供的算子和绑定类型（`TopK@1`、`memory@1`），由构建从编译结果算出；基线的和每个实验多出来的分开记。二进制缺一项就不加载这次发布，当前的计划留着 | 不叫 dependency、feature flag。区别于算子的效果 |
| 撤销 / revoke（`stratarec revoke`） | 从正在服务的发布里去掉一个实验：生成一个序号更大的 manifest | 不叫 rollback（回滚只指尝试的回滚）、kill switch |
| 策略文件 / policy（`policy.jsonc`） | 谁评审动了流水线某一部分的实验。只用于评审：不进发布，不改变什么能编译、什么被服务 | 不叫 ACL、permission |
| 诊断 / diagnostic（`ConfigDiagnostic`） | 配置的一个问题：级别、稳定的代码（`COLUMN_NO_PRODUCER`）、所在的产物、JSON 指针、说明和一句修法 | 不叫 lint、violation。请求期的失败不是诊断，是结果里的错误（`RecErrorKind`） |

## 流水线、计划和运行

| 术语（文档 / 代码） | 在这里指什么 | 不要写成 / 区别于 |
| --- | --- | --- |
| 流水线 / pipeline | 一份配置（`pipelines/*.jsonc`）描述的一次推荐流程：节点、列、参数、输出 | 禁：workflow。不叫 flow、DAG：它是按 `order` 排的一条线，不是图 |
| 计划 / plan（`ExecutionPlan`） | 一条流水线连同它在这次发布里的所有实验，编译一次得到的不可变结果，所有请求共用。强调它包含所有臂组合时说“统一计划” | 区别于流水线（配置）；不叫 graph、program |
| 加载 / load | 把一次发布（工作区或 manifest）编译成计划并换上。加载期做完所有解析和校验；请求期不解析配置、不按名字查找 | 不叫 deploy、init、warm-up |
| 节点 / node | 流水线里的一步：一个算子、它的配置、`when`、`timeout_ms`、`on_failure` | 禁：stage。不叫 step、task（解释里的一步 `ExplainStep` 是另一回事） |
| 位置 / position（`TimelineSlot`） | 时间线上的一处：一个节点 id 和它的 `order`。涉及它的每个臂组合在这里有一个变体，或者没有节点 | 槽位（slot）单独说时指列的物理存储，不指这个 |
| 算子 / operator（`Name@N`） | 节点做的事的实现，由二进制提供，配置按 id 引用；`@N` 是配置契约的版本 | 禁：plugin。不叫 component、handler。配置键 `op` 是它的缩写，仅此一处 |
| 效果 / effect（`Effects`） | 算子声明的、它可以对候选表和请求列做的事：追加行、去掉行、改行序、写列、写请求列。没声明的，编译期和运行时都拦 | 不叫 permission。区别于能力（发布对二进制的要求） |
| 行源 / row source | 追加行的节点：召回、注入 | 不叫 generator、retriever。召回是行源最常见的用途，不是另一个概念；召回的“源”是一个服务，不是数据源 |
| 参数 / param（`param.x`） | 流水线声明、实验可以覆盖的值 | 不叫 setting、flag、config。config 只指节点的 `config` 和算子的配置类 |
| 输出、结果 / output、result（`output`、`RecResult`） | 输出是流水线交回什么：排在前面的 `limit_param` 个存活行的输出列，和请求级的输出值（`values`）。结果是引擎交给宿主的整个回答：项、输出值、错误、实验记录、trace | 不叫 response（response 只指服务的回答，`IServiceResponse`） |
| 宿主 / host | 嵌入引擎的服务里使用方自己的代码：传输、鉴权、线程、谁能用调试选项。它调用 `IRecEngine` | 不叫 server、app。调用方（caller）是相对的：对引擎是宿主，对被调用的流水线是发起调用的节点 |
| 引擎 / engine（`IRecEngine`） | 宿主拿到的入口：持有每条流水线当前的计划，取得请求的臂，运行，失败时改由后备流水线服务 | 不叫 runtime、server |
| 尝试 / attempt | 一个节点的一次执行；失败时候选表回到尝试之前 | 区别于运行和调用 |
| 运行 / run | 一条流水线对一个请求的一次执行：请求本身、它的后备流水线、被调用的流水线各是一次 | 不叫 execution、invocation |
| 调用 / call | 一次对外部服务的调用，或者一个算子对另一条流水线的一次调用 | 请求（request）单独说时只指宿主交给引擎的那一个；`PlanCompileRequest` 这样带前缀的是各自的东西 |
| 服务 / service（`IServiceClient`） | 流水线调用的外部依赖：模型、召回、任何别的服务，同一个契约 | 不再分 model client、candidate source |

## 候选表和值

| 术语（文档 / 代码） | 在这里指什么 | 不要写成 / 区别于 |
| --- | --- | --- |
| 候选表 / candidate table（`CandidateTable`） | 一次运行一张按列存的表，一个键至多一个存活行。被调用的流水线有自己的表，只通过调用的输入和结果往来 | 行（row）是表里的一行；item 指返回结果里的一项（`RankedItem`）、服务调用里的一项和使用方自己的实体名，不指表里的行 |
| 键 / key（`col.entity_id`） | 候选的 id，`int64` 或 `string`，由流水线的 `entity_key_type` 定 | 区别于实体的键（见“实体”：可以由几段组成）和 JSON 的键 |
| 存活行 / live row | 还没被去掉的行。过滤和截断只是把行移出存活的集合；压实（compaction）之后它们占的位置才回收。探针的 `scope` 用同一组词：`entered`、`survived`、`dropped` | 不叫 active、valid |
| 列 / column（`col.x`） | 候选表的一个逻辑列；每个写它的节点提交它的一个版本 | 字段（field）是文档里的成员：数据源的字段、请求输入、日志记录的字段，不指表的列；不叫 attribute |
| 流动列 / flow column（`"flow": true`） | 后面的节点可以再写的列，每次写是一个新版本（`score.final`）。别的列在一条路径上只有一个写者 | 不叫 mutable、shared |
| 版本 / version | 四个互不相干的用法，写的时候带上是谁的：列的版本（一个节点提交的那一份，`col.x@node` 读指定节点的）；算子和绑定类型的 `@N`（配置契约的版本）；持久化格式的版本（日期式，如 `2026-11-21.9`）；缓存的版本（`cache.version`，使用者起的名字） | 不叫 revision、generation |
| 请求列 / request column（`req.x`） | 节点在一次运行里算出的请求级的值；被调用的流水线看不到调用方的 | 区别于请求输入和参数 |
| 请求输入 / request input（`request_schema`、`ctx.request.x`） | 宿主随请求给的值，在任何东西运行之前按流水线声明的类型检查 | 不叫 argument、field；不是参数 |
| 上下文 / context（`ctx.x`） | 一个请求只有一份的值：请求输入（`ctx.request.x`）和上下文实体的特征（`ctx.user.x`） | 类名里的 Context（`OperatorContext`）是算子拿到的句柄，不是这个意思 |
| 类型、可空、宽度 / type、nullable、width | `bool`、`int32`、`int64`、`float32`、`float64`、`string`、`list<E>` 和定长的 `list<E,N>`；后面的 `?` 表示可以缺失；宽度指 32 位还是 64 位，向外交付时不变 | 向量是定长的浮点列表，不是另一种类型。不叫 optional |
| 缺失 / missing | 一个值不存在；是值的一部分，由表达式显式处理（`??`、`has`） | 不等于 0、空串或默认值；不叫 null（null 只是 JSON 里的写法） |
| 默认值 / default | 两处，意思不同。特征的 `default`：键合法地没有值时用它，特征因此不可空。列的 `default`：写进来的 NaN 和 ±∞ 换成它，它不替没被写过的行补值 | 不叫 fallback（后备另有所指） |
| 张量 / tensor | 交给服务的一个输入，或者它回答的一个输出：按声明的元素类型和宽度排好的一批值 | 只在服务调用的边界上用这个词；表里的是列 |

## 特征和数据源

| 术语（文档 / 代码） | 在这里指什么 | 不要写成 / 区别于 |
| --- | --- | --- |
| 特征 / feature（`col.item.x`、`ctx.user.x`） | 目录里声明的值：物理特征由数据源供给，衍生特征（`derived_features`）由表达式算出。候选的特征是列，上下文的特征是请求级的值 | 不叫 signal、attribute |
| 实体 / entity（`entities`） | 特征挂在它上面的东西：由键标识（请求输入、候选的 id、另一个特征，1 到 8 段），或者由服务从参数算出（`args`）。键只由请求级的值组成的是上下文实体，每个请求一个；否则是候选实体，每行一个 | 不叫 object、dimension、scope |
| 数据源 / source（`sources`） | 目录里的特征引用的一个逻辑名字；每个环境的绑定决定它怎么供给 | 不叫 store、table、backend。召回的源是服务 |
| 供给方式 / supply mode（`mode`） | 数据源怎么给出特征。`fetch`：每个请求向连接器取，绑定带 `cache` 时把连接器的回答留一段时间。`local`：读后台加载的本地表，请求路径上没有 I/O。`service`：每个请求调用一次服务，给带 `args` 的实体 | 不叫 strategy、backend type |
| 连接器 / connector（`IFeatureSourceConnector`） | 数据源读外部存储的实现，`fetch` 供给方式用它（带不带缓存都是）；`local` 读本地表，`service` 调用服务，都没有连接器 | 数据源是绑定里的一个名字；连接器是它背后的实现 |
| 本地表 / local table | `local` 方式下进程内的一张表，由更新来源（`bootstrap`，`IFeatureUpdateSource`）在后台加载和更新。计划要等它读的每张表第一次加载完才服务 | 不叫 cache（缓存有期限、会回源，本地表没有）、snapshot（快照是更新来源读的一种输入） |
| 缓存 / cache | 一份有期限（`ttl_ms`）的副本：`fetch` 数据源的，或者服务调用的。两层各自可选、至少一层：进程内（`memory`）和各实例共享的（`distributed`，`ICacheStore`）。`version` 钉住记录从哪来：写了它，配置变了记录也留着，改它才重新开始 | 不指本地表，也不指计划、编译结果这类算一次留着用的东西 |
| 拉取、预取 / fetch、prefetch | 拉取是按键向数据源取一批特征。预取是在读它的节点开始之前就发出的拉取；节点开始时等它，只补拉还缺的键。回填是把取到的值写进列 | 不叫 lookup、pull；加载（load）指的是发布和本地表 |

## 实验

| 术语（文档 / 代码） | 在这里指什么 | 不要写成 / 区别于 |
| --- | --- | --- |
| 实验、臂、层 / experiment、arm、layer | 臂是基线上的一个合并补丁；一层里一个请求至多在一个臂 | 禁：bucket、treatment。中文不写“桶”。variant 另有所指 |
| 基线 / baseline | 没有任何臂生效时的配置：流水线和目录本身。臂的补丁能改的是其中这几节：`params`、`param_schema`、`columns`、`request_columns`、`nodes`、`funnel_groups`、`log_taps`、`features`、`derived_features` | 不叫 control、default。对照臂是一个臂，通常是空补丁 |
| 臂组合 / arm combination | 可能在同一个请求上同时生效的一组臂，每层至多一个。校验和 `stratarec diff` 按组合进行；请求按它所在的组合选变体 | 不叫 cell、cohort |
| 变体 / variant | 一个节点或探针在某些臂的组合下的编译结果（统一计划里同一个位置的几种定义） | 不用来指臂 |
| 分配 / assignment（`IAssignmentProvider`） | 一个请求在哪些臂。由外部的实验平台回答：请求自己带来，或者引擎去问；引擎不决定谁进哪个臂 | 流量分配（比例、扩量）是平台的事，这里没有这个概念。内存也说“分配”（按行分配、0 分配），靠上下文区分 |
| 触发 / trigger | 请求走到了一个实验改动过的位置（实验的站点，site）；在那之前这个实验对请求没有任何作用。实验记录分开给出被分配的臂和被触发的实验 | 不叫 exposure、activation |
| 让出 / yield（`yields_to`） | 发布里声明一个实验让给另一个：请求同时在两者的臂里时，让出的那个不生效（它的层按基线运行），两者因此不会一起运行 | 不叫 priority、override、exclusion |
| 冲突、交互 / conflict、interaction | 冲突：能同时分配给一个请求的两个臂改了同一处，是错误（`PATCH_CONFLICT`）。交互：不同层的两个实验动到同一列，是警告；评审过的记在实验的 `reviewed_interactions` 里就不再重复 | 两个词不混用 |

## 时间和失败

| 术语（文档 / 代码） | 在这里指什么 | 不要写成 / 区别于 |
| --- | --- | --- |
| 截止 / deadline（`deadline_ms`） | 整个请求的时限，从请求开始算；臂的分配、流水线和后备流水线共用 | 不叫 SLA、TTL |
| 超时 / timeout（`timeout_ms`） | 一个节点的一次尝试，或者一次对服务、数据源的调用自己的时限；不会超过请求剩下的时间 | 不用来指请求的截止 |
| 预算 / budget（`AttemptBudget`） | 一次尝试实际能用的时间：节点的超时和请求剩下的时间（先扣掉留给后备的）里小的那个，算子和它发起的每一次外部调用共用 | “分配预算”说的是每个请求的内存分配量的估算，不是这个 |
| 上界、有界等待 / bound、bounded wait（`CallBound`、`BoundedCall`） | 一次等待最晚到什么时刻。到了就不再等，不依赖对方响应取消；之后才到的结果不采用 | 区别于超时：超时是时限有多长，上界是它到期的时刻 |
| 提交、回滚 / commit、rollback | 尝试成功时提交：它写的列的版本、追加的行、请求列从此对后面的节点可见。失败时回滚到尝试之前的检查点（`CandidateTable.Rollback`） | 不指发布的撤销，也不指数据库的事务 |
| 结局 / outcome（`NodeOutcomeKind`） | 一个位置上最后发生了什么，结果、指标、日志和录制里是同一组词：`succeeded`、`degraded`、`fallback`、`skipped_when`、`skipped_deadline`、`skipped_input`、`failed`、`aborted` | 不叫 status、state |
| 降级 / degraded | 节点用不完整或过期的输入提交了输出（一路召回失败、缓存给了过期的值）；请求照常进行 | 区别于后备（节点失败，另一个节点代替它）和跳过（节点没有运行） |
| 后备 / fallback | 节点失败时代替它运行的节点（`fallback`），或流水线失败时宿主改为服务的流水线（`fallback_pipeline`） | 不叫 backup、retry：引擎在一个请求里不重试 |

## 观测和排查

| 术语（文档 / 代码） | 在这里指什么 | 不要写成 / 区别于 |
| --- | --- | --- |
| 探针 / log tap（`log_taps`） | 流水线上一个记录行和列的位置，挂在节点或输出上 | 不叫 probe、hook、logger |
| 漏斗组 / funnel group | 几个探针共用的请求级采样决定：一个请求在组里的各个探针上要么都入样要么都不入样。入样之后每个探针还有自己的行过滤和行采样 | 不保证每个探针都有行 |
| 批次 / batch（`LogBatch`） | 一个探针在一个请求上记下的行和列，交给日志管道 | 拉取的分批（`max_batch`）是把键拆成几次连接器调用，是另一回事 |
| 交付 / delivery | 探针的批次丢不丢得起。`best_effort`：队列满了就丢。`durable`：内存里放不下时交给溢出的出口，关闭时排空；仍然丢了的会被计数 | 不叫 QoS、reliability |
| 录制、回放包 / recording、replay bundle | 一个请求的输入和它在各自的截止之前收到的外部回答的记录；回放用它在同一条执行路径上重跑。中止的请求的包不回放 | 不叫 snapshot、trace |
| trace、解释 / trace、explain | trace 是结果里每个位置的结局、耗时和进出的行数（`--trace`）。解释是一个候选在每个节点之后的状态，以及它被谁去掉（`--explain <id>`） | 不叫 debug log、profile |
| diff、compare | `stratarec diff` 比两份配置编译出的计划；`stratarec compare` 在录制的请求上比两份配置的结果 | 两个词不混用 |
| 调试选项 / debug options（`DebugOptions`） | 随请求带的排查开关，谁能用由宿主决定：强制臂、配置覆盖、trace、解释、录制。改变服务内容的两项（强制臂、配置覆盖）不写持久的日志 | 不叫 dry run、shadow |
| 种子、请求时间 / seed、request time（`time_ms`） | 种子是请求的确定性种子，默认是请求 id 的哈希；请求里的随机都由它经 `StableHash` 得出。请求时间是请求看到的“现在”，默认是到达的时刻，回放时设成录制的值 | 不用进程级的随机数 |
