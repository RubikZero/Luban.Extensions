# Luban.ScriptValidator

[English](README.md) | **简体中文** | [← 返回仓库说明](../README.zh-CN.md)

`Luban.ScriptValidator` 针对 Luban 已加载的全部配置表运行 Lua 校验规则。它是一个稳定的扩展 DLL；规则就是普通的 `.lua` 文件，改完下次导出即生效，无需重新编译 DLL。

## 安装

先用 `Directory.Build.props.example` 生成不受 git 跟踪的 `Directory.Build.props`，并把 `LubanDir` 指向你的 Luban 安装目录，详见[仓库说明](../README.zh-CN.md#初始化)。然后构建并部署全部扩展：

```powershell
dotnet build .\Luban.Extensions.sln -c Release -m:1 -p:DeployLubanExtensions=true
```

部署器会把 `Luban.ScriptValidator.dll` 与托管运行时 `MoonSharp.Interpreter.dll` 一并拷贝到 `LubanDir`，并把这两个程序集登记到 `Luban.deps.json`。

构建时还会引用 `LubanDir` 下的 `Scriban.dll`，因为 `Luban.TemplateExtensions` 里的引用辅助方法继承自 Scriban 的脚本对象。它只在编译期使用，不会被部署。

## 启用 Lua 校验

在 Luban 命令中加上后处理器名称与规则目录：

```bat
-x dataPostprocess=luaValidator ^
-x luaValidator.scriptDir=..\Rules\ConfigValidation
```

多个目录用 `;` 分隔。相对目录以 Luban 命令的当前工作目录为基准。所有 `*.lua` 文件会被递归加载，顺序按路径确定。必须存在一个数据目标（`-d bin`、`-d json` 等），这与普通导出本身的要求一致。

Lua 规则在 Luban 加载完全部表数据、且数据目标装配完成之后运行，但在 `OutputSaver` 写出文件之前。规则目录缺失、Lua 脚本非法、或任何 `fail` / `expect` 失败都会抛出导出错误，使 Luban 以非零退出码结束。Lua 校验失败**不依赖** `#lua` schema 标签或 `--validationFailAsError`。

它是数据后处理器（data postprocess），而 Luban **保存的就是后处理器交回去的那份清单**：本扩展把每个导出文件原样透传，所以开启校验不会改变任何产物内容。

规则失败同样是非破坏性的：运行会在到达数据保存器之前中止，上一次运行的数据文件**逐字节保持原样**——不删除、也不留半份。唯一的例外是代码目标：Luban 让它在独立任务里保存，不与数据阶段互相等待，因此失败的那次运行仍可能留下新写的代码、旁边却是旧的数据。修好规则重跑即可；若要求代码与数据同时更新，就先做一次校验：`-x outputSaver=null` 的那次运行什么都不写、但照样执行规则，只有它以 `0` 退出后再开始真正的生成。

还要注意：陈旧文件被清除**不是**开启校验导致的。只要某个目录**会被写入**，Luban 的 `local` 输出保存器每次运行都会清空它，有没有后处理器都一样；因此不要把手工维护的文件放进 `outputDataDir` 或 `outputCodeDir`。

## 规则 API

每个规则文件定义一个全局 `validate()` 函数：

```lua
function validate()
    for _, row in ipairs(cfg.table("TbLevel")) do
        expect(row.reward_id ~= nil,
            string.format("TbLevel id=%s (%s): reward_id is required", row.id, row.__source))
    end
end
```

可用的全局对象：

| API | 说明 |
| --- | --- |
| `cfg.table("full.table.name")` | 包含全部已加载行的类数组 Lua 表。 |
| `cfg.tables["full.table.name"]` | 等价的自接查找。 |
| `cfg.enumValue("<type>", "<text>")` | `<text>` 所命名或别名的枚举项的**数值**，找不到返回 `nil`。 |
| `cfg.enumName("<type>", "<text>")` | 该枚举项的**项名**，找不到返回 `nil`。 |
| `cfg.enumItem("<type>", "<text>")` | 该枚举项的 `{ name, value, alias, comment }`，找不到返回 `nil`。 |
| `cfg.enumItems("<type>")` | 该枚举全部项的数组，元素为 `{ name, value, alias, comment }`。 |
| `cfg.enums.<type>.<ITEM>` | 预构建的枚举常量表，例如 `cfg.enums.ELevelType.Elite == 2`。 |
| `cfg.ref(row, "<field>")` | 声明了 `#ref` 的字段所引用的行，或 `nil`。 |
| `cfg.ref("<table>", key...)` | 按索引命中的行，或 `nil`。单一主键表传一个值，联合主键表每个字段传一个值。 |
| `cfg.keyed("<table>"[, "<field>", ...])` | 「索引 → 行」的只读映射。联合索引按每个字段嵌套一层；索引有歧义或不存在时返回 `nil`。 |
| `cfg.singleton("<table>")` | `mode="one"` 单例表的那一行，或 `nil`。 |
| `cfg.tableInfo("<table>")` | `{ name, fullName, mode, index, indexFields, indexes, unionIndex, multiKey, keyType, count, loaded, keyed }`。 |
| `row.__source` | 该行的 Luban 来源文件位置。 |
| `row.__autoIndex` | 该行的 Luban 记录序号。 |
| `row.__type` | 具体 bean 类型，例如 `game.Level`。 |
| `fail(message)` | 记录一条校验错误并继续。 |
| `expect(condition, message)` | 条件为假时记录一条错误。 |

### 枚举与引用

Lua 没有原生枚举，而规则文件之间无法共享 Lua 模块——每个规则运行在各自独立的解释器里，且没有 `require`。因此扩展会**把每个枚举的常量表注入到每个脚本中**，规则可以直接使用枚举值，不必自己定义：

```lua
for _, row in ipairs(cfg.table("TbLevel")) do
    if cfg.enumValue("ELevelType", row.level_type) == cfg.enums.ELevelType.Elite then
        -- ...
    end
end
```

常量表同时以**项名和别名**为键，都指向数值；并且在各种类型名拼写下都可达——`cfg.enums.ELevelType.Elite`、`cfg.enums["game.ELevelType"].Elite` 与 `cfg.enums.ELevelType["精英"]` 解析到同一个数值，而且这三种写法返回的是同一个表。项名与别名冲突时，项名优先。常量表是只读的，未知类型名返回 `nil`。

需要的是项的元数据而不是数值时，用 `cfg.enumItems("<type>")`——例如检查每个枚举项是否都被用到：

```lua
for _, item in ipairs(cfg.enumItems("ELevelType")) do
    print(item.name, item.value, item.alias, item.comment)
end
```

枚举类型名带不带顶层模块都可以（`game.ELevelType` 与 `ELevelType`）。无法解析的输入永远不会报错：未知文本、未知类型名、或根本不是枚举的类型，一律返回 `nil`，因此规则可以直接据此断言：

```lua
expect(cfg.enumValue("ELevelType", row.level_type) ~= nil,
    string.format("%s: unknown level_type '%s'", row.__source, row.level_type))

if cfg.enumValue("ELevelType", row.level_type) == cfg.enumValue("ELevelType", "Elite") then
    -- ...
end
```

`cfg.ref` 有两种形式：

```lua
-- 跟随字段自身的 #ref 声明（标量与集合引用都支持）
local reward = cfg.ref(row, "reward_id")

-- 直接用表名 + 主键取行
local item = cfg.ref("TbItem", 10001)
```

- 第一种形式只对**确实声明了 `#ref`** 的字段生效，其他字段一律返回 `nil`。
- 集合引用返回的是「已解析成功的行」组成的数组，未命中的元素会被省略，所以要发现漏引用请比较长度。
- 键可以传数字也可以传字符串——这一点很重要，因为 `long` 字段在 Lua 里是字符串。
- 只有**当前导出目标内**的表才能解析，因为 Luban 只加载了这些数据。
- 返回的行与 `cfg.tables.X` 中的是**同一个对象**，因此规则可以用 `==` 直接比较行本身，而不必比较主键。

### 按索引取行

`cfg.tables.X` 是纯粹的**行数组**，因此 `cfg.tables.X[key]` 是按**位置**取值：key 超过行数时返回 `nil`；而如果某个整数主键恰好落在 `1..#rows` 区间内，它会静默返回**另一行**。要按索引取行请用 `cfg.keyed`，它对应 Luban 生成代码里的 `DataMap`、`GetByXxx` 与 `Get(k1, k2)`。

Luban 的表有四种形态，`cfg.keyed` 一一对应：

| 表形态 | 声明方式 | 取行写法 |
| --- | --- | --- |
| 单一主键 | `mode="map"`、`index="id"` | `cfg.keyed("TbItem")[id]` |
| 多个独立主键 | `mode="list"`、`index="id,name"` | `cfg.keyed("TbX", "id")[id]` 或 `cfg.keyed("TbX", "name")[name]`——每个键单独就能唯一确定一行 |
| 一个联合主键 | `mode="list"`、`index="kind+level"` | `cfg.keyed("TbX")[kind][level]`——每个字段一层嵌套，必须组合才唯一 |
| 单例表 | `mode="one"` | `cfg.singleton("TbCommon")` |

```lua
local levels = cfg.keyed("TbLevel")        -- 首次使用时构建，之后缓存
local items  = cfg.keyed("TbItem", "name") -- 多主键表里指定某一个索引
local grid   = cfg.keyed("TbGrid")         -- 唯一索引是联合主键，因此是嵌套结构
local common = cfg.singleton("TbCommon")

for _, row in ipairs(cfg.tables.TbMission) do
    expect(levels[tonumber(row.level_id)] ~= nil,
        string.format("%s: level_id %s does not exist", row.__source, row.level_id))
end

local cell = grid["alpha"][10]
```

以下几条始终成立：

- **不写字段名**的 `cfg.keyed("<table>")` 只在选择唯一时可用——这包括「只有一个联合索引」的情况。表声明了多个独立索引时它会返回 `nil`，并输出一条指明「请指定字段」的警告。
- 指定一个不属于联合索引的字段 → 扁平映射；把联合索引的全部字段都写出 → 与不写参数得到同样的嵌套映射。
- `cfg.ref("<table>", key...)` 是一次性写法：单一主键表传一个值，联合主键表每个字段传一个值（`cfg.ref("TbGrid", "alpha", 10)`）。对于有多个独立索引的表，它是歧义的，返回 `nil`；请改用 `cfg.keyed(table, "<field>")[key]`。
- 整数键用数字或字符串都可以，因为 `long` 字段到达 Lua 时是字符串：`levels[10001]` 与 `levels["10001"]` 是同一行。
- 所有取值返回的都是与 `cfg.tables.X` **相同的行对象**，因此可以用 `==` 直接比较行；嵌套映射的每一层都是只读的。
- 单例表没有键控视图；不在当前导出目标内的表根本无法建索引。

`cfg.tableInfo("<table>")` 用来查看一张表到底是什么，这是排查「为什么取不到值」最快的手段：

```lua
local info = cfg.tableInfo("TbGrid")
-- info.mode        -> "map" | "list" | "one"
-- info.index       -> "kind+level"
-- info.indexFields -> { "kind", "level" }
-- info.indexes     -> { { spec = "kind+level", fields = { "kind", "level" },
--                         keyTypes = { "string", "int" }, union = true } }
-- info.unionIndex  -> true    -- 一个索引横跨多个字段
-- info.multiKey    -> false   -- 多个互相独立的索引
-- info.keyType     -> "int"   -- 仅对单一主键表有意义
-- info.count       -> 128
-- info.loaded      -> true
-- info.keyed       -> true
```

`cfg.table("<名字>")` 与 `cfg.keyed` / `cfg.ref` / `cfg.tableInfo` 一样，短名与带模块的全名都能接受；而 `cfg.tables` 本身只以规范全名为键，因此枚举它不会出现重复的表。

### 字段名与取值

bean 字段是只读 Lua 表；list、array、set 是只读 Lua 数组；map 是只读 Lua 表。尝试赋值、或调用 `table.insert`、`table.remove`、`table.sort`，都会抛错。

以下四条几乎能解释所有「出乎意料」的情况：

- **字段名是 schema 里的原名**——就是 `__beans__.xlsx` 或 `<var name="...">` 中声明的名字，用 schema 采用的拼写（`level_type`、`reward_id` 或 `ItemId`）。它们**不是**生成 C# 代码里的字段名。读取不存在的字段会得到 `nil` 而不是报错，所以拼错与「值缺失」看起来一模一样。
- **遍历单个 bean 行必须用 `pairs`，不能用 `ipairs`。** bean 行只有字符串键，而 `ipairs` 走的是整数键，于是 `ipairs(row)` 会静默地**一次都不执行**。行列表本身（`cfg.table("TbLevel")`）是真正的数组，用 `ipairs` 是正确的。
- **枚举字段持有的是表里写的那段文本**，可能是项名也可能是别名。格子里写 `精英` 时，Lua 侧读到的就是字符串 `"精英"`，即使项名是 `Elite`。要可靠比较，请先过一遍 `cfg.enumValue` / `cfg.enumName`。
- **`long` 与 `datetime` 是字符串**，以免 Lua 基于 double 的数字丢精度。请用字符串而不是数字去比较。

另外，`pairs(row)` 还会给出 `_type`、`__source`、`__autoIndex` 这几个元数据键；写「遍历所有字段」这类规则时需要排除它们。

解释器运行在 MoonSharp 的软沙箱中。规则只能拿到 `cfg`、`fail`、`expect` 这几个 API。文件、进程与包加载类 API（`io`、`debug`、`require`、`dofile`、`loadfile`、`load`、`loadstring`）都不存在；`os` 只暴露 `clock` / `date` / `difftime` / `time`；可能绕过只读代理的可变助手（`rawget`、`rawset`、`getmetatable`、`setmetatable`、`table.insert`、`table.remove`、`table.sort`）已被移除。MoonSharp 的 CLR 互操作模块同样被移除，因此 `dynamic` 为 `nil`。

## 测试

`tests/SmokeRules/` 里是一条只检查 API 可达的规则。

`tests/Fixture/` 是一个自包含的 Luban 工程——XML schema + CSV 数据，不需要 Excel、也不需要任何游戏数据——每种表形态各一张，另有一张带 `path` 标签字段的表（让路径校验器参与同一次运行），配一条覆盖全部取值写法的规则：

```powershell
dotnet <LubanDir>\Luban.dll -t all -d bin `
  --conf Luban.ScriptValidator/tests/Fixture/luban.conf `
  -x "outputDataDir=<一个空目录>" `
  -x dataPostprocess=luaValidator `
  -x luaValidator.scriptDir=Luban.ScriptValidator/tests/Fixture/rules `
  -x "pathValidator.rootDirs=<一个不可能含资源的目录>;Luban.ScriptValidator/tests/Fixture"
```

它会打印结果、往该目录写出「每张表一个」的数据文件，并以 `0` 退出。日志里的警告是刻意构造的「这张表不能这样取索引」场景，每条都写明了原因。

"写到真实目录"是这项检查的一部分而不是点缀：Luban 保存的就是后处理器交回的清单，所以一个只做校验的后处理器会让输出目录空着、却依然以 `0` 退出。

由于 Luban 会把校验失败记成日志而不让进程失败，fixture 的判据是它的**输出与产物**而不是退出码：上面这次运行只有在路径校验器于**第二个**根目录下找到 `assets/sword.txt` 时才算通过；[共用 action](../.github/actions/build-against-luban/action.yml) 还会把第二个根目录去掉再跑一次，确认校验器此时会报出该字段。

## 许可

本扩展与仓库其余部分一样采用 MIT 许可——见[仓库许可](../LICENSE)。
