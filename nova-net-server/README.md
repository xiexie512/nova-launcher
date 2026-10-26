# NovaNet Hub —— 自研虚拟局域网协调服务器

NovaNet 是 NovaLauncher 自研的虚拟局域网方案，替代原 ZeroTier 集成：

- **零第三方依赖**：房间管理、虚拟 IP 分配、NAT 打洞、中继转发全部自研；
- **P2P 优先**：成员间优先 UDP 打洞直连（低延迟、不占服务器带宽），打洞失败自动回退服务器中继；
- **端到端加密**：设了房间密码时，游戏数据走 AES-GCM 端到端加密，中继服务器只能看到路由头；
- **无需管理员权限**：不装驱动、不改虚拟网卡，用「本地端口映射」实现联机（127.0.0.1:200xx）。

## 端口

| 协议 | 端口 | 用途 |
|---|---|---|
| TCP | 8535 | WebSocket 信令（建房/加入/踢人）+ 中继转发 + HTTP 状态页 |
| UDP | 8535 | STUN 探测（客户端发现自己的公网 UDP 端点，用于打洞） |

> TCP 与 UDP 8535 都必须放行。端口可通过 `--port` 修改（UDP 默认同号）。

## 部署（任选一种）

### 1. Docker（推荐）

```bash
cd nova-net-server
./deploy.sh docker
# 等价于:
docker build -t novanet-hub .
docker run -d --name novanet-hub --restart unless-stopped \
  -p 8535:8535/tcp -p 8535:8535/udp novanet-hub
curl http://127.0.0.1:8535/healthz   # → ok
```

### 2. Linux systemd

```bash
cd nova-net-server
./deploy.sh
```

### 3. 直接运行（本机调试）

```bash
cd nova-net-server
~/.dotnet/dotnet run -- --port 8535 --bind 0.0.0.0
# 或发布后运行:
~/.dotnet/dotnet publish -c Release -o publish
./publish/NovaNetHub --port 8535 --bind 0.0.0.0
```

## 状态页

浏览器打开 `http://服务器IP:8535/` 可查看房间/成员/连接数。

## 防火墙/安全组清单

云厂商安全组、云防火墙、系统防火墙都要放行：
- 入方向 TCP 8535
- 入方向 UDP 8535

## 客户端对接

启动器「虚拟局域网」页把 Hub 地址配置为 `服务器IP:8535`（或 `ws://域名:8535`）即可。
同一台 Hub 可以支撑多个房间、多组好友同时联机，房间码即「网络 ID」。

## 冒烟测试

```bash
cd nova-net-server/tests/NovaNetSmoke
~/.dotnet/dotnet run
# 覆盖：建房/加入、虚拟 IP 分配、UDP 打洞 P2P 直连、中继回退、加密数据往返
```
