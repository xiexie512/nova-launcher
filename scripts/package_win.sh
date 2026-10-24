#!/bin/bash
# ============================================================
# NovaLauncher Windows 打包脚本（macOS 上交叉产出）
# 产物:
#   dist/NovaLauncher-win-x64-portable.zip   便携版（双击 NovaLauncher.exe 即用）
#   dist/NovaLauncher-win-x64-Setup.exe      单文件绿色版（自包含 .NET 运行时）
# ============================================================
set -e
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VER="1.0.0"
export PATH="$HOME/.dotnet:$PATH"

echo "[1/4] publish win-x64 (self-contained)..."
rm -rf "$ROOT/dist/win"
rm -rf "$ROOT/app/NovaLauncher/obj" "$ROOT/app/NovaLauncher/bin"   # 强制全量编译，避免增量缓存产出旧二进制
dotnet publish "$ROOT/app/NovaLauncher/NovaLauncher.csproj" \
  -c Release -r win-x64 --self-contained true \
  -o "$ROOT/dist/win/publish" >/dev/null

echo "[2/4] 放入 C 内核 dll 与示例插件..."
cp "$ROOT/kernel/build_win/nova_kernel.dll" "$ROOT/dist/win/publish/"
mkdir -p "$ROOT/dist/win/publish/plugins"
cp "$ROOT/app/plugins/HelloPlugin/bin/Release/net8.0/HelloPlugin.dll" "$ROOT/dist/win/publish/plugins/"
cp "$ROOT/app/plugins/LaunchNotifier/bin/Release/net8.0/LaunchNotifier.dll" "$ROOT/dist/win/publish/plugins/"
cp "$ROOT/app/plugins/NovaModsLoader/bin/Release/net8.0/NovaModsLoader.dll" "$ROOT/dist/win/publish/plugins/"
cp "$ROOT/app/plugins/PythonHello/plugin.py" "$ROOT/dist/win/publish/plugins/"
if [ -f "$ROOT/app/plugins/CDemo/build/c_demo.dll" ]; then
  cp "$ROOT/app/plugins/CDemo/build/c_demo.dll" "$ROOT/dist/win/publish/plugins/"
fi

echo "[2.5/4] 内置 ZeroTier 安装包（虚拟局域网离线可用）..."
mkdir -p "$ROOT/dist/win/publish/installers"
cp "$ROOT/assets/installers/ZeroTier-One.msi" "$ROOT/dist/win/publish/installers/"

echo "[3/4] 制作便携版 zip..."
cd "$ROOT/dist/win/publish"
rm -f "$ROOT/dist/NovaLauncher-win-x64-portable.zip"
zip -rq "$ROOT/dist/NovaLauncher-win-x64-portable.zip" . -x "*.pdb"

echo "[4/4] 制作单文件版 exe..."
dotnet publish "$ROOT/app/NovaLauncher/NovaLauncher.csproj" \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:IncludeAllContentForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -o "$ROOT/dist/win/single" >/dev/null
cp "$ROOT/dist/win/single/NovaLauncher.exe" "$ROOT/dist/NovaLauncher-win-x64-Setup.exe" 2>/dev/null || \
  echo "  (单文件版失败，便携版已可用)"

echo "完成:"
ls -lh "$ROOT/dist/NovaLauncher-win-x64-portable.zip" "$ROOT/dist/NovaLauncher-win-x64-Setup.exe" 2>/dev/null
