# Plan 31: report catalogs, external and our own, in one place

Status: **proposed, high level** (2026-11-06, from the owner's direction). Not sliced. The goal: apps we publish can **navigate, search and
show** reports hosted in an external report catalog the same way as the suite's own reports and dashboards. As generic as possible; the
first catalogs are **SQL Server Reporting Services (SSRS) and Power BI Report Server (PBIRS)**.

## 1. What already exists or is decided

| Plan | What it says |
| --- | --- |
| 16 section 2a | the suite's own report spec and dashboards on the shared visuals; embedding SSRS and PBIRS reports by URL (`rs:Embed=true` for the viewer, `rs:Format` for exports), with the catalogue read through the report server's REST API (`/reports/api/v2.0`) |
| 16, "Sign-in decisions" item 10 | report servers embedded with **Windows sign-in end to end, now**; off the domain, a link instead of the frame; Kerberos constrained delegation through the serving layer is the later step |
| 26, slice AP17 | the report server's origin is an approved `frame` exception of an app instance; off the domain the frame becomes a link |
| 17 | the other direction: report servers **reading** the semantic models; unchanged by this plan |
| 27 sections 2.7, 2.11 | every definition is searchable as generated knowledge; one search box can ask several sources |

What is missing is the **catalog**: one way to browse, search and open reports from any of these, and our own, side by side.

## 2. One catalog interface, many catalogs

A **report catalog** is anything that can answer these, and each kind of catalog is a **provider** of them:

| Operation | Gives |
| --- | --- |
| **Browse** | folders and items: id, path, name, description, type (paginated report, Power BI report, dashboard, linked report, ...), owner, modified |
| **Parameters** | an item's parameters: names, types, defaults, valid values, which depend on which |
| **Open** | how to show it: for an external catalog, the URL to frame (or a link) with parameters filled in; for our own, the report or dashboard to render |
| **Export** | the formats it offers (PDF, Excel, CSV, ...) and how to get one |
| **Permissions** | who may see an item, as far as the catalog tells us (section 4) |

- **Our own reports and dashboards are a provider too**, over the report and dashboard definitions published to the serving group, so the
  suite's reports are one catalog among the others, not a special case.
- **SSRS and PBIRS are the first external provider** (one provider: their REST API is largely the same; PBIRS adds Power BI reports).
- **Others later** through the same interface (the Power BI service, Tableau, Grafana and the like), each its own provider with its own
  embedding and sign-in.
- **A catalog is a connection** of a new kind in `connection/1` (`report-catalog`, with its provider), like model providers and outside
  systems (plan 27 questions 10 and 15): its address, the service account it reads the catalog with (a secret reference), and per-channel
  binding, so a test app can point at a test report server.

## 3. How a catalog reaches apps

1. **A scheduled read.** An integration task (plan 27 section 4) reads each external catalog through its provider on a schedule (and on
   demand): folders, items, parameters, permissions. The report server is outside the suite, so reaching it is the runner's job, not the
   serving layer's.
2. **Published like any definition.** The task publishes what it read as a `report-catalog` snapshot to the serving group (plan 30), per
   channel. A report server that is down leaves the last snapshot in place.
3. **Searchable with everything else.** The `knowledge` module indexes the snapshot's items as generated knowledge (plan 27 section 2.7),
   so a search for "regional sales" finds an SSRS report, a Power BI report and one of our dashboards together, each marked with its catalog.
4. **One catalog for an app.** An app's manifest names the catalogs it uses (and the folders, if not all); the serving layer gives it the
   merged view through one API.

## 4. Permissions

The hard part. An external catalog has its own security (SSRS item policies by Windows user and group), and the suite must not list a
report to someone who may not see it.

- **Proposed: read the catalog's policies and map them.** SSRS's REST API exposes an item's policies; the scheduled read keeps them, and
  the Windows users and groups they name are mapped to people and groups of the identity home through its external group mappings (plan
  16, "Sign-in decisions" items 2 and 5). The serving layer then filters the catalog for each person, like any generated knowledge.
- **The report server still decides at open time.** The frame signs the person in to the report server with their own Windows identity
  (plan 16 item 10), so a mapping that is wrong can show a title, never a report the person may not see.
- **A catalog that exposes no policies** can be shown to the groups an administrator names, as a recorded choice.
- **To verify** on a real report server: what the API returns for policies, inherited policies, and group nesting.

## 5. Showing and navigating

Standard front-end tools, in a shared package beside `@dbdatakit/knowledge-ui` (plan 27 section 2.10a):

| Component | Does |
| --- | --- |
| **Report browser** | folders, search, favourites and recent items across all of an app's catalogs, each item marked with its catalog |
| **Report view** | our own reports rendered by the shared visuals; an external report in a frame from its report server (the instance's `frame` exception, AP17), or a link off the domain |
| **Parameters pane** | one pane for every catalog, from the catalog's parameter list: filled in by the person, passed to the report (as URL parameters for SSRS and PBIRS) |
| **Export** | the formats the item offers, through its catalog |

- **Portals** (plan 26 section 4a) can mount a catalog folder in their navigation, so a folder of SSRS reports is a menu like any other.
- **Agents** get the catalog as tools: find a report, open it with parameters, and explain what it shows from its description.

## 6. Sign-in

As decided in plan 16 item 10, Windows end to end for now: the person's browser signs in to the report server directly. Later, Kerberos
constrained delegation through the serving layer would let any person reach embedded reports from anywhere, and let catalog reads run
as the person. A cloud catalog (the Power BI service) has its own embedding tokens; its provider handles them when it is added.

## 7. Questions

| # | Question | Proposed |
| --- | --- | --- |
| 1 | The catalog interface (browse, parameters, open, export, permissions), with our own reports as one provider | as section 2 |
| 2 | Catalogs read by a scheduled integration task and published as a `report-catalog` snapshot | as section 3 |
| 3 | Permissions from the catalog's policies, mapped through the identity home; the report server still decides at open | as section 4 |
| 4 | One parameters pane for every catalog, or the report server's own toolbar for external reports | one pane, with the report server's toolbar hidden where it can be |
| 5 | Which catalogs after SSRS and PBIRS | decided when one is needed |
| 6 | A test environment: SSRS and PBIRS run on Windows only, so the shared Linux test engines cannot host them | a Windows machine or CI job with a report server, as the Excel and SSAS work already needs (plan 14) |
