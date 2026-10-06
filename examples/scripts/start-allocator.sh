#!/usr/bin/env bash
# Sample allocator launcher referenced by watchdog.example.yml
# (restart.start_command). Replace with whatever starts YOUR allocator.
# (Make executable on Linux: chmod +x start-allocator.sh)
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"
exec ./Allocator/OrionAllocator "$@"
