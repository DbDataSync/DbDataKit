# What each service lets a person do in the UI

A matrix of service, object and action: what a person can do from the console's screens today, and what is only an API or a command line. Keep it current in the same change that adds, removes or widens a screen (`scripts/check_ui_coverage.py` fails when a page file is missing from the **Screens** column, or a file named there is gone).

Read as of 2026-11-08 (updated after Z07, O01, O03, AP04 and AP24), from the routes and API clients of `DbDataKit/web/*`, not by clicking through every screen. A cell is **Yes**, **Partial** (say what is missing under Notes), or **–** (no screen). **Other actions** are the verbs that are not create, edit or delete.

All screens are shown by the console (`DbDataKit/web/console`) at `/servers/<server>/...`; the products have no web app of their own. Roles gate each action (Viewer, Operator, Author, Approver, Admin); the table says what exists, not who may.

| Service | Object | Create | Edit | Delete | Other actions | Screens |
| --- | --- | --- | --- | --- | --- | --- |
| **Console** | Servers | Pair by announcement, or by address and code | – | Remove | Confirm or reject a waiting server | `InstancesPage.tsx`, `PendingServers.tsx`, `ServerModule.tsx` |
| | Users and invitations | Invite | Set roles and email | Yes | Redeem an invitation | `UsersPage.tsx`, `InvitePage.tsx` |
| | Connections (Sync and Gateway, one list) | – | – | – | Copy between servers | `ConnectionsPage.tsx` |
| | State databases (connections of kind `state`) | Yes | Yes | Yes | Test | `StateConnections.tsx` |
| | Server state: panel, maintenance and the Move wizard | – | – | – | Maintenance on and off, check and run a move | `ServerStatePage.tsx` |
| | Lineage, Deployments (where a package is deployed and what a runner holds, from the runners' lineage), Views (rendered views; **Dashboards** and **Reports** tabs over a **source**: the recorded fixtures, or the documents **published to a Gateway that runs the `views` module** through the console's relay, each widget read as the signed-in person (the source is chosen in the page and kept as `?source=`; with no choice the first such Gateway is used, else the fixtures); a dashboard is opened, laid out to the window, its layout switched and its parameters kept in the address; saving from the editor on a published source publishes a new version; a report is listed and checked, not drawn yet), Monitoring, Docs | – | – | – | Read-only | `LineagePage.tsx`, `DeploymentsPage.tsx`, `ViewsPage.tsx`, `DashboardsPanel.tsx`, `MonitoringPage.tsx`, `DocsPages.tsx`, `HomePage.tsx` |
| **Sync** | Connections | Yes | Yes | Yes | Test | `ConnectionsPage.tsx`, `ConnectionEditPage.tsx` |
| | Replications | Yes | Yes | Yes | Enable, pause and resume | `ReplicationsPage.tsx`, `ReplicationDetailPage.tsx` |
| | Table mappings | Yes | Yes (columns, transforms, segmenting, provisioning, pipeline) | Yes (`delete-mapping-<name>`) | Verify, preview, diagnostics, history diff | `ReplicationDetailPage.tsx` |
| | Runs | – | – | – | Start, cancel, resync, bulk load, reconcile | `ReplicationDetailPage.tsx` |
| | Target provisioning | – | – | – | Plan and apply | `ReplicationDetailPage.tsx` |
| | Scripts | Yes | Yes | Yes | Compile, test | `ScriptsPage.tsx`, `ScriptEditPage.tsx` |
| | Docs | – | – | – | Read the shipped documentation | `DocsPage.tsx` |
| | Drivers, libraries, files, certificate, updates, configuration | Yes | Yes | Yes | Admin screens | `DriversPage.tsx`, `DriverEditPage.tsx`, `LibrariesPage.tsx`, `FilesPage.tsx`, `AdminCertificatePage.tsx`, `AdminUpdatesPage.tsx`, `AdminConfigPage.tsx` |
| **Gateway** | Data sources | Yes | Yes | Yes | Test, adopt (hand to a paired server) | `DataSourcesPage.tsx` |
| | Users, JWT mappings, pass-through | Yes | Yes | Yes | – | `IdentitiesPage.tsx` |
| | Apps: instances, apps and versions, navigation sets, approvals | – (made at the command line: `apps publish`, `instance-save`, `nav-save`) | Pin or follow a version | Remove an instance or an app (the store's rules; a refusal is shown) | Serve an older version (roll back), approve a trusted version, revoke an approval, see a version's data access (models, outside addresses, publisher, approval), open an instance's address | `AppsPage.tsx` |
| | Certificate | – | – | – | Renew | `TlsPage.tsx` |
| | Sessions, queries, lockouts | – | – | – | End a session, cancel a query, lift a lockout | `ActivityPage.tsx` |
| | Configuration history | – | – | – | View and diff commits | `ConfigPage.tsx` |
| | Overview | – | – | – | Read-only | `OverviewPage.tsx` |
| | Serving modules (`flightsql`, `identity`; `apps` and `rest` planned) | – | – | – | **No UI**: the `Modules` setting in `server.yaml` (O01) | |
| **Build** | Projects | Yes (from a template, or import) | – | – | – | `ProjectsPage.tsx`, `NewProjectPanel.tsx`, `ImportPanel.tsx` |
| | Model and project files | Yes | Yes (editor) | Yes | Move or rename | `EditTab.tsx`, `NewModelPanel.tsx`, `FileTools.tsx` |
| | Draft (workspace) | – | – | – | Check, compile, sample, diff, commit, push or merge | `CompilePanel.tsx`, `GitPanel.tsx`, `PreviewPanel.tsx`, `ProjectPage.tsx` |
| | Deploy plans | Yes | – | – | Dry run, apply (approver) | `DeployTabs.tsx` |
| | Tracking tables and seeds | – | – | – | Initialise and seed (admin) | `DeployTabs.tsx` |
| | Connections of a project | Yes | – | – | – | `NewConnectionPanel.tsx`, `DefinitionPanel.tsx` |
| | Publish to a Gateway | – | – | – | Yes | `PublishTab.tsx` |
| **Store** | Models | – | – | – | List them and their entities | `ModelsPage.tsx` |
| | Rows of an entity | Yes | Yes | Yes | Page through rows, history of a row (an entity that keeps one), pick an instance of a model that has them | `EntityPage.tsx` |
| | Model definition, storage (create tables) | – | – | – | **No UI** (API only: Z08, Z09) | |
| | Change requests (an agent's write, or a model that asks for approval) | – (made by `POST changes`, answered 202) | – | – | List and read by state, approve, reject: **API only** (Z07; the console's review screen is not built) | |
| **Runner** | Tasks and instances | – | – | – | Run now, pause, resume (operator); instances with labels and capabilities (read-only) | `TasksPage.tsx` |
| | Runs | – | – | – | List and filter | `RunsPage.tsx` |
| | Packages (published definitions) | – (published by `package publish` or Build's `project publish`; see docs/ui-editors.md) | – | Retire | Activate (with confirmed losses, or anyway with a reason), switch, roll back, see staging per runner and the history | `PackagesPage.tsx` |
| **Apps** | App packages and instances | – | – | – | **No UI, no host yet.** Libraries only: the manifest and bundling (AP01-AP03), the published-definition store with channels and rollback (O03), and instances that resolve to a package and an audience (AP04, PR open). Serving, the shell page and the sandbox are AP05; the Store's controls (`@dbdatakit/store-ui`) are written to be mounted by an app. | |

## Notes and gaps

* **Console connections** lists and copies; a connection is created and edited in the product's own screens (Sync, or the Gateway's data sources).
* **Build**: the connections row is from reading `NewConnectionPanel` and `DefinitionPanel`, not from using them; confirm what they edit before relying on the Edit cell.
* **Semantic models and saved queries** (plan 19, lane J): no screen was found; the Views page renders view documents, and opens dashboards from recorded fixtures (plan 32 DBR23) or from those published to a Gateway that runs the `views` module (DBR11b); it has no screen for the models themselves, which a Build server publishes.
* **Sync** hides most of its screens in `ReplicationDetailPage` and its tab components; the matrix names the page, not each tab.

## Keeping it current

Run `python3 -I scripts/check_ui_coverage.py`. It reads `DbDataKit/web/{console,sync,gateway,build,admin,store,runner}/src/pages`, and fails for any page or tab file (`*Page.tsx`, `*Tab.tsx`, `*Panel.tsx`, `*Pages.tsx`) that this file does not name, and for any `.tsx` file named here that does not exist. It cannot tell whether a cell is right: when a screen gains an action, change the cell too.

Whether a screen is an *editor* for what a person authors or publishes (and what has none) is [ui-editors.md](ui-editors.md), checked by `scripts/check_ui_editors.py`.
