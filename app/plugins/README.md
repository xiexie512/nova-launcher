# NovaLauncher 插件开发指南（C# / Python / C）

> 📖 完整版开发指南见仓库根 [docs/PLUGIN_DEV.md](../docs/PLUGIN_DEV.md)（含三语言 API 对照表、事件、部署调试与 FAQ）。本文是仓库内示例插件的快捷入口。

插件目录：`~/Library/Application Support/NovaLauncher/plugins/`（macOS，Windows 为 `%APPDATA%\NovaLauncher\plugins`）。
把插件文件放进去，重启启动器（或插件页点「⟳ 重新加载」）即可加载。插件列表会显示语言类型。

| 语言 | 文件 | 说明 |
|---|---|---|
| C# | `*.dll` | 程序集实现 `ILauncherPlugin`，功能最全 |
| Python | `*.py` | 一个 .py 文件即一个插件，可热编辑，无需编译 |
| C/C++ | `*.dylib` / `*.so` / `*.dll` | 按 `nova_plugin.h` ABI 导出的原生动态库 |

---

## Python 插件（推荐入门）

示例：`app/plugins/PythonHello/plugin.py`

```python
NAME = "MyPlugin"          # 元数据（均可省略）
VERSION = "1.0.0"
AUTHOR = "Me"
DESCRIPTION = "我的 Python 插件"

def on_load(api):          # 加载时调用；api 即全局 nova_api
    api.log("插件已加载")
    api.register_home_card({"id": "my-card", "title": "卡片", "body": "主页卡片"})
    api.register_page({"key": "my-page", "title": "页面", "body": "正文",
                       "actions": [{"id": "run", "label": "运行", "icon": "▶"}]})

def on_action(action_id):  # 页面按钮点击
    if action_id == "run":
        nova_api.update_page("my-page", "已运行")

def on_event(event_name, data):  # 启动器事件
    if event_name == "game_launched":
        nova_api.log("游戏启动: " + data.get("versionId", "?"))

def on_unload():           # 卸载前
    pass
```

可用 API（`on_load` 里用参数 `api`，其余函数用全局 `nova_api`）：

| 方法 | 说明 |
|---|---|
| `log(message)` / `notify(title, message)` | 日志 / 通知 |
| `get_setting(key, default="")` / `set_setting(key, value)` | 插件设置（按插件名隔离命名空间） |
| `launcher_home` / `game_root` | 启动器数据目录 / 游戏目录 |
| `register_home_card(card)` | 注册主页卡片 `{id,title,body,icon}` |
| `register_page(page)` | 注册侧栏页面 `{key,title,body,icon,actions[]}` |
| `update_page(key, body)` / `update_home_card(card_id, body)` | 更新正文 |

事件：`game_launching` / `game_launched` / `console_line` / `download_progress`，data 为 JSON 对象。
插件 `print()` 的输出会显示在启动器日志（stderr），不会污染桥接协议。

---

## C 插件

ABI 头文件：`kernel/include/nova_plugin.h`；示例：`app/plugins/CDemo`。

```c
#include "nova_plugin.h"
static const nova_plugin_api* g_api;

const char* nova_plugin_name(void) { return "MyCPlugin"; }
const char* nova_plugin_version(void) { return "1.0.0"; }
const char* nova_plugin_author(void) { return "Me"; }
const char* nova_plugin_description(void) { return "我的 C 插件"; }

int nova_plugin_init(const nova_plugin_api* api) {
    g_api = api;
    api->log("MyCPlugin", "已加载");
    api->register_page("MyCPlugin",
        "{\"key\":\"c-page\",\"title\":\"C 页面\",\"body\":\"正文\","
        "\"actions\":[{\"id\":\"run\",\"label\":\"运行\",\"icon\":\"▶\"}]}");
    return 0;
}

void nova_plugin_action(const char* action_id) {
    if (g_api && strcmp(action_id, "run") == 0)
        g_api->update_page("MyCPlugin", "c-page", "已运行");
}

void nova_plugin_unload(void) { g_api = NULL; }
```

编译（macOS）：

```bash
cd app/plugins/CDemo && ./build_c_plugin.sh
# 或手动: cc -std=c11 -O2 -fPIC -shared -I<repo>/kernel/include src.c -o build/xx.dylib
```

API 回调：`log / notify / get_setting / set_setting / path(0=数据目录,1=游戏目录) /
register_card / register_page / update_page / update_card / subscribe_event`。
`get_setting`/`path` 用"插件提供缓冲区"方式（`char* out, int out_size`），无需跨分配器释放字符串。
事件订阅：`api->subscribe_event("MyCPlugin", "game_launched", cb, user)`，回调中 `data_json` 仅在回调期间有效。

---

## C# 插件

实现 `ILauncherPlugin`（`Name/Version/Author/Description`、`OnLoad(ILauncherApi)`、`OnUnload()`、可选 `OnAction(actionId)`），
参考 `app/plugins/HelloPlugin`、`PyCExample`。API：日志、通知、设置、注册主页卡片/侧栏页面、订阅 `LauncherEvents`。
