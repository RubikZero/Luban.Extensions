# Luban.Extensions

**English** | [简体中文](README.zh-CN.md)

Extensions for [Luban](https://github.com/focus-creative-games/luban), the game
configuration tool. Luban validates Excel/XML configuration data against a schema
and then exports data plus generated code; this repository adds behaviour it does
not ship with, as plugins that load into an unmodified Luban installation.

## Projects

Every project directory has its own readme, in English and Chinese.

| Directory | What it adds | Readme |
| --- | --- | --- |
| [`MultiRootPathValidator/`](MultiRootPathValidator) | Replaces the built-in `path` validator so an asset path may live under any of several project roots. | [English](MultiRootPathValidator/README.md) · [简体中文](MultiRootPathValidator/README.zh-CN.md) |
| [`Luban.ScriptValidator/`](Luban.ScriptValidator) | Runs Lua validation rules over the loaded tables, with helpers for enums, `#ref` lookups and primary-key lookups. | [English](Luban.ScriptValidator/README.md) · [简体中文](Luban.ScriptValidator/README.zh-CN.md) |
| [`tools/Luban.Extension.Deployer/`](tools/Luban.Extension.Deployer) | Copies a built extension into a Luban installation and registers it in `Luban.deps.json`. | [English](tools/Luban.Extension.Deployer/README.md) · [简体中文](tools/Luban.Extension.Deployer/README.zh-CN.md) |

## How Luban loads an extension

The mechanism is the same throughout the supported range — see
[Luban version support](#luban-version-support).

1. Luban scans `*.dll` next to `Luban.dll` — **top directory only** — and loads
   every file whose name contains `Luban`.
2. Of those assemblies, only the ones marked `[assembly: RegisterBehaviour]` are
   scanned for behaviours.
3. Behaviours are registered by *type* and *name*. Two behaviours sharing both are
   resolved by `Priority`, the higher one winning — which is how an extension
   replaces a built-in such as the `path` validator without patching Luban.
4. With the .NET 8 build the assembly must also be listed in `Luban.deps.json`;
   otherwise the runtime cannot resolve a plugin that was discovered by file name.

Two consequences shape this repository. An extension assembly must be named
`Luban.*.dll` and carry `[assembly: RegisterBehaviour]`. And a managed dependency
whose name does *not* contain `Luban` — MoonSharp, in `Luban.ScriptValidator` —
must be copied into the Luban directory and registered in `Luban.deps.json` too,
because the file-name scan will never find it.

## Luban version support

**Luban 4.1.0 and later is the supported range.** Everything in it compiles and
runs against these extensions unchanged, and the
[`Build`](.github/workflows/build.yml) workflow checks one release per API era —
4.1.0, 4.7.0, 4.12.0, 5.0.0 and 5.1.0 — downloading each, building the solution
against it, deploying the extensions into it and running the fixture through it.
That fixture asserts behaviour rather than merely loading: it checks the Lua API's
answers, it requires the export to actually write its data files, it runs the path
validator over a root that contains the asset and then over one that cannot, and it
checks that a failing rule is reported the way Luban reports any validation failure.
A run in which validation quietly stopped happening, in which a postprocessor
dropped the export, or in which a failing rule aborted the pipeline therefore fails
the build instead of passing it.

Those five are a sample, not the whole range: the API surface the extensions use
was checked tag by tag across every release from 4.1.0 to 5.1.0, and the result is
that nothing between the era boundaries changes what the extensions touch.

Everything the extensions depend on is stable across that range — the behaviour
registry and its priority rules, `IDataValidator` and `DataValidatorBase`,
`PostProcessBase` and `PostProcessAttribute`, the `DefTable` index model including
`IndexInfo`, `TypeTemplateExtension`, and the plugin discovery rules. The
remaining differences are either additive (new type members) or internal (error
messages moved into a localizable catalog, the launcher singleton replaced by
`PipelineScope` at v5.0.0).

### Why v4.1.0 is the floor

`DMap` renamed its only accessor in that release:

| Version | Member |
| --- | --- |
| v4.0.0 and older | `Dictionary<DType, DType> Datas` |
| **v4.1.0 and later** | `Dictionary<DType, DType> DataMap` |

The Lua validator enumerates map fields through it, and a single source tree cannot
serve both spellings without conditional compilation. Releases before v4.1.0 are
therefore outside the supported range, and they differ from it in further ways:
the 3.x and 1.x lines diverge from the plugin-facing API in several places, and
the 1.x line is a different architecture altogether.

### Moving to 5.x

Two things change on the Luban side. Neither touches this repository, but both can
affect your own setup:

- the CLI flag `--validationFailAsError` became `--strict`, so check your launch
  scripts;
- manager properties such as `EnvManager.Current` and `GenerationContext.Current`
  now throw outside an active `PipelineScope`. That never happens under
  `Luban.dll`, but a host that drives Luban programmatically has to enter a scope
  first.

## Requirements

- .NET 8 SDK
- A Luban installation built for .NET 8 (4.1.0 and later are supported),
  containing `Luban.Core.dll`, `NLog.dll` and `Luban.deps.json`

## Setup

`LubanDir` — the path to your Luban installation — lives in
`Directory.Build.props`, which is **not tracked by git** so that every machine
keeps its own. Create it once from the example:

```powershell
Copy-Item Directory.Build.props.example Directory.Build.props
# then edit LubanDir in the copy
```

Everything else the build needs is machine-independent and stays in the tracked
`Directory.Build.targets`.

You can also skip the file entirely and pass the path on the command line, which
is what CI should do:

```powershell
dotnet build Luban.Extensions.sln -c Release -p:LubanDir="C:\path\to\luban\Tools\Luban"
```

## Build

A normal build compiles everything and deploys **nothing**:

```powershell
dotnet build Luban.Extensions.sln -c Release -m:1
```

Deployment is opt-in. It builds the solution and, for every project that sets
`IsLubanExtension=true`, copies the output DLL into `LubanDir` and registers it in
`Luban.deps.json`:

```powershell
dotnet build Luban.Extensions.sln -c Release -m:1 -p:DeployLubanExtensions=true
```

The deployer writes only the extension DLLs and the matching `Luban.deps.json`
entries. It never creates a per-extension `.deps.json`, never removes files, and
leaves the manifest byte-for-byte unchanged when an extension is already
registered. Failures are reported as ordinary build errors with exit code 1.

Note that deployment targets a Luban installation, which is normally a separate
repository, so a build with `-p:DeployLubanExtensions=true` modifies that working
tree.

## Releases

A release is cut by pushing a tag to `master`:

```powershell
git tag v1.2.3
git push origin v1.2.3
```

The tag has to be a lowercase `v` followed by three dot-separated numbers, and its
commit has to be reachable from `master`. Anything else stops the workflow without
publishing: a tag such as `v1.2` or `V1.2.3` never starts it, and a well-formed tag
that is not on `master` fails the first step with an explanation.

[`.github/workflows/release.yml`](.github/workflows/release.yml) then:

1. downloads the pinned Luban release and uses the directory holding
   `Luban.Core.dll` as `LubanDir`,
2. builds the solution with the tag as the assembly version and deploys the
   extensions into that downloaded Luban,
3. runs [`Luban.ScriptValidator/tests/Fixture`](Luban.ScriptValidator/tests/Fixture)
   through Luban — asserting the Lua API's answers and the path validator's verdict
   — which proves the extensions load and work on a clean machine rather than
   merely compiling,
4. attaches a zip of the extension DLLs to a GitHub Release.

The Luban version the build is pinned to is `LUBAN_VERSION` at the top of the
workflow. Keep it in step with the Luban installation the extensions are deployed
into, since it decides which Luban API they compile against.

Because the release build stamps the tag into the assembly version, deploying the
released DLLs into a Luban that already lists `1.0.0.0` adds a second
`Luban.deps.json` entry rather than replacing it. That is harmless — the deployer
only ever adds entries — but it is why releasing and deploying locally can leave a
few extra lines behind.

## Adding an extension

1. Create the project with `<AssemblyName>Luban.<Something></AssemblyName>` and
   `<IsLubanExtension>true</IsLubanExtension>`.
2. Add an `AssemblyInfo.cs` containing `using Luban;` and
   `[assembly: RegisterBehaviour]`.
3. Reference `Luban.Core.dll` from `$(LubanDir)` with `<Private>false</Private>`
   so Luban's own assemblies are never shipped alongside the extension.
4. Add the behaviour class, decorated with the attribute that matches the
   extension point (`[Validator(...)]`, `[PostProcess(...)]`, and so on), with an
   explicit `Priority` when it is meant to replace a built-in.
5. Add the project to `Luban.Extensions.sln`.
6. Build with `-p:DeployLubanExtensions=true`.

Extra managed dependencies are declared as `LubanExtensionDependency` items so the
deployer copies and registers them as well; see
[`Luban.ScriptValidator.csproj`](Luban.ScriptValidator/Luban.ScriptValidator.csproj)
for the MoonSharp example.

## Repository layout

```text
.github/actions/build-against-luban/  Builds and verifies against one Luban release
.github/workflows/build.yml           Verifies every supported Luban release
.github/workflows/release.yml         Builds and publishes a Release from a vX.Y.Z tag
Directory.Build.props.example    Template for the untracked machine-local paths
Directory.Build.targets          Tracked, machine-independent build settings
LICENSE                          MIT license of this repository
Luban.Extensions.sln             Solution containing every project
MultiRootPathValidator/          Luban.MultiRootPathValidator
Luban.ScriptValidator/           Luban.ScriptValidator
tools/Luban.Extension.Deployer/  Copies built extensions and registers them
```

## License

This project is [MIT licensed](LICENSE).

The path-pattern behaviour in `MultiRootPathValidator` is adapted from
[Luban](https://github.com/focus-creative-games/luban), which is MIT licensed as
well. Luban's license text and copyright notice are kept next to that code, in
[`MultiRootPathValidator/LICENSE.Luban`](MultiRootPathValidator/LICENSE.Luban), as
the MIT terms require.
