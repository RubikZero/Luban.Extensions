# Luban.ScriptValidator

`Luban.ScriptValidator` runs Lua validation rules against all configuration tables
already loaded by Luban. It is a stable extension DLL; rules are ordinary `.lua`
files and take effect on the next export without rebuilding the DLL.

## Installation

Build and deploy every extension:

```powershell
dotnet build .\Luban.Extensions.sln -c Release -m:1 -p:DeployLubanExtensions=true
```

The deployer copies both `Luban.ScriptValidator.dll` and the managed
`MoonSharp.Interpreter.dll` runtime to `LubanDir`, then registers both assemblies
in `Luban.deps.json`.

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
        expect(row.RewardId ~= 0,
            string.format("TbLevel id=%s (%s): RewardId is required", row.Id, row.__source))
    end
end
```

Available globals:

| API | Description |
| --- | --- |
| `cfg.table("full.table.name")` | Array-like Lua table containing every loaded row. |
| `cfg.tables["full.table.name"]` | Equivalent direct lookup. |
| `row.__source` | Luban source file location for the row. |
| `row.__autoIndex` | Luban record index. |
| `fail(message)` | Records a validation error and continues. |
| `expect(condition, message)` | Records an error when the condition is false. |

Bean fields are read-only Lua tables; lists, arrays and sets are read-only Lua
arrays; maps are read-only Lua tables. An attempted assignment, `table.insert`,
`table.remove`, or `table.sort` raises an error. Enums are exposed as their
configured item names. `long` and `datetime` are strings so Lua's double-based
numbers do not lose precision.

The interpreter uses MoonSharp's soft sandbox. Rules receive only the `cfg`,
`fail`, and `expect` APIs; file, process, network, CLR reflection, and package
loading APIs are not exposed.
