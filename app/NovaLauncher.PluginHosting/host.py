#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
NovaLauncher Python 插件桥（host.py）
由启动器内嵌资源写出到 <LauncherHome>/_nova_bridge/host.py，随 python3 -u 启动。

用法: python3 -u host.py <plugin.py>

协议：与启动器通过 stdin/stdout 交换 JSON 行（每行一个对象）：
  插件 -> 启动器:  {"id":N,"type":"api","method":"log","args":[...]}    API 请求
                   {"id":N,"ok":true,"result":...}                      调用(on_load 等)返回
                   {"type":"hello","ok":true,"name":"..","version":"..", ...}
  启动器 -> 插件:  {"id":N,"type":"call","method":"on_load","args":[]}  调用插件函数
                   {"type":"event","event":"game_launched","data":{...}} 事件推送
                   {"id":N,"ok":true,"result":...}                       API 请求响应

插件约定（plugin.py）：
  NAME / VERSION / AUTHOR / DESCRIPTION  模块级元数据（可省略）
  on_load(api)                            加载时调用，api 即全局 nova_api
  on_action(action_id)                    页面操作按钮被点击
  on_event(event_name, data)              启动器事件（game_launching/game_launched/
                                           console_line/download_progress）
  on_unload()                             卸载前调用
"""
import importlib.util
import json
import os
import queue
import sys
import threading
import traceback

# ---------- 输出保护：插件 print 重定向到 stderr，避免污染协议流 ----------
class _StdoutProxy:
    encoding = "utf-8"

    def write(self, s):
        try:
            sys.stderr.write(str(s))
            sys.stderr.flush()
        except Exception:
            pass

    def flush(self):
        try:
            sys.stderr.flush()
        except Exception:
            pass

    def isatty(self):
        return False

    def fileno(self):
        return -1

real_stdout = sys.stdout
sys.stdout = _StdoutProxy()

# ---------- 协议收发 ----------
OUT_LOCK = threading.Lock()
SEQ = {"n": 0}
WAITERS = {}
WAITERS_LOCK = threading.Lock()
CALL_QUEUE = queue.Queue()


def send(obj):
    with OUT_LOCK:
        real_stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
        real_stdout.flush()


# ---------- 插件可用的 API 对象（on_load 的 api 参数即此对象） ----------
class NovaApi:
    """插件通过全局 nova_api（或 on_load 的 api 参数）调用启动器能力。"""

    def _request(self, method, args):
        with WAITERS_LOCK:
            SEQ["n"] += 1
            rid = SEQ["n"]
            ev = threading.Event()
            WAITERS[rid] = ev
        send({"id": rid, "type": "api", "method": method, "args": args})
        if not ev.wait(30):
            with WAITERS_LOCK:
                WAITERS.pop(rid, None)
            raise RuntimeError("与启动器通信超时: " + method)
        with WAITERS_LOCK:
            WAITERS.pop(rid, None)
        res = getattr(ev, "result", None)
        if res is None:
            raise RuntimeError(method + " 无响应")
        if not res.get("ok"):
            raise RuntimeError(res.get("error") or (method + " 调用失败"))
        return res.get("result")

    def log(self, message):
        self._request("log", [message])

    def notify(self, title, message):
        self._request("notify", [title, message])

    def get_setting(self, key, default=""):
        return self._request("get_setting", [key, default])

    def set_setting(self, key, value):
        self._request("set_setting", [key, value])

    @property
    def launcher_home(self):
        return self._request("launcher_home", [])

    @property
    def game_root(self):
        return self._request("game_root", [])

    def register_home_card(self, card):
        self._request("register_home_card", [card])

    def register_page(self, page):
        self._request("register_page", [page])

    def update_page(self, key, body):
        self._request("update_page", [key, body])

    def update_home_card(self, card_id, body):
        self._request("update_home_card", [card_id, body])


API = NovaApi()

# ---------- 线程：读 stdin（把调用/事件入队，把 API 响应交给等待者） ----------
def reader_loop():
    for raw in sys.stdin:
        line = raw.strip()
        if not line:
            continue
        try:
            obj = json.loads(line)
        except Exception:
            continue
        if "id" in obj and ("ok" in obj or "result" in obj or "error" in obj):
            with WAITERS_LOCK:
                ev = WAITERS.get(obj["id"])
            if ev is not None:
                ev.result = obj
                ev.set()
        elif obj.get("type") == "call":
            CALL_QUEUE.put(("call", obj))
        elif obj.get("type") == "event":
            CALL_QUEUE.put(("event", obj))


# ---------- 线程：顺序执行插件函数（避免 on_load 内 API 请求自锁） ----------
def worker_loop():
    while True:
        kind, obj = CALL_QUEUE.get()
        if kind == "call":
            method = obj.get("method") or ""
            args = obj.get("args") or []
            fn = getattr(MOD, method, None)
            if fn is None:
                send({"id": obj.get("id"), "ok": False, "error": "插件未定义 " + method + "()"})
                continue
            try:
                if method == "on_load":
                    result = fn(API)
                else:
                    result = fn(*args)
                send({"id": obj.get("id"), "ok": True, "result": result})
            except Exception:
                send({"id": obj.get("id"), "ok": False, "error": traceback.format_exc()})
        else:
            name = obj.get("event") or ""
            data = obj.get("data") or {}
            fn = getattr(MOD, "on_event", None)
            if fn:
                try:
                    fn(name, data)
                except Exception:
                    traceback.print_exc(file=sys.stderr)


# ---------- 加载插件模块 ----------
def load_module(path):
    base = os.path.splitext(os.path.basename(path))[0].replace("-", "_")
    name = "nova_plugin_" + base
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    sys.modules[name] = mod
    spec.loader.exec_module(mod)
    mod.nova_api = API
    return mod


def meta(mod, attr, default):
    v = getattr(mod, attr, None)
    if v is not None:
        return v
    fn = getattr(mod, attr.lower(), None)
    if callable(fn):
        try:
            return fn()
        except Exception:
            return default
    return default


def main():
    if len(sys.argv) < 2:
        sys.stderr.write("用法: host.py <plugin.py>\n")
        sys.exit(2)
    path = os.path.abspath(sys.argv[1])
    if not os.path.isfile(path):
        send({"type": "hello", "ok": False, "error": "插件文件不存在: " + path})
        sys.exit(1)
    global MOD
    try:
        MOD = load_module(path)
    except Exception:
        send({"type": "hello", "ok": False, "error": traceback.format_exc()})
        sys.exit(1)
    send({
        "type": "hello",
        "ok": True,
        "name": meta(MOD, "NAME", os.path.splitext(os.path.basename(path))[0]),
        "version": meta(MOD, "VERSION", "1.0.0"),
        "author": meta(MOD, "AUTHOR", "unknown"),
        "description": meta(MOD, "DESCRIPTION", ""),
        "python": sys.version.split()[0],
    })
    t = threading.Thread(target=worker_loop, daemon=True)
    t.start()
    reader_loop()


if __name__ == "__main__":
    main()
