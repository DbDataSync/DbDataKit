---
name: nuget-release
description: >-
  Publish a new DbDataSync release from the DbDataKit monorepo: dispatches the dbdatasync-release.yml
  GitHub Actions workflow against main, watches it to completion, and confirms the tag and the
  GitHub Release. Use this whenever the user asks to cut,
  ship, publish, or release a new version of DbDataSync — including a beta or prerelease — or wants the
  dbdatasync CLI tool updated. Trigger on phrases like "let's release", "publish a new version", "ship a
  beta", "cut a release", "push a new nuget package" — even if the user doesn't name the workflow or
  GitHub Actions explicitly.
---

# Releasing DbDataSync

DbDataSync lives in the **`DbDataSync/DbDataKit`** monorepo. One workflow cuts a release:
`.github/workflows/dbdatasync-release.yml` (at the repository root, not in `DbDataSync/`). It packs
`DbDataSync.Cli` (the `dbdatasync` global tool), checks the package, tags the released commit `dbdatasync/v<version>`
and publishes a GitHub Release in this repository with the package and its `.sha512` attached. **It pushes no branch**:
a release is a tag and its assets, because the repo is on a trunk model (short feature branches, pull requests into
`main`, rebase merges). Read `docs/plans/09-MONOREPO-BRANCHING-AND-RELEASES.md` ("Trunk model") and the workflow's
header comment before changing anything.

**What is deliberately not there.** Only the built-in `GITHUB_TOKEN`: no nuget.org push (paused until all three products
are in the monorepo), no container image (dispatch `dbdatasync-publish-image.yml` by hand if needed), no GitHub App or
branch rulesets. `dbdatasync update` reads this repository's releases and keeps the ones tagged `dbdatasync/v...`.

## Running a release

The workflow releases **the newest commit on `main`** (or `--sha <older commit on main>`), never your working tree or
an unpushed commit, and only if that exact commit has a green "DbDataSync CI" run. That run happens on its own when a
pull request is merged, so after a merge, wait for the run on `main` to finish before releasing. Nothing about the
checkout you run the script from matters.

```sh
DbDataSync/.claude/skills/nuget-release/scripts/release.sh                  # a stable release
DbDataSync/.claude/skills/nuget-release/scripts/release.sh --beta           # a prerelease
DbDataSync/.claude/skills/nuget-release/scripts/release.sh --sha <commit>   # an older commit on main
```

With `gh` authenticated. If it isn't available:

```sh
gh workflow run dbdatasync-release.yml --repo DbDataSync/DbDataKit --ref main -f beta=false
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
- Nothing else moved: `main` and every other branch are exactly as they were. The release is the tag and the GitHub Release.

## Reference

- `.github/workflows/dbdatasync-release.yml` — the workflow itself; heavily commented.
- `docs/plans/09-MONOREPO-BRANCHING-AND-RELEASES.md` — the branching and release model.
- `architecture/implementation/done/phase-127-nuget-trusted-publishing.md` — the original design rationale and
  every real bug found building Trusted Publishing (history, from the single-repo era).
