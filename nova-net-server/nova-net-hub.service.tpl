[Unit]
Description=NovaNet Hub (自研虚拟局域网协调服务器)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
WorkingDirectory=__ROOT__
ExecStart=__ROOT__/NovaNetHub --port 8535 --bind 0.0.0.0
Restart=always
RestartSec=3
# 如需改端口，把上面 ExecStart 的 --port 改掉，并同步放行防火墙

[Install]
WantedBy=multi-user.target
