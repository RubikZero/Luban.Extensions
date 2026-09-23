# Luban.Extension.Deployer

**English** | [简体中文](README.zh-CN.md) | [← Repository readme](../../README.md)

The build-time tool that installs a built Luban extension into a Luban
installation and makes it loadable. It is normally run by
[`Directory.Build.targets`](../../Directory.Build.targets) rather than by hand.

## Usage

```text
dotnet run --project tools/Luban.Extension.Deployer -- `
  <extension.dll> <Luban directory> [dependency.dll ...]
```

For every assembly passed in, it:

1. copies the file into the Luban directory, overwriting an older copy, and
2. inserts an entry into `Luban.deps.json`, under both the runtime target and
   `libraries`, unless that entry already exists.

## Why `Luban.deps.json` is involved

Luban discovers plugins by scanning `*.dll` next to `Luban.dll` for file names
containing `Luban`, and loads them by simple name. On .NET 8 that resolution goes
through `Luban.deps.json`, so an assembly that is discovered but not listed cannot
be loaded. That is also why a dependency whose own name does not contain `Luban`
— MoonSharp, for `Luban.ScriptValidator` — has to be passed in as well: the scan
will never find it on its own.

## Guarantees

- It writes only the assemblies it was given plus `Luban.deps.json`. It never
  creates a per-extension `<Extension>.deps.json` and never deletes anything.
- It leaves `Luban.deps.json` byte-for-byte unchanged when every assembly is
  already registered, so repeated builds produce no diff.
- It edits the manifest as text and re-parses the result before writing, so a
  malformed edit is never persisted.
- Failures are reported as a single line and exit code 1, which MSBuild surfaces
  as an ordinary build error.

## Limitations

- Entries are added but never updated. An extension rebuilt under a different
  assembly version would leave the previous version key behind in the manifest.
- The manifest is located through `runtimeTarget.name`; a manifest without that
  property is rejected rather than guessed at.
