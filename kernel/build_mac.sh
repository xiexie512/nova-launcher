#!/bin/bash
# NovaLauncher 内核构建（macOS）
set -e
cd "$(dirname "$0")"
mkdir -p build

echo "[1/2] building libnova_kernel.dylib ..."
clang -shared -fPIC -O2 -Wall -Wextra -I include -I src \
  -o build/libnova_kernel.dylib \
  src/nova_util.c src/nova_json.c src/nova_md5.c src/nova_sha1.c \
  src/nova_thread.c src/nova_http.c src/nova_download.c \
  src/nova_auth.c src/nova_launch.c src/nova_java.c src/nova_kernel.c \
  -lcurl

echo "[2/2] building & running unit tests ..."
clang -O2 -I include -I src \
  -o build/test_kernel \
  tests/test_kernel.c \
  src/nova_util.c src/nova_json.c src/nova_md5.c src/nova_sha1.c \
  src/nova_thread.c src/nova_http.c src/nova_download.c \
  src/nova_auth.c src/nova_launch.c src/nova_java.c src/nova_kernel.c \
  -lcurl

./build/test_kernel
