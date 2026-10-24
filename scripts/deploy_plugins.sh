#!/bin/sh
# 把仓库内插件部署到启动器数据目录插件文件夹，两类来源：
#   1) 仓库根 plugins/ 文件夹（*.py / *.dylib / *.so / *.dll，即 python + C 插件）
#   2) app/plugins/*/bin/Release/net8.0/*.dll（编译好的 C# 插件，如 NovaModsLoader 四加载器+Modrinth）
# 用法: ./scripts/deploy_plugins.sh    （从仓库根目录执行）
set -e
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$ROOT/plugins"

case "$(uname -s)" in
  Darwin)
    DEST="$HOME/Library/Application Support/NovaLauncher/plugins"
    ;;
  Linux)
    DEST="${XDG_DATA_HOME:-$HOME/.local/share}/NovaLauncher/plugins"
    ;;
  *)
    echo "Windows 请手动复制 plugins\\*.py / *.dll / *.dylib 及 app/plugins/*/bin/Release/net8.0/*.dll 到 %APPDATA%\\NovaLauncher\\plugins"
    exit 0
    ;;
esac

mkdir -p "$DEST"
copied=0

# 1) 根 plugins/ 的 python / C 插件
for f in "$SRC"/*.py "$SRC"/*.dylib "$SRC"/*.so "$SRC"/*.dll; do
  [ -f "$f" ] || continue
  cp -f "$f" "$DEST/"
  echo "已部署: $(basename "$f") -> $DEST"
  copied=$((copied+1))
done

# 2) 编译好的 C# 插件 dll（跳过以 NovaLauncher / Avalonia / CommunityToolkit 开头的程序集）
for csproj in "$ROOT"/app/plugins/*/*.csproj; do
  [ -f "$csproj" ] || continue
  proj="$(basename "$csproj" .csproj)"
  outdir="$ROOT/app/plugins/$proj/bin/Release/net8.0"
  [ -d "$outdir" ] || continue
  for dll in "$outdir"/*.dll; do
    [ -f "$dll" ] || continue
    base="$(basename "$dll")"
    case "$base" in
      NovaLauncher*|Avalonia*|CommunityToolkit*) continue ;;
    esac
    cp -f "$dll" "$DEST/"
    echo "已部署: $base -> $DEST"
    copied=$((copied+1))
  done
done

if [ "$copied" -eq 0 ]; then
  echo "没有可部署的插件文件（可先运行 scripts/build_all.sh 编译插件）"
  exit 1
fi
echo "完成：共部署 $copied 个插件。启动器插件页点「⟳ 重新加载」即可生效。"
