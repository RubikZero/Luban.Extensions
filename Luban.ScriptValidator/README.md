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
been assembled, but before `OutputSaver` writes generated files. A rule that fails,
or one that cannot be loaded at all, is reported the same way Luban reports any
validation failure, so the rules stay opt-in through `-x dataPostprocess=luaValidator`
and need no `#lua` schema tag.

It is a data postprocessor, and Luban **saves the manifest a postprocessor hands
back**: this extension passes every exported file through unchanged, so enabling
validation never changes the generated output.

Failures behave exactly like Luban's own validators. Every violation is logged as
an error and recorded in Luban's validation bookkeeping; the run continues and the
export is written as usual; the exit code is decided at the end of the pipeline by
the strict flag — `--validationFailAsError` on 4.x, `--strict` on 5.x:

```powershell
dotnet <LubanDir>\Luban.dll -t client -c cs-bin -d bin --conf luban.conf `
  --validationFailAsError `
  -x dataPostprocess=luaValidator -x luaValidator.scriptDir=..\Rules
```

Without that flag a failing rule is reported and the run still exits `0`, exactly
like a failing `#ref` or `path` tag. Setting the validator up wrongly — no
`luaValidator.scriptDir`, a directory that does not exist, or a directory holding
no `*.lua` file — is a usage error and aborts the run with exit code `1` either way.

Note that stale files are not removed *because* validation is enabled: within a
directory that is written, Luban's `local` output saver clears it on every run,
with or without a postprocessor, so do not keep hand-written files inside
`outputDataDir` or `outputCodeDir`.

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
| `cfg.ref("<table>", key...)` | Row matching the index, or `nil`. One value for a single-key table, one per field for a union key. |
| `cfg.keyed("<table>"[, "<field>", ...])` | Read-only map of index to row. Nested by one level per field for a union index, or `nil` when the index is ambiguous or missing. |
| `cfg.singleton("<table>")` | The only row of a `mode="one"` table, or `nil`. |
| `cfg.tableInfo("<table>")` | `{ name, fullName, mode, index, indexFields, indexes, unionIndex, multiKey, keyType, count, loaded, keyed }`. |
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

### Looking rows up by key

`cfg.tables.X` is a plain **array of rows**, so `cfg.tables.X[key]` indexes by
*position*: it returns `nil` for any key larger than the row count, and would
silently return an unrelated row if an integer key happened to fall inside
`1..#rows`. Use `cfg.keyed` to look a row up by its index instead; it mirrors the
`DataMap`, `GetByXxx` and `Get(k1, k2)` APIs of Luban's generated code.

Luban gives a table one of four shapes, and `cfg.keyed` follows each of them:

| Table | Declared as | How to read it |
| --- | --- | --- |
| Single key | `mode="map"`, `index="id"` | `cfg.keyed("TbItem")[id]` |
| Several independent keys | `mode="list"`, `index="id,name"` | `cfg.keyed("TbX", "id")[id]` or `cfg.keyed("TbX", "name")[name]` — each key is unique on its own |
| One union key | `mode="list"`, `index="kind+level"` | `cfg.keyed("TbX")[kind][level]` — one nesting level per field, unique only together |
| Singleton | `mode="one"` | `cfg.singleton("TbCommon")` |

```lua
local levels = cfg.keyed("TbLevel")        -- built on first use, then cached
local items  = cfg.keyed("TbItem", "name") -- a named index of a multi-key table
local grid   = cfg.keyed("TbGrid")         -- nested, because its only index is a union
local common = cfg.singleton("TbCommon")

for _, row in ipairs(cfg.tables.TbMission) do
    expect(levels[tonumber(row.level_id)] ~= nil,
        string.format("%s: level_id %s does not exist", row.__source, row.level_id))
end

local cell = grid["alpha"][10]
```

The rules that always hold:

- `cfg.keyed("<table>")` with **no field name** works only when the choice is
  unambiguous, which includes a single union index. A table with several
  independent indexes returns `nil` and logs a warning telling you to name one.
- Naming one field that is not part of a union index gives a flat map; naming
  every field of a union index gives the same nested map as the no-argument form.
- `cfg.ref("<table>", key...)` is the one-shot form. It takes one value for a
  single-key table, and one value per field for a union-key table
  (`cfg.ref("TbGrid", "alpha", 10)`). For a table with several independent indexes
  it is ambiguous and returns `nil`; use `cfg.keyed(table, "<field>")[key]`.
- Integer keys work as both numbers and strings, because `long` fields reach Lua
  as strings: `levels[10001]` and `levels["10001"]` are the same row.
- Every lookup returns the **same row objects** as `cfg.tables.X`, so rows can be
  compared with `==`, and every level of a nested map is read-only.
- A singleton has no keyed view, and a table outside the current export target
  cannot be indexed at all.

`cfg.tableInfo("<table>")` reports what a table actually is, which is the quickest
way to find out why a lookup returns `nil`:

```lua
local info = cfg.tableInfo("TbGrid")
-- info.mode        -> "map" | "list" | "one"
-- info.index       -> "kind+level"
-- info.indexFields -> { "kind", "level" }
-- info.indexes     -> { { spec = "kind+level", fields = { "kind", "level" },
--                         keyTypes = { "string", "int" }, union = true } }
-- info.unionIndex  -> true    -- one index spanning several fields
-- info.multiKey    -> false   -- several independent indexes
-- info.keyType     -> "int"   -- only meaningful for a single-key table
-- info.count       -> 128
-- info.loaded      -> true
-- info.keyed       -> true
```

`cfg.table("<name>")` accepts the same short and module-qualified spellings as
`cfg.keyed` / `cfg.ref` / `cfg.tableInfo`. `cfg.tables` itself stays keyed by the
canonical full name only, so enumerating it never yields the same table twice.

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

## Tests

`tests/SmokeRules/` holds a rule that only checks the API is reachable.

`tests/Fixture/` is a self-contained Luban project — an XML schema plus CSV data,
no Excel and no game data — with one table of each shape, a table carrying a
`path`-tagged field so the path validator takes part in the same run, a rule that
exercises every lookup form, and `failing-rules/` with a rule that fails on purpose:

```powershell
dotnet <LubanDir>\Luban.dll -t all -d bin `
  --conf Luban.ScriptValidator/tests/Fixture/luban.conf `
  -x "outputDataDir=<an empty directory>" `
  -x dataPostprocess=luaValidator `
  -x luaValidator.scriptDir=Luban.ScriptValidator/tests/Fixture/rules `
  -x "pathValidator.rootDirs=<a directory that cannot hold assets>;Luban.ScriptValidator/tests/Fixture"
```

It prints its results, writes one data file per table into that directory and exits
`0`. The warnings it logs are the deliberate "this table cannot be indexed that way"
cases, each naming the reason.

Exporting to a real directory is part of the check, not decoration: Luban saves the
manifest the postprocessor returns, so a postprocessor that only validates would
leave the output directory empty while still exiting `0`.

Because Luban logs a failed validation without failing the run, the fixture's
verdict is its output and its exported files, not its exit code:
[the shared action](../.github/actions/build-against-luban/action.yml) checks that
the path validator found `assets/sword.txt` under the *second* of two roots and
reports the field when only the impossible root is left, that the rules ran exactly
once, that every exported file exists, and — using `failing-rules/` — that a failing
rule is reported, keeps the run going, still writes the export, and becomes exit
code `1` only under the strict flag of that Luban version.

## License

This extension is MIT licensed, like the rest of the repository — see the
[repository license](../LICENSE).
