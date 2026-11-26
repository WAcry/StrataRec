# 表达式语言

打分公式、过滤条件、节点的 `when`、衍生特征和探针记录的列都用同一种表达式。它用 CEL 的语法，所以熟悉 CEL 或任何主流语言的人可以直接读写；它的语义是自己的：一次算一整列，运行期不抛错，缺失值要在表达式里写明怎么处理。

这一页的每个 `colexpr` 代码块都由测试执行（`ExpressionGuideTests`）：带 `// =>` 的行求出的值必须相同，带 `// error` 的行必须编译失败，其余的必须能编译。函数表也由它核对，新增函数而不写进这一页会让测试失败。

## 名字

| 写法 | 是什么 |
| --- | --- |
| `ctx.request.<名字>` | 请求输入（`request_schema` 里声明）和内置字段，例如 `ctx.request.seed` |
| `ctx.<实体>.<特征>` | 上下文实体的特征，每个请求一个值，例如 `ctx.user.tenure_days` |
| `param.<名字>` | 这个请求生效的参数，实验的臂可以覆盖 |
| `col.entity_id` | 候选的键 |
| `col.<实体>.<特征>` | 候选的特征，每行一个值，例如 `col.item.age_hours` |
| `col.<列>` | 节点写的列，例如 `col.score.final`；`col.score.final@fusion` 读节点 `fusion` 写的那个版本 |
| `req.<名字>` | 节点算出的请求列，每个请求一个值 |

名字写错时，编译错误给出位置；有相近的名字就给出它，没有就说明这一类名字在哪里声明。

## 一个例子

```colexpr
let utility    = param.w_click * col.score.p_click + param.w_like * col.score.p_like;
let age_hours  = col.item.age_hours ?? 720.0;
let freshness  = exp(-age_hours / param.half_life_hours);
let cold_boost = (col.creator.follower_count < 1000.0 && ctx.user.tenure_days > 30.0) ? param.cold_creator_boost : 1.0;
utility * (1.0 + param.w_fresh * freshness) * cold_boost
```

```colexpr
!(col.entity_id in ctx.request.recent_item_ids) && (col.item.is_policy_safe ?? false)
```

`let 名字 = 表达式;` 给后面的表达式起一个局部的名字。函数可以写成 `startsWith(s, 'a')`，也可以写成 `s.startsWith('a')`。

## 缺失值

类型后面的 `?` 表示可以缺失（`float64?`）。没有 `null`：缺失不是一个可以写出来的值，只能被处理。

- 有一个操作数可能缺失，算术、比较和函数的结果就可能缺失。
- `x ?? y`：`x` 缺失时取 `y`。`has(x)`：`x` 有没有值，结果是 `bool`。
- `&&` 和 `||` 按三值逻辑：一边已经决定结果时，另一边缺失也不影响。
- 过滤条件、`when`、模型输入和写进不可空列的值必须不可空。可空时编译报错，并指出在哪里加 `??`。最常见的两种写法：特征在目录里声明 `default`，或者在用到的地方写 `??`。
- 写进可空列的值可以缺失，这一行就是缺失。排序（`TopK@1`）把缺失的行排在最后，升序降序都是；不要为了能排序把缺失补成 0，那会把它排到负分的前面。

```colexpr
(param.missing_weight ?? 0.5) * 2          // => 1.0
has(param.missing_weight)                  // => false
has(param.missing_weight + 1.0)            // => false
false && (param.missing_weight > 0.0)      // => false
true || (param.missing_weight > 0.0)       // => true
has(true && (param.missing_weight > 0.0))  // => false
```

## 没有运行期错误

一行算不出来不会让整列失败，也不会悄悄变成 0：它要么按浮点数的规则得到一个值，要么缺失。

```colexpr
7 / 2                                  // => 3.5
1 + 1.5                                // => 2.5
1 / 0 > 1e308                          // => true
9223372036854775807 + 1 < 0            // => true
9007199254740993 == 9007199254740992.0 // => true
9007199254740993 == 9007199254740992   // => false
(7 % param.zero) ?? -1                 // => -1
int(2.9) ?? 0                          // => 2
int(-2.9) ?? 0                         // => -2
has(int(1e300))                        // => false
7 % 0                                  // error
```

- `/` 的结果总是 `float64`，整数相除也是（CEL 里 `7 / 2` 得 3）。除以 0 得到 ±∞ 或 NaN。
- `int64` 和 `float64` 混用时整数提升为 `float64`，比较也是：超过 2^53 的整数和浮点数比较之前先舍入。两个 `int64` 之间的比较是精确的，所以 id 和 id 比。`int64` 的 `+`、`-`、`*` 溢出时回绕。
- `%` 只接受整数。除数为 0 的行结果缺失，所以除数不是非零常量时结果可空；写出来就是 0 的除数是编译错误。
- `int(x)` 向零截断；`x` 是 NaN、±∞ 或超出 `int64` 时缺失，所以浮点数的 `int()` 总是可空。`double(x)` 把整数转成 `float64`。
- 写进列的 NaN 和 ±∞：列声明了 `default` 就换成默认值，否则这一行缺失。所以 `columns` 里声明的浮点列要么可空，要么有 `default`，两样都没有时加载报 `COLUMN_NEEDS_DEFAULT`；打分列通常声明 `"default": 0.0`。

## 字符串和列表

```colexpr
size('héllo')                              // => 5
'abc'.contains('b')                        // => true
startsWith('abc', 'ab') && 'abc'.endsWith('bc')   // => true
'cn' in ['cn', 'jp']                       // => true
size([1, 2, 3])                            // => 3
2 in [1, 2, 3]                             // => true
overlap_count([1, 2, 3], [2, 3, 4])        // => 2
overlap_count([3, 3], [3])                 // => 2
overlap_count([3], [3, 3])                 // => 1
'a' < 'b'                                  // error
dot([1.0, 2.0], [3.0, 4.0])                // => 11.0
l2_norm([3.0, 4.0])                        // => 5.0
```

字符串只比较相等（`==`、`!=`、`in`），没有大小；`size` 数的是码点。向量就是浮点数的列表（`list<float32>`、`list<float32,64>` 这样的定长列表）：`dot`、`cosine`、`l2_norm` 按声明的元素类型计算，两边长度不同的行结果缺失，声明的长度不同是编译错误。

## 函数

函数是固定的一组，都没有状态、不做 I/O。使用方自己的逻辑写成算子，不往表达式里加函数。

| 函数 | 作用 |
| --- | --- |
| `min(a, b, ...)`、`max(a, b, ...)` | 最小、最大 |
| `clamp(x, lo, hi)` | 把 `x` 限制在 `lo` 和 `hi` 之间 |
| `abs(x)`、`sqrt(x)`、`pow(x, y)`、`exp(x)`、`log(x)`、`log1p(x)` | 数学函数；定义域之外按浮点数的规则得 NaN 或 ±∞ |
| `int(x)`、`double(x)` | 数值转换，见上 |
| `sigmoid(x)`、`logit(p)` | 1 / (1 + e^(-x)) 和它的反函数 |
| `soft_floor(x, floor[, k])` | 平滑的下限：`x` 远大于 `floor` 时约等于 `x`，远小于时约等于 `floor`；`k` 是正数，越大越接近硬下限 |
| `linear_decay(x, start, end)` | `start` 之前是 1，`end` 之后是 0，中间线性下降；`start` 小于 `end` |
| `piecewise_linear(x, [x0, x1, ...], [y0, y1, ...])` | 按折点线性插值，两端之外取端点的值 |
| `dot(a, b)`、`cosine(a, b)`、`l2_norm(v)` | 浮点列表的点积、余弦、范数 |
| `size(list_or_text)` | 列表的元素数，字符串的码点数 |
| `contains(list, x) or contains(text, s)` | 列表里有没有这个值（同 `x in list`），字符串里有没有这个子串 |
| `overlap_count(list1, list2)` | 第一个列表里有多少个元素出现在第二个列表里；第一个列表里重复的值各算一次，所以两个参数不能对调 |
| `startsWith(s, prefix)`、`endsWith(s, suffix)` | 前缀、后缀 |
| `hash01(a, b, ...)` | 由参数决定的 [0, 1) 内的均匀值；`hash01(ctx.request.seed, col.entity_id)` 是可以回放的随机 |
| `lookup('table', key, default)` | 读工作区里的一张静态表（`stratarec schema table` 给出文件格式），没有这个键时取 `default` |
| `has(x)` | `x` 有没有值 |

```colexpr
sigmoid(0.0)                               // => 0.5
clamp(5, 0, 3)                             // => 3
linear_decay(2, 1, 3)                      // => 0.5
piecewise_linear(2, [0, 4], [0, 1])        // => 0.5
soft_floor(1000.0, 0.0)                    // => 1000.0
hash01(ctx.request.seed, 5) < 1.0          // => true
```

## 和 CEL 不同的地方

按 CEL 写、CEL 的类型检查通过并且能求值的表达式，结果不同的只有整数相除。CEL 在运行期报错的情况（溢出、对 0 取模、越界的转换）在这里得到值或缺失；CEL 拒绝的整数和浮点混用在这里可以写。其余的差异都是编译错误，写错立刻知道：

```colexpr
[1, 2, 3][0]                               // error
{'a': 1}                                   // error
[1, 2, 3].all(x, x > 0)                    // error
'abc'.matches('a.*')                       // error
string(1)                                  // error
1 + true                                   // error
null                                       // error
```

没有下标、map、宏（`all`、`exists`、`map`、`filter`）、正则和到字符串的转换：宏是一行之内的循环，正则逐行匹配太贵，都不能按列计算。布尔值不参与算术，写成 `cond ? 1.0 : 0.0`。拼提示词这类文本处理不属于表达式，由算子用 Mustache 模板做。
