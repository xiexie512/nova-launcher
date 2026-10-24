# CDemo —— C 插件示例

按 `kernel/include/nova_plugin.h` ABI 编写的原生插件，编译产物放进插件目录即可加载。

```
./build_c_plugin.sh        # 产物: build/c_demo.dylib（macOS）
```

演示内容：
- 元数据导出（name/version/author/description）
- `nova_plugin_init`：写日志、发通知、注册主页卡片 + 侧栏页面（两个按钮）、订阅 `game_launched` 事件
- `nova_plugin_action`：按钮分发（执行计数 / 显示数据目录）
- `nova_plugin_unload`：卸载回调

Windows 交叉编译（需 mingw 的 cc）产物为 `build/c_demo.dll`，Linux 为 `build/c_demo.so`。
