#!/bin/bash
# ============================================================
# NovaNet Hub 一键部署（Linux/macOS 通用；推荐在公网 VPS 上运行）
# 用法:
#   1) ./deploy.sh           # 本地构建 + 以 systemd 方式安装（Linux）
#   2) ./deploy.sh docker    # 用 Docker 部署（有 docker 时最省事）
# ============================================================
set -e
ROOT="$(cd "$(dirname "$0")" && pwd)"
PORT="${PORT:-8535}"
BIND="${BIND:-0.0.0.0}"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"

echo "== NovaNet Hub 部署 =="
echo "端口: TCP ${PORT} (信令/中继) + UDP ${PORT} (STUN)"

# ---------- Docker 方式 ----------
if [ "$1" = "docker" ]; then
  docker build -t novanet-hub "$ROOT"
  docker rm -f novanet-hub 2>/dev/null || true
  docker run -d --name novanet-hub --restart unless-stopped \
    -p ${PORT}:8535/tcp -p ${PORT}:8535/udp \
    -e PORT=${PORT} novanet-hub
  echo "✅ 已用 Docker 启动（容器名 novanet-hub）"
  echo "   docker logs -f novanet-hub   # 查看日志"
  echo "   curl http://127.0.0.1:${PORT}/healthz   # 健康检查"
  exit 0
fi

# ---------- 本地发布 ----------
echo "[1/3] 发布服务端..."
mkdir -p "$ROOT/publish"
"$DOTNET" publish "$ROOT/NovaNetHub.csproj" -c Release -o "$ROOT/publish" >/dev/null

if [ "$(uname -s)" = "Darwin" ]; then
  echo "[2/3] macOS：直接前台运行（部署到公网请改跑 Linux VPS）"
  echo "[3/3] 启动："
  echo "  cd $ROOT/publish && ./NovaNetHub --port $PORT --bind $BIND"
  exit 0
fi

# ---------- Linux systemd ----------
echo "[2/3] 安装 systemd 服务..."
UNIT="$ROOT/nova-net-hub.service"
sed -e "s|__ROOT__|$ROOT/publish|g" "$ROOT/nova-net-hub.service.tpl" > "$UNIT"
sudo cp "$UNIT" /etc/systemd/system/nova-net-hub.service
sudo systemctl daemon-reload
sudo systemctl enable nova-net-hub
sudo systemctl restart nova-net-hub
echo "[3/3] 已启动 nova-net-hub 服务"
sleep 1
sudo systemctl status nova-net-hub --no-pager | head -8
echo ""
echo "✅ 部署完成。请确认云安全组/防火墙已放行:"
echo "   TCP ${PORT}（WebSocket 信令 + 中继）"
echo "   UDP ${PORT}（STUN 打洞探测）"
echo "健康检查: curl http://127.0.0.1:${PORT}/healthz"
