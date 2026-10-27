# Implementation Plans: Shared Modules

Plans for extracting shared code into `DbDataKit/DbDataKit/`, derived from the code review (`docs/security-review.md` and the products' reviews: `DbDataSync/architecture/review.md`, `DbDataProxy/docs/review.md`, `DbDataBuild/docs/review.md`). Each plan lists the
current state per product (with file references from the review), design, package layout, phases with exit
criteria, risks and acceptance. Plans are proposals; review the "decisions" in each before starting.

| # | Plan | Main inputs | Key payoff |
|---|---|---|---|
| 00 | [Repo strategy](00-REPO-STRATEGY.md) | hub-and-spokes decision | Cross-cutting changes without a monorepo |
| 01 | [Config / definitions](01-config-definitions.md) | Build strict YAML, Sync typed YAML, Proxy json | Strict parsing everywhere, `schemaVersion` + migrations, Proxy startup validation |
| 02 | [Secrets](02-secrets.md) | Sync keyring, Build env logins, Proxy plaintext | One reference syntax, no plaintext, one redactor |
| 03 | [Windows service / hosting](03-windows-service-hosting.md) | Proxy `ServiceInstaller`, Sync service + certificates | Least-privilege install, ACLs, certs, child hosting |
| 04 | [ODBC and metadata](04-odbc-metadata.md) | Proxy `DbDataKit.Odbc.Metadata` | Extract and harden; neutral catalog reader |
| 05 | [SQL dialect and types](05-sql-dialect.md) | Sync dialects, Build `DialectProfile`, Proxy Arrow map | One quoting, lexer, canonical types, conformance suite |
| 06 | [Connections / providers](06-connections.md) | Sync `IDriver`, Proxy `IDbProvider`, Build logins | One definition, composer, read-only recipes, failure model |
| 07 | [Data transfer, staging, loading](07-data-transfer.md) | Sync pipeline, Build copy models | Shared converters, bulk writers, strategies, write gate |
| 08 | [Install and update](08-update-system.md) | Sync Updates, Proxy install.ps1, Build Scoop | Signed manifest, service swap with rollback |
| 09 | [Monorepo branching, CI, releases](09-MONOREPO-BRANCHING-AND-RELEASES.md) | DbDataSync model, per-product lanes | Per-product dev/test, main as integration |
| 10 | [Proxy adoption](10-proxy-adoption.md) | Plans 01-07 applied to DbDataProxy | Hub CI and publishing, feed and override, per-library adoption order (update system skipped) |
| 11 | [CI and testing](11-ci-and-testing.md) | Three products plus the shared libraries, their test setups | One engine catalogue, shared workflows, Linux and Windows split, nightly full matrix |
| 12 | [Shared TLS rollout](12-shared-tls-rollout.md) | Sync and Proxy certificates, services, systemd | One certificate, service and unit implementation |
| 13 | [Suite standardization](13-suite-standardization.md) | Adoption state of plans 01-12, DuckDB 2.0, Sync scripting | dbdatakit console, product servers with no UI of their own, console-to-server trust, one grammar, shared scheduling, Build authoring; DuckDB 2.0; dotnet-script |
| 13a | [Suite slices](13a-suite-slices.md) | Plan 13 | About 150 small pull requests with a test each, grouped into milestones, each naming the decision it waits on |
| 14 | [Semantic layer](14-semantic-layer.md) | QueryIntentSpec at `139bbf9`, Build metadata, Proxy | Proposal for review (not in 13a): semantic models authored and deployed by Build and hosted by Proxy, one connection managed in Build, one engine registry, semantic SQL, MDX for Excel over XMLA, more protocols |
| 15 | [DbDataStore: master data and documents](15-master-data-and-documents.md) | Owner's direction, plans 13 and 14 | Proposal for review (not in 13a): user- and agent-defined models on SQL engines, documents with WebDAV, extraction, consumed by Sync, Build and semantic queries |
| 16 | [Embedded agents and apps](16-embedded-agents-and-custom-front-ends.md) | Owner's backlog, plans 13 to 15 | Backlog (not scheduled): agents in the console on OpenAI, Anthropic and Microsoft Foundry, every action approved by the user; a standalone app host with DbDataStore data, Vega-Lite dashboards, reports and assistants; Windows, OpenID Connect and Entra sign-in |
| 17 | [Report servers on the semantic models](17-report-servers-on-semantic-models.md) | Plans 14 and 16, Power BI Report Server's supported sources | Documented scenarios (not in 13a), decided per route later: published views, result feeds for many tools, ODBC, Postgres wire with a compatibility layer kept apart from semantic SQL, MDX only via the Excel work |
| 18 | [Lineage across the suite](18-lineage.md) | Build's column lineage, Sync's mappings, Proxy's data sources, plans 14-16 | Proposed: one suite graph (design and run lineage), column level where known and table level otherwise, shaped for OpenLineage export later; each server owns its lineage, the console joins it; manual entries and export later |
| 19 | [The next wave, in parallel lanes](19-next-wave.md) | Plans 13a, 14, 15, 16, 18 | Proposed: slices J (semantic layer), H (schema), N (serving), Y (visuals), Z (DbDataStore 1-2), with G from plan 18, grouped into lanes and waves, and the areas where lanes must be sequenced |
| 20 | [One serving layer](20-serving-layer.md) | Plans 13-19, the owner's direction | Direction decided: every end-user endpoint (Flight SQL, semantic, DbDataStore reads and writes, WebDAV, identity, apps, later Postgres wire, XMLA, feeds) as modules of one layer in the Proxy code base (a rename later); runs from published definitions, so it stays up while back-ends are patched; groups of instances on shared state for scale-out and availability |
| 21 | [Channels, previews and sandboxed writes](21-channels-and-previews.md) | Plan 20, the owner's direction | Two decisions: channels in one install (a channel is part of a published version's key), and sandboxed DbDataStore writes in a draft layer; previews read-only by default. promotion is per product with dependency checks, ordered releases with one approval, and expand-then-contract for shared structure. Connections inherit production's binding for reads and are overridden per channel; writes are never inherited. Build fills test targets by deferring unchanged models to production; Sync runs in production with trial runs; drafts are visible to the serving layer in their channel, not to Build; previews live with their workspace. Not sliced yet |
| 22 | [Filling the suite's major gaps](22-suite-gaps.md) | Plans 13-21, the data-flow diagram | Direction decided: app authoring on shared authoring with an apps back-end, an allow-listed framework and declarative views as components; row- and column-level rules in the serving layer keyed on directory claims and a DbDataStore grants entity; a catalog on the lineage graph; git remotes and database backups; one delivery service; OpenTelemetry. Not sliced yet |
| 23 | [First-run setup and data quality](23-first-run-and-data-quality.md) | Plans 13, 18, 20, 22 | Direction decided: a single-machine bundle paired automatically, a one-time setup link for the first administrator, and a setup checklist; one shared check model used by every product, with warn or block severity per check. Not sliced yet |
| 24 | [Upgrades, compatibility and caching](24-upgrades-and-caching.md) | Plans 13, 20, 21, 22 | Direction decided: the console drives upgrades with each product's installer; serving groups upgrade rolling after draining; N and N-1 compatibility checked at pairing and in CI; opt-in result caching in the serving layer, keyed on access-rule values, channel and version, cleared on data change with a time-to-live backstop. Not sliced yet |

## Dependency graph

```
01 config ──────────────┐
02 secrets ─────────────┼─► 06 connections ─► 07 transfer
05 dialect ─► 04 odbc ──┘          ▲
        └──────────────────────────┘
02 secrets ─► 03 hosting ─► 08 update   (08 also uses 01 for config migration)
```

## Suggested order

1. **Quick security fixes in place** (not shared work): Sync library install validation (SECURITY H1), Proxy read-only/pass-through/TLS key (H2-H4).
2. **00 + CI baseline** in the hub, then **04 Part A** (already in flight) as the first shared package.
3. **05 phase 1** (lexer + quoting) and **01 phase 1** (extract strict YAML): both small, both immediately fix known gaps.
4. **02** abstractions, then **06** (needs 02 and 05); **03** phases 1-3 in parallel (Proxy install hardening).
5. **05** dialect/type model, then **07** phase 0 (written strategy comparison) and onward.
6. **08** phases 1-2 (signed manifest + updater core) once 03 exposes `/version` and drain; service swap after 03 phase 4.

Parallelisable: 01, 02 and 05 have no dependencies on each other; 03 and 04 can proceed independently once 02/05 minimums exist.

## Cross-cutting conventions for all shared packages

- Independent SemVer per package; public API baselines (`PublicApiAnalyzers`); changelog per package.
- Central package management, SourceLink, deterministic builds, nullable on, warnings as errors in new projects.
- Each package ships a README with the API contract; consumers should not need to read internals (agent-friendly).
- Strangler migration: adapters in products first, delete duplicates only after parity tests; never a big-bang rewrite.
- Each plan's conformance/golden tests live with the package and run in the hub's `contract.yml` against all three products.
- Security acceptance criteria in each plan map to `docs/security-review.md` findings and are verified by tests, not review alone.
