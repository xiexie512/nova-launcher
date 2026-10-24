#!/bin/bash
# ============================================================
# NovaLauncher 一键构建：内核 → 主程序 → 插件 → 双平台安装包
# ============================================================
set -e
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export PATH="$HOME/.dotnet:$PATH"

echo "==> 1. 编译 C 内核（mac + win）"
cd "$ROOT/kernel"
./build_mac.sh
bash build_win.sh
cp build/libnova_kernel.dylib "$ROOT/app/NovaLauncher/Native/"

echo "==> 2. 构建主程序（含 PluginHosting 多语言插件加载器）"
cd "$ROOT/app"
dotnet build NovaLauncher/NovaLauncher.csproj -c Release

echo "==> 3. 编译示例插件"
dotnet build plugins/HelloPlugin/HelloPlugin.csproj -c Release
dotnet build plugins/LaunchNotifier/LaunchNotifier.csproj -c Release
dotnet build plugins/NovaModsLoader/NovaModsLoader.csproj -c Release   # 4 大加载器 + Modrinth 插件
dotnet build plugins/PyCExample/PyCExample.csproj -c Release   # Python+C 实例插件（PycSmoke 被测对象）
bash "$ROOT/app/plugins/CDemo/build_c_plugin.sh"   # C 插件（macOS dylib / win dll）

echo "==> 3.5 全量测试（插件加载器 / 日志递归 / 反编译分析 / Python+C 冒烟）"
dotnet run --project tests/PluginHostingTest/PluginHostingTest.csproj
dotnet run --project tests/LogRecursionTest/LogRecursionTest.csproj
dotnet run --project tests/NovaModsLoaderTest/NovaModsLoaderTest.csproj
NOVA_KERNEL_LIB="$ROOT/kernel/build/libnova_kernel.dylib" \
    dotnet run --project tests/PycSmoke/PycSmoke.csproj

echo "==> 4. 打包 macOS dmg"
bash "$ROOT/scripts/package_mac.sh"

echo "==> 5. 打包 Windows"
bash "$ROOT/scripts/package_win.sh"

echo "全部完成！产物见 dist/ 目录："
ls -lh "$ROOT/dist/"
