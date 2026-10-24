#!/usr/bin/env bash
# Start the persistent worker sessions in the background (skips any already running).
# Workers are every ~/office-arr/agents/<name>/ folder with a CLAUDE.md, except daboss.
# Keep every session in the same permission mode, or cross-session messages
# get held for approval. Override with: MODE=auto ~/office-arr/agents/start.sh
set -euo pipefail
MODE="${MODE:-manual}"
running=$(claude agents --json 2>/dev/null || echo '[]')
for md in ~/office-arr/agents/*/CLAUDE.md; do
  dir=$(dirname "$md")
  name=$(basename "$dir")
  [ "$name" = daboss ] && continue
  if grep -q "\"$name\"" <<<"$running"; then
    echo "$name already running"
  else
    (cd "$dir" && claude --bg -n "$name" --permission-mode "$MODE" \
      "You are $name. Read CLAUDE.md, then wait for requests from other agents.")
  fi
done
claude agents
