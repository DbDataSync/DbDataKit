#!/usr/bin/env bash
# Dispatch DbDataSync's dbdatasync-release.yml (in the DbDataKit monorepo), find the run it just created, and watch it to completion.
# See ../SKILL.md for what a successful run means and what to do if it isn't found right away.
set -euo pipefail

REPO="DbDataSync/DbDataKit"
WORKFLOW_FILE="dbdatasync-release.yml"
WORKFLOW_NAME="DbDataSync release"
BETA=false
SHA=""

usage() {
  echo "Usage: $0 [--beta] [--sha <commit>]"
  echo "  --beta         Publish as a SemVer 2 prerelease (hidden from a plain dotnet tool install)."
  echo "  --sha <commit> Release an older commit on main instead of its tip (it needs a green DbDataSync CI run)."
}

while [ $# -gt 0 ]; do
  case "$1" in
    --beta) BETA=true ;;
    --sha) shift; SHA="${1:?--sha needs a commit}" ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage >&2; exit 1 ;;
  esac
  shift
done

# The workflow builds origin/main (or --sha) and refuses unless that exact commit has a green DbDataSync CI run, so nothing about this
# checkout matters: not an uncommitted edit, not an unpushed commit. docs/plans/09-MONOREPO-BRANCHING-AND-RELEASES.md, "Trunk model".
echo "Dispatching $WORKFLOW_FILE (beta=$BETA${SHA:+, sha=$SHA}) against $REPO, ref main..."
before=$(date -u +%Y-%m-%dT%H:%M:%SZ)
gh workflow run "$WORKFLOW_FILE" --repo "$REPO" --ref main -f beta="$BETA" ${SHA:+-f sha="$SHA"}

# `gh workflow run` doesn't hand back a run id, so find the run it just created: the newest
# workflow_dispatch-triggered release run created at or after the moment we dispatched. A short
# retry loop covers the few seconds GitHub sometimes takes to list a run that was just queued.
echo "Waiting for the run to appear..."
run_id=""
for _ in $(seq 1 15); do
  run_id=$(gh run list --repo "$REPO" --workflow "$WORKFLOW_NAME" --limit 5 \
    --json databaseId,createdAt,event \
    --jq "[.[] | select(.event == \"workflow_dispatch\" and .createdAt >= \"$before\")] | sort_by(.createdAt) | .[0].databaseId // empty")
  [ -n "$run_id" ] && break
  sleep 2
done

if [ -z "$run_id" ]; then
  echo "Could not find the dispatched run after 30s. Check manually:" >&2
  echo "  gh run list --repo $REPO --workflow \"$WORKFLOW_NAME\" --limit 5" >&2
  exit 1
fi

echo "Watching run $run_id (https://github.com/$REPO/actions/runs/$run_id)..."
if ! gh run watch "$run_id" --repo "$REPO" --exit-status; then
  status=$?
  echo
  echo "Run failed. See the failing step's log with:"
  echo "  gh run view $run_id --repo $REPO --log-failed"
  exit "$status"
fi

sha=$(gh run view "$run_id" --repo "$REPO" --json headSha --jq .headSha)
echo
echo "Release run succeeded (workflow started on ${sha:0:12})."
echo "Recent releases:"
gh release list --repo "$REPO" --limit 3
