#!/bin/bash
# NovaLauncher 内核交叉编译（macOS -> Windows x64 dll）
set -e
cd "$(dirname "$0")"
mkdir -p build_win

echo "[win] building nova_kernel.dll (mingw-w64, WinHTTP backend) ..."
x86_64-w64-mingw32-gcc -shared -O2 -Wall -D_WIN32 -D_WIN64 \
  -I include -I src \
  -o build_win/nova_kernel.dll \
  src/nova_util.c src/nova_json.c src/nova_md5.c src/nova_sha1.c \
  src/nova_thread.c src/nova_http.c src/nova_download.c \
  src/nova_auth.c src/nova_launch.c src/nova_java.c src/nova_kernel.c \
  -lwinhttp

file build_win/nova_kernel.dll
echo "[win] done"
