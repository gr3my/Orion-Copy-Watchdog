#!/usr/bin/env bash
# ================================================================================
# Orion Copy Watchdog - one-shot install for a LINUX host (Ubuntu 22.04+ assumed).
#
#   cp watchdog.example.yml watchdog.yml && nano watchdog.yml
#   sudo ./install.sh
#
# Idempotent: re-running reapplies config without destroying watchdog.yml.
# Existing watchdog.yml is never overwritten. Flags:
#
#   --no-build    skip dotnet publish (re-apply config + restart service)
#   --status      show service + monitored state and exit
#   --uninstall   stop + disable + remove the systemd unit (leaves data + config)
#
# systemd keeps the WATCHDOG alive (Restart=on-failure); the watchdog keeps your
# configured Orion services healthy. Those are two different jobs.
# ================================================================================
set -euo pipefail

SERVICE_NAME="orion-copy-watchdog"
SERVICE_USER="orion-watchdog"
INSTALL_DIR="/opt/orion-copy-watchdog"
BIN_DIR="$INSTALL_DIR/bin"
CONFIG_DIR="/etc/orion-copy-watchdog"
CONFIG_PATH="$CONFIG_DIR/watchdog.yml"
STATE_DIR="/var/lib/orion-copy-watchdog/state"
UNIT_PATH="/etc/systemd/system/${SERVICE_NAME}.service"
SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

NO_BUILD=0
DO_STATUS=0
DO_UNINSTALL=0
for arg in "$@"; do
  case "$arg" in
    --no-build)  NO_BUILD=1 ;;
    --status)    DO_STATUS=1 ;;
    --uninstall) DO_UNINSTALL=1 ;;
    *) echo "[err]  unknown flag: $arg (see --help in README)"; exit 1 ;;
  esac
done

if [[ $EUID -ne 0 ]]; then
  echo "[err]  run as root: sudo ./install.sh"
  exit 1
fi

if [[ $DO_STATUS -eq 1 ]]; then
  echo "[info] systemd unit:"; systemctl status "$SERVICE_NAME" --no-pager || true
  if [[ -x "$BIN_DIR/orion-watchdog" ]]; then
    echo "[info] monitored state:"
    sudo -u "$SERVICE_USER" "$BIN_DIR/orion-watchdog" --config "$CONFIG_PATH" status || true
  else
    echo "[warn] not installed (no binary at $BIN_DIR/orion-watchdog)"
  fi
  exit 0
fi

if [[ $DO_UNINSTALL -eq 1 ]]; then
  systemctl stop "$SERVICE_NAME" 2>/dev/null || true
  systemctl disable "$SERVICE_NAME" 2>/dev/null || true
  rm -f "$UNIT_PATH"
  systemctl daemon-reload
  echo "[ok]   service removed (config + data left in $CONFIG_DIR and $STATE_DIR)"
  exit 0
fi

# -- prerequisites: .NET 8 SDK ---------------------------------------------------
if [[ $NO_BUILD -eq 0 ]]; then
  if ! dotnet --list-sdks 2>/dev/null | grep -q '^8\.'; then
    echo "[info] installing .NET 8 SDK via Microsoft package feed..."
    if ! command -v apt-get >/dev/null; then
      echo "[err]  apt-get not found; install the .NET 8 SDK manually"
      echo "       (https://learn.microsoft.com/dotnet/core/install/linux) and re-run."
      exit 1
    fi
    . /etc/os-release
    apt-get update
    apt-get install -y wget
    wget -q "https://packages.microsoft.com/config/${ID}/${VERSION_ID}/packages-microsoft-prod.deb" \
      -O /tmp/packages-microsoft-prod.deb
    dpkg -i /tmp/packages-microsoft-prod.deb
    rm -f /tmp/packages-microsoft-prod.deb
    apt-get update
    apt-get install -y dotnet-sdk-8.0
  fi
fi

# -- dedicated service account ---------------------------------------------------
if ! id "$SERVICE_USER" >/dev/null 2>&1; then
  useradd --system --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER"
  echo "[ok]   created service account '$SERVICE_USER'"
fi

# -- build / publish ---------------------------------------------------------------
if [[ $NO_BUILD -eq 0 ]]; then
  echo "[info] publishing orion-watchdog (Release)..."
  dotnet publish "$SRC_DIR/src/OrionCopy.Watchdog/OrionCopy.Watchdog.csproj" \
    -c Release -o "$BIN_DIR"
  echo "[ok]   published to $BIN_DIR"
elif [[ ! -x "$BIN_DIR/orion-watchdog" ]]; then
  echo "[err]  no binary at $BIN_DIR/orion-watchdog and --no-build was given."
  exit 1
fi
chown -R root:"$SERVICE_USER" "$BIN_DIR"
chmod -R 0750 "$BIN_DIR"

# -- configuration (never overwrite an existing watchdog.yml) ----------------------
mkdir -p "$CONFIG_DIR" "$STATE_DIR"
if [[ ! -f "$CONFIG_PATH" ]]; then
  cp "$SRC_DIR/watchdog.example.yml" "$CONFIG_PATH"
  echo "[ok]   created $CONFIG_PATH — EDIT IT for your servers, then re-run sudo ./install.sh"
else
  echo "[info] keeping existing $CONFIG_PATH"
fi
chown -R root:"$SERVICE_USER" "$CONFIG_DIR" "$STATE_DIR"
chmod 0750 "$CONFIG_DIR" "$STATE_DIR"
chmod 0640 "$CONFIG_PATH"

# Point the packaged default state directory at the system path on first install.
if grep -q 'state_directory: ./state' "$CONFIG_PATH"; then
  cp "$CONFIG_PATH" "$CONFIG_PATH.bak"
  sed -i "s|state_directory: ./state|state_directory: $STATE_DIR|" "$CONFIG_PATH"
  echo "[info] state_directory set to $STATE_DIR (backup: $CONFIG_PATH.bak)"
fi

# -- validate before installing ------------------------------------------------------
echo "[info] validating configuration..."
if ! "$BIN_DIR/orion-watchdog" --config "$CONFIG_PATH" validate-config; then
  echo "[err]  fix the errors above in $CONFIG_PATH, then re-run sudo ./install.sh"
  exit 1
fi

# -- systemd unit ----------------------------------------------------------------------
cat > "$UNIT_PATH" <<EOF
[Unit]
Description=Orion Copy Watchdog - health monitor and automatic recovery
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
User=$SERVICE_USER
Group=$SERVICE_USER
ExecStart=$BIN_DIR/orion-watchdog --config $CONFIG_PATH start
Restart=on-failure
RestartSec=5s
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=$STATE_DIR $CONFIG_DIR
AmbientCapabilities=
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
EOF
chmod 0644 "$UNIT_PATH"
systemctl daemon-reload
systemctl enable --now "$SERVICE_NAME"
sleep 3
if systemctl is-active --quiet "$SERVICE_NAME"; then
  echo "[ok]   service '$SERVICE_NAME' is active"
else
  echo "[err]  service installed but not active. See: journalctl -u $SERVICE_NAME -f"
  exit 1
fi

echo "[info] next steps:"
echo "  - status:  sudo -u $SERVICE_USER $BIN_DIR/orion-watchdog --config $CONFIG_PATH status"
echo "  - logs:    journalctl -u $SERVICE_NAME -f"
echo "  - edit:    nano $CONFIG_PATH && sudo ./install.sh --no-build"
