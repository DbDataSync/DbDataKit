# Monorepo branching, CI and releases

Status: DbDataSync, DbDataProxy and DbDataBuild are all in this repository and on the **trunk model** below. This document started as a
per-product `dev` -> `test` -> `main` lane design (plus, later, a repo-wide `dev`/`test` lane on top of it). That was built, run, and retired:
unreleased code only ever sat in a lane briefly, so the lanes cost more than they returned (see "What changed from the lane design"). It is
in git history (PR #1, `b762d5b`, and the lane work through #35). Supersedes the "monorepo rejected" decision in
[00-REPO-STRATEGY.md](00-REPO-STRATEGY.md): the three products and the shared libraries live here.

## Decisions

- **`main` is the only long-lived branch.** Work happens on short feature branches and reaches `main` by pull request, merged with
  **rebase** (linear history). No ruleset is set on `main` for now: the only committer is Claude, at the owner's direction. Imports of another
  repository's history are the exception: they are merge commits (a subtree merge), never squashed or rebased.
- **CI is change-based.** Every product has one CI workflow that runs on pull requests into `main` and on pushes to `main`, and skips its suites
  when nothing it depends on changed. The shared libraries have their own, run once (`dbdatakit-ci.yml`).
- **A release is a tag, not a branch move.** Each product's release workflow is dispatched against `main` (or `-f sha=` for an older commit on
  `main`), gates on that exact commit, builds, tags `<product>/v<version>` and publishes a GitHub Release in this repository. It pushes no branch.
- **Layout:** every product and the shared code is a top-level folder: `DbDataSync/`, `DbDataProxy/`, `DbDataBuild/`, and `DbDataKit/` for the
  shared code (renamed from `shared/`). `LICENSE` is at the root. Each product owns its own `docs/`.
- **Container images are named per product** (`ghcr.io/<owner>/dbdatasync`, ...). Where the image workflow lives
  (here or in a product repo) doesn't matter to the name; for now it lives here with the product's release.
- **All products use CalVer** (`YYYY.M.D.HHmm`, as DbDataSync does). Proxy's and Build's existing semver tags and versions are not carried
  forward; there is no alias or mapping.
- **NuGet publishing is paused** until it is wanted again. DbDataSync is the only product that has ever published to nuget.org.

```
feature branch ──PR (change-based CI)──► rebase merge ──► main ──CI on the merged commits──► release (dispatch)
                                                                                              tag <product>/v<version>
                                                                                              + GitHub Release
```

## Layout

```
DbDataKit/                    repository root
  LICENSE
  .github/workflows/          per-product sets: dbdatasync-ci.yml, dbdatasync-release.yml, ...; dbdatakit-ci.yml for the shared code
  DbDataSync/                 product (imported with its history)
  DbDataProxy/                product (imported with its history)
  DbDataBuild/                product (imported with its history)
  DbDataKit/                  shared libraries (DbDataKit.*), one solution: DbDataKit.Shared.slnx
  docs/                       cross-project plans from before the merge
```

GitHub only runs workflows from the root `.github/workflows/`, so each product's workflows live there with the
product's name as a prefix and run their steps inside that product's folder.

**Root `docs/` cleanup is deferred.** Each product has its own docs, so what is in the root `docs/` should
eventually be moved into the product that owns it, or removed. It isn't urgent: it is an ordinary pull request.

## What changed from the lane design

The first design gave each product `<product>/dev` and `<product>/test` branches, released by merging `<product>/test` into `main`, and merged
`main` back into every other lane immediately. In practice:
- **A release had nothing to merge.** `main` had to be an ancestor of the released commit, so the merge was an empty marker commit; the tag
  already marks the release.
- **Back-merges made the lanes drift.** Each one left a merge commit that existed only in that lane, so the next one could not fast-forward.
- **A push made with `GITHUB_TOKEN` starts no workflow**, so every back-merge needed a hand-dispatched CI run, and `[skip ci]` in a tip commit
  silently skipped the push-triggered run.
- **Unreleased code did not sit in a lane for long**, so isolating it bought little.

So the lanes (`<product>/dev`, `<product>/test`, and the repo-wide `dev`/`test` that proved shared changes against every product), the
`promote` jobs, the `merge-release` action and the "`main` is an ancestor" guard are gone.

## Shared code

`DbDataKit/` is consumed by the products at the same commit, through project references, so a shared change must be proven against every
consumer, not just its author's product:

1. **Each product's CI runs when `DbDataKit/` changes** (its `changes` job includes the path), so the consumers build and test against it.
2. **The shared libraries' own suite runs once**, in `dbdatakit-ci.yml` (it calls `_dbdatakit.yml`: Linux and Windows, against the shared engines
   from `testenv/`), instead of once per product.
3. **Conflicts are ordinary rebase conflicts** on the feature branch, resolved before the merge. There is no release-time merge to fail.

Root files (`LICENSE`, `.gitattributes`, `docs/`) are ordinary pull requests.

## Branch protection

> **Not used.** The only committer is Claude, at the owner's direction, so the model is followed by convention. A ruleset on `main` (pull
> request required, linear history) would fit this model and needs no GitHub App, because no pipeline pushes a branch any more; the one thing
> it would block is a future subtree import, which needs a bypass. Revisit if more people commit.

## Trunk model

- **`main` is the only long-lived branch.** Feature branch, pull request, rebase merge. The CI run on the pull request validates the branch; a
  rebase merge makes new commits, so the run on `main` is the one that validates what landed, and releases look for it.
- **Nightly.** `dbdatabuild-conformance-full.yml` runs every engine in one job on `main`, skipped when nothing of the product's changed in the
  last day. It replaces the old weekly run and the soak that `test` was meant to give. (DbDataSync's and DbDataProxy's suites are run per push
  and have no separate nightly job yet.)
- **A release is a tag, not a branch move.** The workflow pushes no branch, so there is nothing to merge back and no ancestor guard.
- **One standard distribution for every product** (`dist/README.md`). Each release carries per-platform zips named
  `<product>-v<version>-<rid>.zip` and `SHA256SUMS`, plus `install.ps1` and `install.sh`; a stable release also replaces the assets (those
  two scripts and a Scoop manifest, `<product>.json`) of one fixed prerelease per product, `<product>/dist`, so the install URLs never
  change and no commit is needed. The product-specific facts are in `<Product>/dist.json`; `.github/actions/publish-dist` binds the
  templates to them. A product may add routes of its own (DbDataSync's .NET tool package).
- **No ruleset, no GitHub App.** See "Branch protection".

## CI

- **One workflow per product** (`dbdatasync-ci.yml`, `dbdataproxy-ci.yml`, `dbdatabuild-ci.yml`), run on `pull_request` into `main` and `push` to
  `main` (and by dispatch, which runs everything). A first `changes` job diffs the pull request against its base, or a push against its
  previous tip, and the suites run only if the product's folder, `DbDataKit/` (for DbDataSync and DbDataProxy, which consume it), `testenv/`
  (for the products that use it), or the product's workflow files changed. A job skipped by `changes` still lets the run report success.
  A `paths:` filter was not used for the products because a skipped required check is awkward; it is used for `dbdatakit-ci.yml`, which has
  one simple trigger.
- **`dbdatakit-ci.yml`** runs the shared libraries' suite once, on a change under `DbDataKit/` or `testenv/`, on Linux and Windows, with the
  shared engines (SQL Server and PostgreSQL from `testenv/`). See [11-ci-and-testing.md](11-ci-and-testing.md).
- **Working directory.** The products' workflows run their steps inside the product's folder (`defaults.run.working-directory`, explicit
  paths for actions), with cache and artifact names prefixed by product.
- **Cost control.** A newer push to a pull request cancels the older run; runs on `main` always complete. DbDataBuild runs its slow engines
  (SQL Server 2025, Oracle, Spark, BigQuery) after a merge and nightly, not on every pull request, unless the PR has the `ci:full` label.

## Releases, versions and tags

All products use CalVer, `YYYY.M.D.HHmm`, computed from the UTC clock as DbDataSync does today.

| | DbDataSync | DbDataProxy | DbDataBuild |
| --- | --- | --- | --- |
| release trigger | dispatch against `main` | dispatch against `main` | dispatch against `main` |
| older commit | `-f sha=<commit on main>` | `-f sha=<commit on main>` | `-f sha=<commit on main>` |
| gate | a green `DbDataSync CI` run for that exact commit | a green `DbDataProxy CI` run for that exact commit | the full conformance suite, run on that commit |
| tag in this repo | `dbdatasync/v<version>` | `dbdataproxy/v<version>` | `dbdatabuild/v<version>` |
| assets | `.nupkg` + `.sha512`, plus the standard set | the standard set | the standard set |

The standard set is the win-x64 (and, except Proxy, linux-x64) zip, `SHA256SUMS`, `install.ps1` and (except Proxy) `install.sh`. Sync's zips are framework-dependent and cannot `update` themselves.

Common release steps: refuse unless dispatched against `main` and the commit (the tip, or `sha`) is on `main`; gate on that exact commit
(DbDataSync and DbDataProxy look up the CI run rather than re-running it, DbDataBuild runs its full conformance suite because it has one); build,
check and package; **tag after the package is confirmed, so a failed run leaves nothing to clean up**; publish the GitHub Release with
`--latest=false` ("latest" is one marker per repository, not per product). `beta=true` adds `-beta` to the version and marks the release a
prerelease. Nothing else moves: no merge into `main`, no back-merge.

### NuGet

> **Deferred**, together with everything else that needed the App; see "Implementation status".


Paused. The release workflow skips the nuget.org push (and the Trusted Publishing token exchange) behind an
explicit switch, defaulting to off, so the rest of the release (build, smoke test, tag, GitHub Release) still runs. When it's turned back on, the Trusted Publishing policy must be re-registered for
`DbDataSync/DbDataKit` and the new workflow filename, because that policy is bound to repo and workflow file.

### How the app tells products apart

One repository now holds every product's releases, so a release belongs to a product by its **tag prefix**, which is
the product's lowercase name and a slash:

| release | tag |
| --- | --- |
| stable | `dbdatasync/v<version>` |
| beta | `dbdatasync/v<version>-beta` |
| snapshot | `dbdatasync/v<version>-snapshot.g<sha>` |

`dbdatasync update` (`ReleaseCatalog`) lists the repository's releases once and keeps one only if its tag starts with
`dbdatasync/v`, the version after it parses (its suffix is the channel: none, `-beta` or `-snapshot.g<sha>`, so a snapshot
tag reads like a beta tag), the prerelease flag agrees with that channel (stable is the only channel that is not a
prerelease), and it carries
`DbDataSync.<version>.nupkg` and its `.sha512` hosted under this repository's `releases/download/`. The asset names
are a second, independent product check. Other products' releases, drafts, hand-made releases and anything
malformed are skipped silently. A new product just needs its own prefix; DbDataProxy and DbDataBuild releases never
show up in DbDataSync's list, and vice versa.

Every channel now goes through the same download, checksum and `--add-source` install that snapshots always used.
The listing is read once per run and shared across channels, because GitHub allows only 60 anonymous requests an
hour. Releases are created with `--latest=false`: "latest" is one marker per repository, not per product.

### GitHub Releases on the old repo, without pushing code there

> **Superseded.** `dbdatasync update` now reads this repository's releases (see above), so publishing to the old
> repository is no longer needed. Kept as the record of the bridge that was considered.


`dbdatasync update` used to read releases from the old repo (`DbDataSync/DbDataSync`, a constant in
`ReleaseSources.cs`), looks for tags starting `snapshot-`, and downloads assets from under that repo's
`releases/download/`. It never looks at the source tree. So the monorepo's release job can publish *there*
with the Releases API, from here, and the client keeps working unchanged:

```
gh release create "snapshot-<version>" <nupkg> <nupkg>.sha512 \
  --repo DbDataSync/DbDataSync --prerelease \
  --target <a commit that already exists in the old repo> \
  --notes-file notes.md
```

- **Tag names stay as the client expects** (`snapshot-<version>`, bare-version for stable) on the old repo.
  The `dbdatasync/`-prefixed names are for tags in *this* repo, and each snapshot's notes record the
  monorepo commit SHA it was built from.
- **The tag has to point at something.** A release needs a tag, and a tag needs a commit that exists in that
  repo. The monorepo's SHAs don't exist there, and pushing them would be pushing code. Use the old repo's
  final `main` commit (`f1d76ee`) as `--target`. It is a correct, frozen pointer, just not the source the
  build came from; the notes say so, and the release assets are what matters to the client.
- **Credential:** the pipelines App, installed on both repos, minting one token scoped to both
  (`actions/create-github-app-token` with both repositories). No new long-lived secret. The App needs
  Contents: read/write on the old repo, which is the permission that creates releases and tags.
- **Pruning** (keep newest 20, `gh release delete --cleanup-tag`) works the same way with `--repo`.
- **The old repo must not be archived.** An archived repository rejects new releases. Leave it open, freeze it
  with a ruleset that blocks all pushes and tag creation except the App, and put a README pointer to this repo.

This is a bridge, not the end state. Moving the update client to read this repo's releases is a code change
and out of scope for now; do it once all three products are in and NuGet is back.

### Other things tied to the old repo

- **GHCR image name** is per product (`ghcr.io/<owner>/dbdatasync`). A package created by the old repository's
  workflow belongs to that repository: before the first push from here, add `DbDataSync/DbDataKit` with Write
  under the package's "Manage Actions access", or the push is refused.
- **Packed README links.** `tools/docs/nuget-readme.mjs` pins the NuGet README's links to
  `RepositoryUrl/blob/<sha>/docs/…`, and `RepositoryUrl` in `DbDataSync.Cli.csproj` is the old repository. In the
  monorepo those links would point at a commit and path that don't exist there. This is invisible while NuGet is
  paused, but must be fixed (a code change) before it resumes.
- **Existing tags** (`release/v*`, `snapshot-*`) were not carried over by the subtree import. They stay on
  the old repo, which is also where the update client expects them.

## Implementation status

Everything runs on the built-in `GITHUB_TOKEN`; no GitHub App, no rulesets.

| file | state |
| --- | --- |
| `dbdatasync-ci.yml` | trunk: `changes` and the ported suite (Linux, Windows, integration, web, Playwright), on pull requests and `main`. |
| `dbdatasync-release.yml` | dispatch against `main` (or `sha`); needs a green CI run for the commit; packs and checks the nupkg; tag `dbdatasync/v<version>`; GitHub Release in this repo with the package and its `.sha512`. |
| `dbdatasync-publish-image.yml` | dispatch-only; not chained from the release. |
| `dbdataproxy-ci.yml` | trunk: `changes`, the Windows suite (with the package smoke test and installer tests) and the Linux suite against the shared SQL Server. |
| `dbdataproxy-release.yml` | dispatch against `main` (or `sha`); needs a green CI run for the commit; Windows zip; tag `dbdataproxy/v<version>`; GitHub Release with the standard set. |
| `dbdatabuild-ci.yml` | trunk: `changes`, the unit suite on Linux and Windows, the core conformance groups, and the slow engines after a merge or with `ci:full`. |
| `dbdatabuild-conformance-full.yml` | every engine in one job: nightly on `main`, on request, and as the gate inside the release. |
| `dbdatabuild-release.yml` | dispatch against `main` (or `sha`); full conformance on that commit; builds and smoke-tests linux-x64 and win-x64; tag `dbdatabuild/v<version>`; GitHub Release; a stable release also replaces the assets of the fixed `dbdatabuild/dist` release. |
| `dbdatakit-ci.yml` / `_dbdatakit.yml` | the shared libraries' suite, once, on Linux and Windows with the shared engines. |

**DbDataBuild, not moved:** the native library (`polyglot_sql_ffi`) keeps its old arrangement for now. `DbDataBuild/scripts/fetch-native.sh` and
`publish-native.sh` still use the old repository's `native-<pin>` release, and its manual workflow stays where it was, unported, in
`DbDataBuild/.github/workflows/native.yml`. Whether to fetch the library straight from its own upstream release is open.

Consequences:
- **No enforcement.** Anyone with write access can push to `main`; the model is followed by convention.
- **Releases are in this repo, and `dbdatasync update` reads them from here.** See "How the app tells products apart". Stable and beta come
  from this repository's GitHub releases; the old repository is no longer a source.
- **No snapshots, no image chain, no NuGet** until they are brought back (the client's snapshot channel is simply empty).

## Rollout

1. **Done:** DbDataSync, DbDataProxy and DbDataBuild imported with their history (merge commits, so it survives); `shared/` renamed to
   `DbDataKit/`; each product's workflows ported to the root and moved to the trunk model.
2. **To do (by hand, in the GitHub UI):** delete the retired lane branches (`dev`, `test`, `dbdatasync/dev`, `dbdatasync/test`,
   `dbdataproxy/dev`, `dbdataproxy/test`, `dbdatabuild/dev`, `dbdatabuild/test`); nothing triggers on them.
3. Freeze the old repositories (do not archive DbDataSync's: an archived repository rejects new releases, and the native library for
   DbDataBuild is still fetched from its old release).
4. When wanted: re-enable NuGet and the image chain, and bring snapshot releases back.

## Open questions

1. **A ruleset on `main`** (pull request required, linear history), if more people commit. See "Branch protection".
2. **DbDataBuild's native library:** keep fetching the prebuilt `native-<pin>` release from the old repository, move it here, or take it
   straight from its upstream release.
3. **A nightly job for DbDataSync and DbDataProxy**, if their per-push suites are not enough.
