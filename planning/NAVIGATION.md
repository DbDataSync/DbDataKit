# NAVIGATION: a left navigation with named groups

## Goal

A person finds every screen of the console, and of the Workspace, Runner and Gateway it shows, from one labelled left navigation whose entries sit in
a few named groups, the same on every screen; nobody has to hover over an icon to learn where it goes. It starts simple: today's entries regrouped,
labelled and kept in one place, no new screens. It follows `docs/architecture.md` ("Screens follow the work, not the servers"): the groups are named
for what a person is doing, and the servers' own screens sit under one group for administering them.

**Scope: quick and dirty, refined later.** Very moderate organization, borrowing the nav and tab styles of the old Sync admin screens; no page is
redesigned. It will be refined once the owner can use it. The arrangement is described in `DbDataKit/README.md`, "Finding a screen: the navigation".

**The styles to borrow.** The Sync server's web app as it was before it was deleted, `git show 96601f27^:Shared/web/sync/src/<file>`:
`components/AppShell.tsx` (the shell's rail, breadcrumbs and `tabs` slot), `pages/ReplicationDetailPage.tsx` (the `tabbar` of `NavLink` tabs with its
buttons in `.actions` at the right end) and `pages/replication-detail/OverviewPanel.tsx`, `MonitoringPanel.tsx`, `TableMappingForm.tsx` (a level down,
`SubTabs` with `.subtabbar` and `.subtab-panel`). The classes are still in `Shared/web/shell/src/styles.css`: `.rail`, `.rail-mark`, `.rail-item`
(`.active`), `.rail-spacer`, `.topbar` with `.crumb`, `.tabbar` with `.tab` (`.active`) and `.actions`, `.subtabbar` and `.subtab`.

## What exists today

Read from the code of `Shared/web/*` on 2026-11-18 and rendered at 1440x900 with the console built and its API answered by a mock.

- **The chrome** (`Shared/web/shell/src/components/Shell.tsx`): a 46px icon rail (`RailItem[]`: route, title, icon, test id, `adminOnly`), a
  breadcrumb bar whose first crumb is the product's name, and a tab bar slot (`tabs`) that no screen passes today. The rail shows icons only; an
  entry's name is its `title`, a tooltip. It scrolls inside itself (`docs/ci-lessons.md`, the `views-dashboards.spec.ts` row).
- **How it grew.** The rail had two entries when the console's web app was made (Home, Users) and grew one entry per new page: Servers, Monitoring,
  Views, Docs, Connections and Lineage in October; Deployments and Quality on 2026-11-07 and 11-08; thirteen more on 2026-11-11 and 11-12 (Approvals,
  Runs, Agents, Knowledge, Audit, Secrets, Setup, Delivery, Tracking, Bindings, Backlog, Releases). Each page added its line at the end, so the order is
  the order of building. The server modules (`ServerModule.tsx`, `ModuleProvider`) mount each server's screens under `/servers/<id>/` with a rail of
  their own.
- **The console's rail, 22 entries, in this order** (`Shared/web/console/src/ConsoleShell.tsx`; * administrators only): Home `/`, Servers* `/instances`,
  Connections `/connections`, Monitoring `/monitoring`, Runs `/runs`, Tracking `/tracking`, Approvals `/approvals`, Lineage `/lineage`, Quality
  `/quality`, Setup* `/setup`, Deployments `/deployments`, Releases `/releases`, Agents `/agents`, Knowledge `/knowledge`, Backlog `/backlog`,
  Delivery `/delivery`, Bindings `/bindings`, Views `/views`, Docs `/docs`, Users* `/users`, Audit* `/audit`, Secrets* `/secrets`. They have 12
  icons between them: the pulse is Monitoring, Runs and Delivery; the graph is Tracking, Lineage, Deployments and Releases; the shield is Approvals,
  Quality and Audit; the house is Home and Setup; the people are Users and Secrets; the share icon is Connections and Bindings.
- **Console pages with no rail entry:** Upgrades `/upgrades` and a server's state `/state/<id>` (links on the Servers page), a module's docs
  `/docs/<module>/...` (from Docs), the Views tabs `/views/dashboards[/<name>]`, `/views/reports[/<name>]`, `/views/catalog`, and `/invite` (no
  session).
- **Tabs inside console pages, in four styles:** Views (`SubTabs`, links: Rendered views, Dashboards, Reports, Catalog); Agents (Chat, Approvals, Record,
  Agents, Models, Install policy), Knowledge (Index and context, Search and index, Worklists, Items and preview, Suggestions, Check, Install knowledge)
  and Delivery (Channels, Subscriptions, Alerts, History) as `subtab` buttons with `?tab=`; Backlog (Backlog, Board, Sprints, Import and export) as
  button toggles. The Agents tabs render with the browser's own button borders.
- **A server's screens** (`/servers/<id>/...`) replace the console's rail with the server's own: a back arrow to Servers first, then the product's
  entries. The breadcrumb says the product (`DbDataGateway / Overview`), not which server. Leaving a server for a console page takes two clicks (back
  to Servers, then the entry); going from one server to another goes through the Servers page.
  - **Gateway** (`Shared/web/gateway/src/GatewayShell.tsx`), 15 entries, none hidden from a viewer although eight pages answer "This screen is for
    administrators": Overview `/`, Data sources `/data-sources` (an item `/data-sources/<name>`), Users and sign-in `/identities`, Identity home
    `/identity-home`, Secrets `/secrets`, Apps `/apps`, Models `/models` (a model `/models/<name>`, its explorer `/models/<name>/explore`), Saved queries
    `/saved-queries`, Publishers `/trust`, Access rules `/access`, Result cache `/cache`, Certificate `/tls`, Activity `/activity`, Server settings
    `/settings` (in-page tab toggles), Configuration history `/config` (a commit `/config/<sha>`).
  - **Runner** (`RunnerShell.tsx`): Tasks `/`, Runs `/runs`, Packages `/packages` (a package `/packages/<name>`).
  - **Workspace** (`WorkspaceShell.tsx`): Workspaces `/` (the projects and their workspaces), Publisher keys `/keys`; a workspace `/<project>/<id>`
    has a row of up to 11 toggle buttons by project kind (Changes, Check, Models and plan, Compile, Views, Scripts, Provisioning, Rows, Deploy,
    Catalog, Runs, Builds, History; `?tab=runs` is the address the Runner links back to).
  - **Identity home on its own** (`IdentityHomeModule.tsx`): one entry.
- **The Workspace, where authoring happens, has no console entry**: a person reaches projects only through Servers (an administrators' page) or a
  link from another screen.
- **Home** says "No server is paired with this console yet." whether or not servers are paired (`HomePage.tsx` reads nothing).
- **Browser stories depend on the rail's test ids**: 19 `rail-*` ids are clicked by the stories in `DbDataKit/tests/DbDataKit.Server.Web.Tests`
  (`rail-views` 16 times). `docs/quickstart.md` and `DbDataKit/docs/first-run.md` say "**Setup** in the rail".

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| NAVIGATION-1 | The shell's left navigation and page tabs, in the old Sync screens' styles (see above): named groups of labelled entries (each with its icon and name) built from the `.rail` / `.rail-item` styles widened to show names, a group hidden when the person may see none of its entries, a section lit while the location is anywhere inside it, scrolling inside itself; a button collapses it to the icon rail and back, kept in the browser, collapsed below 1000 pixels wide, none in kiosk mode. A flat `RailItem[]` is still accepted as one untitled group, so the Gateway, Runner and Workspace modules are unchanged by this task. The console's entries arranged as `DbDataKit/README.md` says, Upgrades and a server's state lit under Servers. The four in-page tab styles (Views' `SubTabs`, the `subtab` buttons of Agents, Knowledge and Delivery, Backlog's and the Gateway's Server settings' button toggles) become the shell's `tabbar` with `.actions` at its right end, and `subtabbar` a level down; a workspace's toggle row too, its tabs unchanged. No page is otherwise redesigned. Every entry keeps its `rail-*` test id; groups get `nav-group-<name>`; every entry gets an icon of its own. `docs/quickstart.md` and `DbDataKit/docs/first-run.md` name where Setup now is | none | M | vitest of the grouping (admin filter, empty group hidden, active section, collapsed below 1000 pixels); a browser story `navigation.spec.ts`: an administrator sees the groups and entries of the README in order, each by its name, and every entry opens its page with that entry lit; a viewer sees no administrators' entry and no empty group; `views-dashboards.spec.ts` still fits at 800x450; the stories that click a `rail-*` id pass unchanged |
| NAVIGATION-2 | The servers in the navigation: the Servers group lists each paired server by its name and product (marked when it does not answer, from `/api/instances`), and the Workspace's projects get the Projects entry under Build and release; inside a server, the console's navigation stays and the server's entries are nested under its name in the Servers group (the module's back arrow goes; the `docs/ci-lessons.md` row about clicking a console `rail-*` id from a server's page gets a new row saying it no longer applies), and the breadcrumb names the server (`gateway-east`), not only the product | NAVIGATION-1 | M | browser story: from Lineage, open `runner-a` in the navigation, see its Tasks, Runs and Packages, open Runs, then reach Approvals in one click; with two Gateways paired, both are listed and each opens its own screens; the breadcrumb on a Gateway page names that Gateway |
| NAVIGATION-3 | The Gateway's entries in the sub-groups Serving, Access and Server (`DbDataKit/README.md`), its administrators' pages hidden from a viewer the way the console's are; the Runner's and the Workspace's few entries stay ungrouped | NAVIGATION-1 | S | browser story against the paired test Gateway: an administrator sees its entries under their group names; a viewer sees no entry whose page answers "for administrators"; `first-run.spec.ts`, `gateway-apps.spec.ts` and `views-apps-addresses.spec.ts` pass unchanged |
| NAVIGATION-4 | A check that keeps it from becoming a jumble again: every route of the console and of each module is either an entry of the navigation or listed, with the page that links to it, as a page under an entry (`/state/<id>` under Servers, `/models/<name>` under Models) | NAVIGATION-1 | S | a vitest fails, naming the route, when a route is added to `App.tsx` or a module with neither an entry nor a parent; a test of the vitest proves it fails on such a route |

## Needs from other subjects

None. Subjects that add console screens put them into a group of this tree when they land: INSTALLER-13 (the Add a server screen, under Servers),
TRUSTZONES-11 (a Gateway group shown as one node; the Servers group would list the group with its instances), KNOWLEDGE-12 and PAGEFORMAT-11 (inside
existing pages).

## Open designs

### Tabs inside pages: what remains after NAVIGATION-1

What: the tab style is settled (the old Sync screens' `tabbar` and `subtabbar`, NAVIGATION-1). Left open: whether a tab is always in the address (as
`?tab=` is for Agents, Knowledge and Delivery but not for a workspace except `runs`), and how a workspace's up to 11 tabs are grouped (edit, check,
run, publish). Why it waits: the owner will refine once the navigation is in use.

### What Home shows

What: Home lists nothing and says no server is paired even when servers are. Why it waits: what a person lands on (their waiting approvals, failed runs,
recent projects) is a choice about roles and should come after the groups are settled. Open question: one Home for everyone, or per role.

## Shared areas touched

- `Shared/web/shell` (`Shell.tsx`, `module.tsx`, `styles.css`), `Shared/web/console` (`ConsoleShell.tsx`, `ServerModule.tsx`, `App.tsx`),
  `Shared/web/gateway`, `Shared/web/runner`, `Shared/web/workspace` (their `*Shell.tsx`).
- The browser stories in `DbDataKit/tests/DbDataKit.Server.Web.Tests` (the `rail-*` test ids are kept).
- `docs/quickstart.md`, `DbDataKit/docs/first-run.md`, the console's docs (`DbDataKit/docs/`), `docs/ci-lessons.md` (a row added), `docs/ui-coverage.md`
  (only if a page file is added or renamed).
