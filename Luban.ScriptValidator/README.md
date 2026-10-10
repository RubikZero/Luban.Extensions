# Luban.ScriptValidator

**English** | [简体中文](README.zh-CN.md) | [← Repository readme](../README.md)

`Luban.ScriptValidator` runs Lua validation rules against all configuration tables
already loaded by Luban. It is a stable extension DLL; rules are ordinary `.lua`
files and take effect on the next export without rebuilding the DLL.

Rules come in two kinds: **export rules** that check the whole database once per
export, and **column rules** that a schema attaches to a single field with the
`#lua=<name>` tag and that run for every record.

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
validation failure.

There are two kinds of rule, and they are configured separately:

| Kind | Configured by | Runs | What it can use |
| --- | --- | --- | --- |
| Export rules | `-x luaValidator.scriptDir=<dirs>` plus `-x dataPostprocess=luaValidator`; a file defines `validate()` | once per export, after the tables are loaded | `cfg`, and every table's rows |
| Column rules | `-x luaValidator.columnDir=<dirs>` (default: `columns` below every script directory); a file calls `register(...)` and a field is tagged `#lua=<name>` | once per record, per tagged field, in the normal validation phase | `cfg`, the field's value, and the row it belongs to |

This section and the rule API below describe export rules;
[Column rules](#column-rules) covers the other kind.

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

## Column rules

A column rule validates **one field of every record**. It is attached where the
field is declared, with the `#lua=<name>` type tag, so the intent stays visible in
the schema and no separate list of fields has to be kept in sync:

```xml
<bean name="Level">
    <var name="id"   type="int"/>
    <var name="icon" type="string#path=normal;assets/*.txt#lua=iconName"/>
    <var name="unit" type="UnitInfo#lua=unitRef"/>
</bean>
```

Because it is a Luban validator rather than a postprocessor, it runs in the normal
validation phase of every export: no `-x dataPostprocess=luaValidator` is needed,
and a failure is reported and recorded exactly like a built-in `#ref` or `path`
failure.

### Where the scripts live

| Option | Meaning |
| --- | --- |
| `-x luaValidator.columnDir=<dir1;dir2>` | Directories holding column scripts. |
| *not set* | Every `<scriptDir>/columns` that exists, so one rule tree can hold both kinds. |

A directory named on `luaValidator.columnDir` has to exist; a missing default
subdirectory is simply "no column rules". Directories listed under a column
directory are never read as export rules, so `rules/columns/*.lua` cannot be
mistaken for a missing `validate()`.

### Registering a rule

Column scripts are loaded while Luban compiles the schema — before any data is
read — so the top level of such a file may only register. `cfg`, `row`, `owner`,
`fail` and `expect` exist when the rule runs, not when it is registered:

```lua
register{
    name     = "iconName",
    type     = "string",              -- the input type this rule accepts
    validate = function(value, row, owner)
        expect(value ~= "", "icon must not be empty")
    end,
}

-- The positional form is equivalent.
register("positiveWeight", "int", function(value, row, owner)
    expect(value > 0, "weight must be positive")
end)
```

The declared `type` is checked against the field it is attached to while the schema
is compiled, so a rule cannot silently run on the wrong kind of field. Accepted
values are `any`, the Luban type names (`int`, `long`, `float`, `double`, `bool`,
`string`, `datetime`, `list`, `map`, …), the generic `bean`, `enum` and
`collection`, and the type exactly as the schema spells it — `UnitInfo`, `UnitType`
— which is how a rule says *which* bean it accepts.

Values arrive converted the same way they do for export rules: enums as their item
name, `long` as a string so no digit is lost, and beans and collections as
read-only tables. See [Field names and values](#field-names-and-values).

The three arguments are:

| Argument | What it is |
| --- | --- |
| `value` | The tagged field's value. When the tag is on a field whose type is a bean, this is the whole bean. |
| `row` | The record the field belongs to, as a read-only table. |
| `owner` | The bean that contains the tagged field. For a field of the record itself it is the record, so `owner == row` holds. |

`owner` is what makes a rule **declared inside a bean** able to read its siblings:
that rule is called for every place the bean is used, and the field's own name and
position are not passed to it, so `owner.unit_type` is the only way to reach the
neighbouring field without hard-coding where the bean sits.

### Where to attach a rule

| The rule needs | Attach it to | Example |
| --- | --- | --- |
| Only the field's own value (not empty, in range, matches a pattern) | a field **inside the bean**, so it applies wherever the bean is used | `type="int#lua=positiveWeight"` |
| The field's value plus its siblings | also a field inside the bean, read the siblings from `owner` | `type="int#lua=unitRef"` with `owner.unit_type` |
| The whole bean, in one chosen place only | the field that **uses the bean**, so `value` is the bean | `type="UnitInfo#lua=unitRef"` |

Declaring a rule inside a bean means every use of that bean is checked and no use
site can opt out; attaching it at a use site means the opposite.

### Looking other tables up

The rule runs after the tables are loaded, so a field's validity can depend on
another table — including on *which* table, chosen by a sibling field:

```lua
-- Attached to UnitInfo.unit_id, so it applies wherever UnitInfo is used. The enum
-- arrives in Lua as its item name, so the mapping is keyed by name; `owner` is the
-- UnitInfo this field belongs to, whichever field name it is used under.
local unitTables = { Hero = "TbHero", Monster = "TbMonster" }

register{
    name = "unitRef",
    type = "int",
    validate = function(value, row, owner)
        local tableName = unitTables[owner.unit_type]
        expect(tableName ~= nil, "unknown unit type: " .. tostring(owner.unit_type))
        if tableName == nil then return end

        local keyed = cfg.keyed(tableName)
        expect(keyed ~= nil, tableName .. " is not exported")
        if keyed == nil then return end

        expect(keyed[value] ~= nil,
            string.format("%s has no unit_id=%d", tableName, value))
    end,
}
```

`cfg.keyed`, `cfg.table`, `cfg.enums` and the rest of the rule API are the same as
for export rules.

### Failures

A violation is logged with the record's location and the rule file, and recorded in
Luban's validation bookkeeping:

```text
ERROR|[lua] test.TbAsset[1].icon (.../data/asset.csv): units.lua: icon must not be empty
```

Like any other validation failure it does not stop the run by itself; the strict
flag decides the exit code. These are usage errors and abort the run instead,
because each of them would otherwise mean "nothing was validated":

- a field tagged `#lua=<name>` while no column directory is configured,
- a name that no column script registered,
- a registration whose declared `type` does not match the tagged field,
- a column script that fails to load, registers the same name twice, uses an
  unsupported key in `register{...}`, or defines `validate()` instead of
  registering (that is an export rule, and the message says so).

Note that Luban ignores a tag name it does not know, so a typo in the tag itself
(`#luaa=name`) is silent; a wrong *name* is not, because it is resolved.

### Notes

- Column rules run in the validation phase, which `LoadDatas()` triggers — that is,
  when the command has a data target (`-d ...`) or passes `-f`. A code-only run
  validates nothing unless you add `-f`.
- Lua functions are called one at a time: MoonSharp is not thread-safe while Luban
  validates one table per task, so the extension queues the calls instead of giving
  every thread its own copy of the read-only view. The tables themselves are built
  once per run.

## Tests

`tests/SmokeRules/` holds a rule that only checks the API is reachable.

`tests/Fixture/` is a self-contained Luban project — an XML schema plus CSV data,
no Excel and no game data — with one table of each shape, a table carrying both a
`path`-tagged field and a `#lua`-tagged one, a rule that exercises every lookup
form, `rules/columns/` with column rules (a string check, and a cross-table rule
declared *inside* the bean that picks `TbHero` or `TbMonster` from an enum value and
is used from two tables under different field names), `failing-rules/` with an
export rule that fails on purpose, and `columns-failing/` plus `columns-bad-type/`
for the two ways a column rule is rejected on purpose:

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
reports the field when only the impossible root is left, that the export rules ran
exactly once, that each column rule ran once per tagged field and reached the table
it looked up, that every exported file exists, and — using the deliberately broken
rule sets — that a failing rule (export or column) is reported, keeps the run going,
still writes the export, and becomes exit code `1` only under the strict flag of
that Luban version, while a wrong declared type or an unregistered name fails the
build.

## License

This extension is MIT licensed, like the rest of the repository — see the
[repository license](../LICENSE).
