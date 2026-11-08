# Plan 20: the serving layer, what is left

Status: **in progress**. The layer (three layers, principles, modules, outage table, groups and identity, the contract) is described in [docs/serving-layer.md](../serving-layer.md), which also records the decisions, among them the rename: **the serving layer is DbDataProxy today and DbDataGateway after the rename**. This plan holds only unbuilt work. Slice ids O01 to O17 are cited from code and other plans; the done rows are in git history.

## Slices still open

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| O05 | High availability in CI: two instances behind a balancer; one stopped during a query run; the back-ends stopped | O04 (done) | M | queries and sign-ins continue; the Flight SQL client reconnects to the other instance (test) |
| O07 | Identity on its own, forwarded: the home behind the firewall, the serving layer forwarding its public endpoints | O06 (done), 13a U15 | S | the O06 test passes with the home moved out |
| O08b | Entities as Flight SQL sources: `SELECT ... FROM` an entity of a published model, now or as of a date, as the caller, with the model's roles. Also the store module's remainders: instances of a model (`instances: connection`) are not served, only the default copy; a token's scope narrowing is not read; `WriteAsync` as a change set on `IServingEntities` | O08a (done) | M | a Flight SQL client reads an entity as of a date with DbDataStore's server stopped (test) |
| O09 | The `webdav` module: documents with versions; extraction on save in a child process | O08, plan 15 M5, M6 | M | Excel's recorded traffic against it; a saved workbook updates its extracted entity (test) |
| O11 | The `apps` module, what remains: app code in child processes; calls to `semantic` in process as the signed-in user (the `store` calls are in process, O08c). The module, shell, token broker, storage, portals and trust are built (plan 26) | O06, O08, plan 16 | M | a code app reads and writes an entity with the back-ends stopped (Playwright), and runs in a process of its own |
| O12 | The `mcp` module: the agents' endpoint, every action approved by the user (the contract needs an approval request on the platform service) | O06, plan 16 section 1 | S | an agent's write waits for approval (test) |
| O13 | The outage table as tests: one per row | O05, O08 | S | each row's "keeps working" holds with that back-end stopped |
| O14 | **The rename to DbDataGateway**, by a script after batch 12: product, commands, packages; old names refused with a message (clean break); the documents that say DbDataProxy | | S | CLI snapshot; a release note |
| O15 | WebDAV sign-in (plan 16, "Sign-in decisions" item 3): Office forms-based sign-in (MS-OFBA) to the home's sign-in page, Windows, and the home user name with a personal access token | O06, O09, 13a U20 | M | Office's recorded traffic: the forms-based challenge leads to the home's sign-in and the session it ends with is accepted on the WebDAV requests; a personal access token works as the password; Windows on the Windows CI job; Office for Mac checked on the Excel VM and the result recorded |
| O16 | XMLA sign-in over HTTPS (plan 16, "Sign-in decisions" item 3): Excel's Analysis Services provider at an `https://` address, with Windows or the home user name and a personal access token | O06, 13a U20, plan 19 J20 | S | a recorded Excel session with a user name and token replayed in CI; Windows on the Windows CI job; a wrong token is an XMLA error, not a hang |
| O17 | Entra tokens on the REST endpoints for Power Query's organizational account (plan 16, "Sign-in decisions" item 3): for an install with Entra configured, the feeds, saved queries and OData endpoints answer an unauthenticated call with a bearer challenge naming the tenant and accept that tenant's access tokens for the configured resource, the person linked to their home account by UPN (13a U16, U18) | O06, 13a U18, plan 19 J28 | M | tests with a stand-in tenant: the challenge names the tenant; a token for the configured resource gives the home user and the home's roles; a token for another audience or tenant is refused; nothing changes when Entra is not configured; a real Power Query refresh recorded on the Excel VM |

## Leftovers of built slices

Not done in slices that are otherwise finished; each belongs to the slice named, which stays cited from code.

- **O01 modules**: a health report per module (`IServingModule.Health()` is not wired to `/health`); the identity home, the apps host and the store module as `IServingModule` implementations (only `flightsql` is one; the others are hosted web applications on their own listeners).
- **O02 / O02b state**: `dbdatakit identity run`'s signing keys are protected with ASP.NET data protection keys in its own directory, so a group of those homes needs the folder copied.
- **O03 definitions**: a request pinned to one version while it runs, with the version named in its answer; `expected_version` is checked per instance, so two publishes through two instances at once can both pass it.
- **O04 groups**: the other products' groups (Build, Sync and Store run once); adopting a hand-made data source on a group keeps it the instance's file; the console shows a group's data sources as the instance it reaches lists them, and a group as one node with its instances.
- **O06b**: Entra's group overage (more than 200 groups) gives nothing; Windows on Linux with a keytab is built but untested (no KDC in the test environment); `identity run` does not offer the upstreams yet.
- **O10 / O10b publishing**: permissions (not in `model/1`); publishing from the store's management API and the console's screens (command line only); a model with instances cannot be published yet.
- **Semantic** (plan 14, J rows): the combine runs in process under limits, not in a child process; semantic state in the Proxy's store.

## Contract work, added when a module needs it

The services that are not in the code: Documents (O09), Platform (audit, limits, the result cache, OpenTelemetry, child processes for heavy work, an approval request for `mcp`) and Definitions (a request pinned to a version). Per module, the additions listed in the contract's table in `docs/serving-layer.md` arrive with the module that needs them: a multi-leg authentication exchange, cancel by session and ingest for `pgwire`; member listing and perspectives and the cell-set axes convention for `xmla`; a data version for `feeds`; the change-set `WriteAsync` for `store`.

## Open questions

- Whether apps move to an instance of their own per group when the module count grows (the module list is configuration, so this is deployment, not code).
- Sign-in slices O15 to O17 depend on recordings made on the Excel VM (plan 14 R7).
