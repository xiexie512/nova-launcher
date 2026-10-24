#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
PyCExample 实例插件 · Python 演示脚本 2：离线 UUID（MD5 v3）
与 C 内核 nova_offline_uuid 完全相同的算法，一键对照两种语言实现。
用法：python3 py_uuid.py <玩家名>
"""
import hashlib
import sys


def offline_uuid(name: str) -> str:
    digest = hashlib.md5(("OfflinePlayer:" + name).encode("utf-8")).digest()
    b = bytearray(digest)
    b[6] = (b[6] & 0x0F) | 0x30  # version 3
    b[8] = (b[8] & 0x3F) | 0x80  # variant
    h = b.hex()
    return f"{h[0:8]}-{h[8:12]}-{h[12:16]}-{h[16:20]}-{h[20:32]}"


if __name__ == "__main__":
    name = sys.argv[1] if len(sys.argv) > 1 else "Steve"
    print(offline_uuid(name))
