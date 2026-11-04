#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
node --version
npx -y gitnexus@latest analyze --index-only --name MAIC
npx -y gitnexus@latest status
echo "Done. Project .codex/config.toml configures GitNexus MCP with a response budget."
