# Monorepo branching, CI and releases

Status: proposed. Supersedes the "monorepo rejected" decision in [00-REPO-STRATEGY.md](00-REPO-STRATEGY.md): the
three products move into this repo (DbDataSync first, DbDataProxy and DbDataBuild later). This document adapts
DbDataSync's `dev` → `test` → `main` model (`DbDataSync/architecture/branching-and-releases.md`) to several
products in one repository.

## Decision

- Each product gets its own **`dev` and `test` branches, prefixed by product**: `sync/dev`, `sync/test`,
  `proxy/dev`, `proxy/test`, `build/dev`, `build/test`.
- **`main` is the integration branch for all products.** It holds the last released state of every product
  plus the shared code and docs, and it is the repository's default branch.
- DbDataSync's rules carry over per product: work lands on `<p>/dev`; nobody pushes to `<p>/test` or `main`;
  pipelines move them.

```
sync/dev ──CI green──► sync/test ──release (dispatch)──► main  (merge commit, tag sync/vX)
proxy/dev ─CI green──► proxy/test ─release (dispatch)──► main  (merge commit, tag proxy/vX)
build/dev ─CI green──► build/test ─release (dispatch)──► main  (merge commit, tag build/vX)
                                                          │
                          main ──back-merge after each release──► every <p>/dev
```

## What changes from the single-product model

**`main` can no longer be a fast-forward.** In DbDataSync, releasing fast-forwards `main` to the released
commit, so `main` is "the last release and nothing else". With three products on independent cadences, the
released commits diverge and no single product's SHA can be `main`. The release now **merges `<p>/test` into
`main` with a merge commit** (`--no-ff`), after the package is confirmed published. Because products live in
disjoint directories, those merges don't conflict. `main` still means "released state", but per product: the
tree under `DbDataSync/` is whatever `sync/vX` shipped.

**Back-merge.** Each `<p>/dev` periodically needs `main` merged in so it picks up the other products'
releases and any `shared/` change. A bot does this after every release (and fails loudly on conflict instead
of guessing). Without it, `<p>/dev` drifts from `shared/` and a later release merge could conflict.

**Lane rule.** A change on `<p>/dev` should touch only `<p>/`. A CI guard fails a push or PR to `<p>/dev`
that touches other paths, with an allowlist for deliberate cross-cutting edits. This is what keeps the
release merges conflict-free.

## Where `shared/`, `docs/` and root files go (decision needed)

Not covered by the product lanes. Options:

1. **A fourth lane, `shared/dev` → `shared/test` → `main`** (recommended). Same model, same protections.
   Its CI builds and tests *every* product against the change (the contract-test idea from plan 00), and its
   promotion requires that to be green. Consumers get it through the back-merge.
2. Shared changes ride along on whichever product's `dev` needs them. Simple, but a product can then release
   shared code the other products never validated.
3. Humans PR directly to `main`. Contradicts "nobody pushes to `main`".

Root files (`LICENSE`, `.gitattributes`, `docs/`) are low-risk: either put them in the `shared/` lane or allow
PRs to `main` for non-code paths. Pick one when we get there.

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

- **Path-scoped.** One workflow per product (`ci-sync.yml`, ...) built on a shared reusable workflow. Each
  triggers on `push` to `<p>/dev` and `pull_request` to `<p>/dev` (carrying over DbDataSync's "CI on `dev`
  only" decision), with `paths:` limiting it to `<p>/**` plus `shared/**`.
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
- **Shared-change CI** (if option 1 above): `shared/**` triggers all three product suites.
- **Reuse.** Common steps (restore/build/test matrix, app-token mint, ff-push helper) live in
  `.github/workflows/_*.yml` reusable workflows, which is what plan 00 already intended for the hub.

## Releases and tags

| | Sync | Proxy | Build |
| --- | --- | --- | --- |
| trigger | dispatch against `sync/test` | dispatch against `proxy/test` | dispatch against `build/test` |
| version | CalVer `YYYY.MM.DD.HHmm` (unchanged) | existing `v*` semver | existing `v[0-9]+.[0-9]+.[0-9]+*` semver |
| tag | `sync/v<version>` | `proxy/v<version>` | `build/v<version>` |
| snapshot tag | `sync/snapshot-<version>` | n/a | n/a |

Common release steps, from DbDataSync: refuse unless dispatched against `<p>/test`; require a green CI run for
that exact SHA (look it up, don't re-run); build and publish; tag after the publish is confirmed; merge into
`main` last, refusing rather than forcing. Proxy and Build today release on a tag push from `main`; they
adopt the dispatch model when they move in, not before.

### Things that break when releases move repos

These tie to the old repository and need a decision before the first monorepo release, not after:

- **nuget.org Trusted Publishing** is bound to a repo and workflow filename. The DbDataSync policy has to be
  re-registered for `DbDataSync/DbDataKit` and the new workflow name.
- **`dbdatasync update`** reads snapshots from `api.github.com/repos/<old repo>/releases`
  (`DbDataSync.Updates/ReleaseSources.cs`). Either keep publishing snapshot releases to the old repo, or ship
  a version that reads the new one and keep both populated through a transition. Changing the constant is a
  code change, so it is out of scope for the import but must be scheduled.
- **GHCR image name** (`publish-image.yml`) follows the repo; decide whether the image keeps its name.
- **Existing tags** (`release/v*`, `snapshot-*`) were not carried over by the subtree import. They remain on
  the old repo; the history is intact here.

## Rollout

1. **Done:** DbDataSync imported with history (`DbDataSync/`), submodule removed.
2. Cut `sync/dev` and `sync/test` from the import commit (DbDataSync's `main`, `dev` and `test` were all at
   `f1d76ee`, so they start identical). Set `main` as the default branch.
3. Port DbDataSync's workflows to the root with the path/working-directory changes, branch names, and promotion
   folded into CI. Run them with no rulesets, on a throwaway change.
4. Install the pipelines App, add the rulesets (`main` first, then `*/test`), and do one beta release
   (`beta=true`) to prove the whole path including the `main` merge and back-merge.
5. Retarget publishing (nuget policy, update feed, image) and archive the old DbDataSync repo.
6. Repeat 2–5 for DbDataProxy and DbDataBuild when their active work reaches a stopping point; they bring
   their history the same way and need the dispatch-release workflow written for them.

## Open questions

1. Where does `shared/` land: a fourth lane (recommended) or ride-along?
2. Back-merge: automatic bot after each release (recommended), or manual?
3. Update feed: keep the old repo's releases as the source of truth through a transition, or cut over at once?
4. Do Proxy and Build keep their semver schemes, or move to the CalVer used by Sync?
