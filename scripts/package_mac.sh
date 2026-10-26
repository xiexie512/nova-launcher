#!/bin/bash
# ============================================================
# NovaLauncher macOS 打包脚本
# 产物: dist/NovaLauncher-1.0.0-arm64.dmg
# ============================================================
set -e
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VER="1.0.0"
export PATH="$HOME/.dotnet:$PATH"

echo "[1/5] publish osx-arm64 (self-contained)..."
rm -rf "$ROOT/dist/mac"
rm -rf "$ROOT/app/NovaLauncher/obj" "$ROOT/app/NovaLauncher/bin"   # 强制全量编译，避免增量缓存产出旧二进制
dotnet publish "$ROOT/app/NovaLauncher/NovaLauncher.csproj" \
  -c Release -r osx-arm64 --self-contained true \
  -o "$ROOT/dist/mac/publish" >/dev/null

echo "[2/5] 组装 NovaLauncher.app..."
APP="$ROOT/dist/mac/NovaLauncher.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/MacOS/plugins" "$APP/Contents/Resources"
cp -R "$ROOT/dist/mac/publish/." "$APP/Contents/MacOS/"
cp "$ROOT/assets/AppIcon.icns" "$APP/Contents/Resources/"
cp "$ROOT/app/plugins/HelloPlugin/bin/Release/net8.0/HelloPlugin.dll" "$APP/Contents/MacOS/plugins/"
cp "$ROOT/app/plugins/LaunchNotifier/bin/Release/net8.0/LaunchNotifier.dll" "$APP/Contents/MacOS/plugins/"
cp "$ROOT/app/plugins/NovaModsLoader/bin/Release/net8.0/NovaModsLoader.dll" "$APP/Contents/MacOS/plugins/"
cp "$ROOT/app/plugins/PythonHello/plugin.py" "$APP/Contents/MacOS/plugins/"
cp "$ROOT/app/plugins/CDemo/build/c_demo.dylib" "$APP/Contents/MacOS/plugins/"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>NovaLauncher</string>
  <key>CFBundleDisplayName</key><string>NovaLauncher 我的世界启动器</string>
  <key>CFBundleIdentifier</key><string>com.novalauncher.app</string>
  <key>CFBundleExecutable</key><string>NovaLauncher</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundleVersion</key><string>$VER</string>
  <key>CFBundleShortVersionString</key><string>$VER</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSPrincipalClass</key><string>NSApplication</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.games</string>
</dict>
</plist>
PLIST
chmod +x "$APP/Contents/MacOS/NovaLauncher"

echo "[3/5] 代码签名（ad-hoc）..."
codesign --force --deep --sign - "$APP" 2>/dev/null || echo "  (签名跳过，本机缺少 codesign 能力)"

echo "[4/5] 制作 dmg..."
rm -f "$ROOT/dist/NovaLauncher-$VER-arm64.dmg"
hdiutil create -volname "NovaLauncher" -srcfolder "$APP" \
  -ov -format UDZO "$ROOT/dist/NovaLauncher-$VER-arm64.dmg" >/dev/null

echo "[5/5] 完成:"
ls -lh "$ROOT/dist/NovaLauncher-$VER-arm64.dmg"
