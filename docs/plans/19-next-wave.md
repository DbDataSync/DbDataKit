# Plan 19: the next wave, in parallel lanes

Status: **proposed** (2026-10-26). Turns plans 14, 15, 16 (visuals) and 18 into slices, and groups them, together with what is left of
plan 13a, into **lanes** that separate agents can work at the same time without stepping on each other.

Same rules as plan 13a: one slice, one branch `suite/<id>-<short-name>`, one pull request, a "Done when" test, the status set in this file
in the same change. Slice letters are new (13a uses F K V S A B C T U E P R M W L X): **J** semantic layer, **H** schema, **N** serving,
**Y** visuals, **Z** DbDataStore, **G** lineage (plan 18). Plan 14's steps (Q1-Q14) and plan 15's (M1-M9) are the order; the slices
below are those steps cut small, and each row names the step it belongs to.

**Amended 2026-10-26 by [plan 20](20-serving-layer.md)** (one serving layer in the Proxy code base): lane Z builds
`DbDataKit.Store.*` as libraries that the serving layer hosts, so DbDataStore's own server keeps only models and structure. Lane N
extracts the code for the serving layer's modules. Plan 20's lane O runs beside them.

## 1. What is ready and what is not

| Ready to slice (this plan) | Needs a design conversation first (not sliced) |
| --- | --- |
| Semantic layer phase 1: QIS as `DbDataKit.Semantic.*`, Build semantic files, Proxy semantic data source, deploy and hosting (plan 14 Q1-Q10, Q12) | XMLA and MDX for Excel (Q13): waits on the VM catalogue; only the recording forwarder is sliced (J20) |
| Semantic SQL (Q11): the spec and the translator, after J01-J04 | Postgres wire (Q14) and the compatibility layer (plan 17 R4) |
| `DbDataKit.Schema` out of Build's planner (plan 15 M3) | Embedded agents beyond the agent tools (plan 16 section 1) |
| `DbDataKit.Serving.FlightSql` out of Proxy (plan 15 M9, first half) | DbDataApps (plan 16 section 2), reports and report servers (plans 16 2a, 17) |
| DbDataStore phases 1-2 (plan 15 M1, M2) | DbDataStore documents, extraction, promotion (M5-M8); its approval model |
| Shared visuals, `@dbdatakit/visuals` (plan 16 section 2a) | Lineage manual entries and export (plan 18, later) |
| Lineage, first version (plan 18) | |

## 2. Lanes

A lane is a sequence one agent can own. Lanes run in parallel; arrows inside a lane are the order. "Touches" is where a lane's changes
land, so two lanes that touch the same area are sequenced (section 4) instead of run side by side.

| Lane | Slices | Touches | Starts when |
| --- | --- | --- | --- |
| **J1** semantic engine | J01-J06 | new `DbDataKit/src/DbDataKit.Semantic*`, `DbDataKit/tests`, `dbdatakit-ci.yml` | now |
| **J2** semantic in Build | J07-J09 | `DbDataBuild.Define`, `.Models`, `.Cli`, templates | J04, and 13a B02 merged |
| **J3** semantic in Proxy | J10-J13 | `DbDataProxy.Server`, Proxy's store | J04 and N02 |
| **J4** semantic in the console and agents | J14-J15 | `DbDataKit/web/console`, the command surface | J10, Y02, 13a U08 |
| **J5** semantic SQL | J17-J19 | `DbDataKit.Semantic` (translator), QIS docs and fixtures | J04 |
| **J6** Excel traffic | J20 | a new tool under `DbDataKit/tools/` | now (the owner's VM for the recording) |
| **H** schema | H01-H05 | `DbDataBuild.Planning`, `.Targets` (Ddl), new `DbDataKit.Schema` | H01 now; H02 after 13a B04 |
| **N** serving | N01-N03 | `DbDataProxy.Server/Flight`, new `DbDataKit.Serving*` | N01 now; N02 after 13a P05 |
| **Y** visuals | Y01-Y05 | new `DbDataKit/web/visuals` workspace | now |
| **Z** DbDataStore | Z01-Z07 | new `DbDataStore/` product folder | Z01-Z02 now; Z03 after H03 |
| **G** lineage | G01-G12 (plan 18) | new `DbDataKit.Lineage`; then each product's server | G01-G03 now; G05-G08 after G04 |

Plan 13a's remaining slices are lanes of their own, already in flight; this plan does not change them:

| 13a lane | Slices | Touches |
| --- | --- | --- |
| Scripting | S01-S11 | Sync's libraries, drivers and scripts; new `DbDataKit.Scripting` |
| Transfer | A14-A16, B08 | Sync staging and merge; Build copies |
| Build on shared libraries | B01, B02, B04-B07, A19 | Build's quoting, logins, catalog reads, write gate |
| Formats | E01, E03-E05 | connection files in Sync and Build |
| Proxy management | P05, P09 | Proxy's client identities and its console module |
| Trust and identity | T04, T10, T11, U13-U15 | pairing, tokens, the identity home |
| Console | U08, U11, M11, R06, R08, R09 | the relay, monitoring view, Build module |
| Authoring | W02-W10 | Build workspaces and the console editor |
| CI and packaging | F10, F12, L01, L03-L06 | workflows, images, MSIs |
| Cleanup | A11, A20, X01-X03 | removals at the end |

## 3. Slices

### J: the semantic layer (plan 14)

| # | Slice | Step | Needs | Size | Done when |
| --- | --- | --- | --- | --- | --- |
| J01 | Import `Qis.Contracts` as `DbDataKit.Semantic.Abstractions` and `Qis.Engine` without its executors as `DbDataKit.Semantic` (R3); namespaces renamed; QIS's unit tests moved with it | Q1 | | M | the moved unit tests pass in `DbDataKit/tests`; nothing in the products references it yet |
| J02 | QIS's fixtures run in `dbdatakit-ci.yml` on the `testenv/` engines | Q1 | J01 | S | the fixture job green in CI on SQL Server and PostgreSQL |
| J03 | The executor over `DbDataKit.Connections` (SQL Server and DuckDB first) | Q2 | J01 | M | fixtures pass through the shared connections; QIS's own executors are not imported |
| J04 | Emitters on `ISqlDialect`; semantic YAML read through `Config.Yaml` | Q2 | J03 | M | the emitter golden tests pass on the shared dialects; a malformed file fails naming the file and line |
| J05 | PostgreSQL emitter | Q8 | J04 | S | the fixtures pass on PostgreSQL |
| J06 | The engine registry (R5): `engine/1` descriptors, read by the dialects and connections; the generated support tables | Q10 | | M | every shared dialect has a descriptor; the docs' support table is generated and checked in CI |
| J07 | Build: semantic files in projects (schema, discovery, the project schema change) and compile-time validation | Q3 | J04, 13a B02 | M | a template project with a semantic model compiles; a bad reference is a diagnostic with the file and line |
| J08 | Build: the generated physical layer and references from semantic members to built models | Q3 | J07 | M | `project show` lists the semantic model's bindings; a renamed column breaks the compile (test) |
| J09 | Build: generated hierarchy and relationship tests; the calendar template | Q4 | J08 | S | the generated tests run in the conformance quick group |
| J10 | Proxy: a semantic data source (`CommandStatementQuery` with a JSON request, `DoAction`), written against the serving host interface | Q5 | J04, N02 | M | Flight SQL tests: a semantic request returns the fixture's rows; a plain SQL source is unchanged |
| J11 | Proxy: combine in a child process; semantic state in Proxy's store | Q5 | J10 | S | a combine that exceeds its limits is stopped and reported (test) |
| J12 | Build produces a semantic package and deploys it, with the managed read-only data source, to the paired Proxy (R2a) | Q6 | J08, J11, 13a T10 | M | end-to-end: deploy from Build, query through Proxy; the write login never leaves Build (test) |
| J13 | Hosting on Proxy (R1): validate against the live catalog, switch atomically, keep versions, roll back from Build | Q12 | J12 | M | a failing package leaves the old one serving; rollback restores the previous version (test) |
| J14 | Console semantic module: model browser, query explorer with views, explain | Q7 | J10, Y02, 13a U08 | M | Playwright: browse a model, run a query, see the view |
| J15 | Agent tools for semantic queries on the shared command surface | Q7 | J10, 13a C01 | S | command tests: describe, query and explain documents validate against their schemas |
| J16 | SSAS on Proxy (Windows): `DbDataKit.Semantic.Ssas` hosted by Proxy; the DAX path in CI against SSAS | Q9 | J10 | M | the SSAS fixtures pass in the Windows CI job |
| J17 | Semantic SQL: the spec (`docs/semantic-sql.md` beside `query.md`, model-anchored names, flattened names and `flat_name`) and the SQL form of every fixture | Q11 | J04 | M | the owner approves the spec; every fixture has a SQL form checked in |
| J18 | The translator, SQL to request | Q11 | J17 | M | every fixture's SQL form translates to its request (test) |
| J19 | The translator, request to SQL (round trip) | Q11 | J18 | S | round trip on every fixture (test) |
| J20 | The recording forwarder for Excel's XMLA traffic (a pass-through that logs requests and responses to files) | Q13 | | S | records a session against SSAS in CI; the owner runs it on the VM for the catalogue |

### H: `DbDataKit.Schema` (plan 15 M3)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| H01 | Characterization: golden tests of Build's structure decisions (create, add, alter, rebuild) on every template project and target, recorded before anything moves | | S | the goldens pass on main |
| H02 | `DbDataKit.Schema`: the structure model (desired and actual tables, columns, keys), the decision table and the plan document types, moved out of `DbDataBuild.Planning`; Build references it | H01, 13a B04 | M | H01's goldens unchanged; `DbDataBuild.Planning` keeps only Build's model order and refresh plans |
| H03 | DDL rendering per dialect on `DbDataKit.Dialect` (from `Targets/Ddl`) | H02 | M | H01's goldens unchanged; the DDL cases in the shared conformance suite |
| H04 | The actual structure read through `DbDataKit.Metadata` | H02, 13a B04 | M | structure tests against SQL Server and PostgreSQL on `testenv/` |
| H05 | A second consumer test: a small fixture host (not Build) plans and applies a table change through `DbDataKit.Schema` | H03, H04 | S | the fixture passes on both engines; nothing in `DbDataKit.Schema` references Build |

### N: `DbDataKit.Serving.*` (plan 14 R8, plan 15 section 11)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| N01 | `DbDataKit.Serving.Abstractions`: the host interface (authenticate a caller, describe what can be queried, run under the caller's limits, optional writes) | | S | the interface and a fake host with unit tests |
| N02 | Move Proxy's Flight SQL server into `DbDataKit.Serving.FlightSql`; Proxy implements the host interface; no behaviour change | N01, 13a P05 | M | Proxy's Flight SQL suite passes unchanged; `DbDataKit.Serving.FlightSql` does not reference Proxy |
| N03 | A conformance suite for any serving host (handshake, catalog, query, limits, refused writes) run against the fake host and Proxy | N02 | S | green on both |

### Y: shared visuals (plan 16 section 2a)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| Y01 | `@dbdatakit/visuals` workspace in `DbDataKit/web`: the view document schema (QIS view documents, the Vega-Lite subset) and its validator | | S | schema tests: an allowed spec passes; a spec with a URL or a custom expression is refused |
| Y02 | The renderer: Vega-Lite through `vega-embed`, the matrix grid, the table; light and dark | Y01 | M | component tests render each fixture view; a snapshot per kind |
| Y03 | A views page in the console (open a view document, render it) | Y02 | S | Playwright: a fixture view renders in the console |
| Y04 | The graph view for lineage: a short comparison of ELK.js, Cytoscape.js and React Flow (size, layout quality on 500 nodes, licence), then the component with levels of detail | Y01 | M | the comparison checked in; a component test lays out the fixture graph |
| Y05 | Rendering outside a browser: headless Chromium against Vega-Lite to SVG, decided with a spike | Y02 | S | a written result; the chosen path renders a fixture to SVG in CI |

### Z: DbDataStore phases 1-2 (plan 15 M1, M2)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| Z01 | The product: the `DbDataStore/` folder (decided), `dbdatastore server run` on the plan 13 shape (hosting, management API, pairing, state), `DbDataStore.slnf`, CI on the reusable workflow | 13a F10 | M | the server starts, pairs with a console and reports health (test) |
| Z02 | `model/1`: schema, validation, the file format and its refusal of unknown versions | | M | schema and validation tests; a bad file fails naming the file |
| Z03 | Storage for new models on SQL Server and PostgreSQL (create only) through `DbDataKit.Schema` | Z01, Z02, H03 | M | a fixture model creates its tables on both engines |
| Z04 | Row writes with rules and audit | Z03 | M | write tests: a rule violation is refused with the rule's name; every write audited |
| Z05 | History: versioned and effective-dated, native temporal tables or app-managed, behind one conformance suite; the design leaves room for a draft layer per channel or preview (plan 21) | Z04 | M | the history suite passes for both designs on both engines |
| Z06 | Console grid editor module | Z04, 13a U08 | M | Playwright: edit rows of an entity, a rule violation shown |
| Z07 | Agent tools as change requests (every agent action approved by the user as it happens) | Z04, 13a C01 | S | command tests; an agent's write waits for approval (test) |

### G: lineage

Slices G01-G12 are in [plan 18](18-lineage.md), section 7.

## 4. Running lanes side by side

**Wave 0, now** (independent; new folders only):

- J01-J04, J06, J20; H01; N01; Y01-Y02, Y04; Z02; G01-G03.
- Each of these creates new projects or packages. They touch shared files only to register themselves.

**Wave 1**, as their 13a prerequisites land:

- H02-H04, after B04.
- N02-N03, after P05.
- J05, J07-J09, after B02.
- J17-J19.
- Y03, Y05.
- Z01, after F10.
- G04-G08.

**Wave 2:**

- J10-J13.
- Z03-Z07.
- J14-J15 and G09-G10, after U08.
- H05.

**Wave 3:**

- J16, G11, G12.
- What section 1 lists as needing design, once it is designed.

At peak, about six agents can work without sharing an area: J, H, N, Y, Z and G, beside the 13a lanes.

**Sequenced on purpose** (two lanes would otherwise edit the same code):

| Area | Lanes | Order and why |
| --- | --- | --- |
| Build's planner and catalog reads | 13a B04-B07, H | B04 first (H04 reads through the same `Metadata` calls); H02 can start once B04 merges. B06/B07 (write gate, `DbDataBuild.Apply`) and H run side by side |
| Build's project schema and CLI | 13a E03, W lane, J2 | E03 changes the project schema; J07 adds semantic files to it. Merge E03 first or rebase J07 onto it; W05's schema-driven forms then cover semantic files for free |
| Proxy's Flight SQL server | 13a P05/P09, N, J3 | P05 (client identities) before N02 moves the server; J10 is written on the host interface after N02, so the semantic source is not moved twice |
| Proxy's state | J11, G08 | both add tables to Proxy's store; small, but one at a time |
| The console's web workspace | Y, J4, Z06, G10, 13a U/M/R/W | Y lives in its own workspace; the console pages for J14, Z06 and G10 are separate routes and conflict only in the navigation file |
| `DbDataKit.Connections` and dialects | J03/J04, J06, 13a A/B | J06 adds descriptors beside the dialects without changing them; J03 consumes connections only |

**Shared files every lane touches**, and how to keep them cheap:

- `DbDataKit.slnx` and the `.slnf` files: one line per project. Take both sides of a conflict; `scripts/check_solution.py` checks the result.
- `Directory.Packages.props`: one line per package version. Take both sides of a conflict; never two versions of one package.
- `DbDataKit/web/package-lock.json`: never merged by hand. Take main's copy, then run `npm install` in `DbDataKit/web`.
- `.github/workflows`: a new job per lane, not edits to another lane's job.
- The status rows: in this file (and plan 18 for G), each lane edits only its own rows.
- `testenv/`: lanes share the engines. A lane that needs another engine adds it to `testenv/` in its own slice. Integration tests create their own databases, named for the suite, so parallel CI runs do not collide.

## 5. Decisions

All decided by the owner on 2026-10-26:

- DbDataStore's folder is `DbDataStore/` and its command is `dbdatastore`.
- Semantic SQL starts after QIS is integrated (J01-J04), as planned; lane J5 can then run beside J2 and J3.
- Tracing a value from a result or a visual back to its sources, and to its rows, is a later feature, not in lineage's first version (plan 18).
- Lineage's run history follows the servers' retention policy for run detail.
