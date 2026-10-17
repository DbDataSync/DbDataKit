# Monorepo branching, CI and releases

Status: proposed. Supersedes the "monorepo rejected" decision in [00-REPO-STRATEGY.md](00-REPO-STRATEGY.md): the
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
- **`shared/` has no lane of its own.** Shared code is changed through the product branches, like any other
  code (see "Shared code" below).
- **All products use CalVer** (`YYYY.MM.DD.HHmm`, as DbDataSync does). Proxy's and Build's semver tags end
  when they move in.
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

Because `shared/` is changed through product branches, three things follow:

1. **Product CI runs the consumers.** A product's CI always builds and tests its own directory; if the change
   touches `shared/**` it also builds and tests *every* product against it, since `shared/` is consumed at
   the same commit through project references. A shared change cannot go green by only checking its author's
   product.
2. **The lane guard allows `shared/**`** (and the product's own directory) and rejects anything else on a
   `<product>/dev` push or PR, so a stray edit to another product's directory can't make the release merge
   conflict.
3. **The release merge can conflict on `shared/`** if two products changed the same shared file. The ancestor
   check above is what turns that into a visible "sync first" instead of a surprise at release time: the
   second product must have the first one's shared change merged in, and its CI re-run, before it can release.

Root files (`LICENSE`, `.gitattributes`, `docs/`) don't have a lane either; either allow a small, explicit
carve-out in the lane guard or route them through whichever product's branch is touching them.

## Branch protection

Repository rulesets, carried over from DbDataSync with wildcard patterns:

| ruleset | targets | rules |
| --- | --- | --- |
| Pipelines only | `main`, `*/test` | `update`, `deletion`, `non_fast_forward`; bypass = the pipelines GitHub App only |
| (none) | `*/dev` | open, as today |

The **DbDataSync Pipelines** GitHub App (id 5026881) is currently installed on the DbDataSync repo only. It
must be installed on this repo (Contents: read/write) before any ruleset is enabled. Follow DbDataSync's
"ordering trap" lesson: land the workflows first, lock `main` first, `*/test` last.

## CI

- **Path-scoped.** One workflow per product (`ci-dbdatasync.yml`, ...) built on a shared reusable workflow.
  Each triggers on `push` to `<product>/dev` and `pull_request` to `<product>/dev` (carrying over DbDataSync's
  "CI on `dev` only" decision), with `paths:` limiting it to `<product>/**` plus `shared/**`.
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
- **Reuse.** Common steps (restore/build/test matrix, app-token mint, ff-push helper) live in
  `.github/workflows/_*.yml` reusable workflows, which is what plan 00 already intended for the hub.

## Releases, versions and tags

All products use CalVer, `YYYY.MM.DD.HHmm`, computed from the UTC clock as DbDataSync does today.

| | DbDataSync | DbDataProxy | DbDataBuild |
| --- | --- | --- | --- |
| release trigger | dispatch against `dbdatasync/test` | dispatch against `dbdataproxy/test` | dispatch against `dbdatabuild/test` |
| tag in this repo | `dbdatasync/v<version>` | `dbdataproxy/v<version>` | `dbdatabuild/v<version>` |
| snapshot tag in this repo | `dbdatasync/snapshot-<version>` | n/a yet | n/a yet |

Common release steps, from DbDataSync: refuse unless dispatched against `<product>/test`; require `main` to be
an ancestor of it; require a green CI run for that exact SHA (look it up, don't re-run); build and publish;
tag after the publish is confirmed; merge into `main`; back-merge into the other `dev` branches. Proxy and
Build release on a tag push from `main` today; they adopt the dispatch model when they move in.

### NuGet

Paused. The release workflow skips the nuget.org push (and the Trusted Publishing token exchange) behind an
explicit switch, defaulting to off, so the rest of the release (build, smoke test, tag, GitHub Release,
`main` merge) still runs. When it's turned back on, the Trusted Publishing policy must be re-registered for
`DbDataSync/DbDataKit` and the new workflow filename, because that policy is bound to repo and workflow file.

### GitHub Releases on the old repo, without pushing code there

`dbdatasync update` reads releases from the old repo (`DbDataSync/DbDataSync`, a constant in
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

- **GHCR image name** (`publish-image.yml`) follows the repo. Decide whether the image keeps its name.
- **Existing tags** (`release/v*`, `snapshot-*`) were not carried over by the subtree import. They stay on
  the old repo, which is also where the update client expects them.

## Rollout

1. **Done:** DbDataSync imported with history (`DbDataSync/`), submodule removed.
2. Cut `dbdatasync/dev` and `dbdatasync/test` from the import commit (DbDataSync's `main`, `dev` and `test`
   were all at `f1d76ee`, so they start identical). Set `main` as the default branch.
3. Port DbDataSync's workflows to the root with the path/working-directory changes, branch names, promotion
   folded into CI, and the NuGet switch off. Run them with no rulesets, on a throwaway change.
4. Install the pipelines App on this repo (and confirm it on the old one), add the rulesets (`main` first,
   then `*/test`), and do one beta release (`beta=true`) to prove the whole path: old-repo release, `main`
   merge, back-merge.
5. Freeze the old repo as above (not archived).
6. Repeat 2–5 for DbDataProxy and DbDataBuild when their active work reaches a stopping point; they bring
   their history the same way and need the dispatch-release workflow written for them, and a decision on
   how their old tags map to CalVer.
7. When all three are in: re-enable NuGet, repoint the update client, and retire the bridge.

## Open questions

1. GHCR image: keep the current name, or rename it with the monorepo?
2. Proxy and Build have existing semver tags (`v0.2.0`, `v0.4.0`). Leave them as history, or add CalVer
   aliases?
3. Root files (`LICENSE`, `docs/`): carve-out in the lane guard, or ride along with a product branch?
