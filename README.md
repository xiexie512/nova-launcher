<p align="center">
  <img src="assets/icon_1024.png" width="120" alt="NovaLauncher" />
</p>

# NovaLauncher · 我的世界启动器

**自研 C 高性能内核 + Avalonia 跨平台界面的 Minecraft 启动器**，支持 macOS / Windows 双平台。

内置**三语言插件系统**（C# / Python / C），插件不只是"挂件"——可以注册主页卡片、新增侧栏页面、订阅启动/日志/下载事件，**直接修改整个启动器**。

---

## ✨ 功能特性

| 模块 | 说明 |
|---|---|
| ⚡ 自研 C 内核 | libcurl / WinHTTP 双后端，多线程并行下载，SHA-1 校验秒过；已装版本校验通过时跳过下载，热启动更快 |
| 🌐 多源聚合下载 | Mojang 官方源 + BMCLAPI 镜像源，双源并行、自动切换兜底；断点续传 |
| 🔑 三种登录 | 微软正版（设备码流程）、Yggdrasil 外置登录（LittleSkin 等）、离线（UUID v3） |
| 👤 多账户 | 多账户切换，各自独立的皮肤 / 披风配置 |
| 📦 整合包 | `.mrpack`（Modrinth）一键导入、CurseForge zip 导入、导出为 zip |
| 🌍 虚拟局域网 | 集成 ZeroTier——应用内检测引擎、一键加入/离开网络、显示节点与虚拟 IP，一键复制地址跨公网联机；**安装包内置官方安装程序，离线可用** |
| 🃏 自定义主页 | 所有功能均为可自由拖动的卡片（拖动摆放、自动保存、重置布局），区块可开关 |
| 🧩 插件系统 | 三种语言插件，可注册主页卡片 / 侧栏新页面 / 持久化设置 / 订阅事件，修改整个启动器 |
| 🤖 AI Nova | 任意 OpenAI 兼容接口（DeepSeek / OpenAI / Ollama），实验室页可用 |
| 🧪 实验室 | 投影预览 / 存档同步 / 开放联机核心导入 SDK（陶瓦联机等） |

---

## 🚀 获取安装包

源码仓库不含编译产物，安装包请从 **Releases** 下载（或本地 `dist/` 目录）：

| 平台 | 安装包 | 说明 |
|---|---|---|
| macOS（Apple 芯片） | `NovaLauncher-1.0.0-arm64.dmg` | 双击 dmg → 拖入「应用程序」 |
| Windows 10/11 x64 | `NovaLauncher-win-x64-Setup.exe` | 单文件绿色版，双击即用（自包含 .NET 运行时） |
| Windows 便携版 | `NovaLauncher-win-x64-portable.zip` | 解压后双击 `NovaLauncher.exe` |

> macOS 首次打开提示「无法验证开发者」：右键点 App → 打开 → 确认（ad-hoc 签名）。
> Windows 若提示 SmartScreen：点「更多信息」→「仍要运行」。

---

## 🧩 插件开发（重点）

**给其他人开发插件？看这里：**

| 语言 | 文件 | 特点 |
|---|---|---|
| **Python** | 一个 `.py` 文件 | 无需编译，**可热编辑**，最适合入门 |
| **C/C++** | `*.dylib` / `*.so` / `*.dll` | 原生性能，按 `kernel/include/nova_plugin.h` ABI 导出 |
| **C#** | `*.dll` | 功能最全，实现 `ILauncherPlugin` |

- 📖 **完整开发指南** → [docs/PLUGIN_DEV.md](docs/PLUGIN_DEV.md)（API 对照表、事件、三语言示例、部署调试）
- 📖 插件文件夹说明 → [plugins/README.md](plugins/README.md)
- 📖 仓库内示例插件总览 → [app/plugins/README.md](app/plugins/README.md)
- ✍️ **示例插件**：
  - 仓库根 `plugins/`：`plugin.py`（Python 即插即用）+ `c_demo.dylib`（C 已编译产物）及 `src/` 源码
  - `app/plugins/HelloPlugin`（C#）、`app/plugins/LaunchNotifier`（C#）、`app/plugins/NovaModsLoader`（C#，4 大加载器 + Modrinth）、`app/plugins/PyCExample`（C# 内嵌 Python+C）、`app/plugins/PythonHello`（Python）、`app/plugins/CDemo`（C）

**一分钟上手（以 Python 为例）：**

```python
NAME = "MyPlugin"
VERSION = "1.0.0"
AUTHOR = "Me"
DESCRIPTION = "我的第一个插件"

def on_load(api):
    api.log("插件已加载")
    api.register_home_card({"id": "my-card", "title": "卡片", "body": "主页卡片"})
    api.register_page({"key": "my-page", "title": "页面", "body": "正文",
                       "actions": [{"id": "run", "label": "运行", "icon": "▶"}]})

def on_action(action_id):
    if action_id == "run":
        nova_api.update_page("my-page", "已运行")

def on_event(event_name, data):
    if event_name == "game_launched":
        nova_api.log("游戏启动: " + data.get("versionId", "?"))
```

把文件放进插件目录（macOS `~/Library/Application Support/NovaLauncher/plugins/`，Windows `%APPDATA%\NovaLauncher\plugins`），在插件页点「⟳ 重新加载」即可生效。

开发调试时最直接的方式——**让启动器直接读取仓库插件文件夹**：

```bash
NOVA_PLUGINS_DIR=/路径/nova-launcher/plugins open /Applications/NovaLauncher.app
# 或一键部署到数据目录：
scripts/deploy_plugins.sh
```

---

## 🛠️ 从源码构建

### 环境要求

| 依赖 | 说明 |
|---|---|
| macOS | Xcode Command Line Tools（clang）、Homebrew + `libcurl`、.NET 8 SDK |
| Windows 交叉产出 | macOS 上执行 `scripts/package_win.sh` 可直接交叉产出 Windows 安装包 |

> 若默认 `dotnet` 版本低于 8，请使用 `~/.dotnet/dotnet`（8.x）。

### 一键构建（推荐）

```bash
./scripts/build_all.sh
```

依次完成：编译 C 内核（mac + win）→ 构建主程序 → 编译全部示例插件 → 跑全量测试 → 打包 macOS dmg → 打包 Windows 便携版/单文件版。产物输出到 `dist/`。

### 分步构建

```bash
# 1. C 内核（macOS）
cd kernel && ./build_mac.sh          # 产物 build/libnova_kernel.dylib + 跑单元测试
bash kernel/build_win.sh             # Windows 内核（macOS 上交叉编译）

# 2. 主程序（Avalonia UI + 多语言插件宿主）
cd app && dotnet build NovaLauncher/NovaLauncher.csproj -c Release

# 3. 示例插件
dotnet build plugins/HelloPlugin/HelloPlugin.csproj -c Release
bash app/plugins/CDemo/build_c_plugin.sh
```

### 测试

```bash
cd app
dotnet run --project tests/PluginHostingTest/PluginHostingTest.csproj   # 插件加载器（Python 桥 + C ABI）
dotnet run --project tests/LogRecursionTest/LogRecursionTest.csproj
dotnet run --project tests/NovaModsLoaderTest/NovaModsLoaderTest.csproj
NOVA_KERNEL_LIB="$PWD/../kernel/build/libnova_kernel.dylib" \
  dotnet run --project tests/PycSmoke/PycSmoke.csproj
```

---

## 📁 目录结构

```
nova-launcher/
├── app/                    # .NET 8 / Avalonia 主程序
│   ├── NovaLauncher/       # 主程序（UI / Services / ViewModels / Views）
│   ├── NovaLauncher.PluginApi/    # 插件 API（ILauncherPlugin / ILauncherApi / LauncherEvents）
│   ├── NovaLauncher.PluginHosting/ # 多语言插件宿主（Python 桥 host.py / C 宿主）
│   ├── plugins/            # C# 示例插件（HelloPlugin / LaunchNotifier / NovaModsLoader …）
│   └── tests/              # 插件加载 / 日志 / 加载器 / PyC 冒烟测试
├── kernel/                 # 自研 C 内核（HTTP / 下载 / JSON / 登录 / 启动参数）
│   ├── include/            # nova_kernel.h / nova_plugin.h（C 插件 ABI）
│   ├── src/ tests/
│   ├── build_mac.sh        # macOS 内核构建 + 单元测试
│   └── build_win.sh        # Windows 内核交叉编译
├── plugins/                # 开箱即用的插件文件夹（Python + C 示例）
├── scripts/                # 一键构建 / 打包 / 部署插件脚本
├── assets/                 # 图标 / 内置 ZeroTier 安装包
├── docs/                   # 开发文档（插件开发指南）
└── dist/                   # 构建产物（安装包，不入库）
```

---

## 🧪 常见问题

- **微软登录没反应**：设置页需先填「微软 Client ID」（Azure 应用注册的应用程序客户端 ID）。
- **下载慢**：勾选两个下载源（Mojang + BMCLAPI）并调大并发数（默认 8）。
- **Java 报错**：设置页「下载 Java 21」（Temurin），或手动指定已装 Java 路径。
- **想参与开发？** 见 [CONTRIBUTING.md](CONTRIBUTING.md)。

---

## 📄 许可证

[MIT](LICENSE)
