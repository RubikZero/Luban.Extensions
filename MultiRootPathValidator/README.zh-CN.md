# Luban.MultiRootPathValidator

[English](README.md) | **简体中文** | [← 返回仓库说明](../README.zh-CN.md)

针对 Luban 内建 `path` 校验器的即插即用扩展。它保留原有的写法（`path=unity`、`path=normal;...` 等），但允许在多个工程/资源根目录下做校验。

## 原理

Luban 会扫描自身可执行文件所在目录下文件名包含 `Luban` 的 DLL，对其中带 `[assembly: RegisterBehaviour]` 的程序集扫描自定义行为。本扩展以更高优先级注册了另一个名为 `path` 的校验器，因此在不修改 Luban 本体的前提下替换了内建的 `path` 校验器。

在 .NET 8 版本的 Luban 上，扩展还必须登记到 `Luban.deps.json`，否则运行时无法解析这个「按文件名发现」的扩展。共享的 `Luban.Extension.Deployer` 工具会同时完成 DLL 拷贝与清单登记（见 [构建](#构建)）。**不要只手工拷贝 DLL。**

## 环境要求

- 面向 .NET 8 构建的 Luban（已针对 Luban 4.5.0 验证）
- 构建本扩展需要 .NET 8 SDK
- Luban 可执行文件目录下存在 `Luban.Core.dll` 与 `NLog.dll`

## 构建

用 Visual Studio 打开 `Luban.Extensions.sln`，或在仓库根目录执行下面的命令。它会构建解决方案中的全部扩展，并对每个设置了 `IsLubanExtension=true` 的项目执行部署：

```powershell
dotnet build Luban.Extensions.sln -c Release -m:1 -p:DeployLubanExtensions=true
```

`LubanDir` 来自 `Directory.Build.props`，该文件不受 git 跟踪——每台机器用 `Directory.Build.props.example` 生成一次即可，详见[仓库说明](../README.zh-CN.md#初始化)。需要时也可以不修改文件、直接在命令行覆盖：

```powershell
dotnet build Luban.Extensions.sln -c Release -m:1 `
  -p:DeployLubanExtensions=true `
  -p:LubanDir="C:\path\to\luban\Tools\Luban"
```

### 部署产物

部署器只会往 `LubanDir` 写两样东西：扩展 DLL，以及 `Luban.deps.json` 中对应的条目。它**不会**在 `Luban.dll` 旁边生成按扩展命名的 `<Extension>.deps.json`，也从不删除文件。因此，如果某个 Luban 安装目录里存在独立的 `Luban.MultiRootPathValidator.deps.json`，那是更早期手工部署留下的遗留物，可以直接删除；运行时只读 `Luban.deps.json`。

### 新增一个扩展

1. 创建扩展工程，并在其 `.csproj` 中设置 `<IsLubanExtension>true</IsLubanExtension>`。
2. 把它加入 `Luban.Extensions.sln`（Visual Studio 的 *添加 > 现有项目*，或 `dotnet sln Luban.Extensions.sln add <project.csproj>`）。
3. 用 `-p:DeployLubanExtensions=true` 构建解决方案。

共享部署器会接收每个已选择加入的工程输出的 DLL，拷贝到 `LubanDir`，并仅在条目缺失时登记到 `Luban.deps.json`。

输出的 DLL 位于：

```text
bin\Release\net8.0\Luban.MultiRootPathValidator.dll
```

C# 部署器会保持所需的 `Luban.deps.json` 条目同步；当扩展已登记时，它会保持清单逐字节不变。

## 用法

根目录要填「表里存的值相对于哪个目录」。如果表里存的是相对于 Unity 工程的路径：

```text
Assets/UI/Icon/Foo.png
```

那么根目录就填工程目录：

```powershell
dotnet Luban.dll `
  ... `
  -x "pathValidator.rootDirs=C:\path\to\client;C:\path\to\art"
```

因为 `path=unity` 检查的是：

```text
<root>/<field value>
```

所以只要下列任一文件存在就会通过：

```text
C:\path\to\client\Assets\UI\Icon\Foo.png
C:\path\to\art\Assets\UI\Icon\Foo.png
```

如果表里存的是相对于 `Assets` 的路径，则根目录应填各工程的 `Assets` 目录。

已有的 schema / 配置无需改动：

```text
string#path=unity
```

### 向后兼容的选项名

本扩展同时接受 Luban 原有的选项名。只有一个根目录时，行为与内建校验器完全一致：

```powershell
-x "pathValidator.rootDir=C:\path\to\client"
```

也可以用它传多个根目录：

```powershell
-x "pathValidator.rootDir=C:\path\to\client;C:\path\to\art"
```

若同时提供 `rootDirs` 与 `rootDir`，以 `rootDirs` 为准。

## 支持的 path 模式

本扩展保留了内建的全部模式：

- `path=unity`
- `path=unity?`
- `path=normal;<pattern>`
- `path=normal?;<pattern>`
- `path=ue`
- `path=ue?`
- `path=godot`
- `path=godot?`

唯一的语义变化是：只要**任意一个**已配置的根目录下存在对应文件，校验即通过。

## 备注

- 多个根目录之间用 `;` 分隔。在 PowerShell/CMD 中请给整个 `-x` 参数加引号，避免被 shell 解释。
- 空的根目录条目会被忽略。
- 重复的根目录会被去重（Windows 上忽略大小写）。
- 相对路径根目录保持 .NET/Luban 常规的「相对进程当前目录」语义。
- 若未提供 `rootDirs`/`rootDir`，路径校验会被关闭，这与 Luban 内建行为一致。此时只会输出一条警告、运行仍然成功——也就是说，漏配根目录会让所有路径检查被静默跳过。

## 许可说明

本扩展与仓库其余部分一样采用 MIT 许可——见[仓库许可](../LICENSE)。

路径模式的行为改编自 Luban，后者采用 MIT 许可。按照 MIT 的要求，Luban 自己的许可证文本与版权声明与该处代码放在一起，即 `LICENSE.Luban`。
