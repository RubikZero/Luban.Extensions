# Lua 校验规则编写指南

## 1. 两种规则

| | 导出级规则 | 列级规则 |
| --- | --- | --- |
| 触发方式 | 每次导出执行一次 | 每条记录的每个被打标签的字段各执行一次 |
| 文件里要写什么 | 一个全局函数 `validate()` | 调用 `register{...}` 注册具名规则 |
| 怎么开启 | `-x dataPostprocess=luaValidator` + `-x luaValidator.scriptDir=<目录>` | 字段类型上加 `#lua=<规则名>`；`-x luaValidator.columnDir=<目录>`（缺省取 `<scriptDir>/columns`） |
| 适合做什么 | 整库检查、跨表一致性、统计 | 单字段/单列检查、依赖本行其它字段的检查 |

两种可以同时使用，互不影响。

**目录参数**：多个目录用 `;` 分隔；相对路径按执行 Luban 的当前目录解析；目录下所有 `*.lua` 会递归加载，顺序按路径排序。

---

## 2. 导出级规则

一个文件一个 `validate()`：

```lua
-- Rules/ConfigValidation/level.lua
function validate()
    for _, row in ipairs(cfg.table("TbLevel")) do
        expect(row.reward_id ~= nil,
            string.format("TbLevel id=%s: reward_id 不能为空", tostring(row.id)))
    end
end
```

- 每个文件在**独立解释器**中执行，文件之间不共享全局变量，也没有 `require`。
- 规则在数据装载完成之后、导出写盘之前执行。
- `validate()` 的返回值没有意义；发现问题就调用 `fail` / `expect`。
- 一个文件里除 `validate()` 之外还可以定义自己的局部函数/常量，但不能依赖其它规则文件。

命令行：

```bat
-x dataPostprocess=luaValidator ^
-x luaValidator.scriptDir=..\Rules\ConfigValidation
```

---

## 3. 列级规则

### 3.1 注册

两种写法等价，`type` 可以省略（等于 `any`）：

```lua
-- 表写法
register{
    name     = "positiveWeight",   -- 规则名（唯一），字段上用 #lua=名字 引用
    type     = "int",              -- 这条规则接受的输入类型，编译期会校验
    validate = function(value, row, owner)
        expect(value > 0, "weight 必须为正数")
    end,
}

-- 位置参数写法
register("nonEmptyIcon", "string", function(value, row, owner)
    expect(value ~= "", "icon 不能为空")
end)
```

列级脚本在 **schema 编译期**加载，此时数据还没读，所以**文件顶层只能做注册**：`cfg`、`row`、`owner`、`fail`、`expect` 都只在规则执行时存在。

规则名必须唯一；重名、脚本语法错误、`type` 与字段不匹配，都会让导出直接失败（不是警告）。

### 3.2 挂到字段上

在字段的类型串后面加 `#lua=<规则名>`，可以和其它标签并列：

```xml
<bean name="Level">
    <var name="id"     type="int"/>
    <var name="icon"   type="string#path=normal;assets/*.txt#lua=nonEmptyIcon"/>
    <var name="weight" type="int#lua=positiveWeight"/>
</bean>
```

**容器上的位置决定触发粒度**（两者可同时写）：

```xml
<!-- 标签写在元素类型之后：每个元素触发一次，value 是单个元素 -->
<var name="weights" type="(list#sep=,),int#lua=weightRule"/>

<!-- 标签写在容器括号内：每条记录触发一次，value 是整张集合（只读表） -->
<var name="weights" type="(list#sep=,#lua=wholeRule),int"/>
```

写成 `(list,int)#lua=x`（标签在括号外）是无效写法，Luban 会判为非法类型。

### 3.3 规则签名

```lua
validate = function(value, row, owner) ... end
```

| 参数 | 是什么 |
| --- | --- |
| `value` | 被打标签的那个值：字段本身、集合的一个元素，或整个集合（见上面的位置规则） |
| `row` | 该字段所在的那条记录，只读表 |
| `owner` | 包含该字段的那个 bean。字段属于记录本身时 `owner == row`；字段在某个嵌套 bean 里时，`owner` 就是那个 bean（可以读它的兄弟字段） |

### 3.4 `type` 允许写什么

- `any`（或不写）：不检查
- 基本类型名：`int`、`long`、`float`、`double`、`bool`、`string`、`datetime`、`list`、`map` 等
- 笼统写法：`bean`、`enum`、`collection`
- schema 里的原名：如 `UnitInfo`、`UnitType`

当标签写在**元素的**位置时，收到的是元素，所以这里要写元素类型（`int`、`UnitInfo`）。写错会报错并告诉你实际会收到什么值。

### 3.5 一个注意点：空值不会触发列级规则

Luban 在遍历字段时会**跳过值为 `null` 的字段**，所以可空列（`int?`、`string?`）留空时，列级规则不会被调用。
需要检查"有没有填"时，请用导出级规则，或在列标题上用 `#default=...` 让留空的单元格先取到值。

---

## 4. DLL 暴露给 Lua 的全部方法

规则里可以直接用下面这些全局对象与方法。**除此之外没有任何东西**（见第 6 节沙箱）。

### 4.1 表数据

| 方法 | 说明 |
| --- | --- |
| `cfg.tables["完整表名"]` | 该表全部记录（只读数组），键是带模块的完整表名，如 `game.TbLevel` |
| `cfg.table("表名")` | 同上，接受短名或完整名；表不存在返回 `nil` |
| `cfg.keyed("表名" [, "字段", ...])` | **索引 → 行** 的只读映射。单键表直接用 `keyed[10001]`；联合索引逐层嵌套；表有多个独立索引时必须指定字段名，否则返回 `nil` 并打一条警告 |
| `cfg.singleton("表名")` | `mode="one"` 单例表的那一行；不是单例则返回 `nil` 并警告 |
| `cfg.tableInfo("表名")` | 表的元信息（见下）；表不存在返回 `nil` |
| `cfg.ref("表名", key...)` | 按索引取行，等价于 `cfg.keyed(...)` 的便捷写法 |
| `cfg.ref(row, "字段名")` | 跟随该字段声明的 `#ref` 取被引用的行；没声明或找不到返回 `nil` |

`cfg.tableInfo` 返回：

```
name, fullName, mode, index, indexFields, keyType, count, loaded, keyed,
indexes = { { spec, fields, keyTypes, union }, ... },
unionIndex, multiKey
```

`keyed` 的键同时支持字符串和数字写法（`keyed[10001]` 与 `keyed["10001"]` 等价）；表不存在、索引缺失或有歧义时返回 `nil`。

### 4.2 枚举

| 方法 | 说明 |
| --- | --- |
| `cfg.enums.<枚举类型>.<项名>` | 预置常量表，直接得到数值，如 `cfg.enums.ELevelType.Elite`；别名同样可用 |
| `cfg.enumValue("<类型>", "<文本>")` | 文本对应的数值，找不到返回 `nil` |
| `cfg.enumName("<类型>", "<文本>")` | 文本对应的**项名**，找不到返回 `nil` |
| `cfg.enumItem("<类型>", "<文本>")` | `{ name, value, alias, comment }`，找不到返回 `nil` |
| `cfg.enumItems("<类型>")` | 全部项的数组，元素同上；类型不存在返回 `nil` |

`<文本>` 三种写法都接受：**项名**、**别名**、**数值**（表格里写 `2` 和写 `Monster` 等价）。枚举类型名带不带模块前缀都可以。
`cfg.enums` 同时提供项名与别名两种键，两者冲突时项名优先。

### 4.3 报告错误

| 方法 | 说明 |
| --- | --- |
| `fail(message)` | 记录一条校验失败，继续执行 |
| `expect(condition, message)` | 条件为假时记录一条校验失败，继续执行 |

失败会被汇总成 ERROR 日志（带记录位置和来源文件），并在结束时决定退出码（见第 7 节）。
**不要用 `print` 报告问题**：`print` 只是普通输出，不会让校验失败。

### 4.4 行对象

每条记录是一个只读表，字段名就是 schema 里的字段名。另有几个元数据键：

| 键 | 说明 |
| --- | --- |
| `row.__source` | 该行的来源位置（文件/sheet） |
| `row.__autoIndex` | 记录在表中的序号（从 0 开始） |
| `row.__type` | 完整类型名（带顶层模块） |
| `row._type` | 类型名 |

用 `pairs(row)` 遍历字段时记得排除以上 4 个键（它们以 `_`/`__` 开头）。

### 4.5 只读

所有 `cfg.*` 与行对象都是**只读**的：给它们赋值会抛出 `attempt to modify read-only configuration data`。
规则不能修改配置数据——校验只负责报告。

---

## 5. 值在 Lua 里长什么样

| 字段类型 | Lua 里的形式 |
| --- | --- |
| `bool` | `boolean` |
| `byte` / `short` / `int` | `number` |
| `long` | **`string`**（避免精度丢失，如 `"1234567890123"`） |
| `float` / `double` | `number` |
| `string` | `string` |
| 枚举 | `string`：表格里写的那个文本（项名/别名/数字），需要规范化时用 `cfg.enumName` |
| `datetime` | `string`（格式化后的时间） |
| bean | 只读表，字段同名 |
| `list` / `array` / `set` | 只读数组，下标从 1 开始 |
| `map` | 只读表，键是转换后的值 |

规则看到的都是 Luban **装载后**的值：如果在列标题上写了 `#default=...`，留空的单元格在读入时就已经是默认值。

---

## 6. 沙箱：能用什么、不能用什么

规则运行在 MoonSharp 的软沙箱中，并且额外移除了下列内容：

- 文件、进程、包加载：`io`、`debug`、`require`、`dofile`、`loadfile`、`load`、`loadstring` 都不存在
- `os` 只有 `clock`、`date`、`difftime`、`time`
- 可能绕过只读代理的可变助手被移除：`rawget`、`rawset`、`getmetatable`、`setmetatable`、`table.insert`、`table.remove`、`table.sort`
- CLR 互操作模块被移除：`dynamic` 为 `nil`

可用的常见内容：`print`、`string.*`（除已列出的）、`math.*`、`table.concat`、`table.unpack`、`pairs`、`ipairs`、`#`、`select`、`tostring`、`tonumber`、`type`、`pcall`、`error`。

---

## 7. 失败、日志与退出码

一条失败长这样：

```
ERROR|[lua] test.TbAsset[1].icon (.../data/asset.csv): units.lua: icon 不能为空
```

- 前半段是记录位置与来源文件，后半段是规则文件与消息。
- 校验失败**不会**让 Luban 立即中断：产物照常写出。
- 退出码由 Luban 的严格模式开关决定：
  - 4.x：`--validationFailAsError`
  - 5.x：`--strict`
  不加开关时只报错、退出码仍为 0。

**配置类错误会直接中断**（退出码 1），常见的有：

| 现象 | 原因 |
| --- | --- |
| `没有列规则注册这个名字` | 字段写了 `#lua=名字`，但没有脚本注册该名字 |
| `声明输入类型 ... 但实际收到的值类型是 ...` | 注册的 `type` 与该字段实际值不匹配 |
| `无法解析: ... column rule script failed` | 列级脚本语法错误，或在脚本顶层用了 `cfg` |
| `Lua validation requires -x luaValidator.scriptDir=<directory>` | 忘了给规则目录 |

---

## 8. 常见写法

### 8.1 必填与范围（导出级）

```lua
function validate()
    for _, row in ipairs(cfg.table("TbLevel")) do
        expect(row.reward_id ~= nil, "reward_id 不能为空: " .. tostring(row.__source))
        expect(row.weight > 0 and row.weight <= 100,
            string.format("weight 超出范围: %s (id=%s)", tostring(row.weight), tostring(row.id)))
    end
end
```

### 8.2 跨表引用检查（导出级）

```lua
function validate()
    local items = cfg.keyed("TbItem")
    for _, row in ipairs(cfg.table("TbLevel")) do
        if row.reward_id ~= nil and items[row.reward_id] == nil then
            fail(string.format("TbLevel id=%s 引用了不存在的道具 %s",
                tostring(row.id), tostring(row.reward_id)))
        end
    end
end
```

### 8.3 枚举比较

```lua
function validate()
    for _, row in ipairs(cfg.table("TbLevel")) do
        -- 表格里可能写项名、别名或数字，先规范化
        local levelType = cfg.enumName("ELevelType", row.level_type)
        expect(levelType ~= nil, "未知的关卡类型: " .. tostring(row.level_type))
        if levelType == "Elite" then
            expect(row.icon ~= nil, "精英关必须有 icon")
        end
    end
end
```

### 8.4 列级：单字段

```lua
register("positiveWeight", "int", function(value, row, owner)
    expect(value > 0, "weight 必须为正数")
end)
```

### 8.5 列级：依赖本行其它字段（用 `owner`）

```lua
-- 挂在某个 bean 内部字段上时，owner 就是这个 bean
register("unitRef", "int", function(value, row, owner)
    if owner == nil then return end
    local kind = cfg.enumName("UnitType", owner.unit_type)
    local tableName = ({ Item = "TBItem", Equip = "TbEquipBase" })[kind]
    if tableName == nil then return end

    local keyed = cfg.keyed(tableName)
    expect(keyed ~= nil and keyed[value] ~= nil,
        string.format("%s 中不存在 id=%s", tableName, tostring(value)))
end)
```

### 8.6 列级：容器逐元素 vs 整表

```lua
-- 挂在元素上：每个元素调用一次
register("weightElement", "int", function(value, row, owner)
    expect(value > 0, "元素必须为正数")
end)

-- 挂在容器括号内：每条记录调用一次，value 是整张集合
register("weightsNotEmpty", "list", function(value, row, owner)
    expect(#value > 0, "列表不能为空")
end)
```

---

## 9. 性能提示

- 列级规则是**每个单元格调用一次**，规则体里不要做重活；`cfg.keyed(...)` 的结果在框架内有缓存，重复取用没有明显开销。
- 想"每个字段只提醒一次"可以用文件顶层的局部变量记录状态（同一次导出内所有调用共享同一个解释器）：

```lua
local warned = false
register("someRule", "int", function(value, row, owner)
    if value <= 0 and not warned then
        warned = true
        fail("发现非正数（其余同类问题不再重复提示）")
    end
end)
```

- 不要在规则里 `print` 每个单元格，日志会非常巨大。
