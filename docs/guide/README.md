# 使用指南

给用 StrataRec 写配置和把引擎嵌进服务的人。这里不重复能从工具里问出来的东西：字段的含义在 JSON Schema 里（编辑器里悬停就能看到），算子的清单和配置由二进制自己列出，示例是 CI 每次都验证和运行的工作区。

## 从哪里开始

1. [`README.md`](../../README.md) 的“快速开始”：`stratarec init` 写出一个能校验、能运行的最小工作区，每个配置文件开头的注释说明先改哪里。
2. `samples/home_feed`：一个完整的工作区：目录、绑定、一条从召回到打散的流水线（召回、粗排、模型、融合、打散、探针）、实验和发布文件。
3. `samples/StrataRec.Samples.Host`：把引擎嵌进 ASP.NET Core 服务，自己写算子、服务客户端和特征连接器（`Components/` 下每个文件一个扩展点），一个页面由几条流水线组成。它的 `README.md` 逐段说明。

## 想知道一件事时问谁

| 想知道 | 问 |
| --- | --- |
| 有哪些算子，各有什么效果 | `stratarec operators` |
| 一个节点实际读写哪些列 | `stratarec explain-plan -c <工作区> --arms <实验>/<臂> --format json`：这个臂组合下每个节点的 `reads`、`writes`；没有实验的流水线不用 `--arms` |
| 一个算子的配置怎么写 | `stratarec schema operator TopK@1`，或者在编辑器里看补全（工作区的 `schemas/`） |
| 一种文件有哪些字段 | `stratarec schema pipeline`（`catalog`、`bindings`、`experiment`、`release`、`request` 同理） |
| 配置对不对 | `stratarec validate -c <工作区>`：类型、数据流、实验冲突、所有可能同时生效的臂组合；每个错误带位置和修法 |
| 编译出来的计划是什么样 | `stratarec explain-plan -c <工作区>`：节点的时间线、各臂的变体、参数 |
| 一个请求实际怎么跑的 | `stratarec run -c <工作区> -r <请求> --trace`；某一项为什么没返回：`--explain <id>` |
| 线上的一个请求为什么是这个结果 | 录制它（`--record`，或宿主的录制选项），再 `stratarec replay` |
| 改了配置，结果会怎么变 | `stratarec diff`（比较两份配置：默认是基线和每个臂单独生效时，`--arms` 指定一个组合），`stratarec compare`（在录制的请求上比较两份配置的结果） |
| 表达式怎么写 | [`expressions.md`](expressions.md) |
| 常见排序需求用哪些算子组合 | [`README.md`](../../README.md) 的“常见需求的写法” |
| 一个词在这里指什么 | [`../glossary.md`](../glossary.md) |

每个命令的选项：`stratarec <命令> --help`。

## 写自己的扩展

扩展点的清单在 [`../architecture.md`](../architecture.md) 的“扩展点”一节；每个契约的规则写在它的文档注释里。自定义算子、服务客户端和特征连接器在宿主示例的 `Components/` 里各有一个最小的实现；其余契约看内置的实现（例如 `RedisCacheStore`、`MemoryFeatureUpdateSource`、`JsonLinesLogSink`）。写完之后，宿主自己的二进制带着这些组件运行全部 `stratarec` 命令（示例的 `README.md` 里有写法），所以自定义算子的配置同样有 Schema、校验和解释。
