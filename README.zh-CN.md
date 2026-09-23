# Luban.Extensions

[English](README.md) | **简体中文**

针对游戏配置工具 [Luban](https://github.com/focus-creative-games/luban) 的扩展仓库。Luban 按 schema 校验 Excel/XML 配置数据，再导出数据与代码；本仓库以「插件」形式为它补上原本没有的能力，**不需要修改 Luban 本体**。

## 子项目

每个子项目目录都有自己的说明文档，提供英文与中文两个版本。

| 目录 | 提供的能力 | 说明文档 |
| --- | --- | --- |
| [`MultiRootPathValidator/`](MultiRootPathValidator) | 替换内建的 `path` 校验器，使资源路径可以在多个工程根目录中的任意一个下命中。 | [English](MultiRootPathValidator/README.md) · [简体中文](MultiRootPathValidator/README.zh-CN.md) |
| [`Luban.ScriptValidator/`](Luban.ScriptValidator) | 对已加载的表运行 Lua 校验规则，并提供枚举、`#ref` 引用与主键取值的辅助函数。 | [English](Luban.ScriptValidator/README.md) · [简体中文](Luban.ScriptValidator/README.zh-CN.md) |
| [`tools/Luban.Extension.Deployer/`](tools/Luban.Extension.Deployer) | 把构建好的扩展拷贝进 Luban 安装目录，并登记到 `Luban.deps.json`。 | [English](tools/Luban.Extension.Deployer/README.md) · [简体中文](tools/Luban.Extension.Deployer/README.zh-CN.md) |

## Luban 如何加载扩展

已针对 Luban 4.5.0 验证；4.x 与 5.x 各版本中都存在这套「扫描 + 注册」机制。

1. Luban 扫描 `Luban.dll` 同级目录下的 `*.dll`（**仅顶层目录，不递归**），加载其中文件名包含 `Luban` 的全部文件。
2. 在这些程序集里，只有带 `[assembly: RegisterBehaviour]` 的才会被扫描自定义行为。
3. 行为按**类型 + 名称**注册。两者都相同时由 `Priority` 决定，**优先级高者胜出**——这正是扩展能够在不打补丁的前提下替换内建能力（例如 `path` 校验器）的原因。
4. 在 .NET 8 版本上，程序集还必须登记到 `Luban.deps.json`，否则运行时无法解析这个「按文件名发现」的插件。

由此产生两条对本仓库的硬约束：扩展程序集必须命名为 `Luban.*.dll` 且带 `[assembly: RegisterBehaviour]`；而**文件名不含 `Luban`** 的托管依赖（例如 `Luban.ScriptValidator` 用到的 MoonSharp）必须一并拷贝进 Luban 目录并登记到 `Luban.deps.json`——按文件名的扫描永远找不到它。

## 环境要求

- .NET 8 SDK
- 面向 .NET 8 构建的 Luban（已针对 Luban 4.5.0 验证），且安装目录下存在 `Luban.Core.dll`、`NLog.dll` 与 `Luban.deps.json`

## 初始化

`LubanDir`（你的 Luban 安装目录）配置在 `Directory.Build.props` 中，该文件**不受 git 跟踪**，以便每台机器保留自己的路径。首次使用从示例生成一份：

```powershell
Copy-Item Directory.Build.props.example Directory.Build.props
# 然后编辑副本中的 LubanDir
```

构建所需的其余配置与机器无关，都放在受跟踪的 `Directory.Build.targets` 里。

也可以完全不用这个文件，直接在命令行传路径——CI 场景推荐这么做：

```powershell
dotnet build Luban.Extensions.sln -c Release -p:LubanDir="C:\path\to\luban\Tools\Luban"
```

## 构建

普通构建只编译，**不会部署任何东西**：

```powershell
dotnet build Luban.Extensions.sln -c Release -m:1
```

部署需要显式开启。它会构建解决方案，并对每个设置了 `IsLubanExtension=true` 的工程，把输出的 DLL 拷贝进 `LubanDir` 并登记到 `Luban.deps.json`：

```powershell
dotnet build Luban.Extensions.sln -c Release -m:1 -p:DeployLubanExtensions=true
```

部署器只写扩展 DLL 与 `Luban.deps.json` 中对应的条目。它不会生成按扩展命名的 `.deps.json`，不会删除任何文件；当扩展已登记时会保持清单**逐字节不变**。失败会以普通构建错误 + 退出码 1 的形式报出。

注意：部署的目标是 Luban 安装目录，而它通常属于另一个仓库，因此带 `-p:DeployLubanExtensions=true` 的构建会修改那个仓库的工作区。

## 新增一个扩展

1. 创建工程，设置 `<AssemblyName>Luban.<Something></AssemblyName>` 与 `<IsLubanExtension>true</IsLubanExtension>`。
2. 新增 `AssemblyInfo.cs`，内容为 `using Luban;` 与 `[assembly: RegisterBehaviour]`。
3. 以 `<Private>false</Private>` 引用 `$(LubanDir)` 下的 `Luban.Core.dll`，避免把 Luban 自身的程序集随扩展一起发布。
4. 编写行为类，加上对应扩展点的特性（`[Validator(...)]`、`[PostProcess(...)]` 等）；若意在替换内建实现，请显式指定 `Priority`。
5. 把工程加入 `Luban.Extensions.sln`。
6. 用 `-p:DeployLubanExtensions=true` 构建。

额外的托管依赖用 `LubanExtensionDependency` 项声明，部署器会一并拷贝并登记；MoonSharp 的例子见 [`Luban.ScriptValidator.csproj`](Luban.ScriptValidator/Luban.ScriptValidator.csproj)。

## 仓库结构

```text
Directory.Build.props.example    本机路径配置的模板（生成的文件不受跟踪）
Directory.Build.targets          受跟踪的、与机器无关的构建配置
LICENSE                          本仓库的 MIT 许可
Luban.Extensions.sln             包含全部项目的解决方案
MultiRootPathValidator/          Luban.MultiRootPathValidator
Luban.ScriptValidator/           Luban.ScriptValidator
tools/Luban.Extension.Deployer/  拷贝并登记构建好的扩展
```

## 许可

本项目采用 [MIT 许可](LICENSE)。

`MultiRootPathValidator` 中的路径模式行为改编自 [Luban](https://github.com/focus-creative-games/luban)，后者同样采用 MIT 许可。按照 MIT 的要求，Luban 的许可证文本与版权声明与该处代码放在一起：[`MultiRootPathValidator/LICENSE.Luban`](MultiRootPathValidator/LICENSE.Luban)。
