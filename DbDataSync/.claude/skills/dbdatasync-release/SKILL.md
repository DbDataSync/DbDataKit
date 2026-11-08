---
name: dbdatasync-release
description: >-
  Publish a new DbDataSync release from the DbDataKit monorepo: dispatches the dbdatasync-release.yml
  GitHub Actions workflow against main, watches it to completion, and confirms the tag and the
  GitHub Release. Use this whenever the user asks to cut, ship, publish, or release a new version of
  DbDataSync, including a beta or prerelease. Trigger on phrases like "let's release", "publish a new
  version", "ship a beta", "cut a release", even if the user doesn't name the workflow or GitHub
  Actions explicitly. For a release of the whole suite, dispatch suite-release.yml instead (see below).
---

# Releasing DbDataSync

DbDataSync lives in the **`DbDataSync/DbDataKit`** monorepo. One workflow cuts its release:
`.github/workflows/dbdatasync-release.yml` (at the repository root, not in `DbDataSync/`). It builds the standard
zip packages (win-x64, linux-x64), the Windows installer (`.msi`) and `SHA256SUMS`, checks them, tags the released
commit `dbdatasync/v<version>` and publishes a GitHub Release in this repository with those files attached, plus the
install scripts and Scoop manifest (`dist/README.md`). **There is no .NET tool package** (no `.nupkg`, no `.sha512`,
no `dotnet tool install`: dropped by owner decision on 2026-11-08). **It pushes no branch**: a release is a tag and its
assets, because the repo is on a trunk model (short feature branches, pull requests into `main`, rebase merges). Read
`docs/releases.md` and the workflow's header comment before changing
anything.

**What is deliberately not there.** Only the built-in `GITHUB_TOKEN`: no NuGet push (paused), no container image
(dispatch `dbdatasync-publish-image.yml` by hand if needed), no GitHub App or branch rulesets.

The whole suite is released from one commit with one version by `.github/workflows/suite-release.yml` (it calls this
workflow); `-f dry_run=true` builds everything and keeps the files on the run without tagging or publishing.

## Running a release

The workflow releases **the newest commit on `main`** (or `--sha <older commit on main>`), never your working tree or
an unpushed commit, and only if that exact commit has a green "DbDataSync CI" run. That run happens on its own when a
pull request is merged, so after a merge, wait for the run on `main` to finish before releasing. Nothing about the
checkout you run the script from matters.

```sh
DbDataSync/.claude/skills/dbdatasync-release/scripts/release.sh                  # a stable release
DbDataSync/.claude/skills/dbdatasync-release/scripts/release.sh --beta           # a prerelease
DbDataSync/.claude/skills/dbdatasync-release/scripts/release.sh --sha <commit>   # an older commit on main
```

With `gh` authenticated. If it isn't available:

```sh
gh workflow run dbdatasync-release.yml --repo DbDataSync/DbDataKit --ref main -f beta=false
gh run list --repo DbDataSync/DbDataKit --workflow "DbDataSync release" --limit 3
gh run watch <id> --repo DbDataSync/DbDataKit --exit-status
```

## Beta or stable: default to stable unless told otherwise

Default to a stable release (`beta=false`) unless the user asks for a beta/prerelease or says they are
testing the pipeline. Caution belongs in *confirming with the user before running the script*, not in
silently downgrading the request. A beta appends `-beta` to the version and marks the GitHub Release a
prerelease; the installer scripts treat only a stable release as "newest" (install a beta with `-Version` /
`--version` or Scoop's `dist-beta` manifest).

## What a successful run means

- The version is `YYYY.M.D.Hmm` from the UTC clock at run time: never typed, never read from a tag.
- `dbdatasync/v<version>` is pushed only after the zips and the MSI are built and checked, so a tag existing means the
  version shipped; a failed run leaves nothing to clean up.
- The GitHub Release (`gh release view dbdatasync/v<version> --repo DbDataSync/DbDataKit`) carries the zips, the MSI and
  `SHA256SUMS`; the install scripts and the Scoop manifest are attached by the `publish-dist` step.
- Nothing else moved: `main` and every other branch are exactly as they were. The release is the tag and the GitHub Release.

## Reference

- `.github/workflows/dbdatasync-release.yml`: the workflow itself; heavily commented.
- `dist/README.md`: the distribution routes (zips, MSI, scripts, Scoop) and what each installer does.
- `docs/releases.md`: the branching and release model.
