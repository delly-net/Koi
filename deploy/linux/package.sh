#!/bin/bash
# 将发布目录打包为 deb / rpm 安装包，便于离线分发与统一安装。
#
# 用法：./package.sh [all|deb|rpm] [-p 发布目录] [-o 输出目录] [目录]
#   - 发布目录默认 /project/delly-net/publish/Koi/files（api-build.sh 与 ui-build.sh 的产物），
#     也可通过 KOI_PUBLISH_DIR 指定；
#   - 输出目录默认 <发布目录的上一级>/packages，可通过 KOI_PACKAGE_DIR 指定；
#   - 位置目录里含 koi 可执行文件时按发布目录处理，否则按输出目录处理；
#   - 包版本号取打包时刻 yyyyMMddHHmmss，产物文件名同样是该时间戳结尾：
#       koi-20261030121530.deb / koi-20261030121530.rpm
#
# 安装布局与 install.sh 保持一致：程序目录 /opt/delly-net/Koi，
# unit 文件 /etc/systemd/system/koi.service（内容需与 install.sh 保持同步）。
# 升级与卸载不会删除 /opt/delly-net/Koi/koi.db —— 它不属于包内容，用户数据得以保留。
#
# 依赖工具：
#   - deb：dpkg-deb；缺失时用 ar + tar 手工组装（binutils 自带 ar）。
#   - rpm：rpmbuild。
#       Debian/Ubuntu: apt install rpm
#       RHEL/Fedora:   dnf install rpm-build
#   缺少某个工具时只跳过对应格式，最后以非 0 退出码报告。

# 本脚本使用 bash 特性（BASH_SOURCE、数组等）。Debian 的 /bin/sh 是 dash，
# 直接用 `sh package.sh` 调用会报 Bad substitution，这里切换到 bash 重新执行。
if [ -z "${BASH_VERSION:-}" ]; then
    exec bash "$0" "$@"
fi

set -euo pipefail

APP_NAME="koi"
KOI_HOME="/opt/delly-net/Koi"
UNIT_NAME="koi.service"
UNIT_PATH="/etc/systemd/system/${UNIT_NAME}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

info() { echo "[==========$*==========]"; }
warn() { echo "警告：$*" >&2; }
die() { echo "错误：$*" >&2; exit 1; }

usage() {
    cat <<'EOF'
用法：./package.sh [all|deb|rpm] [-p 发布目录] [-o 输出目录] [目录]

  all                 同时生成 deb 与 rpm（默认）
  deb                 只生成 deb
  rpm                 只生成 rpm
  -p, --publish DIR   发布目录（含 koi 可执行文件与 wwwroot）
  -o, --output DIR    安装包输出目录

  位置目录：含 koi 可执行文件时按发布目录处理，否则按输出目录处理。
  发布目录默认 /project/delly-net/publish/Koi/files（KOI_PUBLISH_DIR 可覆盖）；
  输出目录默认 <发布目录上一级>/packages（KOI_PACKAGE_DIR 可覆盖）。

示例：
  ./package.sh
  ./package.sh rpm
  ./package.sh all /project/delly-net/publish/Koi/files
  ./package.sh all -o packages
  ./package.sh all -p /srv/koi-publish -o /srv/koi-packages
EOF
}

# ---------------------------------------------------------------- 参数与环境
TARGET=""
PUBLISH_DIR="${KOI_PUBLISH_DIR:-/project/delly-net/publish/Koi/files}"
OUT_DIR="${KOI_PACKAGE_DIR:-}"
PUBLISH_SET=0
OUT_SET=0
POSITIONAL=""

while [ $# -gt 0 ]; do
    case "$1" in
        -h|--help) usage; exit 0 ;;
        -p|--publish)
            [ $# -ge 2 ] || die "$1 缺少目录参数"
            PUBLISH_DIR="$2"; PUBLISH_SET=1; shift 2 ;;
        --publish=*|-p=*)
            PUBLISH_DIR="${1#*=}"; PUBLISH_SET=1; shift ;;
        -o|--output)
            [ $# -ge 2 ] || die "$1 缺少目录参数"
            OUT_DIR="$2"; OUT_SET=1; shift 2 ;;
        --output=*|-o=*)
            OUT_DIR="${1#*=}"; OUT_SET=1; shift ;;
        all|deb|rpm) TARGET="$1"; shift ;;
        -*)
            die "未知参数 $1（见 --help）" ;;
        *)
            [ -z "$POSITIONAL" ] || die "只能给出一个目录参数，其余目录请用 -p / -o 指定"
            POSITIONAL="$1"; shift ;;
    esac
done
TARGET="${TARGET:-all}"

# 位置目录二义时按内容判断：有 koi 可执行文件就是发布目录，否则按输出目录处理。
if [ -n "$POSITIONAL" ]; then
    if [ -f "$POSITIONAL/koi" ]; then
        [ "$PUBLISH_SET" -eq 1 ] && die "发布目录被指定了两次：-p $PUBLISH_DIR 与 $POSITIONAL"
        PUBLISH_DIR="$POSITIONAL"
        PUBLISH_SET=1
    elif [ -d "$POSITIONAL" ] || [ -d "$PUBLISH_DIR" ]; then
        [ "$OUT_SET" -eq 1 ] && die "输出目录被指定了两次：-o $OUT_DIR 与 $POSITIONAL"
        OUT_DIR="$POSITIONAL"
        OUT_SET=1
        echo "提示：$POSITIONAL 中没有 koi 可执行文件，按输出目录处理。"
    else
        PUBLISH_DIR="$POSITIONAL"
    fi
fi

[ -d "$PUBLISH_DIR" ] || die "发布目录不存在：$PUBLISH_DIR（请先执行 api-build.sh 与 ui-build.sh，或用 -p 指定）"
PUBLISH_DIR="$(cd "$PUBLISH_DIR" && pwd)"
[ -f "$PUBLISH_DIR/koi" ] || die "发布目录缺少可执行文件 koi：$PUBLISH_DIR（发布目录应为 koi + wwwroot；输出目录请用 -o 指定）"
[ -f "$PUBLISH_DIR/wwwroot/index.html" ] || warn "发布目录缺少 wwwroot/index.html，安装包将只提供 API，没有前端页面。"

[ -n "$OUT_DIR" ] || OUT_DIR="$(dirname "$PUBLISH_DIR")/packages"
case "$OUT_DIR" in /*) ;; *) OUT_DIR="$PWD/$OUT_DIR" ;; esac
mkdir -p "$OUT_DIR" || die "无法创建输出目录：$OUT_DIR"
OUT_DIR="$(cd "$OUT_DIR" && pwd)"
# 输出目录若嵌在发布目录内，上一次的安装包会被再次打进新包。
case "$OUT_DIR/" in
    "$PUBLISH_DIR/"*) warn "输出目录位于发布目录内，发布目录中已有的安装包会被一起打进新包，建议用 -o 指定其他目录。" ;;
esac

STAMP="$(date +%Y%m%d%H%M%S)"
DEB_FILE="$OUT_DIR/${APP_NAME}-${STAMP}.deb"
RPM_FILE="$OUT_DIR/${APP_NAME}-${STAMP}.rpm"

case "$(uname -m)" in
    x86_64|amd64) DEB_ARCH="amd64"; RPM_ARCH="x86_64" ;;
    aarch64|arm64) DEB_ARCH="arm64"; RPM_ARCH="aarch64" ;;
    armv7l|armv7*) DEB_ARCH="armhf"; RPM_ARCH="armv7hl" ;;
    *) DEB_ARCH="$(uname -m)"; RPM_ARCH="$(uname -m)" ;;
esac

# 维护者信息优先取 git 配置，其次取环境变量，最后落到占位值。
MAINTAINER="${KOI_MAINTAINER:-}"
if [ -z "$MAINTAINER" ]; then
    git_name="$(git -C "$SCRIPT_DIR" config user.name 2>/dev/null || true)"
    git_email="$(git -C "$SCRIPT_DIR" config user.email 2>/dev/null || true)"
    if [ -n "$git_name" ] && [ -n "$git_email" ]; then
        MAINTAINER="$git_name <$git_email>"
    else
        MAINTAINER="Koi Packager <koi@localhost>"
    fi
fi

info "打包信息"
echo "版本号      ：$STAMP"
echo "发布目录    ：$PUBLISH_DIR"
echo "输出目录    ：$OUT_DIR"
echo "目标架构    ：deb=$DEB_ARCH rpm=$RPM_ARCH"
echo "维护者      ：$MAINTAINER"

# ---------------------------------------------------------------- 暂存目录
WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/${APP_NAME}-package.XXXXXX")"
trap 'rm -rf "$WORK_DIR"' EXIT

STAGE="$WORK_DIR/root"                 # deb 的根目录结构，rpm 也从这里取文件
APP_STAGE="$STAGE$KOI_HOME"
mkdir -p "$APP_STAGE" "$STAGE/DEBIAN" "$(dirname "$STAGE$UNIT_PATH")"

info "收集发布产物"
cp -a "$PUBLISH_DIR/." "$APP_STAGE/"
# 运行期数据库不进入安装包，避免升级时覆盖已有会话库。
rm -f "$APP_STAGE/koi.db" "$APP_STAGE/koi.db-shm" "$APP_STAGE/koi.db-wal"
chmod +x "$APP_STAGE/koi"

# 前端运行时配置：发布目录缺失时用仓库内的生产配置兜底。
if [ ! -f "$APP_STAGE/wwwroot/conf/setting.json" ] && [ -f "$SCRIPT_DIR/conf/setting.json" ]; then
    warn "发布目录缺少 wwwroot/conf/setting.json，使用 deploy/linux/conf/setting.json。"
    mkdir -p "$APP_STAGE/wwwroot/conf"
    cp "$SCRIPT_DIR/conf/setting.json" "$APP_STAGE/wwwroot/conf/setting.json"
fi

# unit 内容必须与 install.sh 写入的一致：WorkingDirectory 指向程序目录，
# koi.db 与 wwwroot 才能按内容根目录解析。
cat > "$STAGE$UNIT_PATH" <<EOF
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
ExecStart=$KOI_HOME/koi
Restart=on-failure
RestartSec=5
TimeoutStopSec=30
KillSignal=SIGINT
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
EOF
chmod 0644 "$STAGE$UNIT_PATH"

DEB_OK=0
RPM_OK=0

# ---------------------------------------------------------------- deb
build_deb() {
    info "生成 deb"
    rm -f "$DEB_FILE"

    local size_kb
    size_kb="$(du -sk "$APP_STAGE" | cut -f1)"
    cat > "$STAGE/DEBIAN/control" <<EOF
Package: $APP_NAME
Version: $STAMP
Section: admin
Priority: optional
Architecture: $DEB_ARCH
Maintainer: $MAINTAINER
Installed-Size: $size_kb
Depends: libc6, libgcc-s1, libstdc++6, libpam0g, zlib1g, libssl3 | libssl3t64, libicu | libicu-dev
Homepage: https://github.com/delly-net/Koi
Description: Koi Linux Web UI
 以浏览器方式管理 Linux 主机：使用系统账号登录，提供多个在线终端、
 文件管理与在线文本编辑。
EOF

    # unit 视为配置文件：升级时保留本地修改。
    printf '%s\n' "$UNIT_PATH" > "$STAGE/DEBIAN/conffiles"

    cat > "$STAGE/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e

if [ "$1" = "configure" ] && command -v systemctl >/dev/null 2>&1 && [ -d /run/systemd/system ]; then
    systemctl daemon-reload >/dev/null 2>&1 || true
    systemctl enable koi.service >/dev/null 2>&1 || true
    systemctl restart koi.service >/dev/null 2>&1 || true
fi

exit 0
EOF

    cat > "$STAGE/DEBIAN/prerm" <<'EOF'
#!/bin/sh
set -e

case "$1" in
    remove|deconfigure)
        if command -v systemctl >/dev/null 2>&1 && [ -d /run/systemd/system ]; then
            systemctl stop koi.service >/dev/null 2>&1 || true
            systemctl disable koi.service >/dev/null 2>&1 || true
        fi
        ;;
esac

exit 0
EOF

    cat > "$STAGE/DEBIAN/postrm" <<'EOF'
#!/bin/sh
set -e

if command -v systemctl >/dev/null 2>&1 && [ -d /run/systemd/system ]; then
    systemctl daemon-reload >/dev/null 2>&1 || true
fi

exit 0
EOF

    chmod 0644 "$STAGE/DEBIAN/control" "$STAGE/DEBIAN/conffiles"
    chmod 0755 "$STAGE/DEBIAN/postinst" "$STAGE/DEBIAN/prerm" "$STAGE/DEBIAN/postrm"

    if command -v dpkg-deb >/dev/null 2>&1; then
        if dpkg-deb --help 2>&1 | grep -q -- '--root-owner-group'; then
            dpkg-deb --root-owner-group --build "$STAGE" "$DEB_FILE" \
                || { echo "错误：dpkg-deb 执行失败。" >&2; return 1; }
        else
            dpkg-deb --build "$STAGE" "$DEB_FILE" \
                || { echo "错误：dpkg-deb 执行失败。" >&2; return 1; }
        fi
    elif command -v ar >/dev/null 2>&1; then
        # 没有 dpkg-deb（例如在 RHEL 主机上）时，按 deb 规范手工组装 ar 归档。
        echo "未找到 dpkg-deb，改用 ar + tar 组装 deb。"
        local archive_dir="$WORK_DIR/deb-archive"
        mkdir -p "$archive_dir"
        printf '2.0\n' > "$archive_dir/debian-binary"
        tar -C "$STAGE/DEBIAN" --owner=0 --group=0 --numeric-owner -czf "$archive_dir/control.tar.gz" . \
            || { echo "错误：打包 control.tar.gz 失败。" >&2; return 1; }
        tar -C "$STAGE" --exclude=./DEBIAN --owner=0 --group=0 --numeric-owner -czf "$archive_dir/data.tar.gz" . \
            || { echo "错误：打包 data.tar.gz 失败。" >&2; return 1; }
        rm -f "$DEB_FILE"
        ( cd "$archive_dir" && ar rc "$DEB_FILE" debian-binary control.tar.gz data.tar.gz ) \
            || { echo "错误：ar 组装 deb 失败。" >&2; return 1; }
    else
        echo "错误：dpkg-deb 与 ar 均不可用，无法生成 deb。" >&2
        return 1
    fi

    if [ ! -s "$DEB_FILE" ]; then
        echo "错误：未生成 deb 文件 $DEB_FILE。" >&2
        return 1
    fi
    echo "已生成 $DEB_FILE"
    DEB_OK=1
}

# ---------------------------------------------------------------- rpm
build_rpm() {
    info "生成 rpm"

    if ! command -v rpmbuild >/dev/null 2>&1; then
        echo "错误：未找到 rpmbuild，无法生成 rpm。" >&2
        echo "      Debian/Ubuntu: apt install rpm" >&2
        echo "      RHEL/Fedora:   dnf install rpm-build" >&2
        return 1
    fi

    local top="$WORK_DIR/rpmbuild"
    mkdir -p "$top/BUILD" "$top/BUILDROOT" "$top/RPMS" "$top/SOURCES" "$top/SPECS" "$top/SRPMS"

    local spec="$top/SPECS/$APP_NAME.spec"
    cat > "$spec" <<'EOF'
Name:           koi
Version:        %{_koi_version}
Release:        1
Summary:        Koi Linux Web UI
License:        Proprietary
URL:            https://github.com/delly-net/Koi
Packager:       %{_koi_maintainer}
AutoReqProv:    no
BuildArch:      %{_koi_arch}
Requires:       glibc
Requires:       libgcc
Requires:       libstdc++
Requires:       pam
Requires:       zlib
Requires:       libicu
Requires:       openssl-libs

# 单文件自包含发布把运行时打包在可执行文件尾部，strip/objcopy 有破坏风险。
%global debug_package %{nil}
%define __strip /bin/true
%define __objcopy /bin/true

%description
以浏览器方式管理 Linux 主机：使用系统账号登录，提供多个在线终端、
文件管理与在线文本编辑。终端依赖 POSIX PTY，仅支持 Linux。

%prep
%build

%install
rm -rf %{buildroot}
install -d %{buildroot}/opt/delly-net/Koi
cp -a %{_koi_stage}/opt/delly-net/Koi/. %{buildroot}/opt/delly-net/Koi/
install -d %{buildroot}%{_sysconfdir}/systemd/system
install -m 0644 %{_koi_stage}%{_sysconfdir}/systemd/system/koi.service \
    %{buildroot}%{_sysconfdir}/systemd/system/koi.service

%post
if [ -d /run/systemd/system ]; then
    systemctl daemon-reload >/dev/null 2>&1 || :
    systemctl enable koi.service >/dev/null 2>&1 || :
    systemctl restart koi.service >/dev/null 2>&1 || :
fi

%preun
if [ "$1" -eq 0 ] && [ -d /run/systemd/system ]; then
    systemctl stop koi.service >/dev/null 2>&1 || :
    systemctl disable koi.service >/dev/null 2>&1 || :
fi

%postun
if [ -d /run/systemd/system ]; then
    systemctl daemon-reload >/dev/null 2>&1 || :
fi

%files
%config(noreplace) %{_sysconfdir}/systemd/system/koi.service
/opt/delly-net/Koi
EOF

    rpmbuild -bb \
        --define "_topdir $top" \
        --define "_koi_stage $STAGE" \
        --define "_koi_version $STAMP" \
        --define "_koi_arch $RPM_ARCH" \
        --define "_koi_maintainer $MAINTAINER" \
        "$spec" >"$WORK_DIR/rpmbuild.log" 2>&1 || {
            cat "$WORK_DIR/rpmbuild.log" >&2
            echo "错误：rpmbuild 执行失败，日志见上。" >&2
            return 1
        }

    local built
    built="$(find "$top/RPMS" -type f -name '*.rpm' | head -n 1)"
    [ -n "$built" ] || { echo "错误：rpmbuild 未产出 rpm 文件。" >&2; return 1; }

    cp -f "$built" "$RPM_FILE" || { echo "错误：复制 rpm 到输出目录失败。" >&2; return 1; }
    if [ ! -s "$RPM_FILE" ]; then
        echo "错误：未生成 rpm 文件 $RPM_FILE。" >&2
        return 1
    fi
    echo "已生成 $RPM_FILE"
    RPM_OK=1
}

# ---------------------------------------------------------------- 执行
if [ "$TARGET" = "all" ] || [ "$TARGET" = "deb" ]; then
    build_deb || true
fi

if [ "$TARGET" = "all" ] || [ "$TARGET" = "rpm" ]; then
    build_rpm || true
fi

info "打包结果"
if [ "$DEB_OK" -eq 1 ]; then
    echo "deb：$DEB_FILE"
else
    echo "deb：未生成"
fi
if [ "$RPM_OK" -eq 1 ]; then
    echo "rpm：$RPM_FILE"
else
    echo "rpm：未生成"
fi
if [ "$DEB_OK" -eq 1 ] || [ "$RPM_OK" -eq 1 ]; then
    echo "安装：sudo dpkg -i ${APP_NAME}-${STAMP}.deb   或   sudo rpm -Uvh ${APP_NAME}-${STAMP}.rpm"
fi

if [ "$TARGET" = "deb" ] && [ "$DEB_OK" -ne 1 ]; then
    exit 1
fi
if [ "$TARGET" = "rpm" ] && [ "$RPM_OK" -ne 1 ]; then
    exit 1
fi
if [ "$TARGET" = "all" ] && { [ "$DEB_OK" -ne 1 ] || [ "$RPM_OK" -ne 1 ]; }; then
    exit 1
fi
