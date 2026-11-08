# Branches, CI and releases

How code reaches `main`, how CI decides what to run, and how a release is cut. The workflows under `.github/workflows/` are the source of
truth; their header comments say how to dispatch them. What a release contains and how people install it is in [`dist/README.md`](../dist/README.md).

## Decisions

- **`main` is the only long-lived branch.** Work goes in batches (CLAUDE.md, "Making changes"): one integration branch `suite/batch-<n>`, one
  pull request, one commit per slice. The merge style (rebase, merge commit or squash) is free; rebase keeps `main` readable and is preferred.
- **No branch protection and no ruleset.** The committers follow the batch model by convention. Revisit if more people commit.
- **CI is change-based.** Each product has one CI workflow that runs on pull requests into `main` and on pushes to `main`. A first `changes`
  job diffs the pull request (or the push) and the suites run only when the product's folder, `DbDataKit/`, `testenv/` or the product's
  workflow files changed. A skipped suite still lets the run report success. A newer push to a pull request cancels the older run; runs on
  `main` always complete.
- **A release is a tag, not a branch move.** A release workflow pushes no branch and merges nothing; it tags a commit that is already on `main`
  and publishes a GitHub Release in this repository.
- **One version scheme: CalVer `YYYY.M.D.HHmm`** from the UTC clock, for every product. A `-beta` suffix is a beta and `-snapshot.g<sha>` a
  snapshot; both are prereleases.
- **Names are per product.** Tags are `<product>/v<version>` (`dbdatabuild/`, `dbdatasync/`, `dbdataproxy/`, `dbdatakit/` for the console).
  Container images are `ghcr.io/<owner>/<product>`, tagged with the version (a stable release also moves `latest`).
  Releases are created with `--latest=false`: GitHub's "latest" is one marker per repository, not per product.
- **NuGet publishing is paused.** No release workflow has a nuget.org push step today. Bringing it back needs a new step and a new Trusted
  Publishing policy, because that policy is bound to the repository and the workflow file name.

## Shared code

`DbDataKit/` is consumed by the products at the same commit through project references, so a shared change must be proven against every consumer.

1. **Each product's CI runs when `DbDataKit/` changes** (its `changes` job includes the path).
2. **The shared libraries' own suite runs once**, in `dbdatakit-ci.yml` (it calls `_dbdatakit.yml`: Linux and Windows, against the engines of
   `testenv/`), not once per product.
3. Conflicts are ordinary merge or rebase conflicts on the batch branch, resolved before the pull request is merged.

`dbdatabuild-conformance-full.yml` runs every engine in one job nightly on `main` (skipped when nothing of Build's changed in the last day),
on request, and as the gate inside Build's release.

## Cutting a release

**The suite release** (`suite-release.yml`) releases every product from one commit with one CalVer version. Its `guard` job picks the commit
and computes the version, which it hands to the console job and to each product's release workflow (their `version` input). It produces:

- the console (`dbdatakit`): zips for win-x64 and linux-x64, the MSI, `SHA256SUMS`, the tag `dbdatakit/v<version>` and its GitHub Release;
- DbDataBuild, DbDataProxy and DbDataSync, each by calling its own release workflow (zips, MSI, install files, tag, GitHub Release);
- container images for the console, Build and Proxy, pushed to GHCR after a container health check. DbDataSync's image is built to prove the
  commit makes one, and pushed by `dbdatasync-publish-image.yml` (dispatch-only, against an existing `dbdatasync/v<version>` tag).

```
gh workflow run suite-release.yml --ref main                       # a release of everything
gh workflow run suite-release.yml --ref main -f beta=true          # a beta of everything
gh workflow run suite-release.yml --ref main -f dry_run=true       # build everything, tag and publish nothing
gh workflow run suite-release.yml --ref main -f sha=<older commit on main>
gh workflow run suite-release.yml --ref <branch> -f snapshot=true  # a snapshot of any branch
```

**Each product release is still dispatchable alone** (`dbdatabuild-release.yml`, `dbdataproxy-release.yml`, `dbdatasync-release.yml`, same
inputs; an empty `version` means the clock now).

**The guard.** Unless it is a snapshot, a release refuses to run when dispatched against anything but `main`, and when the commit (the tip, or
`sha`) is not on `main`. It also needs a gate on that exact commit:

- the console, Proxy and Sync look up a successful run of the product's CI workflow for the commit (a lookup, not a re-run; any successful run
  counts, so a flake that was re-run to green is accepted);
- Build runs the full conformance suite on every engine on that commit, then the unit suite and a smoke test of each executable.

**Inputs.** `beta` adds `-beta` and marks the release a prerelease. `snapshot` cuts a prerelease from any branch or commit with none of the
guards (not on `main`, no green CI, no conformance gate) and builds no images; it never touches a stable `<product>/dist` release. `dry_run`
builds everything, keeps each product's files as the run's `release-<product>` artifacts, checks that each holds its zips, its MSI and a
`SHA256SUMS` that lists them, and tags and pushes nothing.

**Order of steps.** Build, check and package; then tag; then publish. The tag is made after the package is confirmed, so a failed run leaves
nothing to clean up and a retry just computes a new version. A stable release also replaces the assets of the fixed `<product>/dist` release
(stable install URLs); see `dist/README.md`.

## Registry notes

- A GHCR package that was created by another repository's workflow belongs to that repository: under the package's "Manage Actions access",
  add this repository with Write before the first push from here, or the push is refused.
- NuGet is paused (see Decisions).
