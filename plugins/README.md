# NovaLauncher 插件文件夹

本文件夹就是启动器读取并执行的**插件文件夹**。启动器（`PluginService`）扫描插件目录顶层的文件，按扩展名加载并执行：

| 扩展名 | 语言 | 加载方式 |
|---|---|---|
| `*.py` | Python | 子进程 `python3 -u host.py <plugin.py>`，JSON 行协议桥接，可热编辑 |
| `*.dylib` / `*.so` / `*.dll` | C/C++ | 按 `src/nova_plugin.h` ABI 动态加载（Windows 上非托管 `.dll` 自动回退为 C 插件） |
| `*.dll` | C# | 程序集内实现 `ILauncherPlugin` 的插件 |

> 以 `NovaLauncher` / `Avalonia` / `CommunityToolkit` 开头的文件会被跳过（防止误加载自身程序集）。

## 本文件夹内容

```
plugins/
├── README.md        # 本说明
├── plugin.py        # Python 示例插件（直接可加载、可热编辑）
├── c_demo.dylib     # C 示例插件（macOS 已编译产物，直接可加载）
└── src/
    ├── c_demo.c     # C 插件源码
    ├── nova_plugin.h# C 插件 ABI 头文件
    ├── build.sh     # macOS/Linux 一键编译 → ../c_demo.dylib
    └── build.bat    # Windows 一键编译 → ..\c_demo.dll
```

## 启动器如何读取本文件夹

运行时启动器默认读取数据目录的插件文件夹：

- macOS：`~/Library/Application Support/NovaLauncher/plugins/`
- Windows：`%APPDATA%\NovaLauncher\plugins`

三种方式让启动器执行本文件夹里的插件：

1. **环境变量覆盖（开发/调试最直接）**：启动启动器前设置
   ```bash
   NOVA_PLUGINS_DIR=/路径/nova-launcher/plugins  # 启动器直接读取本文件夹
   open /Applications/NovaLauncher.app          # 或直接运行 exe
   ```
2. **一键部署**：把本文件夹内容复制进数据目录插件文件夹
   ```bash
   scripts/deploy_plugins.sh   # macOS/Linux
   # Windows: 手动复制 *.py / *.dll / *.dylib 到 %APPDATA%\NovaLauncher\plugins
   ```
3. **随安装包播种**：发布打包时把本文件夹内容放到可执行文件旁的 `plugins/` 子目录，首次运行自动复制到数据目录（`AppServices.SeedBundledPlugins`）。

部署/覆盖后，在启动器「插件」页点「⟳ 重新加载」即可，无需重启。

## 新增插件

### Python 插件（最简单）

放一个 `.py` 到插件文件夹即可。约定：

```python
NAME = "MyPy"              # 元数据（均可省略）
VERSION = "1.0.0"
AUTHOR = "Me"
DESCRIPTION = "示例"

def on_load(api):          # 加载时调用，api 即全局 nova_api
    api.log("已加载")

def on_action(action_id):  # 插件页面按钮被点击
    pass

def on_event(event_name, data):  # 启动器事件
    pass

def on_unload():
    pass
```

API：`nova_api.log / notify / get_setting / set_setting / launcher_home / game_root / register_home_card / register_page / update_page / update_home_card`。插件 `print()` 会进启动器日志（stderr），不会污染协议流。完整示例见 `plugin.py`。

### C 插件

按 `src/nova_plugin.h` 的 ABI 导出以下符号，编译为 `*.dylib`（macOS）/ `*.dll`（Windows）/ `*.so`（Linux）放进插件文件夹：

- `nova_plugin_meta()`（推荐）或 `nova_plugin_name/version/author/description()`：元数据
- `int nova_plugin_init(const nova_plugin_api* api)`：加载入口，返回 0 表示成功
- `void nova_plugin_action(const char* action_id)`：可选，页面按钮点击
- `void nova_plugin_unload(void)`：可选，卸载回调

编译（macOS / Linux）：

```bash
cd plugins/src && ./build.sh
```

Windows 用 `build.bat`。示例见 `src/c_demo.c`。

### C# 插件

编译为 `*.dll` 放入插件文件夹，实现 `ILauncherPlugin`（见 `app/plugins/HelloPlugin`、`app/plugins/NovaModsLoader`）。
