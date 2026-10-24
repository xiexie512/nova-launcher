# -*- coding: utf-8 -*-
# plugin.py —— NovaLauncher Python 插件示例（放入插件文件夹即可被启动器加载）
#
# 约定：
#   NAME / VERSION / AUTHOR / DESCRIPTION   模块级元数据（可省略）
#   on_load(api)                            加载时调用；api 即全局 nova_api
#   on_action(action_id)                    插件页面操作按钮被点击
#   on_event(event_name, data)              启动器事件（game_launching/game_launched/
#                                           console_line/download_progress）
#   on_unload()                             卸载前调用
#
# 其他函数里用全局 nova_api 调用启动器能力（log/notify/设置/页面/卡片）。
NAME = "PythonHello"
VERSION = "1.0.0"
AUTHOR = "Nova"
DESCRIPTION = "Python 插件示例：日志 / 主页卡片 / 侧栏页面 / 操作按钮 / 事件订阅"


def on_load(api):
    api.log("PythonHello 已加载（Python 插件）")
    api.register_home_card({
        "id": "py-hello-card",
        "title": "Python Hello",
        "icon": "🐍",
        "body": "由 Python 插件注册的主页卡片",
    })
    api.register_page({
        "key": "py-hello",
        "title": "Python 示例",
        "icon": "🐍",
        "body": "点击下方按钮测试 Python 插件的动作与事件。",
        "actions": [
            {"id": "say", "label": "打个招呼", "icon": "👋"},
            {"id": "time", "label": "当前时间", "icon": "🕐"},
        ],
    })


def on_action(action_id):
    if action_id == "say":
        nova_api.notify("PythonHello", "你好！这是 Python 插件发来的通知")
        nova_api.update_page("py-hello", "最近动作：say —— 已发送通知")
    elif action_id == "time":
        import time
        nova_api.update_page("py-hello", "最近动作：time —— " +
                             time.strftime("%Y-%m-%d %H:%M:%S"))


def on_event(event_name, data):
    if event_name == "game_launched":
        nova_api.log("游戏已启动（版本 %s / 玩家 %s）" %
                     (data.get("versionId", "?"), data.get("accountName", "?")))


def on_unload():
    pass
