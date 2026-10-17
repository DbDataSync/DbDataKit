# Monorepo branching, CI and releases

Status: decided and in place for DbDataSync (merged to `main`; `dbdatasync/dev` and `dbdatasync/test` exist). The workflows have not yet run end to end; see "Implementation status". Supersedes the "monorepo rejected" decision in [00-REPO-STRATEGY.md](00-REPO-STRATEGY.md): the
three products move into this repo (DbDataSync first, DbDataProxy and DbDataBuild later). This document adapts
DbDataSync's `dev` → `test` → `main` model (`DbDataSync/architecture/branching-and-releases.md`) to several
products in one repository.

## Decisions

- Each product gets its own **`dev` and `test` branches, prefixed by the product's full lowercase name**:
  `dbdatasync/dev`, `dbdatasync/test`, `dbdataproxy/dev`, `dbdataproxy/test`, `dbdatabuild/dev`,
  `dbdatabuild/test`. No abbreviations.
- **`main` is the integration branch for all products.** It holds the last released state of every product
  plus the shared code and docs, and it is the repository's default branch.
- DbDataSync's rules carry over per product: work lands on `<product>/dev`; nobody pushes to `<product>/test`
  or `main`; pipelines move them.
- **Layout:** every product and the shared code is a top-level folder: `DbDataSync/`, `DbDataProxy/`,
  `DbDataBuild/`, and `DbDataKit/` for the shared code (renamed from `shared/`). `LICENSE` is at the root.
  Each product owns its own `docs/`.
- **Container images are named per product** (`ghcr.io/<owner>/dbdatasync`, ...). Where the image workflow lives
  (here or in a product repo) doesn't matter to the name; for now it lives here with the product's release.
- **`DbDataKit/` has no lane of its own.** Shared code is changed through the product branches, like any other
  code (see "Shared code" below).
- **All products use CalVer** (`YYYY.MM.DD.HHmm`, as DbDataSync does). Proxy's and Build's existing semver
  tags and versions are not carried forward; there is no alias or mapping.
- **Back-merge is immediate**: a release merges `main` into the other products' `dev` branches as its last step.
- **NuGet publishing is paused** until all three products are in this repo. DbDataSync is the only product
  that has ever published to nuget.org.

```
dbdatasync/dev ──CI green──► dbdatasync/test ──release (dispatch)──► main (merge commit, tag dbdatasync/v<ver>)
dbdataproxy/dev ─CI green──► dbdataproxy/test ─release (dispatch)──► main (merge commit, tag dbdataproxy/v<ver>)
dbdatabuild/dev ─CI green──► dbdatabuild/test ─release (dispatch)──► main (merge commit, tag dbdatabuild/v<ver>)
                                                                      │
                          main ──immediately, after the merge──► every other <product>/dev
```

## Layout

```
DbDataKit/                    repository root
  LICENSE
  .github/workflows/          per-product sets: dbdatasync-ci.yml, dbdatasync-release.yml, ...
  DbDataSync/                 product (imported with its history)
  DbDataProxy/                product (still a submodule until it moves in)
  DbDataBuild/                product (still a submodule until it moves in)
  DbDataKit/                  shared libraries (DbDataKit.*), one solution: DbDataKit.Shared.slnx
  docs/                       cross-project plans from before the merge
```

GitHub only runs workflows from the root `.github/workflows/`, so each product's workflows live there with the
product's name as a prefix and run their steps inside that product's folder.

**Root `docs/` cleanup is deferred.** Each product has its own docs, so what is in the root `docs/` should
eventually be moved into the product that owns it, or removed. It isn't urgent and needs no special lane:
those changes can be merged from any one product's `dev` branch.

## What changes from the single-product model

**`main` can no longer be a fast-forward.** In DbDataSync, releasing fast-forwards `main` to the released
commit, so `main` is "the last release and nothing else". With three products on independent cadences, the
released commits diverge and no single product's SHA can be `main`. The release now **merges `<product>/test`
into `main` with a merge commit** (`--no-ff`), after the release is confirmed published. Products live in
disjoint directories, so those merges don't conflict. `main` still means "released state", but per product:
the tree under `DbDataSync/` is whatever `dbdatasync/v<version>` shipped.

**Immediate back-merge.** The last step of a release, after `main` has moved, merges `main` into each *other*
product's `dev` branch. The released product's own `dev` already contains everything in the release; it picks
up the other products' releases the next time one of them back-merges, or on the next release of its own (the
ancestor check below forces that). A back-merge that conflicts fails the job loudly and opens an issue; it
never guesses. The push is made with the pipelines App token, so it triggers that branch's CI and the normal
promotion, with no special handling.

**A release requires `main` to be an ancestor of `<product>/test`.** If another product released since this
one last synced, the release refuses and says to wait for the back-merge to land. This is what stops two
products from racing each other into conflicting shared-code merges.

## Shared code

Because `DbDataKit/` is changed through product branches, three things follow:

1. **Product CI runs the consumers.** A product's CI always builds and tests its own directory; if the change
   touches `DbDataKit/**` it also builds and tests *every* product against it, since `DbDataKit/` is consumed at
   the same commit through project references. A shared change cannot go green by only checking its author's
   product.
2. **The lane guard allows `DbDataKit/**`** (and the product's own directory) and rejects anything else on a
   `<product>/dev` push or PR, so a stray edit to another product's directory can't make the release merge
   conflict.
3. **The release merge can conflict on `DbDataKit/`** if two products changed the same shared file. The ancestor
   check above is what turns that into a visible "sync first" instead of a surprise at release time: the
   second product must have the first one's shared change merged in, and its CI re-run, before it can release.

Root files (`LICENSE`, `.gitattributes`, `docs/`) have no lane either: they are merged from whichever product's
`dev` branch is touching them, and the lane guard allows them.

## Branch protection

> **Deferred.** Not in place; see "Implementation status".


Repository rulesets, carried over from DbDataSync with wildcard patterns:

| ruleset | targets | rules |
| --- | --- | --- |
| Pipelines only | `main`, `*/test` | `update`, `deletion`, `non_fast_forward`; bypass = the pipelines GitHub App only |
| (none) | `*/dev` | open, as today |

The **DbDataSync Pipelines** GitHub App (id 5026881) is currently installed on the DbDataSync repo only. It
must be installed on this repo (Contents: read/write) before any ruleset is enabled. Follow DbDataSync's
"ordering trap" lesson: land the workflows first, lock `main` first, `*/test` last.

## CI

- **Product-scoped by a `changes` job, not `paths:`.** One workflow per product (`dbdatasync-ci.yml`, ...). It
  triggers on every `push` and `pull_request` to `<product>/dev` (carrying over DbDataSync's "CI on `dev` only"
  decision). A first job diffs against `<product>/test` (the last validated commit) and the heavy jobs run only if
  `<product>/**`, `DbDataKit/**` or the product's workflow files changed. A `paths:` filter was rejected: the
  post-release back-merge only brings *other* products' files, a path-filtered run would never fire for it, `test`
  would never advance, and the next release's ancestor check would block forever. With `changes`, such a push runs
  no suite but is still promoted.
- **Working directory.** DbDataSync's workflows assume the product is the repo root (`DbDataSync.slnx`,
  `tools/`, `docker/`). They need `defaults.run.working-directory`, path updates, and cache/artifact keys
  prefixed by product. This is a workflow edit, not a code change, but it is real work and is the main risk
  in the first phase.
- **Promotion inside CI, not `workflow_run`.** DbDataSync promotes with a separate `workflow_run` workflow,
  which GitHub reads from the *default branch* — hence the documented ordering trap and "a new workflow does
  nothing until a release puts it on `main`". Here it would be worse: `main` moves only at releases, so every
  lane would depend on the same stale copy. Proposal: make `promote` (and snapshot publishing) the final jobs
  of the product's CI run, gated on `needs:` the test jobs and `github.event_name == 'push'`. The promotion
  then uses `github.sha` directly, which also resolves DbDataSync's open question about promoting the exact SHA
  CI passed on.
- **Reuse.** Per-product workflows are kept as straight ports of DbDataSync's, so DbDataProxy and DbDataBuild
  can be added the same way. Extracting shared steps into reusable `_*.yml` workflows is worth doing once a second
  product exists to show which steps are actually common.

## Releases, versions and tags

All products use CalVer, `YYYY.MM.DD.HHmm`, computed from the UTC clock as DbDataSync does today.

| | DbDataSync | DbDataProxy | DbDataBuild |
| --- | --- | --- | --- |
| release trigger | dispatch against `dbdatasync/test` | dispatch against `dbdataproxy/test` | dispatch against `dbdatabuild/test` |
| tag in this repo | `dbdatasync/v<version>` | `dbdataproxy/v<version>` | `dbdatabuild/v<version>` |

Common release steps, from DbDataSync: refuse unless dispatched against `<product>/test`; require `main` to be
an ancestor of it; require a green CI run for that exact SHA (look it up, don't re-run); build and publish;
tag after the publish is confirmed; merge into `main`; back-merge into the other `dev` branches. Proxy and
Build release on a tag push from `main` today; they adopt the dispatch model when they move in.

### NuGet

> **Deferred**, together with everything else that needed the App; see "Implementation status".


Paused. The release workflow skips the nuget.org push (and the Trusted Publishing token exchange) behind an
explicit switch, defaulting to off, so the rest of the release (build, smoke test, tag, GitHub Release,
`main` merge) still runs. When it's turned back on, the Trusted Publishing policy must be re-registered for
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

**Simplified for now.** Locked branches are not being pursued yet, so the workflows on `main` run on the built-in
`GITHUB_TOKEN` and leave out everything that needed the pipelines GitHub App. Nothing below the line "Deferred" is
implemented; the full versions (App token, old-repo releases, snapshots, image chain, NuGet switch) are in PR #1's
history (`b762d5b`) and can be restored.

| file | state |
| --- | --- |
| `dbdatasync-ci.yml` | `changes`, `dbdatakit`, the ported suite, and `promote` (fast-forwards `dbdatasync/test` with `GITHUB_TOKEN`). Also runnable by dispatch. |
| `dbdatasync-release.yml` | dispatch on `dbdatasync/test`; ancestor and CI checks; tag `dbdatasync/v<version>`; GitHub Release **in this repo**; merge into `main`; back-merge into every `*/dev` and dispatch that product's CI. |
| `dbdatasync-publish-image.yml` | dispatch-only; no longer chained from the release. |

Consequences of the simplification:
- **No rulesets, no App.** Anyone with write access can push to `main` and `*/test`; the model is followed by
  convention, not enforced.
- **Pushes with `GITHUB_TOKEN` start no workflows.** That is why `promote` lives inside the CI run, and why the release
  dispatches CI by hand after each back-merge.
- **Releases are in this repo, and `dbdatasync update` reads them from here.** See "How the app tells products apart".
  Stable, beta and snapshot all come from this repository's GitHub releases; the old repository is no longer a source.
- **No snapshots, no image, no NuGet** until they are brought back.

**Deferred:** the pipelines App and rulesets (`main`, `*/test`), snapshot releases (the client's snapshot channel is
simply empty until they come back), the container image chained from the release, NuGet publishing, the lane guard.

## Rollout

1. **Done:** DbDataSync imported with history (`DbDataSync/`), submodule removed; `shared/` renamed to
   `DbDataKit/` (the solution builds from the new path); DbDataSync's workflows ported; merged to `main` (PR #1,
   as a merge commit so the imported history survives).
2. **Done:** `dbdatasync/dev` and `dbdatasync/test` cut from `main` (both at the merge commit). Stale-reference
   cleanup (release skill, dead workflow copies, doc banners) goes to `dbdatasync/dev` like any other change.
3. Run CI on a throwaway change to exercise `changes`, the suite and the `promote` job, then a `--beta` release.
4. Install the pipelines App on this repo (and confirm it on the old one), add the rulesets (`main` first,
   then `*/test`), and do one beta release (`beta=true`) to prove the whole path: old-repo release, `main`
   merge, back-merge.
5. Freeze the old repo as above (not archived).
6. Repeat 2–5 for DbDataProxy and DbDataBuild when their active work reaches a stopping point; they bring
   their history the same way and need the dispatch-release workflow written for them.
7. When all three are in: re-enable NuGet, repoint the update client, and retire the bridge.

## Open questions

1. **Deferred:** the lane guard (reject edits outside `<product>/`, `DbDataKit/`, `docs/` and root files on a `<product>/dev`
   push) is described here but not written yet. Do it as a CI job, or as a ruleset path restriction?
