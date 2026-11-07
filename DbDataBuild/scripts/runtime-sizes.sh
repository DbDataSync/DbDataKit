#!/usr/bin/env bash
# The size report of a runtime package (plan 28c E05): every file of <dir> with its size, largest first, and the total. Each file is an artifact of the runner's cache,
# whose cap is 512 MiB per file; a file above <max-mib> (default 400) fails with its name and size, so the cap is never found out by a runner refusing a publish.
#   scripts/runtime-sizes.sh <dir> [max-mib]
set -euo pipefail
DIR="${1:?usage: runtime-sizes.sh <dir> [max-mib]}"
MAX_MIB="${2:-400}"
[ -d "$DIR" ] || { echo "runtime-sizes: '$DIR' is not a directory." >&2; exit 2; }
LIMIT=$((MAX_MIB * 1024 * 1024))
# size and path; `wc -c` reads the length of a sparse or large file without its content being copied
LIST=$(find "$DIR" -type f -exec wc -c {} + | grep -v ' total$' | sort -rn)
echo "$LIST" | awk -v dir="$DIR" '{ s=$1; $1=""; sub(/^ /,""); printf "%10.1f MiB  %s\n", s/1048576, substr($0, length(dir)+2) }'
echo "$LIST" | awk '{ t+=$1 } END { printf "%10.1f MiB  total\n", t/1048576 }'
TOO_BIG=$(echo "$LIST" | awk -v limit="$LIMIT" '$1 > limit { s=$1; $1=""; sub(/^ /,""); printf "  %s is %.1f MiB\n", $0, s/1048576 }')
if [ -n "$TOO_BIG" ]; then
  echo "runtime-sizes: a file of the runtime package is above $MAX_MIB MiB (the artifact cache's cap is 512 MiB per file):" >&2
  echo "$TOO_BIG" >&2
  echo "Publish with --framework-dependent, which holds the natives as separate files, or make the file smaller." >&2
  exit 1
fi
