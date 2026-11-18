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
| NAVIGATION-3 | The Gateway's entries in the sub-groups Serving, Access and Server (`DbDataKit/README.md`), its administrators' pages hidden from a viewer the way the console's are; the Runner's and the Workspace's few entries stay ungrouped | none (the grouped navigation is built) | S | browser story against the paired test Gateway: an administrator sees its entries under their group names; a viewer sees no entry whose page answers "for administrators"; `first-run.spec.ts`, `gateway-apps.spec.ts` and `views-apps-addresses.spec.ts` pass unchanged |
| NAVIGATION-4 | A check that keeps it from becoming a jumble again: every route of the console and of each module is either an entry of the navigation or listed, with the page that links to it, as a page under an entry (`/state/<id>` under Servers, `/models/<name>` under Models) | none (the grouped navigation is built) | S | a vitest fails, naming the route, when a route is added to `App.tsx` or a module with neither an entry nor a parent; a test of the vitest proves it fails on such a route |

## Proposed decisions

### NAVIGATION-P4: when the navigation is the icon rail (default, awaiting the owner)

Scenario: a person opens the navigation on a 1440-pixel laptop and leaves it open; the same browser later shows a dashboard in an 800-pixel window, then
the window is made wide again.

- **Option A (built, recommended): under 1000 pixels it always starts as the icon rail**, whatever the person chose; at 1000 and more it is as they last
  left it (the browser keeps it), and open when they never chose. Made narrow, it collapses at once; made wide again, it goes back to their choice. The
  button still opens it in a narrow window for that page.
- **Option B: the person's choice wins at every width**; only a person who never chose gets the rail under 1000 pixels. A dashboard at 800 pixels then
  loses 164 pixels to the names for whoever once left it open.

### NAVIGATION-P5: which tab rows are the page's tab bar and which are a level down (default, awaiting the owner)

Scenario: a person opens Agents, then a workspace of a build project, then a Gateway's Server settings.

- **Built (recommended for now):** the tabs that choose what a whole page shows (Views, Agents, Knowledge, Delivery, Backlog) are the shell's tab bar
  under the breadcrumb (`.tabbar`); the rows inside a page (a workspace's row under its file editor, Server settings' Form and server.yaml) are the
  `.subtabbar` a level down, where they were. A workspace's row was left inside the page because it belongs to the file column, not to the whole page.
- **Other option:** a workspace's row in the tab bar too (the file list and editor would sit under it). It waits for the open design "Tabs inside pages".

### NAVIGATION-P6: what Projects opens, with one Workspace and with several (default, awaiting the owner)

Scenario: a person opens **Projects** under Build and release; later a second Workspace, `workspace-b`, is paired.

- **Built (recommended for now):** with one Workspace, **Projects** opens that Workspace's list of projects and workspaces (its first screen,
  `/servers/<id>`); with several, one entry each, **Projects on workspace-a** and **Projects on workspace-b**. The Workspace is also listed under Servers
  by its name, where its own entries (Workspaces, Publisher keys) are nested while it is open.
- **Other option:** one **Projects** entry that lists the projects of every paired Workspace on one console page. It needs a new page (a console list
  over the Workspaces), which this quick pass does not build.

### NAVIGATION-P7: what a server's screens show at the top, and who sees the servers (default, awaiting the owner)

Scenario: an administrator opens `gateway-east`'s Data sources; a viewer signs in on a console with three paired servers.

- **Built (recommended for now):** the breadcrumb reads `DbDataGateway / gateway-east / Data sources` (the product, the server's name as a link to
  its first screen, the page). The servers are listed under Servers for whoever the console's list of servers answers (`/api/instances`); a viewer it
  answers sees each server by name but not **All servers**. On a server's screens a console entry whose test id the server also uses is
  `rail-console-<name>` (the console's Runs beside a runner's Runs), and the module's back arrow is gone, so `first-run.spec.ts` opens **All
  servers** (`rail-instances`) where it pressed the arrow.
- **Other option:** the breadcrumb starts with the console (`dbdatakit / Servers / gateway-east / Data sources`), one crumb longer on every server page.

## Needs from other subjects

None. Subjects that add console screens put them into a group of this tree when they land: INSTALLER-13 (the Add a server screen, under Servers),
TRUSTZONES-11 (a Gateway group shown as one node; the Servers group would list the group with its instances), KNOWLEDGE-12 and PAGEFORMAT-11 (inside
existing pages).

## Open designs

### Tabs inside pages: what remains now the tab style is settled

What: the tab style is settled (the old Sync screens' `tabbar` and `subtabbar`, built). Left open: whether a tab is always in the address (as
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
