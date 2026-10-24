# 贡献指南

感谢你对 NovaLauncher 感兴趣！无论你是想**开发插件**、修 bug 还是加功能，都欢迎。

## 🧩 只想开发插件？

不用改主程序代码——直接看 [docs/PLUGIN_DEV.md](docs/PLUGIN_DEV.md) 和仓库内示例插件，把 `.py` / `.dylib` / `.dll` 放进启动器插件目录即可。写好的插件可以分享给社区，也可以在 README 的示例列表里加上你的作品。

## 🐛 报告问题

提 Issue 时请包含：

1. **平台与版本**：macOS / Windows、启动器版本号（插件页可看）
2. **复现步骤**：做了什么、期望什么、实际发生了什么
3. **日志**：数据目录 `logs/` 下的启动器与控制台日志（可脱敏后贴出）

如果是**插件问题**，请注明插件名称与语言类型（Python / C / C#）。

## 💻 开发环境

```bash
# 前置：macOS（clang + libcurl + .NET 8 SDK）、Windows（.NET 8 SDK）
./scripts/build_all.sh   # 内核 → 主程序 → 插件 → 测试 → 双平台安装包
```

详细步骤见 [README.md](README.md#从源码构建)。修改代码后请跑：

```bash
cd app
dotnet run --project tests/PluginHostingTest/PluginHostingTest.csproj
dotnet run --project tests/LogRecursionTest/LogRecursionTest.csproj
dotnet run --project tests/NovaModsLoaderTest/NovaModsLoaderTest.csproj
```

## 🔀 提交 PR

1. Fork 本仓库，新建分支（`feat/xxx` 或 `fix/xxx`）
2. 保持改动聚焦；涉及插件 API 的改动需同步更新 `docs/PLUGIN_DEV.md` 与示例插件
3. 本地跑通全量测试，描述里写明改动与验证结果
4. PR 标题用中文或英文均可，建议格式：`feat: 添加 xxx` / `fix: 修复 xxx`

## ✅ 代码约定

- **C#**：遵循 .NET 命名规范（类型 `PascalCase`、局部变量 `camelCase`），`Nullable enable`
- **C 内核**：`nova_` 前缀、头文件注释保留 ABI 说明；新增导出需同步更新 `nova_plugin.h` / `nova_kernel.h`
- **插件**：元数据（NAME/VERSION/AUTHOR/DESCRIPTION）必填，公共 API 行为向后兼容
- 不做破坏性改动；如需破坏，先开 Issue 讨论
