# What each service lets a person do in the UI

A matrix of service, object and action: what a person can do from the console's screens today, and what is only an API or a command line. Keep it current in the same change that adds, removes or widens a screen (`scripts/check_ui_coverage.py` fails when a page file is missing from the **Screens** column, or a file named there is gone).

Read as of 2026-11-02, from the routes and API clients of `DbDataKit/web/*`, not by clicking through every screen. A cell is **Yes**, **Partial** (say what is missing under Notes), or **–** (no screen). **Other actions** are the verbs that are not create, edit or delete.

All screens are shown by the console (`DbDataKit/web/console`) at `/servers/<server>/...`; the products have no web app of their own. Roles gate each action (Viewer, Operator, Author, Approver, Admin); the table says what exists, not who may.

| Service | Object | Create | Edit | Delete | Other actions | Screens |
| --- | --- | --- | --- | --- | --- | --- |
| **Console** | Servers | Pair by announcement, or by address and code | – | Remove | Confirm or reject a waiting server | `InstancesPage.tsx`, `PendingServers.tsx`, `ServerModule.tsx` |
| | Users and invitations | Invite | Set roles and email | Yes | Redeem an invitation | `UsersPage.tsx`, `InvitePage.tsx` |
| | Connections (Sync and Proxy, one list) | – | – | – | Copy between servers | `ConnectionsPage.tsx` |
| | Lineage, Views, Monitoring, Docs | – | – | – | Read-only | `LineagePage.tsx`, `ViewsPage.tsx`, `MonitoringPage.tsx`, `DocsPages.tsx`, `HomePage.tsx` |
| **Sync** | Connections | Yes | Yes | Yes | Test | `ConnectionsPage.tsx`, `ConnectionEditPage.tsx` |
| | Replications | Yes | Yes | Yes | Enable, pause and resume | `ReplicationsPage.tsx`, `ReplicationDetailPage.tsx` |
| | Table mappings | Yes | Yes (columns, transforms, segmenting, provisioning, pipeline) | – | Verify, preview, diagnostics, history diff | `ReplicationDetailPage.tsx` |
| | Runs | – | – | – | Start, cancel, resync, bulk load, reconcile | `ReplicationDetailPage.tsx` |
| | Target provisioning | – | – | – | Plan and apply | `ReplicationDetailPage.tsx` |
| | Scripts | Yes | Yes | Yes | Compile, test | `ScriptsPage.tsx`, `ScriptEditPage.tsx` |
| | Docs | – | – | – | Read the shipped documentation | `DocsPage.tsx` |
| | Drivers, libraries, files, certificate, updates, configuration | Yes | Yes | Yes | Admin screens | `DriversPage.tsx`, `DriverEditPage.tsx`, `LibrariesPage.tsx`, `FilesPage.tsx`, `AdminCertificatePage.tsx`, `AdminUpdatesPage.tsx`, `AdminConfigPage.tsx` |
| **Proxy** | Data sources | Yes | Yes | Yes | Test, adopt (hand to a paired server) | `DataSourcesPage.tsx` |
| | Users, JWT mappings, pass-through | Yes | Yes | Yes | – | `IdentitiesPage.tsx` |
| | Certificate | – | – | – | Renew | `TlsPage.tsx` |
| | Sessions, queries, lockouts | – | – | – | End a session, cancel a query, lift a lockout | `ActivityPage.tsx` |
| | Configuration history | – | – | – | View and diff commits | `ConfigPage.tsx` |
| | Overview | – | – | – | Read-only | `OverviewPage.tsx` |
| **Build** | Projects | Yes (from a template, or import) | – | – | – | `ProjectsPage.tsx`, `NewProjectPanel.tsx`, `ImportPanel.tsx` |
| | Model and project files | Yes | Yes (editor) | Yes | Move or rename | `EditTab.tsx`, `NewModelPanel.tsx`, `FileTools.tsx` |
| | Draft (workspace) | – | – | – | Check, compile, sample, diff, commit, push or merge | `CompilePanel.tsx`, `GitPanel.tsx`, `PreviewPanel.tsx`, `ProjectPage.tsx` |
| | Deploy plans | Yes | – | – | Dry run, apply (approver) | `DeployTabs.tsx` |
| | Tracking tables and seeds | – | – | – | Initialise and seed (admin) | `DeployTabs.tsx` |
| | Connections of a project | Yes | – | – | – | `NewConnectionPanel.tsx`, `DefinitionPanel.tsx` |
| | Publish to a Proxy | – | – | – | Yes | `PublishTab.tsx` |
| **Store** | Models, storage, rows | – | – | – | **No UI.** The management API exists (Z08–Z10); the model editor (Z07) and grid editor (Z06) are not built. | |
| **Apps** | – | – | – | – | **No UI.** Only the bundling library (AP01, AP02); the host is O11. | |

## Notes and gaps

* **Console connections** lists and copies; a connection is created and edited in the product's own screens (Sync, or the Proxy's data sources).
* **Build**: the connections row is from reading `NewConnectionPanel` and `DefinitionPanel`, not from using them; confirm what they edit before relying on the Edit cell.
* **Semantic models and saved queries** (plan 19, lane J): no screen was found; the Views page renders view documents only.
* **Sync** hides most of its screens in `ReplicationDetailPage` and its tab components; the matrix names the page, not each tab.

## Keeping it current

Run `python3 -I scripts/check_ui_coverage.py`. It reads `DbDataKit/web/{console,sync,proxy,build,admin}/src/pages`, and fails for any page or tab file (`*Page.tsx`, `*Tab.tsx`, `*Panel.tsx`, `*Pages.tsx`) that this file does not name, and for any `.tsx` file named here that does not exist. It cannot tell whether a cell is right: when a screen gains an action, change the cell too.
