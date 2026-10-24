# PyCExample —— Python + C 实例插件

给 NovaLauncher 的示例插件：**同一个插件内同时集成 C 和 Python 两种语言**。

| 项 | 值 |
|---|---|
| 插件名 | PyCExample |
| 版本 | 1.0.0 |
| 页面 | 侧栏「Python+C 实例」（`pyc-demo`） |
| 卡片 | 主页「Python + C 实例插件」（`pyc-card`） |

## 它演示了什么

- **C 集成**：通过 P/Invoke 直接调用启动器自研 C 内核 `nova_kernel`
  - `nova_version()` —— 内核版本
  - `nova_offline_uuid(name)` —— 离线 UUID（MD5 v3）
  - `nova_http_get(url)` —— 用 C 内核发 HTTP 请求（示例：Mojang 版本清单）
  - `nova_java_check(path)` —— Java 路径有效性校验
- **Python 集成**：调用本机 `python3` 执行脚本，捕获 stdout 显示到插件页
  - `hello.py` —— 参数传递 + 中文输出
  - `py_uuid.py` —— 用 Python 实现**同一套离线 UUID 算法**，与 C 内核一键对照

## 安装

把 `PyCExample.dll` 放入启动器插件目录（macOS）：

```
~/Library/Application Support/NovaLauncher/plugins/
```

重启启动器即可在侧栏看到「Python+C 实例」页面。

## 操作按钮

| 按钮 | 作用 |
|---|---|
| 刷新状态 | 显示 C 内核版本/库路径、Python 解释器/版本、脚本目录 |
| C内核·离线UUID | C 内核计算离线 UUID |
| C内核·HTTP请求 | C 内核拉取 Mojang 版本清单 |
| Python·hello.py | 运行 Python 脚本（中文输出） |
| Python·离线UUID | Python 与 C 对照计算离线 UUID |
| 一键全跑 | C 与 Python 各项能力依次执行 |

## 自定义

- **脚本热编辑**：脚本首次加载时被写出到
  `~/Library/Application Support/NovaLauncher/PyCExample/scripts/`，
  直接用编辑器改 `.py` 文件，下次点按钮即生效，无需重新编译插件。
- **演示玩家名**：启动器设置项 `PyCExample.demoName`（默认 `Steve`）。
- **Python 找不到**：默认依次探测 `python3` / `/usr/bin/python3` / `python` / `py`。

## 重新编译

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet build app/plugins/PyCExample/PyCExample.csproj -c Release
# 产物：app/plugins/PyCExample/bin/Release/net8.0/PyCExample.dll
```

## 加入一键打包（可选）

在 `scripts/build_all.sh` 的“编译示例插件”段加一行：

```bash
dotnet build plugins/PyCExample/PyCExample.csproj -c Release
```

在 `scripts/package_mac.sh` 的插件拷贝段加一行（Windows 包同理）：

```bash
cp "$ROOT/app/plugins/PyCExample/bin/Release/net8.0/PyCExample.dll" "$APP/Contents/MacOS/plugins/"
```
