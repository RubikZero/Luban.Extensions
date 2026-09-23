# Luban.ScriptValidator

**English** | [简体中文](README.zh-CN.md) | [← Repository readme](../README.md)

`Luban.ScriptValidator` runs Lua validation rules against all configuration tables
already loaded by Luban. It is a stable extension DLL; rules are ordinary `.lua`
files and take effect on the next export without rebuilding the DLL.

## Installation

Create the untracked `Directory.Build.props` from `Directory.Build.props.example`
and point `LubanDir` at your Luban installation, as described in the
[repository readme](../README.md#setup). Then build and deploy every extension:

```powershell
dotnet build .\Luban.Extensions.sln -c Release -m:1 -p:DeployLubanExtensions=true
```

The deployer copies both `Luban.ScriptValidator.dll` and the managed
`MoonSharp.Interpreter.dll` runtime to `LubanDir`, then registers both assemblies
in `Luban.deps.json`.

Building also references `Scriban.dll` from `LubanDir`, because the reference
helpers in `Luban.TemplateExtensions` are Scriban script objects. It is a
compile-time reference only and is never deployed.

## Enable Lua validation

Add the postprocessor name and rule directory to the Luban command:

```bat
-x dataPostprocess=luaValidator ^
-x luaValidator.scriptDir=..\Rules\ConfigValidation
```

Multiple directories can be separated by `;`. Relative directories are resolved
from the current working directory of the Luban command. Every `*.lua` file is
loaded recursively in deterministic path order. A data target (`-d bin`, `-d
json`, and so on) must be present, as is already the case for ordinary exports.

Lua rules run after Luban has loaded all table data and after data targets have
been assembled, but before `OutputSaver` writes generated files. A missing rule
directory, an invalid Lua script, or any `fail` / `expect` failure throws an
export error and stops Luban with a non-zero exit code. No `#lua` schema tag or
`--validationFailAsError` dependency is required for Lua failures.

## Rule API

Each rule file defines a global `validate()` function:

```lua
function validate()
    for _, row in ipairs(cfg.table("TbLevel")) do
        expect(row.reward_id ~= nil,
            string.format("TbLevel id=%s (%s): reward_id is required", row.id, row.__source))
    end
end
```

Available globals:

| API | Description |
| --- | --- |
| `cfg.table("full.table.name")` | Array-like Lua table containing every loaded row. |
| `cfg.tables["full.table.name"]` | Equivalent direct lookup. |
| `cfg.enumValue("<type>", "<text>")` | Numeric value of the enum item named or aliased by `<text>`, or `nil`. |
| `cfg.enumName("<type>", "<text>")` | Item name of that enum item, or `nil`. |
| `cfg.enumItem("<type>", "<text>")` | `{ name, value, alias, comment }` of that enum item, or `nil`. |
| `cfg.enumItems("<type>")` | Array of `{ name, value, alias, comment }` for every item of the enum. |
| `cfg.enums.<type>.<ITEM>` | Pre-built enum constant table, e.g. `cfg.enums.ELevelType.Elite == 2`. |
| `cfg.ref(row, "<field>")` | Row referenced by a field declared with `#ref`, or `nil`. |
| `cfg.ref("<table>", key)` | Row whose primary key is `key`, or `nil`. |
| `cfg.keyed("<table>")` | Read-only map of primary key to row, or `nil` when the table has no single-field key. |
| `cfg.tableInfo("<table>")` | `{ name, fullName, mode, index, indexFields, keyType, count, loaded, keyed }`. |
| `row.__source` | Luban source file location for the row. |
| `row.__autoIndex` | Luban record index. |
| `row.__type` | Concrete bean type, e.g. `game.Level`. |
| `fail(message)` | Records a validation error and continues. |
| `expect(condition, message)` | Records an error when the condition is false. |

### Enums and references

Lua has no native enum, and rule files cannot share a Lua module with each other
because every rule runs in its own interpreter with no `require`. The extension
therefore injects a **constant table for every enum into every script**, so a
rule can use enum values directly without defining anything:

```lua
for _, row in ipairs(cfg.table("TbLevel")) do
    if cfg.enumValue("ELevelType", row.level_type) == cfg.enums.ELevelType.Elite then
        -- ...
    end
end
```

The constant table is keyed by **item name and by item alias**, both mapping to
the numeric value, and it is reachable under every type-name spelling — so
`cfg.enums.ELevelType.Elite`, `cfg.enums["game.ELevelType"].Elite` and
`cfg.enums.ELevelType["精英"]` all resolve to the same number, and the three
lookups return the same table. On a name/alias collision the item name wins. The
tables are read-only, and an unknown type name yields `nil`.

Use `cfg.enumItems("<type>")` when you need the item metadata rather than the
value — for example to check that every item is reachable from somewhere:

```lua
for _, item in ipairs(cfg.enumItems("ELevelType")) do
    print(item.name, item.value, item.alias, item.comment)
end
```

Enum type names accept both spellings, with and without the top module
(`game.ELevelType` and `ELevelType`). Unresolvable input is never an error: an
unknown text, an unknown type name, or a type that is not an enum all return
`nil`, so a rule can assert on it directly:

```lua
expect(cfg.enumValue("ELevelType", row.level_type) ~= nil,
    string.format("%s: unknown level_type '%s'", row.__source, row.level_type))

if cfg.enumValue("ELevelType", row.level_type) == cfg.enumValue("ELevelType", "Elite") then
    -- ...
end
```

`cfg.ref` has two forms:

```lua
-- Follow the field's own #ref declaration (scalar and collection refs).
local reward = cfg.ref(row, "reward_id")

-- Look a row up directly by table name and primary key.
local item = cfg.ref("TbItem", 10001)
```

- Only fields that actually declare `#ref` resolve in the first form; any other
  field returns `nil`.
- For a collection ref the result is an array of the rows that resolved,
  unresolved elements are omitted, so compare lengths to spot the misses.
- Keys may be passed as a number or as a string, which matters because `long`
  fields arrive in Lua as strings.
- Only tables included in the current export target can be resolved, because
  that is all the data Luban loaded.
- The returned row is the **same object** as the one in `cfg.tables.X`, so a rule
  can compare rows by identity with `==` instead of comparing keys.

### Looking rows up by primary key

`cfg.tables.X` is a plain **array of rows**, so `cfg.tables.X[id]` indexes by
*position*: it returns `nil` for any id larger than the row count, and would
silently return an unrelated row if an integer key happened to fall inside
`1..#rows`. To look a row up by its primary key, use `cfg.keyed`, which mirrors
the `DataMap` of Luban's generated code:

```lua
local levels = cfg.keyed("TbLevel")   -- built on first use, then cached

for _, row in ipairs(cfg.tables.TbMission) do
    expect(levels[tonumber(row.level_id)] ~= nil,
        string.format("%s: level_id %s does not exist", row.__source, row.level_id))
end
```

Both `levels[10001]` and `levels["10001"]` resolve, because `long` keys reach
Lua as strings. `cfg.keyed` returns `nil`, and logs one warning, for a table
without a single-field primary key (list and singleton tables) or for a table
outside the current export target. Rows returned this way are the same objects as
in `cfg.tables.X`.

`cfg.tableInfo("<table>")` reports what a table actually is, which is the quickest
way to find out why a lookup returns `nil`:

```lua
local info = cfg.tableInfo("TbLevel")
-- info.mode        -> "map" | "list" | "one"
-- info.index       -> "id"
-- info.indexFields -> { "id" }
-- info.keyType     -> "int"
-- info.count       -> 128
-- info.loaded      -> true
-- info.keyed       -> true
```

### Field names and values

Bean fields are read-only Lua tables; lists, arrays and sets are read-only Lua
arrays; maps are read-only Lua tables. An attempted assignment, `table.insert`,
`table.remove`, or `table.sort` raises an error.

Four rules explain almost every surprise:

- **Field names are the raw schema names** — exactly the names declared in
  `__beans__.xlsx` or in `<var name="...">`, in the spelling the schema uses
  (`level_type`, `reward_id`, or `ItemId`). They are **not** the names in the
  generated C# code. Reading a field that does not exist yields
  `nil` instead of raising an error, so a typo looks exactly like a missing
  value.
- **A single bean row must be walked with `pairs`, not `ipairs`.** A bean row has
  only string keys while `ipairs` walks integer keys, so `ipairs(row)` silently
  iterates **zero** times. `ipairs` is correct for the row list itself
  (`cfg.table("TbLevel")`), which is a real array.
- **Enum fields hold the text written in the sheet**, which is either the item
  name or the item alias. A cell showing `精英` reads as the string `"精英"` in
  Lua even though the item name is `Elite`. Pass it through
  `cfg.enumValue` / `cfg.enumName` to compare it reliably.
- **`long` and `datetime` are strings** so Lua's double-based numbers do not lose
  precision. Compare them with strings, not with numbers.

`pairs(row)` also yields the metadata keys `_type`, `__source` and
`__autoIndex`; exclude them when a rule asserts something about "every field".

The interpreter uses MoonSharp's soft sandbox. Rules receive only the `cfg`,
`fail` and `expect` APIs. File, process and package loading APIs (`io`, `debug`,
`require`, `dofile`, `loadfile`, `load`, `loadstring`) are absent, `os` exposes
only `clock` / `date` / `difftime` / `time`, and the mutation helpers that could
bypass the read-only proxies (`rawget`, `rawset`, `getmetatable`, `setmetatable`,
`table.insert`, `table.remove`, `table.sort`) are removed. MoonSharp's CLR
interop module is removed as well, so `dynamic` is `nil`.

## License

This extension is MIT licensed, like the rest of the repository — see the
[repository license](../LICENSE).
