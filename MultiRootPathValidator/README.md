# Luban.MultiRootPathValidator

A drop-in extension for Luban's built-in `path` validator. It keeps the existing validator syntax (`path=unity`, `path=normal;...`, etc.) but allows validation against multiple project/resource roots.

## Why this works

Luban scans DLLs next to its executable whose file names contain `Luban`. Assemblies marked with `[assembly: RegisterBehaviour]` are scanned for custom behaviours. This extension registers another validator named `path` with a higher priority, so it replaces the built-in `path` validator without modifying Luban itself.

With the .NET 8 Luban build, the extension must also be listed in `Luban.deps.json`; otherwise the runtime cannot resolve an extension discovered by name. `build.ps1 -Deploy` copies the DLL and updates that manifest together. Do not copy just the DLL manually.

## Requirements

- Luban built for .NET 8 (current main branch)
- .NET 8 SDK to build this extension
- `Luban.Core.dll` and `NLog.dll` in the Luban executable directory

## Build

Open `Luban.Extensions.sln` in Visual Studio, or run the following command from
the repository root. It builds every extension in the solution and deploys
each extension project that opts in with `IsLubanExtension=true`:

```powershell
dotnet build ..\Luban.Extensions.sln -c Release -m:1 -p:DeployLubanExtensions=true
```

The shared default Luban directory is in `Directory.Build.props`. Override it
without editing files when necessary:

```powershell
dotnet build ..\Luban.Extensions.sln -c Release -m:1 `
  -p:DeployLubanExtensions=true `
  -p:LubanDir="D:\Tools\Luban"
```

`build.ps1` is retained only for backward compatibility. New workflows should
use the solution command above.

### Adding another extension

1. Create the extension project and set `<IsLubanExtension>true</IsLubanExtension>` in its `.csproj`.
2. Add it to `Luban.Extensions.sln` (Visual Studio's *Add > Existing Project*, or `dotnet sln Luban.Extensions.sln add <project.csproj>`).
3. Build the solution with `-p:DeployLubanExtensions=true`.

The shared deployer receives each opted-in project's output DLL, copies it to
`LubanDir`, and registers it in `Luban.deps.json` only when its entry is
missing.

The output DLL is:

```text
bin\Release\net8.0\Luban.MultiRootPathValidator.dll
```

The C# deployer keeps the required `Luban.deps.json` entry in sync. It leaves
the manifest byte-for-byte unchanged when the extension is already registered.

## Usage

Recommended:

```powershell
dotnet Luban.dll `
  ... `
  -x "pathValidator.rootDirs=D:\GameProgram\Assets;D:\GameArt\Assets"
```

If the value stored in the table is:

```text
Assets/UI/Icon/Foo.png
```

then the roots should normally be the Unity project directories instead:

```powershell
-x "pathValidator.rootDirs=D:\GameProgram;D:\GameArt"
```

because `path=unity` checks:

```text
<root>/<field value>
```

For example it will accept the value if either file exists:

```text
D:\GameProgram\Assets\UI\Icon\Foo.png
D:\GameArt\Assets\UI\Icon\Foo.png
```

Existing schema/configuration does not need to change:

```text
string#path=unity
```

### Backward-compatible option

The extension also accepts the original option name. A single root behaves exactly like Luban's built-in validator:

```powershell
-x "pathValidator.rootDir=D:\GameProgram"
```

You may also supply multiple roots through it:

```powershell
-x "pathValidator.rootDir=D:\GameProgram;D:\GameArt"
```

If both `rootDirs` and `rootDir` are provided, `rootDirs` wins.

## Supported path modes

This extension preserves the built-in modes:

- `path=unity`
- `path=unity?`
- `path=normal;<pattern>`
- `path=normal?;<pattern>`
- `path=ue`
- `path=ue?`
- `path=godot`
- `path=godot?`

The only semantic change is that a path succeeds when **any** configured root contains the corresponding file.

## Notes

- Root directories are separated with `;`. Quote the full `-x` argument in PowerShell/CMD so the shell does not interpret it.
- Empty root entries are ignored.
- Duplicate roots are removed (case-insensitively on Windows).
- Relative roots keep normal .NET/Luban process-relative path semantics.
- If no `rootDirs`/`rootDir` option is provided, path validation is disabled, matching Luban's built-in behaviour.

## License note

The path-pattern behavior is adapted from Luban, which is MIT-licensed. See `LICENSE.Luban`.

