#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
PyCExample 实例插件 · Python 演示脚本 1：参数传递 + 中文输出
用法：python3 hello.py <玩家名> [任意消息...]
插件的「Python·hello.py」按钮会调用本脚本，并捕获 stdout 显示在插件页。
"""
import sys


def main() -> None:
    name = sys.argv[1] if len(sys.argv) > 1 else "玩家"
    words = " ".join(sys.argv[2:]) if len(sys.argv) > 2 else "吃了吗？"
    print(f"Python 说：你好 {name}！{words}")
    print("（这行输出来自 python3 脚本，由插件捕获并显示）")


if __name__ == "__main__":
    main()
