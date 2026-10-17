# Repo Strategy: Hub and Spokes

Status: **superseded by [09-MONOREPO-BRANCHING-AND-RELEASES.md](09-MONOREPO-BRANCHING-AND-RELEASES.md)** — the monorepo option was later chosen; this records the earlier hub-and-spokes decision. Original text: how DbDataSync, DbDataProxy, DbDataBuild and the shared code are organised.

## Decision

- **Products stay as separate repos** (same org after the planned move): DbDataSync, DbDataProxy, DbDataBuild.
- **DbDataKit is the hub**: it holds the shared code (`DbDataKit/`), cross-project docs and landing page, workspace
  tooling, reusable CI templates, and the contract-test CI.
- **Products consume shared code as versioned NuGet packages.** The hub builds and publishes them.
- **Workspace checkout** uses either git submodules or sibling clones, driven by one script (`dev.ps1`).
  Submodules are used initially (already added); the script hides them, so switching to a manifest + sibling
  clones later is a script change, not a workflow change.
- **Rejected:** a monorepo (for now), and branch-per-product with no shared root. See "Alternatives".

Re-evaluate after the first three shared packages ship (see "Exit criteria").

## Why

The pain to solve is not separation itself but three things that tooling can fix without merging repos:

1. Local cross-repo development (branching, building and testing against sibling source).
2. Cross-repo validation (a shared change must not break a consumer).
3. Process drift (CI, release, agent instructions diverging per repo).

Separate repos keep simple per-product CI and release, scoped agent context, and per-product release
cadence (Build is pre-1.0, Sync is shipping betas, Proxy is a service).

## Layout

```
DbDataKit/
  DbDataSync/          submodule (product)
  DbDataProxy/         submodule (product)
  DbDataBuild/         submodule (product)
  DbDataKit/              shared packages (DbDataKit.*), one solution, independently versioned
    src/ tests/ Directory.Build.props Directory.Packages.props
  docs/
    index.md           landing page: what each project is, how they relate, why they differ
    review/            review findings
    plans/             implementation plans (this folder)
  tools/
    dev.ps1            workspace commands
    dev.config.json    repo manifest (name, url, branch, path)
  .github/workflows/
    reusable-ci.yml            called by products
    reusable-release.yml       called by products
    contract.yml               builds all products against shared source
  CLAUDE.md            cross-project rules and "which repo owns what" map
```

## Workspace script (`tools/dev.ps1`)

Reads `tools/dev.config.json`; each entry has `name`, `url`, `branch`, `path`, and `mode` (`submodule` or
`clone`). Commands (all operate on every repo unless `-Only` is given):

| Command | Behaviour |
|---|---|
| `init` | Clone or `submodule update --init` everything; set `submodule.recurse=true`. |
| `status` | One-line-per-repo: branch, ahead/behind, dirty, pointer drift. |
| `pull` | Fast-forward each repo to its tracked branch; refuse on dirty trees. |
| `branch <name>` | Create the same branch name in every repo (or `-Only`). |
| `test` | Build and test each product with its own solution; shared tests too. |
| `local-on` / `local-off` | Toggle source-override (below). |
| `pr` | `gh pr create` per repo with a common title and cross-links in each body. |
| `pins` | Show which shared package versions each product references. |

Rules: no destructive commands (no reset, force-push, clean); the script prints what it will do and stops on
any dirty tree or non-fast-forward. Pointer bumps in the hub are a separate, explicit `dev.ps1 pin` step.

## Source override for local cross-cutting work

Each product's `Directory.Build.props` imports an optional `Directory.Build.local.props` (git-ignored) that
`local-on` generates. When present it sets `UseLocalDbDataShared=true`, and a shared `.targets` file swaps
`PackageReference Include="DbDataKit.*"` for `ProjectReference` to `../DbDataKit/DbDataKit/src/<pkg>`.
CI never creates the file, so published builds always use released packages. The swap logic lives in the
`DbDataKit.Build` props package so each product imports it once.

## Cross-repo validation

`contract.yml` in the hub runs on: any change under `DbDataKit/`, nightly, and manual dispatch. It checks out
the products at `main`, enables the source override, then builds and tests each. A failing product blocks the
shared PR. Products additionally run `dotnet list package --vulnerable` and `npm audit` in their own CI.
Dependabot or Renovate in each product opens PRs when a new shared package version is published.

## Process unification

- **Reusable workflows** in the hub (`reusable-ci.yml`, `reusable-release.yml`) called by products with inputs:
  solution path, project name, tag prefix, publish target. Actions are SHA-pinned; `permissions:` is
  least-privilege. NuGet Trusted Publishing stays per repo; each repo registers its own caller workflow.
- **`DbDataKit.Build` props package**: central package versions, analyzers, SourceLink, nullable/warnings policy,
  the source-override targets.
- **`CLAUDE.md` skeleton** in every repo: build, test, release commands; where shared code comes from;
  "shared behaviour changes go to the hub".
- **One release skill pattern**: generalise Sync's `nuget-release` so releasing any product or shared package
  is the same request with a different tag prefix.

## Agent workflow

- Product task: start the session in the product repo. Focused context, product CLAUDE.md, product skills.
- Cross-cutting task: start in the hub, `dev.ps1 branch <name>`, work across `DbDataKit/` and the product
  submodules, `dev.ps1 test`, `dev.ps1 pr`.
- Shared packages' public API is documented (`DbDataKit/docs/api/*.md`) and versioned with a changelog, so a
  product session rarely needs to read shared internals.
- Name collisions (three `Dialect`-like classes today) are a hazard until consolidation completes; scope
  searches by folder.

## Alternatives considered

| Option | Verdict |
|---|---|
| Monorepo (`products/*`, `DbDataKit/`) | Best for atomic cross-cutting change and CI reuse; worse for per-product release, scoped agent context and Sync's size. Kept as the fallback. Migration is `git subtree add` per product, so this decision is reversible. |
| Branch per product, no shared root | Rejected: shared issues, tags and Actions namespace; default-branch quirks (Dependabot, `workflow_dispatch`, CODEOWNERS); no atomic cross-branch change; confusing for people and agents. |
| Submodules only, no script | Rejected: detached HEADs and pointer-bump churn without tooling. |
| Separate repos, no hub | Rejected: shared code has no home and CI drifts. |

## Exit criteria (when to reconsider a monorepo)

Review after the first three shared packages ship. Move to a monorepo if, over that period:

- more than about half of merged PRs touch more than one repo, or
- `dev.ps1` and `contract.yml` need frequent manual intervention, or
- version skew between products causes repeated breakage.

Move to separate repos for a product (or keep it out of any monorepo) if it may be open-sourced, sold or
handed to another team.

## Migration steps

1. Move the three repos to the same org; update `.gitmodules` URLs (`git submodule sync`).
2. Create `DbDataKit/` with the CI baseline (central package versions, pinned Actions, vulnerability scan).
3. Add `tools/dev.ps1` and `dev.config.json`; verify on a clean clone.
4. Add reusable workflows and `contract.yml`; point one product (Proxy, smallest) at them first.
5. Extract `DbDataKit.Odbc.Metadata` as the first shared package (see plan 04).
6. Add `docs/index.md` landing page and CLAUDE.md files.
7. Roll the reusable workflows to Build and Sync.
