# NovaLauncher 插件开发指南

NovaLauncher 的插件**不只是占位挂件**——通过 API 可以注册主页卡片、注册侧栏新页面、持久化设置、订阅启动/日志/下载事件，**直接修改整个启动器**。

支持三种语言：

| 语言 | 文件 | 特点 | 适合 |
|---|---|---|---|
| **Python** | 一个 `.py` 文件 | 无需编译、**可热编辑**、即插即用 | 入门、脚本型扩展 |
| **C/C++** | `*.dylib` / `*.so` / `*.dll` | 原生动态库、高性能 | 性能敏感、复用现有 C/C++ 库 |
| **C#** | `*.dll` | 功能最全、直接调用启动器程序集 | 深度集成、复杂插件 |

---

## 1. 插件如何被加载

### 1.1 插件目录

运行时启动器默认读取数据目录下的插件文件夹：

- macOS：`~/Library/Application Support/NovaLauncher/plugins/`
- Windows：`%APPDATA%\NovaLauncher\plugins`
- Linux：`${XDG_DATA_HOME:-~/.local/share}/NovaLauncher/plugins`

`PluginService` 扫描插件目录**顶层文件**，按扩展名分发到对应宿主加载：

| 扩展名 | 语言 | 加载方式 |
|---|---|---|
| `*.py` | Python | 子进程 `python3 -u host.py <plugin.py>`，JSON 行协议桥接 |
| `*.dylib` / `*.so` / `*.dll` | C/C++ | 按 `nova_plugin.h` ABI 动态加载（Windows 非托管 `.dll` 自动回退为 C 插件） |
| `*.dll` | C# | 程序集内实现 `ILauncherPlugin` 的插件 |

> 以 `NovaLauncher` / `Avalonia` / `CommunityToolkit` 开头的文件会被跳过（防止误加载自身程序集）。

### 1.2 让插件生效

- **重启启动器**，或
- 在启动器「插件」页点 **「⟳ 重新加载」**（无需重启）

Python 插件支持**热编辑**：修改 `.py` 后重新加载即可，无需编译。

### 1.3 三种方式使用仓库内插件

```bash
# ① 环境变量覆盖（开发/调试最直接）：启动器直接读取仓库插件文件夹
NOVA_PLUGINS_DIR=/路径/nova-launcher/plugins open /Applications/NovaLauncher.app

# ② 一键部署到数据目录
scripts/deploy_plugins.sh    # 自动部署根 plugins/ + app/plugins/*/bin/Release/*.dll

# ③ 随安装包播种：发布时把插件放到可执行文件旁的 plugins/ 子目录，首次运行自动复制
```

---

## 2. Python 插件

### 2.1 最小示例

```python
NAME = "MyPlugin"          # 元数据（均可省略，缺省用文件名）
VERSION = "1.0.0"
AUTHOR = "Me"
DESCRIPTION = "我的第一个插件"

def on_load(api):          # 加载时调用；api 即全局 nova_api
    api.log("插件已加载")

def on_action(action_id):  # 插件页面按钮被点击
    pass

def on_event(event_name, data):  # 启动器事件
    pass

def on_unload():           # 卸载前调用
    pass
```

完整示例见仓库根 `plugins/plugin.py`（含主页卡片、侧栏页面、操作按钮、事件订阅）与 `app/plugins/PythonHello/plugin.py`。

### 2.2 可用 API

`on_load` 的参数 `api` 与全局 `nova_api` 是同一个对象，其他函数里直接用 `nova_api`：

| 方法 / 属性 | 说明 |
|---|---|
| `log(message)` | 写入启动器日志 |
| `notify(title, message)` | 发送系统通知 |
| `get_setting(key, default="")` | 读取插件设置（按插件名隔离命名空间） |
| `set_setting(key, value)` | 保存插件设置（持久化） |
| `launcher_home` | 启动器数据目录（属性） |
| `game_root` | 游戏目录（属性） |
| `register_home_card(card)` | 注册主页卡片 `{"id","title","body","icon"}` |
| `register_page(page)` | 注册侧栏页面 `{"key","title","body","icon","actions":[{id,label,icon}]}` |
| `update_page(key, body)` | 更新已注册页面的正文（实时刷新） |
| `update_home_card(card_id, body)` | 更新主页卡片正文 |

### 2.3 事件

`on_event(event_name, data)`，`data` 为 JSON 对象：

| 事件 | data 字段 |
|---|---|
| `game_launching` | `versionId` / `accountName` / `memoryMb` / `gameDir` |
| `game_launched` | 同上 |
| `console_line` | `line`（游戏控制台输出） |
| `download_progress` | `key` / `downloaded` / `total` / `done` |

### 2.4 约定与技巧

- 插件 `print()` 的输出会进启动器日志（stderr），**不会污染协议流**
- 插件内的异常会被捕获并回传启动器日志，不会让启动器崩溃
- 可以在插件里 `import` 任意 Python 标准库或已安装的第三方库

---

## 3. C/C++ 插件

### 3.1 ABI 总览

ABI 头文件：`kernel/include/nova_plugin.h`（版本 `NOVA_PLUGIN_ABI_VERSION 1`）。

**插件必须导出的符号：**

| 符号 | 说明 |
|---|---|
| `nova_plugin_meta()` | 返回 `nova_plugin_info*`（推荐），未导出时回退到 `name/version/author/description()` |
| `int nova_plugin_init(const nova_plugin_api* api)` | 加载入口，**返回 0 表示成功** |
| `void nova_plugin_action(const char* action_id)` | 可选，页面按钮点击 |
| `void nova_plugin_unload(void)` | 可选，卸载回调 |

**加载流程：** 读元数据 → `nova_plugin_init(&api)`（此后可调用 `api->log/register_page` 等）→ 按钮点击时 `nova_plugin_action` → 卸载时 `nova_plugin_unload`。

### 3.2 最小示例

```c
#include "nova_plugin.h"
#include <string.h>

static const nova_plugin_api* g_api;

const char* nova_plugin_name(void)    { return "MyCPlugin"; }
const char* nova_plugin_version(void) { return "1.0.0"; }
const char* nova_plugin_author(void)  { return "Me"; }
const char* nova_plugin_description(void) { return "我的 C 插件"; }

int nova_plugin_init(const nova_plugin_api* api) {
    g_api = api;
    api->log("MyCPlugin", "已加载");
    api->register_page("MyCPlugin",
        "{\"key\":\"c-page\",\"title\":\"C 页面\",\"body\":\"正文\","
        "\"actions\":[{\"id\":\"run\",\"label\":\"运行\",\"icon\":\"▶\"}]}");
    return 0;  // 0 = 成功
}

void nova_plugin_action(const char* action_id) {
    if (g_api && strcmp(action_id, "run") == 0)
        g_api->update_page("MyCPlugin", "c-page", "已运行");
}

void nova_plugin_unload(void) { g_api = NULL; }
```

完整示例：仓库根 `plugins/src/c_demo.c`（含卡片、页面、事件订阅、动作分发）。

### 3.3 编译

```bash
# macOS / Linux（仓库根 plugins/ 的示例）
cd plugins/src && ./build.sh          # 产物 ../c_demo.dylib

# 任意 C 插件（使用官方 ABI 头）
cc -std=c11 -O2 -fPIC -shared -I<repo>/kernel/include src.c -o build/my_plugin.dylib

# Windows（示例用 build.bat，需 mingw 的 cc 在 PATH）
cd app/plugins/CDemo && ./build_c_plugin.sh   # 产物 build/c_demo.dll
```

### 3.4 API 回调（`nova_plugin_api`）

| 回调 | 签名 | 说明 |
|---|---|---|
| `log` | `(plugin, message)` | 写日志 |
| `notify` | `(plugin, title, message)` | 通知 |
| `get_setting` | `(plugin, key, default, out, out_size)` | 读设置，成功返回 1，值写入 `out` |
| `set_setting` | `(plugin, key, value)` | 写设置 |
| `path` | `(plugin, which, out, out_size)` | `which` 0=数据目录，1=游戏目录；成功返回 1 |
| `register_card` | `(plugin, card_json)` | 注册主页卡片（JSON） |
| `register_page` | `(plugin, page_json)` | 注册侧栏页面（JSON，含 actions） |
| `update_page` | `(plugin, key, body)` | 更新页面正文 |
| `update_card` | `(plugin, card_id, body)` | 更新卡片正文 |
| `subscribe_event` | `(plugin, event_name, cb, user)` | 订阅事件（回调 `data_json` 仅在回调期间有效，需要时自行拷贝） |

**注意：** `get_setting` / `path` 采用"调用方提供缓冲区"方式（`char* out, int out_size`），无需跨分配器释放字符串。所有回调均可随时调用。

---

## 4. C# 插件

### 4.1 最小示例

引用 `NovaLauncher.PluginApi.dll`，实现 `ILauncherPlugin`：

```csharp
using NovaLauncher.PluginApi;

public sealed class MyPlugin : ILauncherPlugin
{
    public string Name => "我的插件";
    public string Version => "1.0.0";
    public string Author => "你";
    public string Description => "插件描述";

    public void OnLoad(ILauncherApi api)
    {
        api.Log("插件已加载");

        // 订阅启动事件
        LauncherEvents.GameLaunching += info => api.Log($"启动 {info.VersionId}");
        LauncherEvents.GameLaunched += info => api.Notify("启动成功", info.VersionId);
        LauncherEvents.ConsoleLine += line => { /* 观察游戏日志 */ };
        LauncherEvents.DownloadProgress += d => { /* 下载进度 */ };
    }

    public void OnUnload() { }

    // 可选：插件页面操作按钮
    public void OnAction(string actionId) { }
}
```

完整示例：`app/plugins/HelloPlugin/HelloPlugin.cs`（卡片+页面+事件+设置计数）、`app/plugins/LaunchNotifier`、`app/plugins/NovaModsLoader`（4 大加载器 + Modrinth 下载站）、`app/plugins/PyCExample`（C# 内嵌 Python+C 执行器）。

### 4.2 工程引用

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../NovaLauncher.PluginApi/NovaLauncher.PluginApi.csproj" />
  </ItemGroup>
</Project>
```

编译：`dotnet build MyPlugin.csproj -c Release`，把 `bin/Release/net8.0/MyPlugin.dll` 放进插件目录。

### 4.3 API（`ILauncherApi`）

| 成员 | 说明 |
|---|---|
| `Log(message)` / `Notify(title, message)` | 日志 / 通知 |
| `GetSetting(key, default="")` / `SetSetting(key, value)` | 插件设置（按插件名隔离命名空间） |
| `LauncherHome` / `GameRoot` | 启动器数据目录 / 游戏目录 |
| `RegisterHomeCard(HomeCardInfo)` | 注册主页卡片（`Id/Title/Body/Icon`） |
| `RegisterPage(PluginPageInfo)` | 注册侧栏页面（`Key/Title/Body/Icon/Actions`） |
| `UpdatePage(key, body)` / `UpdateHomeCard(cardId, body)` | 更新正文（实时刷新） |
| `SetPluginName(name)` | 供 Python/C 宿主更新插件名 |

### 4.4 事件总线（`LauncherEvents`）

| 事件 | 参数 |
|---|---|
| `GameLaunching` | `LaunchInfo{VersionId, AccountName, MemoryMb, GameDir}` |
| `GameLaunched` | `LaunchInfo` |
| `ConsoleLine` | `string` |
| `DownloadProgress` | `DownloadInfo{Key, Downloaded, Total, Done}` |

---

## 5. 三语言 API 对照

| 能力 | Python | C | C# |
|---|---|---|---|
| 日志 | `api.log(msg)` | `api->log(name, msg)` | `api.Log(msg)` |
| 通知 | `api.notify(title, msg)` | `api->notify(name, title, msg)` | `api.Notify(title, msg)` |
| 读设置 | `api.get_setting(key, d="")` | `api->get_setting(name, key, d, out, sz)` | `api.GetSetting(key, d="")` |
| 写设置 | `api.set_setting(key, v)` | `api->set_setting(name, key, v)` | `api.SetSetting(key, v)` |
| 数据目录 | `api.launcher_home` | `api->path(name, 0, out, sz)` | `api.LauncherHome` |
| 游戏目录 | `api.game_root` | `api->path(name, 1, out, sz)` | `api.GameRoot` |
| 主页卡片 | `api.register_home_card({...})` | `api->register_card(name, json)` | `api.RegisterHomeCard(info)` |
| 侧栏页面 | `api.register_page({...})` | `api->register_page(name, json)` | `api.RegisterPage(info)` |
| 更新页面 | `api.update_page(key, body)` | `api->update_page(name, key, body)` | `api.UpdatePage(key, body)` |
| 更新卡片 | `api.update_home_card(id, body)` | `api->update_card(name, id, body)` | `api.UpdateHomeCard(id, body)` |
| 订阅事件 | `on_event(name, data)` | `api->subscribe_event(name, ev, cb, user)` | `LauncherEvents.X += ...` |
| 页面按钮 | `on_action(action_id)` | `nova_plugin_action(action_id)` | `OnAction(actionId)` |
| 卸载 | `on_unload()` | `nova_plugin_unload()` | `OnUnload()` |

---

## 6. 设置持久化与命名空间

- 所有语言的 `get_setting` / `set_setting` 都会按 **「插件名.key」** 自动命名空间隔离，不同插件互不干扰
- 设置保存在启动器数据目录的 `settings.json`，重启后仍保留
- 插件名来自元数据（Python `NAME` / C `nova_plugin_name` / C# `Name`）；Python/C 宿主在元数据就绪后可通过 `SetPluginName` 更新

---

## 7. 部署与调试

### 调试小贴士

1. **开发期**：用 `NOVA_PLUGINS_DIR` 指向仓库插件文件夹，改完直接「重新加载」
2. **看日志**：数据目录 `logs/` 下的启动器日志；插件 `log()`/`print()` 都会出现在这里
3. **Python 报错**：异常 traceback 会回传启动器日志，按提示修改即可；Python 插件可热编辑，无需重编译
4. **C 插件崩溃**：确认 ABI 头版本一致、`nova_plugin_init` 返回 0、缓冲区长度的 `out_size` 传够

### 一键部署

```bash
scripts/deploy_plugins.sh
# 自动部署：根 plugins/ 的 .py/.dylib/.so/.dll + app/plugins/*/bin/Release/net8.0/*.dll
```

---

## 8. 示例插件索引

| 示例 | 语言 | 位置 | 亮点 |
|---|---|---|---|
| PythonHello | Python | `app/plugins/PythonHello/plugin.py` | 卡片/页面/动作/事件全演示 |
| plugin.py | Python | `plugins/plugin.py` | 仓库插件文件夹版，即插即用 |
| CDemo | C | `app/plugins/CDemo/`、`plugins/src/c_demo.c` | 原生 ABI 全演示 + 一键编译脚本 |
| HelloPlugin | C# | `app/plugins/HelloPlugin/` | 入门：卡片+页面+事件+设置计数 |
| LaunchNotifier | C# | `app/plugins/LaunchNotifier/` | 启动事件通知 |
| NovaModsLoader | C# | `app/plugins/NovaModsLoader/` | 实战：Fabric/Quilt/Forge/NeoForge 加载器 + Modrinth 下载 |
| PyCExample | C# | `app/plugins/PyCExample/` | 高级：C# 内嵌 Python+C 执行器 |

---

## 9. 常见问题

**Q：插件目录放好了，启动器里看不到？**
A：点插件页「⟳ 重新加载」；确认文件在插件目录顶层（不递归子目录）；确认文件名不以 `NovaLauncher/Avalonia/CommunityToolkit` 开头。

**Q：Python 插件能装第三方库吗？**
A：可以，插件以子进程运行，可直接 `import` 系统 Python 环境已安装的包。

**Q：C 插件怎么在 Windows 上编译？**
A：需要 mingw 的 `cc`，示例见 `app/plugins/CDemo/build_c_plugin.sh`（MINGW/MSYS/CYGWIN 分支产出 `.dll`）。

**Q：如何让插件显示中文/emoji 图标？**
A：卡片 `icon` / 页面 `icon` 支持 emoji 与文本，直接填即可（如 `"🧩"`）。
