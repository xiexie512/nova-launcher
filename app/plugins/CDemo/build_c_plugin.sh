#!/bin/bash
# 编译 CDemo C 插件
#   macOS   -> build/c_demo.dylib
#   Windows -> build/c_demo.dll  (需 mingw 的 cc 在 PATH 中)
#   Linux   -> build/c_demo.so
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
HDR="$(cd "$DIR/../../../kernel/include" && pwd)"
OUT="$DIR/build"
mkdir -p "$OUT"
case "$(uname -s)" in
  Darwin)
    cc -std=c11 -O2 -fPIC -shared -I"$HDR" "$DIR/src/c_demo.c" -o "$OUT/c_demo.dylib"
    echo "产物: $OUT/c_demo.dylib"
    ;;
  MINGW*|MSYS*|CYGWIN*)
    cc -std=c11 -O2 -shared -I"$HDR" "$DIR/src/c_demo.c" -o "$OUT/c_demo.dll"
    echo "产物: $OUT/c_demo.dll"
    ;;
  *)
    cc -std=c11 -O2 -fPIC -shared -I"$HDR" "$DIR/src/c_demo.c" -o "$OUT/c_demo.so"
    echo "产物: $OUT/c_demo.so"
    ;;
esac
