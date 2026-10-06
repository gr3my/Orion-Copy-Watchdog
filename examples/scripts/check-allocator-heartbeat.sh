#!/usr/bin/env bash
# Sample heartbeat-freshness probe referenced by examples/allocator.yml.
# Usage in watchdog.yml:
#   - type: command
#     command: ./examples/scripts/check-allocator-heartbeat.sh http://127.0.0.1:9095/health 30
# Exit 0 = allocator healthy and recently heard from, non-zero = unhealthy.
# Adapt the parsing to YOUR allocator's status endpoint.
# (Make executable on Linux: chmod +x check-allocator-heartbeat.sh)
set -euo pipefail

URL="${1:-http://127.0.0.1:9095/health}"
MAX_AGE_SECS="${2:-30}"

body="$(curl -fsS --max-time 8 "$URL")" || {
  echo "allocator health endpoint unreachable: $URL"
  exit 1
}

# If your endpoint exposes the last heartbeat age (seconds), enforce it here, e.g.:
#   age="$(printf '%s' "$body" | grep -o '"heartbeat_age_seconds":[0-9]*' | grep -o '[0-9]*')"
#   if [ "${age:-999999}" -gt "$MAX_AGE_SECS" ]; then
#     echo "allocator heartbeat stale: ${age}s old (max ${MAX_AGE_SECS}s)"
#     exit 1
#   fi

echo "allocator ok: $URL"
exit 0
