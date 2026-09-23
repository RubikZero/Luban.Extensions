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

这套机制在受支持的整个范围内都一致——见 [Luban 版本支持](#luban-版本支持)。

1. Luban 扫描 `Luban.dll` 同级目录下的 `*.dll`（**仅顶层目录，不递归**），加载其中文件名包含 `Luban` 的全部文件。
2. 在这些程序集里，只有带 `[assembly: RegisterBehaviour]` 的才会被扫描自定义行为。
3. 行为按**类型 + 名称**注册。两者都相同时由 `Priority` 决定，**优先级高者胜出**——这正是扩展能够在不打补丁的前提下替换内建能力（例如 `path` 校验器）的原因。
4. 在 .NET 8 版本上，程序集还必须登记到 `Luban.deps.json`，否则运行时无法解析这个「按文件名发现」的插件。

由此产生两条对本仓库的硬约束：扩展程序集必须命名为 `Luban.*.dll` 且带 `[assembly: RegisterBehaviour]`；而**文件名不含 `Luban`** 的托管依赖（例如 `Luban.ScriptValidator` 用到的 MoonSharp）必须一并拷贝进 Luban 目录并登记到 `Luban.deps.json`——按文件名的扫描永远找不到它。

## Luban 版本支持

**支持范围是 Luban 4.1.0 及之后的版本。** 该范围内的一切都能零改动编译并运行；[`Build`](.github/workflows/build.yml) 工作流按 API 代际各取一个发行版验证——4.1.0、4.7.0、4.12.0、5.0.0、5.1.0——逐个下载、对着它编译、把扩展部署进去、再用它跑一遍 fixture。这个 fixture 断言的是**行为**而不只是「加载成功」：既核对 Lua 接口返回的结果，也要求导出真的写出了数据文件，还把路径校验器分别跑在「含该资源的根目录」和「不可能含该资源的根目录」上——于是「校验其实已经静默失效」或「后处理器把产物丢了」这类情况会让构建失败，而不是照样通过。

这五个是抽样而非全部：扩展用到的 API 面已按 tag 逐个核对过 4.1.0 到 5.1.0 的每一个发行版，结论是代际边界之间没有任何改动触及扩展使用的东西。

该范围内扩展依赖的一切都是稳定的——行为注册表及其优先级规则、`IDataValidator` 与 `DataValidatorBase`、`PostProcessBase` 与 `PostProcessAttribute`、`DefTable` 的索引模型（含 `IndexInfo`）、`TypeTemplateExtension`，以及插件发现规则。其余差异要么是纯追加（新增类型成员），要么是内部改动（错误文案移入可本地化的消息表、v5.0.0 起启动器单例改为 `PipelineScope`）。

### 下界为什么是 v4.1.0

`DMap` 在那个版本改了唯一的访问器名：

| 版本 | 成员 |
| --- | --- |
| v4.0.0 及更早 | `Dictionary<DType, DType> Datas` |
| **v4.1.0 及之后** | `Dictionary<DType, DType> DataMap` |

Lua 校验器正是通过它遍历 map 字段，而同一份源码不加条件编译无法同时服务两种拼写。因此 v4.1.0 之前的发行版不在支持范围内；它们与当前 API 的差异还不止这一处——3.x 与 1.x 在多个插件相关 API 上都与之不同，而 1.x 更是完全另一套架构。

### 升级到 5.x

Luban 侧有两处变化。它们都不影响本仓库，但可能影响你自己的环境：

- 命令行参数 `--validationFailAsError` 改名为 `--strict`，请检查你的启动脚本；
- `EnvManager.Current`、`GenerationContext.Current` 这类管理器属性在没有活动 `PipelineScope` 时会**抛异常**。通过 `Luban.dll` 运行时永远不会遇到，但以编程方式驱动 Luban 的宿主必须先进入 scope。

## 环境要求

- .NET 8 SDK
- 面向 .NET 8 构建的 Luban（支持 4.1.0 及之后的版本），且安装目录下存在 `Luban.Core.dll`、`NLog.dll` 与 `Luban.deps.json`

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

## 发布

推送一个 tag 到 `master` 即触发发布：

```powershell
git tag v1.2.3
git push origin v1.2.3
```

tag 必须是小写 `v` 加三段以点分隔的数字，且其提交必须能从 `master` 到达。其余情况都不会发布：`v1.2`、`V1.2.3` 这类 tag 根本不会启动流程；格式正确但不在 `master` 上的 tag 会在第一步失败并给出原因。

[`.github/workflows/release.yml`](.github/workflows/release.yml) 随后会：

1. 下载固定版本的 Luban 发行包，并以包含 `Luban.Core.dll` 的目录作为 `LubanDir`；
2. 以 tag 作为程序集版本构建解决方案，并把扩展部署进那份下载下来的 Luban；
3. 用 Luban 跑一遍 [`Luban.ScriptValidator/tests/Fixture`](Luban.ScriptValidator/tests/Fixture)——既断言 Lua 接口的返回结果，也断言路径校验器的判定——这验证的是「扩展在一台干净机器上真的能加载并工作」，而不只是「能编译」；
4. 把扩展 DLL 打包成 zip，附到 GitHub Release 上。

构建所固定的 Luban 版本是工作流顶部的 `LUBAN_VERSION`。它决定了扩展编译时对照的 Luban API，因此要与实际部署扩展的那个 Luban 安装保持一致。

由于发布构建会把 tag 戳进程序集版本，把发布产物部署进一个已登记 `1.0.0.0` 的 Luban 时，`Luban.deps.json` 里会**多出**一条而非替换原有条目。这没有危害——部署器本来就只增不改——但这也是为什么「发布」与「本地部署」混用会在清单里留下多余几行。

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
.github/actions/build-against-luban/  针对单个 Luban 发行版构建并验证
.github/workflows/build.yml           验证全部受支持的 Luban 发行版
.github/workflows/release.yml         由 vX.Y.Z tag 构建并发布 Release
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
