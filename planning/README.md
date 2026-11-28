# Planning

The one index of open work. Each subject has a file `planning/<SLUG>.md` with its goal, what exists, its tasks (`<SLUG>-<n>`, in priority order), what it
needs from other subjects, its proposed decisions (`<SLUG>-P<n>`) and the shared areas it touches. How the work is run, and who may edit what, is
[ROLES.md](ROLES.md). How the product works, and why, is in the product documentation (`docs/`, the READMEs), never here.

Subjects in priority order. The owner sets the order, the status and the assignment.

| # | Subject | Goal | Status | Assigned |
| --- | --- | --- | --- | --- |
| 1 | [INSTALLER](INSTALLER.md) | Services own their data, start one way, fail in words, join the console by a generated command; no pairing codes | active | unassigned |
| 2 | [ENDPOINTS](ENDPOINTS.md) | The Gateway listens on one public HTTPS port routed by path, plus Flight SQL and management | next | unassigned |
| 3 | [TRUSTZONES](TRUSTZONES.md) | The console links Gateway and Runner groups; Serving asks Work through an outbox; Work tells and reads Serving; logins per service | next | unassigned |
| 4 | [CERTIFICATES](CERTIFICATES.md) | Enterprise certificates on the console's and the Gateway's user-facing endpoints; management certificates renew themselves without breaking a link (has open designs) | queued | unassigned |
| 5 | [ALERTING](ALERTING.md) | Failed runs, checks, approvals, a silent console or a dead server reach a person, from the console and from each service | queued | unassigned |
| 6 | [DATAQUALITY](DATAQUALITY.md) | The checks of replications, Store models, data sources and catalogs are evaluated; a blocked dataset keeps its last good version served | queued | unassigned |
| 7 | [AUTHORING](AUTHORING.md) | Everything the deleted servers did is done from Workspace projects, with browser stories | queued | unassigned |
| 8 | [SYNCAUTHORING](SYNCAUTHORING.md) | A Workspace sync project's replications are authored in the Sync screens restored as its Designer, with Files beside it as the fallback | queued | unassigned |
| 9 | [PAGEFORMAT](PAGEFORMAT.md) | `page/1` replaces `dashboard/1`, `report/1` and `view-spec/1` | queued | unassigned |
| 10 | [PUBLISHING](PUBLISHING.md) | Every kind on the one publish API: per-kind routes gone, apps by file, the approver's signature, `blocked_by_check` | queued | unassigned |
| 11 | [SUBSCRIPTIONS](SUBSCRIPTIONS.md) | Subscriptions run as Runner tasks, as the subscriber | queued | unassigned |
| 12 | [REPORTCATALOGS](REPORTCATALOGS.md) | Reports on outside report servers found in the suite: a Runner task reads each catalog, the Gateway shows it per person, each entry links out | queued | unassigned |
| 13 | [APPHOSTING](APPHOSTING.md) | Several sites with their own host names, certificates and sign-in on one Gateway | queued | unassigned |
| 14 | [BACKUPRESTORE](BACKUPRESTORE.md) | One backup set, one key backup, one restore drill in CI | queued | unassigned |
| 15 | [KNOWLEDGE](KNOWLEDGE.md) | Knowledge served by the Gateway, agents front and back, actions on path 1 | queued | unassigned |
| 16 | [CHANNELS](CHANNELS.md) | Build test targets and Sync trial runs from the Workspace; previews removed on merge | queued | unassigned |
| 17 | [SEMANTICS](SEMANTICS.md) | Semantic models checked, typed, described and configurable; SSAS bounded | queued | unassigned |
| 18 | [MASTERDATA](MASTERDATA.md) | A change feed on the store module, native entity ids, instances, entities over Flight SQL | queued | unassigned |
| 19 | [RUNNER](RUNNER.md) | Runner limits and accounts, Sync state on the target, lineage retention, availability tests | queued | unassigned |
| 20 | [CODECLEANUP](CODECLEANUP.md) | No dead code or stale docs of the deleted servers; the old state path gone | queued | unassigned |
| 21 | [FABRICSERVING](FABRICSERVING.md) | A Fabric warehouse as a Gateway data source, signed in with Entra; close to working, low priority until the product is consistent | open design | unassigned |
| 22 | [AGENTTOOLS](AGENTTOOLS.md) | What an agent can do in the console and the Gateway, over MCP or embedded, as the person: one implementation per operation, data rules per channel, the MCP endpoints and their tools | queued | unassigned |
| 23 | [CLOUDDEV](CLOUDDEV.md) | A cloud session runs the four services as one development instance, shows the console in the preview and reaches it through a development MCP | queued | unassigned |
| 24 | [SAMPLEDATA](SAMPLEDATA.md) | Sample data for every model (by query or generated from structure and hints) to run transforms, build tests and stand in for real data | open design | unassigned |
| 25 | [CLI](CLI.md) | Fewer programs for a person to run, each keeping only the commands that run and configure the services; Build, Sync and the Store are called as libraries | queued | unassigned |
| 26 | [DOCSREVIEW](DOCSREVIEW.md) | Reference docs the owner approves, and every doc and plan checked against them | queued | unassigned |
| 27 | [WORKSPACEISOLATION](WORKSPACEISOLATION.md) | The Workspace service calls no other service and holds no login to a data system; the control service deploys what it builds | queued | unassigned |
| 28 | [ERRORDETAILS](ERRORDETAILS.md) | An administrator can investigate every failure: its full detail is kept and shown to administrators; everyone else sees the classified words and an id | queued | unassigned |
| 29 | [PLUGINS](PLUGINS.md) | Each kind of project is a plugin with parts per role, compiled in first and loaded from a feed later | open design | unassigned |
| 30 | [SANDBOX](SANDBOX.md) | Author code runs sandboxed and reaches outside systems only through hosted connections; testable on the Workspace with no live system (draft) | open design | unassigned |
| 31 | [CI](CI.md) | A pull request runs only what its change touches; everything runs weekly and on demand; a stable release needs a green full run; flakes are tracked to a fix (draft) | queued | unassigned |
| 32 | [SYNC](SYNC.md) | Sync after the Designer: watching and operating runs, the replication and script pages, Preview SQL, MySQL and Oracle privileges, the installed-suite story | queued | unassigned |
| 33 | [CONNECTIONS](CONNECTIONS.md) | Every connection kind has a form with Test, a channel never reaches production's connection, each kind proven on a real system | queued | unassigned |
| 34 | [INTEGRATIONS](INTEGRATIONS.md) | The integration product's features: the kit, its sources and targets, triggers and screens | open design | unassigned |
| 35 | [ACTIONS](ACTIONS.md) | Scripts that apps and agents call to do something: the format, where they run, the editor; elevated actions wait for the security model | queued | unassigned |
| 36 | [AGENTS](AGENTS.md) | Agents authored, tested and run: definitions, providers, test conversations, evaluation, the console's chat, the Gateway, integrations | queued | unassigned |
| 37 | [TESTING](TESTING.md) | Every screen has a browser story that runs in CI; the installed suite as one test fixture | queued | unassigned |
| 38 | [BUILD](BUILD.md) | Build's remaining work as a Workspace project and a Runner package, starting with its tracking | queued | unassigned |
| 39 | [LINEAGE](LINEAGE.md) | Lineage the suite cannot see on its own: declared entries, OpenLineage export, tracing a value back | queued | unassigned |

## Parked

Work that is not scheduled is in [PARKED.md](PARKED.md), each item with its whole design.
