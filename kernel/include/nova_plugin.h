/*
 * NovaLauncher 插件 ABI v1 —— C/C++ 插件动态库接口
 * 把编译产物放入启动器插件目录（macOS: *.dylib，Windows: *.dll，Linux: *.so）即可被加载。
 *
 * 加载流程：
 *   1. 读取元数据：nova_plugin_meta()（推荐）；未导出时回退 name/version/author/description()
 *   2. 调用 nova_plugin_init(&api)；返回 0 表示加载成功
 *   3. 页面操作按钮被点击时调用 nova_plugin_action(action_id)（可选导出）
 *   4. 卸载时调用 nova_plugin_unload()（可选导出）
 *
 * 注意：
 *   - 所有回调（log/notify/...）均可随时调用；get_setting/path 采用"调用方提供缓冲区"方式，
 *     无需跨分配器释放字符串。
 *   - subscribe_event 回调中 data_json 仅在回调期间有效，需要时请自行拷贝。
 */
#ifndef NOVA_PLUGIN_H
#define NOVA_PLUGIN_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define NOVA_PLUGIN_ABI_VERSION 1

/* ---------- 回调：插件 -> 启动器（由插件在运行时调用） ---------- */

typedef void (*nova_p_log_fn)(const char* plugin, const char* message);
typedef void (*nova_p_notify_fn)(const char* plugin, const char* title, const char* message);

/* 读取插件设置。key 自动按 "插件名.key" 命名空间隔离。
   成功返回 1 并把值写入 out；失败返回 0。 */
typedef int (*nova_p_get_setting_fn)(const char* plugin, const char* key,
                                     const char* default_value, char* out, int out_size);
typedef void (*nova_p_set_setting_fn)(const char* plugin, const char* key, const char* value);

/* which: 0=启动器数据目录(LauncherHome)，1=游戏目录(GameRoot)。成功返回 1。 */
typedef int (*nova_p_path_fn)(const char* plugin, int which, char* out, int out_size);

/* 注册主页卡片 / 侧栏页面。card_json / page_json 为 JSON 字符串：
   card: {"id":"...","title":"...","body":"...","icon":"..."}
   page: {"key":"...","title":"...","body":"...","icon":"...",
          "actions":[{"id":"..","label":"..","icon":".."}]} */
typedef void (*nova_p_register_card_fn)(const char* plugin, const char* card_json);
typedef void (*nova_p_register_page_fn)(const char* plugin, const char* page_json);
typedef void (*nova_p_update_page_fn)(const char* plugin, const char* key, const char* body);
typedef void (*nova_p_update_card_fn)(const char* plugin, const char* card_id, const char* body);

/* 订阅启动器事件（game_launching / game_launched / console_line / download_progress）。 */
typedef void (*nova_p_event_cb)(const char* event_name, const char* data_json, void* user);
typedef void (*nova_p_subscribe_event_fn)(const char* plugin, const char* event_name,
                                          nova_p_event_cb cb, void* user);

typedef struct nova_plugin_api {
    int api_version;
    const char* launcher_version;
    nova_p_log_fn            log;
    nova_p_notify_fn         notify;
    nova_p_get_setting_fn    get_setting;
    nova_p_set_setting_fn    set_setting;
    nova_p_path_fn           path;
    nova_p_register_card_fn  register_card;
    nova_p_register_page_fn  register_page;
    nova_p_update_page_fn    update_page;
    nova_p_update_card_fn    update_card;
    nova_p_subscribe_event_fn subscribe_event;
} nova_plugin_api;

/* ---------- 插件必须导出 ---------- */

/* 元数据（可选，但推荐）：返回指向静态结构体的指针，调用方只读不释放 */
typedef struct nova_plugin_info {
    const char* name;
    const char* version;
    const char* author;
    const char* description;
} nova_plugin_info;
const nova_plugin_info* nova_plugin_meta(void);

/* 元数据回退导出（nova_plugin_info 未提供时使用），也可同时提供 */
const char* nova_plugin_name(void);
const char* nova_plugin_version(void);
const char* nova_plugin_author(void);
const char* nova_plugin_description(void);

/* 加载：返回 0 表示成功（此后可调用 log/register_page 等 API） */
int nova_plugin_init(const nova_plugin_api* api);

/* 页面操作按钮被点击（action_id 来自 register_page 的 actions[].id），可选 */
void nova_plugin_action(const char* action_id);

/* 卸载前回调，可选 */
void nova_plugin_unload(void);

#ifdef __cplusplus
}
#endif
#endif /* NOVA_PLUGIN_H */
