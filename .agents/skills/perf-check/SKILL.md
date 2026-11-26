---
name: perf-check
description: 改动涉及按行、按特征的路径（候选表、列缓冲、表达式后端、特征拉取和回填、算子的逐行循环、日志捕获）或者声称改善了性能、分配时使用：怎么测量，怎么判断值不值。
---

# perf-check：先测量，再决定值不值

## 1. 什么值得优化

只有随候选数或特征数增长的开销。请求级的固定开销（几个对象、一次字典查找）交给 GC，不为它加池、加约定。判断一项优化：

- 它随候选或特征增长吗？不增长，通常不做。
- 能集中在一处、被测试守住吗？散在各处的优化会被下一次改动悄悄破坏。
- 使用者要为它知道什么吗？要使用者改写法才快的优化不做。
- 更简单的写法差多少？差得很小就用简单的。

## 2. 分配：有测试守着

分配测试断言的是性质（不随候选数增长、同步路径零分配），不是某个字节数：

```sh
dotnet test --project tests/StrataRec.IntegrationTests -c Release --filter-class StrataRec.IntegrationTests.RequestAllocationTests
dotnet test --project tests/StrataRec.UnitTests -c Release --filter-class "*AllocationTests"
```

新增一条按行的路径时，给它加同样性质的测试：让一个用到这条路径的配置在 250 和 4,000 个候选下各跑一次，比较分配。测量照 `RequestAllocationTests` 的做法（先预热，读当前线程的分配字节数，允许几 KB 的抖动），测试类放进 `AllocationMeasurement` 这个串行的集合里，免得别的测试的分配混进来；现有的示例配置不会自动覆盖新的分支。每请求的字节数是估算，看趋势，不设硬门槛。

## 3. 耗时：改动前后各跑一次基准

```sh
dotnet run -c Release --project tests/StrataRec.Benchmarks -- --filter '*PipelineBenchmarks*'    # 端到端，500 和 5,000 个候选
dotnet run -c Release --project tests/StrataRec.Benchmarks -- --filter '*ExpressionBenchmarks*'  # 表达式后端
dotnet run -c Release --project tests/StrataRec.Benchmarks -- --list flat                        # 全部基准
```

- 基线在另一个检出里跑（`git worktree add <临时目录> <基线提交>`），同一台机器、前后各一次，比较均值和每次操作的分配；差异在误差范围内就是没有差异。
- 共享的 CI 机器太吵，CI 只验证基准还能构建和运行，不设回归阈值。尾延迟、GC 行为取决于部署环境，不在仓库里测。
- 没有覆盖这条路径的基准时，加一个比猜测可靠；基准用 `tests/StrataRec.Benchmarks/HomeFeedBenchWorkspace.cs` 的工作区。

## 4. 记下结论

性能上的取舍（为什么多了这些复杂度，或者为什么没做某个优化）记下核心结论和一两个关键数字：以后有人想“简化”或“优化”它时，这是唯一的依据。只影响一个函数或文件的，写成代码旁的一句注释；跨模块的记进 `docs/decisions/<领域>.md`。测量可以做得很多，记录不必：不记全部测量和每个试过的方案的数字，为一次比较写的重型对照工具不留在仓库里，结论不必都能从仓库复现。提交说明里写前后的数字。
