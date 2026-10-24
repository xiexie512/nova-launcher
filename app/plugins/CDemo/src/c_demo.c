/*
 * CDemo —— NovaLauncher C 插件示例
 *
 * 展示：元数据导出、init 回调、注册页面/卡片、订阅事件、动作分发、卸载。
 *
 * 编译（macOS，产物 build/c_demo.dylib）：
 *   ./build_c_plugin.sh
 * 或手动：
 *   cc -std=c11 -O2 -fPIC -shared -I<repo>/kernel/include src/c_demo.c -o build/c_demo.dylib
 */
#include "nova_plugin.h"
#include <stdio.h>
#include <string.h>

static const nova_plugin_api* g_api = NULL;
static int g_runs = 0;

const char* nova_plugin_name(void) { return "CDemo"; }
const char* nova_plugin_version(void) { return "1.0.0"; }
const char* nova_plugin_author(void) { return "Nova"; }
const char* nova_plugin_description(void) {
    return "C 插件示例：原生动态库加载、动作分发、事件订阅";
}

/* 事件回调：data_json 仅在回调期间有效 */
static void on_launched(const char* event_name, const char* data_json, void* user) {
    (void)user;
    if (!g_api) return;
    char buf[256];
    snprintf(buf, sizeof(buf), "C 插件收到事件 %s -> %s", event_name, data_json);
    g_api->log("CDemo", buf);
}

int nova_plugin_init(const nova_plugin_api* api) {
    g_api = api;
    char buf[256];
    snprintf(buf, sizeof(buf), "C 插件已加载（launcher %s）",
             api->launcher_version ? api->launcher_version : "?");
    api->log("CDemo", buf);
    api->notify("CDemo", "CDemo", "C 插件已加载");

    api->register_card("CDemo",
        "{\"id\":\"c-demo-card\",\"title\":\"CDemo\",\"icon\":\"⚙️\","
        "\"body\":\"由 C 插件注册的主页卡片\"}");
    api->register_page("CDemo",
        "{\"key\":\"c-demo\",\"title\":\"C 示例\",\"icon\":\"⚙️\","
        "\"body\":\"C 插件页面：点击下方按钮测试。\","
        "\"actions\":[{\"id\":\"run\",\"label\":\"执行一次\",\"icon\":\"▶\"},"
        "{\"id\":\"home\",\"label\":\"显示数据目录\",\"icon\":\"📁\"}]}");
    api->subscribe_event("CDemo", "game_launched", on_launched, NULL);
    return 0;
}

void nova_plugin_action(const char* action_id) {
    if (!g_api) return;
    char buf[256];
    if (strcmp(action_id, "run") == 0) {
        g_runs++;
        snprintf(buf, sizeof(buf), "C 插件已执行 %d 次", g_runs);
        g_api->update_page("CDemo", "c-demo", buf);
    } else if (strcmp(action_id, "home") == 0) {
        char home[1024];
        if (g_api->path("CDemo", 0, home, sizeof(home)) > 0) {
            snprintf(buf, sizeof(buf), "数据目录：%s", home);
            g_api->update_page("CDemo", "c-demo", buf);
        }
    }
}

void nova_plugin_unload(void) {
    if (g_api) g_api->log("CDemo", "C 插件已卸载");
    g_api = NULL;
}
