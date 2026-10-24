# PythonHello —— Python 插件示例

一个 `.py` 文件即一个插件，放进插件目录即可加载，无需编译、可热编辑。

```
~/Library/Application Support/NovaLauncher/plugins/plugin.py
```

演示内容：
- `on_load(api)` 注册主页卡片 + 侧栏页面（含两个操作按钮）
- `on_action` 分发按钮动作（打招呼 / 当前时间）
- `on_event` 订阅 `game_launched` 启动事件
- 全局 `nova_api` 在任意函数里调用启动器能力

详见 `../README.md` 的「Python 插件」一节。
