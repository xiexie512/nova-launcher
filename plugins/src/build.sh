#!/bin/sh
# 编译 C 插件示例：产物输出到上级插件文件夹（c_demo.dylib）
# 用法: cd plugins/src && ./build.sh
set -e
cd "$(dirname "$0")"
HDR="$(pwd)/nova_plugin.h"
OUT="../c_demo.dylib"
cc -std=c11 -O2 -fPIC -shared -I"$(pwd)" c_demo.c -o "$OUT"
echo "已生成: $(cd .. && pwd)/c_demo.dylib"
