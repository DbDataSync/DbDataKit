# SANDBOX: author code runs with only the access it needs

**Draft.** This subject is being worked out with the owner and is incomplete: the last section lists what is proposed and not yet approved. No task
starts until the plan is complete and approved.

## Goal

Code that a person writes runs in a sandbox. It has no network, no files outside its own working folder and no secrets. It reaches outside systems
only through **hosted connections**: our code, running in a trusted host process, opens each connection, checks every call against the binding and the
egress rules, traces it and records it in the audit. The same connections have fakes and recordings, so an author tests an integration on the
Workspace with no live system.

Three ways code can run:

- **Sandboxed**, the default for author code.
- **A trusted function in the host**, for work that has to sit next to the data: a version of a .NET assembly an administrator has approved, or a
  WebAssembly module.
- **Marked trusted**, which runs as code runs today, with its own connections. An administrator allows a specific version, and the console and the
  audit label it, so a deviation from the rule is visible without being blocked.

The suite moves to this one piece at a time. Development and testing on the Workspace come first, then integrations on the Runner. Sync and Build run
as provided by the suite until their author-supplied pieces (scripts, drivers) move.

Example: Ben writes an integration that pulls orders from a supplier's API into a staging table. On the Workspace, its test runs in a sandbox against a
recorded API and a fake table, with no network. On the Runner, the same code runs in a sandbox. It asks its `supplier` connection for each page, and
the host makes the request to the address the binding names. It then asks its `warehouse` connection to write each batch, and the host writes the
rows. Ben's code never holds the API key or the database password, and a request to any other address is refused and recorded.

## What exists today

- **App builds on the Workspace (Linux):** Bun runs in a network namespace with no route and a cleared environment (`docs/apps.md`). It runs as the
  server's own user, so it can read what that user can. On Windows and macOS the build runs without the namespace and says so.
- **Draft SQL on the Workspace:** samples and model tests run in DuckDB with file and network access off.
- **The outbound list (`DbDataKit.Outbound`):** the destinations a service's own code may connect to. Programs the service starts can ignore it.
- **Runner tasks:** each task runs in a process of its own, with memory and CPU limits (`DbDataKit.Runner.Limits`). It can reach anything the Runner's
  host can.
- **Connections in a task:** a job opens a slot (`Slots.Warehouse.OpenAsync`). The task host asks the Runner (`ResolveConnection`), receives the
  address and the login, and opens the connection inside the task's own process. The author's code and the password are in the same process. An
  executable task can be given its login in environment variables.
- **Sync's drivers and scripts:** YAML and compiled drivers, `driver.csx` and metadata scripts load from NuGet packages and run inside the Sync
  engine's process, which holds the source's and target's logins.
- **Test doubles:** `RecordedHttp` (format `http-recording/1`) and `FakeFileShare` in `DbDataKit.Runner.Kit.Testing`.
- **A script's Test:** builds and stages the whole Sync set on a Runner (WORKSPACEISOLATION-P31, P32, open).

## Scenarios

Each scenario says what code runs, where it runs, and what it may reach. "Today" says how it runs now; the designs below say where it goes.

1. **Build tools on the Workspace.** `dotnet restore` and `dotnet build` for integrations and compiled drivers, and `bun install` and `bun build` for
   apps. They read the draft's copy and write its build folder. They reach only the package sources (the NuGet feeds, npm or the vendored app store),
   with those sources' own credentials and no others.
   Today: Bun runs in a network namespace on Linux. `dotnet` runs with the server's own access.
2. **Git on the Workspace.** Clone, fetch and push a project's workspace. Git reaches the project's remotes only, with the remote's own credential and
   no others.
   Today: git runs with the server's own access. WORKSPACEISOLATION-9 limits our own code's connections to the outbound list.
3. **Develop, test and debug on the Workspace.** An integration's tests, a Sync script's Test, a Sync driver or metadata script, and a data model's build
   and tests. These run in a sandbox with no network, no real source or target, and no secrets. They reach fakes, recorded or generated responses,
   sample data and DuckDB through hosted connections.
   Today: a data model's tests run in DuckDB with external access off. A script's Test stages the Sync set on a Runner. An integration's tests run in
   a child process with the server's access.
4. **Integrations on the Runner.** An integration's tasks, which are the author's code, run sandboxed. They reach outside systems only through hosted
   connections bound in the run's channel, with the host enforcing the egress rules. Bulk work runs as a trusted function in the host.
   Today: the task process holds its logins and opens its own connections.
5. **Our own engines on the Runner.** Sync, Build, the Store, the metadata reader, delivery and quality. This is our code, provided by the suite, with
   its own connections and the Runner's limits. Their author-supplied pieces move to the sandbox later: Sync's scripts and drivers, and a Build model's
   macros if it gains any.
   Today: as described here, with Sync's scripts and drivers inside the engine's process.
6. **Connection kinds.** The code that implements a hosted connection: the SFTP client, the HTTP client and each database's client. A kind the suite
   ships runs in the host. A version of an added kind runs in the host when an administrator trusts that version. Built and tested as the section
   "Building and testing connection kinds" says, not inside a sandbox.
7. **Services that call out for themselves.** The Gateway's report catalog reads, model providers for agents, and the console's delivery. This is our
   code in a service process, limited by the service's outbound list. Whether PLUGINS' plugins run sandboxed is that subject's to decide; the sandbox
   library is open to it.
8. **Apps in the browser.** Already contained by the Gateway's content security policy. Out of scope here.

## The sandbox

**One policy, many platforms.** A shared library, `DbDataKit.Sandbox`, starts a process under a policy that every caller writes the same way, on every
platform:

- **files:** paths it may read, paths it may write, and nothing else visible. Its own scratch folder is always writable.
- **network:** none, or a list of hosts and ports.
- **environment:** cleared, plus the variables the caller names.
- **identity:** never the service's own. The process runs as a sandbox identity with no logins, no ticket cache and no access to the service's secret
  store.
- **limits:** memory, CPU, process count and wall time. These come from `DbDataKit.Runner.Limits` where the backend has none.

Example: an integration's test on the Workspace gets read access to its build folder, write access to its scratch folder, no network and a 5-minute
limit.

**Backends.** The library maps the policy onto the platform's own sandbox through MXC (Microsoft's sandboxing library, NuGet `Microsoft.Mxc.Sdk`):

| Platform | Backend | No network | Host list | Files |
| --- | --- | --- | --- | --- |
| Linux (systemd service) | Bubblewrap | a new network namespace, enforced by the kernel | through a proxy in the host; whether a program can get around it is checked by the first spike | bind mounts, read-only where asked |
| Windows 11 (service) | ProcessContainer (AppContainer) | no network capability | firewall rules for the container; whether they take host names is checked by the first spike | Brokered File System or access-list entries |
| Windows Server 2022, 2025 | not supported by MXC today | — | — | — |
| Linux in a container (Docker) | Bubblewrap, nested | depends on user namespaces in the container; checked by the spike | | |
| macOS | Seatbelt | yes | no host names | yes |

**Where a backend is missing.** An administrator setting decides what happens, per service and per scenario: refuse to run; run marked trusted,
labelled and audited; or run in a container where Docker or Podman is present. On Windows Server today, a sandboxed run is refused or runs marked
trusted.

**Telemetry.** On eligible Windows builds, MXC sends four diagnostic events that administrative policy does not block. Settled before MXC ships in the
product: turned off in our build, or documented.

**Open questions for the first spike.**

- Whether MXC runs from a Windows service under a virtual service account (`NT SERVICE\…`) in session 0 without administrator rights.
- Whether a host list on Linux can be bypassed by a program that opens raw sockets.
- Whether Windows firewall rules accept host names.
- Whether Bubblewrap runs nested inside the suite's containers.
- The cost of starting one sandbox per test or run.

**WebAssembly (low priority).** A second execution kind for host functions and for integrations written in languages with good WebAssembly support
(Rust, Go, JavaScript, Python). It runs inside the host through a runtime such as Wasmtime, with the hosted-connection API as a WIT interface. It is
the same on every platform, including Windows Server. C# stays on the process sandbox: C# compiled to WebAssembly is experimental, and a Roslyn script
would need the .NET runtime itself compiled to WebAssembly. Without a worker process, a runtime escape lands next to the host's logins, so the
WebAssembly host may run in a worker process that holds only the run's bindings.

## Hosted connections

**What a connection is.** A connection kind is a package of trusted code that the host loads. A binding in a channel names the connection a slot uses,
and the host opens it. Author code never holds the connection itself. It holds a handle, and every call on the handle is an operation the host carries
out.

**Kinds come from packages, the way Sync's drivers do.** A connection kind is a NuGet package with a manifest naming the kind, the families it
implements, its settings, and the version of each family's API it was built against. The host pulls the packages a run's bindings need, as Sync
restores a driver's libraries today. A kind we ship is trusted. A version of a kind an administrator adds is not loaded until an administrator trusts
that version (the section "Building and testing connection kinds").

**One stable API per family, versioned.** An integration is written against the API of a family, never against a particular product's client:

| Family | Operations | Kinds |
| --- | --- | --- |
| database | run a query, stream rows, write a batch, run a statement, read the catalog | SQL Server, PostgreSQL, MySQL, Oracle, DuckDB, Flight SQL |
| documents | get, put, query by filter, stream | Cosmos DB, MongoDB, Elasticsearch and OpenSearch indexes |
| search | query with relevance | Elasticsearch, OpenSearch, Azure AI Search |
| key-value | get, put, delete, list by prefix | Redis, Azure Table, DynamoDB, a table used as a store |
| files | list, read, write without leaving a partial file, move, delete | SFTP, FTPS, SMB, S3, Azure Blob, local folder |
| HTTP | send a request; page through results; respect rate limits and `Retry-After` | any API, with its sign-in |
| directory | look up a person or group, list members, find by attribute, read a manager chain; create a user, set an attribute, add to a group | LDAP and Active Directory, Entra ID through Microsoft Graph, SCIM |
| messages (later) | send, receive, acknowledge | queues, Kafka, Service Bus, SQS, email |

A family's API changes only by a new version. A package built against version 1 keeps working while version 2 exists.

**A kind may implement several families.** One sign-in to one service can offer several: `azure-storage` offers files (Blob), key-value (Table) and
messages (Queue); `cosmos` offers documents, or MongoDB documents when the account uses that API; Snowflake offers a database, file stages and HTTP.
The kind's manifest lists what it offers. A slot names the family it uses, so one connection can serve several slots: a job's `uploads` slot uses
Azure Storage's files family, and its `checkpoints` slot uses its key-value family, with one sign-in and one binding.

**API description profiles for HTTP.** A binding of an HTTP connection can carry a description of the API:

- **OpenAPI (now):** a typed client for the author; the binding can allow named operations only (`getOrders`, not every path); requests and responses
  are checked against the schema; the same document drives the generated fake responses for tests.
- **GraphQL (on demand):** the host parses each operation and enforces an allow-list of operations (or persisted queries) and depth and cost limits; the
  schema drives typed results and fakes.
- **OData (low priority):** queryable REST described by `$metadata`, mapped onto reads in the documents family as well as HTTP.
- **gRPC, SOAP / WSDL (on demand).**

Example: Ben's `crm` slot is bound to a GraphQL API with a profile that allows `query Orders` and `mutation MarkExported`. A `mutation DeleteCustomer`
is refused before it leaves the host, with a message naming the binding's allowed operations, and the refusal is audited.

**What a database binding may do.** A binding of a database connection says which schemas and tables the slot sees, read-only or writable per table;
which kinds of statement it may run (only `SELECT`; `SELECT` and `INSERT`; DDL too); and limits on rows and time. The host checks them before a
statement runs, using the SQL parsing the suite already has (polyglot-sql, `DbDataKit.Dialect`).

Example: Ben's `erp` slot may read `sales.orders` and `sales.lines` and run nothing but `SELECT`. A job that sends `delete from sales.orders` is refused
before the statement reaches the server.

**What the host does on every call.** It checks the call against the binding (the slot, the channel, read or write access); checks the address against
the egress rules; traces the call (who, which slot, what operation, how much data, how long); records it in the audit; and classifies failures the same
way for every kind.

Example: Ben's job calls `supplier.Send(GET /orders?page=3)`. The host checks that `supplier` is bound in `prod` to `https://api.supplier.example`,
that the address is on the Runner's outbound list, adds the sign-in, sends the request, and records "run 4182, slot supplier, GET /orders, 200, 48 KB,
310 ms". If the job asks for `https://other.example`, it is refused with a message naming the binding, and the refusal is audited.

**How author code reaches the host.**

- **A .NET job in a process sandbox:** through the runner protocol, which gains the operations of each family. The kit's `Slot<T>` keeps its shape
  (`Slots.Warehouse.OpenAsync`), so existing jobs change little: what `OpenAsync` returns becomes a proxy to the host instead of a live client.
- **A WebAssembly component (low priority):** through the same operations declared as a WIT interface, called directly inside the host.
- **Marked trusted:** as today, with the connection opened in its own process.

**Results are handles.** A query's result and a file are handles held by the host. They are not data sent to the job. The job reads rows or bytes
through a handle only when its own code needs to look at them. Otherwise it passes the handle on, and the data stays in the host.

**Bulk data.**

- **Copy in the host:** a job that moves data from one slot to another passes a handle and lets the host do the work, so the data never enters the
  sandbox.
  - Rows: `warehouse.WriteFrom(erp.Query("select * from orders where changed > @since"))`. Transfer's writers do the copy.
  - Files: `archive.Write("2026/11/orders.csv", inbox.Open("orders.csv"))` copies a file from an SFTP folder to Azure Blob, streamed by the host.
  - Rows to a file, or a file to rows: `archive.Write("orders.parquet", erp.Query(...).As(Parquet))` and
    `warehouse.WriteFrom(inbox.Open("orders.csv").As(Csv))`. These use the kit's formats in the host.
- **Streams:** when the job's code has to read the data, rows cross into the sandbox as Arrow record batches and bytes as chunks, over the protocol's
  socket, in both directions.
- **Functions in the host:** for work that has to see every row or byte but shouldn't pull it into the sandbox (a transform, a hash, a validation), the
  job names a function that runs inside the host next to the data. The function is a version of a .NET assembly an administrator has approved and
  signed, or a WebAssembly module.

Example: Ben's job lists the files in the supplier's SFTP folder. For each new file, it copies the file to the archive and loads it into the staging
table, passing handles both times. It reads only the first line itself, to check the header. The file's contents never enter the sandbox.

**Moving from today.** The first step changes nothing for authors. `ResolveConnection` keeps working for marked-trusted code. New sandboxed runs get
proxies instead. A package declares in its manifest whether it is written for hosted connections, so the Runner knows which way to run it.

**Open design: one SQL dialect everywhere (future, not high priority).** A supported option for every database binding: the author writes queries in
one dialect (DuckDB, as Build models are written) and the host translates them for the engine, for consistency and tighter control of reads and writes.

**Open design: a shared data shape (needs more design and a review before any task).** One description of a data shape for every family: columns or
fields with canonical types (`DbDataKit.Dialect`), nullability and keys, nested fields for documents; attached to a slot's result, a table, a file's
layout, an HTTP body or a document collection; imported from a database catalog (`catalog-cache/2`), JSON Schema, OpenAPI or a Parquet file's schema.
Arrow's schema as the in-memory form; one file format registered in `docs/formats.json`. It would give typed handles, checks in the host before a write,
generated fakes, and lineage and contracts. Related: SAMPLEDATA, DATAQUALITY, the Runner's contracts.

## Testing without live systems

**The same API, a fake host.** On the Workspace, an integration's test runs in the sandbox exactly as it runs on the Runner, and its slots are bound to
fakes in a test host. The job's code is the same: `Slots.Supplier`, `Slots.Warehouse` and the handles they return behave as on the Runner. Only the
host behind them answers differently. No network, no real source or target, no secrets.

**A fake for each family.**

| Family | Fake |
| --- | --- |
| database | a DuckDB database the test fills: sample rows, or rows built from the table's data shape. A query written for SQL Server runs against it through the host's dialect translation, or the test supplies the rows a named query returns. |
| documents, key-value, search | an in-memory store the test fills. |
| files | `FakeFileShare` (built): a folder the test fills, with the same list, read and write behaviour as SFTP or S3, including a file that appears while the job runs. |
| HTTP | recorded responses (`RecordedHttp`, format `http-recording/1`, built), or responses generated from the binding's OpenAPI document: each operation answers with an example or a value built from its schema, and a test can pin particular answers. |
| directory | a set of people and groups the test lists. |

**Where recordings come from.** The Workspace cannot reach a live system, so a recording is made on a Runner. A run in a non-production channel with
recording on writes each request and response, with secrets and sign-in headers removed. The author pulls the recording into the draft as a test file.

Example: Ben runs his supplier integration once in `dev` with recording on. The run's page offers the recording, and Ben adds it to his project as
`tests/orders-page-1.http-recording`. His test then replays it on the Workspace, with no network.

**What a test checks.** Besides the job's own assertions, the test host records every operation, as the real host does. A test can assert which rows
the job wrote, which files it copied, which requests it sent; that it touched nothing outside its slots; and that it read no data it didn't need to (a
handle passed on instead of read).

**Debugging.** An author steps through a job's test on the Workspace screens or in their own editor. The sandbox allows a debugger to attach to the
test's process; the policy for a debug run is the same as for a test, plus the debugger's port on the loopback.

**Sample data.** Rows, documents and files built from a data shape are SAMPLEDATA's. This subject uses them as one source of fake data.

**Limits of a fake.** A fake shows that the job's logic is right, not that the real system behaves as recorded. The connection kinds' own tests cover
the real systems. A run in a non-production channel on a Runner covers the job against them.

## Building and testing connection kinds

A connection kind is trusted code that runs in the host with real logins. It is never sandboxed, so it is held to a higher bar than an integration: it
is tested against real systems before it ships or is trusted.

**One conformance suite per family.** Each family has a suite every kind of that family must pass. The suites live in `Shared/tests`, as Transfer's
conformance suite does today.

- **Every operation does what the API says:** list, read, write without leaving a partial file, page, retry, stream, and resume after a dropped
  connection.
- **Failures are classified the same way:** a wrong password, an unreachable host, a timeout, a missing file, a refused statement.
- **Security checks:** a call outside the binding is refused; an address off the egress list is refused; no secret appears in a log, a trace, an error
  message or the data sent to the sandbox; a host key or certificate that doesn't match its pin is refused.

Example: the files suite runs against the SFTP kind and the S3 kind alike. It writes a 2 GB file and kills the connection halfway, then checks that no
partial file is left, that the retry finishes, and that the trace shows both attempts.

**Test servers in containers.** The suites run against real servers started from container setups in `testenv`, one profile per system, as the
database engines are today: an SFTP server, an S3-compatible store, an HTTP API with a known OpenAPI description, an LDAP directory, a document store, a
key-value store. They run weekly and on demand (the section on CI). As a later goal, an author developing a new kind starts the same containers on
their own machine with one command.

**Systems a container cannot stand in for.** Windows SMB with Kerberos, Entra ID, and a cloud provider's own storage are tested on a real host or
account, by hand or in a scheduled job, and the kind says which of its claims are proven that way.

**Testing on a live system before use.** Before a binding is first used, and whenever an administrator asks, the connection's test runs on the Runner,
on the real system, with the bound login. It checks that the sign-in works, that the operations the binding allows succeed, and that a write lands
where it should. This is the binding test that exists today, widened to every family.

**Trusting an added kind.** A kind an administrator adds is not loaded until an administrator trusts a specific version of it.

- **What the administrator sees:** the package, its publisher, its signature, the families it implements, and whether that version passed the family's
  conformance suite (the suite runs on the Workspace against the test-server containers).
- **Where it runs:** an administrator chooses to trust a specific version of an added kind to run in the host, as a kind the suite ships does. A new
  version is not loaded until an administrator trusts that version too; until then, bindings keep using the version already trusted. The kind is
  flagged wherever it is used. This is the first version of the feature.
- **Later:** an added kind can run in a connection host of its own: a separate process under the sandbox, holding only the logins of the connections
  bound to that kind and reaching only the addresses those bindings name. Trusting a version to run in the host stays available as the administrator's
  choice.
- **Unproven:** a version that hasn't passed the suite can still be trusted; the console labels it as unproven.
- The trust is recorded in the audit.

## Moving to the rule, and the escape hatches

**Order of the work.**

1. **The sandbox library and its first backend.** `DbDataKit.Sandbox` over MXC, on Linux and Windows 11. Nothing uses it yet. Windows Server is
   documented as unsupported.
2. **Development and testing on the Workspace.** An integration's tests, a Sync script's Test and a driver's test run sandboxed against fakes. This
   replaces staging the Sync set on a Runner for a script's Test (WORKSPACEISOLATION-P31, P32). Build tools (`dotnet`, `bun`) and git run under a policy
   too, with only the package sources and remotes they need, and their own credentials.
3. **Hosted connections for the files and HTTP families**, with their fakes and conformance suites. These are the two families integrations use most.
   Versions of added connection kinds run in the host when an administrator trusts them, flagged; the connection host of their own comes after step 6.
4. **Integrations on the Runner, sandboxed.** A package that declares hosted connections runs in the sandbox, and its slots return proxies. A package
   that doesn't runs marked trusted, as today.
5. **The database, documents, search, key-value and directory families.**
6. **Host-side copies and functions** (trusted assemblies), then WebAssembly (low priority).
7. **Sync's and Build's author-supplied pieces** (scripts, drivers, metadata scripts) move to the sandbox. The engines stay provided by the suite.

**How code is labelled. None of the labels is a deprecation.** Every piece of code that runs shows how it runs, on the package's page, the run's page
and the Runner's Packages screen:

- **Provided by the suite:** our own engines and built-in packages (Sync, Build, the Store, the metadata reader, delivery). They run with the access
  they need, shown as native to the suite, with no added risk shown.
- **Sandboxed:** author code that follows the rule.
- **Marked trusted:** a version of author code an administrator allowed to run outside the rule. Fully supported and not on a path to removal. Shown as
  carrying more risk, with who allowed it, when, and what it can reach (direct connections, the network, the disk). A new version published to the
  Runner starts sandboxed unless the administrator allows that version too.
- **Added connection kind:** a version of a connection kind from outside the suite that an administrator trusted to run in the host. Flagged as
  carrying more risk on the kind's page and on every run that used it, with who trusted it, when, its publisher and signature, and whether it passed
  its family's conformance suite.

The audit records each allowance or trust and each run that used it.

Example: after the upgrade, Ben's existing SFTP integration shows "Marked trusted: opens its own connections; allowed by Ana on 2026-12-03". It keeps
running that way for as long as Ana leaves that version allowed. A run that used an added kind shows "Uses 1 added connection kind: `supplier-files`
1.4.0, trusted to run in the host by Ana on 2026-12-05, conformance passed". When 1.5.0 is added, the kind's page shows it waiting for an
administrator's trust, and runs keep using 1.4.0. A Sync replication shows "Provided by the suite".

**How it fits CI.** SANDBOX adds no workflow that runs on every pull request.

- **On a pull request:** only the tests of the projects the change touches: a family's unit tests, the sandbox library's tests on Linux.
- **Weekly, and on demand before a stable release:** each family's conformance suite against its test-server containers; the sandbox backends on
  Windows 11 and Linux; the hostile-job tests (a job that tries to reach the network, the disk and the protocol's other calls from inside the sandbox).
- **By hand or on a schedule against a real account:** systems a container can't stand in for (SMB with Kerberos, Entra ID, cloud storage).

Which jobs run when is set by CIPLAN.

## Open designs

### Proposed, not yet approved by the owner

- A **Terms** section fixing: sandbox (vs the Runner's "sandbox run" and apps' "sandboxed frame"), trusted (vs "trusted code" needing the approver's
  signature, PUBLISHING-3), kind (project, package, connection, plugin), host (task host, apps host, trusted host, connection host), approve / allow /
  trust.
- An open design **"Shared ground with PLUGINS, PUBLISHING and the Runner"**: PLUGINS' SDK connection contract as the hosted-connection API; connection
  kinds and the plugin manifest; load contexts and the connection host; one vocabulary of approval.
- **Other plans touched** (reviewed in conversation): WORKSPACEISOLATION (-9, -10, -14, -15, P31, P32), COMMANDLINES-8, TRUSTZONES (OS outbound rules,
  -14), TASKENGINE (limits, accounts), PLUGINS, PUBLISHING-3, KNOWLEDGE (-18, -20), APPHOSTING-7, MASTERDATA (extraction), SAMPLEDATA, DATAQUALITY,
  CHANNELS (-3, -7), SYNCAUTHORING, ERRORDETAILS, INSTALLER (labels, Bubblewrap, Windows Server note, testenv), CIPLAN (new).
