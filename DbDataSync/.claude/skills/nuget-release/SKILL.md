---
name: nuget-release
description: >-
  Publish a new DbDataSync release from the DbDataKit monorepo: dispatches the dbdatasync-release.yml
  GitHub Actions workflow against dbdatasync/test, watches it to completion, and confirms the tag and the
  GitHub Release. Use this whenever the user asks to cut,
  ship, publish, or release a new version of DbDataSync — including a beta or prerelease — or wants the
  dbdatasync CLI tool updated. Trigger on phrases like "let's release", "publish a new version", "ship a
  beta", "cut a release", "push a new nuget package" — even if the user doesn't name the workflow or
  GitHub Actions explicitly.
---

# Releasing DbDataSync

DbDataSync lives in the **`DbDataSync/DbDataKit`** monorepo. One workflow cuts a release:
`.github/workflows/dbdatasync-release.yml` (at the repository root, not in `DbDataSync/`). It packs
`DbDataSync.Cli` (the `dbdatasync` global tool), tags the released commit `dbdatasync/v<version>`, publishes a
GitHub Release in this repository with the package attached, merges the lane into `main`, and merges `main` back into
every `*/dev` branch. Read `docs/plans/09-MONOREPO-BRANCHING-AND-RELEASES.md` and the workflow's header comment before
changing anything.

**This is the simplified setup.** It uses only the built-in `GITHUB_TOKEN`. There is no nuget.org push (paused until all
three products are in the monorepo), no container image (dispatch `dbdatasync-publish-image.yml` by hand if needed), and
no GitHub App or branch rulesets. `dbdatasync update` still reads releases from the old repository
(`DbDataSync/DbDataSync`), so a release made here is not visible to it until that client is repointed.

## Running a release

The workflow always builds what is on `origin/dbdatasync/test` — not your working tree, not an unpushed commit, and not
`main`; it refuses unless dispatched against `dbdatasync/test`. It also refuses unless `main` is already an ancestor of
that commit: if another product released since, wait for the post-release back-merge to reach `dbdatasync/dev` and be
promoted. Before dispatching, make sure the checkout is clean, pushed to `dbdatasync/dev`, and promoted to
`dbdatasync/test` (automatic after a green CI run — check `git log --oneline -1 origin/dbdatasync/test`).
`scripts/release.sh` checks this and refuses otherwise, so the common case is:

```sh
DbDataSync/.claude/skills/nuget-release/scripts/release.sh           # a stable release
DbDataSync/.claude/skills/nuget-release/scripts/release.sh --beta    # a prerelease
```

Run it from inside a clone with `dbdatasync/test` checked out (HEAD must equal `origin/dbdatasync/test`), with `gh`
authenticated. If it isn't available:

```sh
gh workflow run dbdatasync-release.yml --repo DbDataSync/DbDataKit --ref dbdatasync/test -f beta=false
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
- `dbdatasync/v<version>` is pushed only after the package is built and checked, so a tag existing means the version
  shipped; a failed run leaves nothing to clean up.
- The GitHub Release (`gh release view dbdatasync/v<version> --repo DbDataSync/DbDataKit`) carries the `.nupkg`.
- `main` has a new merge commit ("Release dbdatasync <version>") whose first-parent history is the release log.
- Every `*/dev` branch has had `main` merged in and its CI dispatched (a push made with `GITHUB_TOKEN` starts no
  workflow, so the release does it by hand). If a back-merge conflicted, the run fails after the release itself
  succeeded and opens an issue; resolve it by merging `main` into that branch by hand.

## Reference

- `.github/workflows/dbdatasync-release.yml` — the workflow itself; heavily commented.
- `docs/plans/09-MONOREPO-BRANCHING-AND-RELEASES.md` — the branching and release model.
- `architecture/implementation/done/phase-127-nuget-trusted-publishing.md` — the original design rationale and
  every real bug found building Trusted Publishing (history, from the single-repo era).
