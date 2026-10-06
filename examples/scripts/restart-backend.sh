#!/usr/bin/env bash
# Sample restart helper referenced by examples/simple-http.yml.
# Replace the body with whatever restarts YOUR backend, e.g.:
#   systemctl restart orion-copy-server
#   ssh gamebox "powershell -Command Restart-Service OrionCopyServer"
# Exit 0 = restart command accepted, non-zero = failed.
# (Make executable on Linux: chmod +x restart-backend.sh)
set -euo pipefail

echo "restart-backend: restarting orion-copy-server..."
systemctl restart orion-copy-server
systemctl is-active --quiet orion-copy-server
echo "restart-backend: done"
