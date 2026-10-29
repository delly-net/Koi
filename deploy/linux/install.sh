#!/bin/bash
# 将 /opt/delly-net/Koi 中的后端程序注册到 systemd，并设置开机自启。
# 用法：sudo ./install.sh [服务名]   （服务名默认 koi，可带或不带 .service 后缀）
# 程序目录可通过 KOI_HOME 环境变量覆盖：sudo KOI_HOME=/opt/delly-net/Koi ./install.sh
set -euo pipefail

KOI_HOME="/opt/delly-net/Koi"
SERVICE_NAME="koi"
SERVICE_NAME="${SERVICE_NAME}.service"
SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
EXECUTABLE="${KOI_HOME}/Koi.Api"

echo "[==========检查运行环境==========]"
if [ "$(id -u)" -ne 0 ]; then
    echo "错误：请以 root 身份运行（sudo $0）。" >&2
    exit 1
fi

if [ ! -d "$KOI_HOME" ]; then
    echo "错误：程序目录不存在 $KOI_HOME，请先执行 publish.sh 发布产物。" >&2
    exit 1
fi

if [ ! -f "$EXECUTABLE" ]; then
    echo "错误：未找到可执行文件 $EXECUTABLE，请确认发布产物完整。" >&2
    exit 1
fi

# 发布产物偶尔会丢失可执行位，这里补齐。
if [ ! -x "$EXECUTABLE" ]; then
    chmod +x "$EXECUTABLE"
fi

echo "[==========写入 unit 文件==========]"
# 数据库 koi.db 与静态目录 wwwroot 均按内容根目录解析，
# 因此 WorkingDirectory 必须指向程序目录，否则数据会落到根目录、前端 404。
cat > "$SERVICE_FILE" <<EOF
[Unit]
Description=Koi Linux Web UI
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
# pam_unix 校验系统账号需要读取 /etc/shadow 等特权资源，故以 root 运行。
User=root
Group=root
WorkingDirectory=$KOI_HOME
ExecStart=$EXECUTABLE
Restart=on-failure
RestartSec=5
TimeoutStopSec=30
KillSignal=SIGINT
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
EOF
echo "已写入 $SERVICE_FILE"

echo "[==========注册并启动服务==========]"
systemctl daemon-reload
systemctl enable "${SERVICE_NAME}.service"
# restart 兼容首次安装与重复执行：未运行时等价于 start，已运行时加载新程序。
systemctl restart "${SERVICE_NAME}.service"

systemctl --no-pager --full status "${SERVICE_NAME}.service" || true
echo "[==========安装完成==========]"
echo "服务名：${SERVICE_NAME}.service，监听地址见 api/appsettings.json 的 Urls（默认 http://0.0.0.0:5000）"
echo "查看日志：journalctl -u ${SERVICE_NAME} -f"
