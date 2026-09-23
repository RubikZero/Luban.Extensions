# Luban.Extension.Deployer

[English](README.md) | **简体中文** | [← 返回仓库说明](../../README.zh-CN.md)

构建期工具：把构建好的 Luban 扩展安装进 Luban 目录并使其可被加载。它通常由 [`Directory.Build.targets`](../../Directory.Build.targets) 调用，而不是手工执行。

## 用法

```text
dotnet run --project tools/Luban.Extension.Deployer -- `
  <extension.dll> <Luban 目录> [dependency.dll ...]
```

对传入的每一个程序集，它会：

1. 把文件拷贝进 Luban 目录（覆盖旧副本）；
2. 在 `Luban.deps.json` 的运行时目标节点与 `libraries` 节点下各插入一条条目——若该条目已存在则跳过。

## 为什么必须动 `Luban.deps.json`

Luban 通过扫描 `Luban.dll` 同级目录下**文件名包含 `Luban`** 的 `*.dll` 来发现插件，并按简单名加载。在 .NET 8 上，这个名字解析要经过 `Luban.deps.json`，所以「被发现但未登记」的程序集无法加载。这也解释了为什么自身名字不含 `Luban` 的依赖（例如 `Luban.ScriptValidator` 用到的 MoonSharp）必须一并传入：扫描永远找不到它。

## 保证

- 只写入被传入的程序集与 `Luban.deps.json`。它不会生成按扩展命名的 `<Extension>.deps.json`，也不会删除任何文件。
- 当所有程序集都已登记时，保持 `Luban.deps.json` **逐字节不变**，因此重复构建不会产生 diff。
- 以文本方式修改清单，并在写回前重新解析校验，因此损坏的修改不会被落盘。
- 失败时输出单行信息并以退出码 1 结束，MSBuild 会把它呈现为普通构建错误。

## 已知限制

- 条目只增不改。若某个扩展以不同的程序集版本重新构建，旧版本键会残留在清单中。
- 清单位置通过 `runtimeTarget.name` 定位；缺少该属性的清单会被直接拒绝，而不是靠猜测处理。
