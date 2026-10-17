---
name: nuget-release
description: >-
  Publish a new DbDataSync release from the DbDataKit monorepo: dispatches the dbdatasync-release.yml
  GitHub Actions workflow against dbdatasync/test, watches it to completion, and confirms the tag, the
  GitHub Release and (when enabled) the nuget.org package. Use this whenever the user asks to cut,
  ship, publish, or release a new version of DbDataSync — including a beta or prerelease — or wants the
  dbdatasync CLI tool updated. Trigger on phrases like "let's release", "publish a new version", "ship a
  beta", "cut a release", "push a new nuget package" — even if the user doesn't name the workflow or
  GitHub Actions explicitly.
---

# Releasing DbDataSync

DbDataSync lives in the **`DbDataSync/DbDataKit`** monorepo. One workflow cuts a release:
`.github/workflows/dbdatasync-release.yml` (at the repository root, not in `DbDataSync/`). It packs
`DbDataSync.Cli` (the `dbdatasync` global tool, package id `DbDataSync`), tags the released commit
`dbdatasync/v<version>` in this repo, creates the GitHub Release on the **old** repository
`DbDataSync/DbDataSync` (that is what `dbdatasync update` reads), merges the lane into `main`, and
merges `main` back into every `*/dev` branch. Read `docs/plans/09-MONOREPO-BRANCHING-AND-RELEASES.md`
for the model and the workflow's own header comment before changing anything.

**NuGet publishing is paused** until all three products are in the monorepo. The workflow has a
`publish_nuget` input that defaults to off; the release otherwise runs in full. Pass `--nuget` only when
the user explicitly asks to publish to nuget.org (this also needs the Trusted Publishing policy
re-registered for the monorepo and the workflow filename).

## Running a release

The workflow always builds what is on `origin/dbdatasync/test` — not your working tree, not an unpushed
commit, and not `main`; it refuses unless dispatched against `dbdatasync/test`. It also refuses unless
`main` is already an ancestor of that commit: if another product released since, wait for the
post-release back-merge to reach `dbdatasync/dev` and be promoted. Before dispatching, make sure the
checkout is clean, pushed to `dbdatasync/dev`, and promoted to `dbdatasync/test` (automatic after a green
CI run — check `git log --oneline -1 origin/dbdatasync/test`). `scripts/release.sh` checks this and
refuses otherwise, so the common case is:

```sh
DbDataSync/.claude/skills/nuget-release/scripts/release.sh           # a stable release (no NuGet push)
DbDataSync/.claude/skills/nuget-release/scripts/release.sh --beta    # a prerelease
```

Run it from inside a clone with `dbdatasync/test` checked out (HEAD must equal `origin/dbdatasync/test`), with `gh`
authenticated. If it isn't available:

```sh
gh workflow run dbdatasync-release.yml --repo DbDataSync/DbDataKit --ref dbdatasync/test -f beta=false -f publish_nuget=false
gh run list --repo DbDataSync/DbDataKit --workflow "DbDataSync release" --limit 3
gh run watch <id> --repo DbDataSync/DbDataKit --exit-status
```

## Beta or stable — default to stable unless told otherwise

Default to a stable release (`beta=false`) unless the user asks for a beta/prerelease or says they are
testing the pipeline. Caution belongs in *confirming with the user before running the script*, not in
silently downgrading the request. A beta appends `-beta` to the version and marks the GitHub Release a
prerelease; nuget.org (when publishing is on) hides it from a plain `dotnet tool install`.

## What a successful run means

- The version is `YYYY.M.D.Hmm` from the UTC clock at run time — never typed, never read from a tag.
- `dbdatasync/v<version>` is pushed to this repo only after the package is built and checked (and, when
  enabled, published), so a tag existing means the version shipped; a failed run leaves nothing to clean up.
- The GitHub Release (`gh release view <version> --repo DbDataSync/DbDataSync`) carries the `.nupkg`. Its
  tag in that repo points at a frozen commit; the notes link the real monorepo commit.
- `main` has a new merge commit ("Release dbdatasync <version>") whose first-parent history is the release log.
- Every `*/dev` branch has had `main` merged in. If a back-merge conflicted, the run fails after the release
  itself succeeded and opens an issue; resolve it by merging `main` into that branch by hand.
- With `--nuget`: the version is on `https://www.nuget.org/packages/DbDataSync/<version>`. nuget.org can take
  from under a minute to ~15 minutes to index a fresh publish; confirm with
  `dotnet tool install --tool-path /tmp/verify-release DbDataSync --version <version> --no-cache` and wait
  rather than assuming failure (see `architecture/implementation/done/phase-127-nuget-trusted-publishing.md`).

## Reference

- `.github/workflows/dbdatasync-release.yml` — the workflow itself; heavily commented.
- `docs/plans/09-MONOREPO-BRANCHING-AND-RELEASES.md` — the branching and release model.
- `architecture/implementation/done/phase-127-nuget-trusted-publishing.md` — the original design rationale and
  every real bug found building Trusted Publishing (history, from the single-repo era).
