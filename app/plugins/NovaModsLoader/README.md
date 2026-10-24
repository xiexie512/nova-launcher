# NovaModsLoader —— Nova 启动器「模组加载器」插件

基于 Nova 启动器插件开发库（`ILauncherPlugin` / `ILauncherApi`）构建的加载器插件。
放入启动器 `plugins` 目录即可加载，在侧栏出现「模组加载器」页面。

## 能力

| 能力 | 说明 |
|---|---|
| **4 大模组加载器** | Fabric、Quilt（官方 Meta API 生成版本 JSON，最稳）；Forge、NeoForge（官方 installer 无头安装）。多版本兼容。 |
| **Modrinth 下载** | 原版官方 `api.modrinth.com/v2`：搜索 + 下载模组 .jar 到实例 mods 目录。 |
| **反编译启动器** | 反射分析 Nova 启动器结构（服务/视图模型/启动流水线/插件接口面），导出 Markdown 报告到 `exports/`。 |

## 使用（GUI）

1. 启动 Nova，进入侧栏「**模组加载器**」页。
2. 默认 MC 版本 1.21.1（可在设置改插件键 `NovaModsLoader.mcVersion`）。
3. 点「安装 Fabric / Quilt / Forge / NeoForge」→ 生成对应加载器版本（如 `fabric-loader-1.21.1`）。
4. 到「版本」页选择该版本启动，Nova 会自动下载全部库。
5. 点「Modrinth 搜索模组」→ 列出结果并记住首个 slug；改 `NovaModsLoader.modSlug` 可换模组，再点「下载上次搜索的模组」下载到 `versions/<fabric-loader-<mc>>/instance/mods/`。
6. 点「反编译启动器」→ 报告写到 `~/Library/Application Support/NovaLauncher/exports/nova-launcher-decompile.md`。

## 可配置插件键（设置页或 settings.json 的 PluginSettings）

| 键 | 默认 | 含义 |
|---|---|---|
| `NovaModsLoader.mcVersion` | `1.21.1` | 目标 MC 版本 |
| `NovaModsLoader.modQuery` | `popular` | Modrinth 搜索关键词 |
| `NovaModsLoader.modSlug` | （自动） | 待下载模组 slug |
| `NovaModsLoader.modsInstance` | `fabric-loader-<mc>` | 模组下载到的实例目录 |

## 代码结构

```
app/plugins/NovaModsLoader/
├── NovaModsLoader.cs      # 插件入口：页面/卡片、后台任务队列、按钮分发
├── LoaderInstaller.cs     # 4 加载器安装（Fabric/Quilt Meta API；Forge/NeoForge 官方 installer）
├── ModrinthApi.cs         # 原版 Modrinth API 搜索 + 下载
└── LauncherAnalyzer.cs    # 反编译（反射分析启动器）
```

依赖的启动器扩展（插件按钮 + 页面实时更新）：
- `app/NovaLauncher.PluginApi/` —— `ILauncherPlugin.OnAction`（默认接口方法）、`ILauncherApi.UpdatePage/UpdateHomeCard`、`PluginPageInfo.Actions`。
- `app/NovaLauncher/Services/PluginService.cs` —— 页面/卡片归属与按钮回调路由。
- `app/NovaLauncher/Views/PluginPageView.axaml` —— 操作按钮渲染。

## 验证

- 端到端测试：`app/tests/NovaModsLoaderTest`（模拟 PluginService 加载 → 触发动作 → 校验产物）。
  Fabric 版本 JSON 含 fabricmc mainClass + 103 库、Quilt 含 quiltmc + 108 库、Modrinth 搜索返回 12 条、反编译导出 168 类型报告 —— 全部通过。
- Forge/NeoForge 采用官方 installer 无头安装（`--installClient`），依赖网络访问对应 maven 主机。
