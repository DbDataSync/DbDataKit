#!/usr/bin/env bash
# Installs the npm workspace of Shared/web for local work (agents, worktrees). With bun it links every file into bun's global cache, so a worktree
# costs almost no disk (npm copies about 325 MB per worktree); without bun it falls back to `npm ci --ignore-scripts`.
# CI keeps `npm ci` and package-lock.json: bun reads that lock file, writes the untracked bun.lock and may resolve a package or two differently.
# Usage: scripts/web-install.sh   (run it through scripts/build-slot.sh when other builds run)
set -eu
cd "$(dirname "$0")/../web" 2>/dev/null || cd "$(dirname "$0")/../Shared/web"
if command -v bun >/dev/null 2>&1 && [ "${DBDATAKIT_WEB_INSTALLER:-bun}" = "bun" ]; then
  bun install --linker hoisted
else
  npm ci --ignore-scripts --no-audit --no-fund
fi
