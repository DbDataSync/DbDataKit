# ENDPOINTS: the Gateway's listeners

## Goal

A Gateway answers end users on one public HTTPS listener that routes by path (identity, apps, store, views, REST feeds, MCP, each under a path the administrator sets), Flight SQL on a port of its
own, and its management API on a port of its own, on the loopback address by default. A module may still be given a listener of its own by its `listen` setting. A
PostgreSQL wire listener can be added later. Every listener takes a free value of the Gateway's sub-block, pinned, and every instance of a group
listens on the same ports (`docs/install.md`, "Ports"). A client set to an old port finds nothing and is set again.

## What exists today

- One public HTTPS listener (`PublicListener`, `public.listen` in `server.yaml`) serves the identity home, apps, store, views, feeds and MCP
  modules, each under a path set by its `path` setting (`/identity`, `/apps`, `/store`, `/views`, `/feeds`, `/mcp` by default; `docs/serving-layer.md`, "The public listener"); `/` answers 404; a module with a `listen` set listens there instead and has no path.
- Every listener of the Gateway has one `listen` setting; the public listener, Flight SQL and management take free ports of the block 7820 to 7839 at the first start and pin them (`GatewayListeners`); `scripts/check_default_ports.py` checks the block and that no listener has a fixed default again.
- The apps host routes by path and host name for app instances (`AppAddress`, `docs/apps.md`); on the public listener it also answers every path of a bound name.
- The identity home is hosted by the Gateway by default; `dbdatakit identity run` hosts it on its own (port 7819, no path), and nothing forwards a Gateway's public
  identity paths to a home elsewhere.
- Traces: the console's relay passes `traceparent` to every host; the serving contract's `TraceId` and the hop through a module are not traced
  (`docs/observability.md`).

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| ENDPOINTS-3 | Clean break for clients: the release notes, `docs/suite-listeners-and-auth.md` and `docs/quickstart.md` list each old port and where its new one is read (the console's Servers page, or the Gateway's pinned settings); the app manifest's and the views' absolute addresses, the identity issuer and the redirect URIs follow the public listener; `dist/compose/quickstart-apps.yml` and its scripts move to the public listener, dropping the two module `listen` settings (identity 8817, apps 8818) for one published port with the modules under their prefixes, and the redirect URI they register becomes `https://localhost:<public port>/apps/-/callback` | none | S | a test signs in through the public listener and an app's redirect URI resolves under it; a test reads the quickstart: no module has a `listen` of its own, one port is published and the registered redirect URI is under the apps prefix; `check_docs_commands.py` passes; no doc names 8817 to 8822 as a current port |
| ENDPOINTS-4 | The identity home behind the firewall: the public listener forwards the identity paths to a home that runs on its own (`identity.forward_to`) | none | S | the sign-in test passes with the home in another process reached only through the Gateway's public listener |
| ENDPOINTS-5 | Traces through the public listener and a module: the serving contract's `TraceId` is the W3C trace id; a request through the public listener and into a module is one trace; spans carry the person's user id (never a name, a secret or a parameter value); the console's status and pairing calls to a server carry `traceparent` like its relayed calls | none | S | a test with an in-memory exporter finds one trace with the listener's span and the module's span as its child, and the contract's id equal to it; a span names the caller's user id and no parameter value; a status call's server span shares the console's trace id |
| ENDPOINTS-6 | Reserve the PostgreSQL wire listener: a `pgwire` module entry whose listener takes a free value of the Gateway's block like the others (no fixed port, 5432 included), and refuses to start while the protocol is not built (the protocol itself is parked) | none | S | a test: with `pgwire` enabled the Gateway refuses to start naming the setting and that the protocol is not built |
| ENDPOINTS-8 | The root app: a third address kind for the Gateway's own host names (the names its certificate serves), next to `subdomain` and `custom`, with no certificate of its own; the save check says which names the server offers. The root app is an instance address of that kind with path `/` (`AppAddress`, one instance per host and path pair); the instance's pages are `/`, `/_app/...` and `/_token` and it keeps working at `/apps/<instance>/`. Module prefixes answer before the root app, an instance whose name equals a module prefix is refused when it is saved, and the apps module's plain-text hint is answered only under the module's prefix. No `apps.root` setting and no `root: true` flag on the instance. The kind needs its entry in `docs/apps.md`, the Gateway's Apps screen and the `app-instance` row of `docs/ui-editors.md` | none | M | a test: an instance with an address of the new kind on a Gateway host name with path `/` answers its page at `/` and at `/apps/<instance>/`, `/store/api/v1/models` stays the store's, a name that is not one of the server's host names is refused when saved, and an instance named like a module prefix is refused |

## Needs from other subjects

- INSTALLER-15: the join that hands a group's pinned ports to a further Gateway instance.

## Proposed decisions

### ENDPOINTS-P11: Flight SQL's setting is the top-level `listen` (default, awaiting the owner)

Scenario: an administrator opens `config/server.yaml` of a Gateway that has been started once and finds `public: { listen: ... }`, `management: { listen: ... }` and `listen: https://0.0.0.0:7821`. The last is Flight SQL, as it is the Gateway's one top-level `listen`, like the console's, the Runner's and the Workspace's. Option B: a section of its own, `flightsql: { listen: ... }`, so every listener of the Gateway is `<name>.listen` and the top-level `listen` does not exist. Recommendation: B is the more consistent shape; the default keeps the old key name and the Gateway's old top-level section. Changing it is a rename of one setting (the options, the retired-key message, the example file, the docs).

### ENDPOINTS-P12: the public listener starts in every case (default, awaiting the owner)

Scenario: a Gateway with `modules: [flightsql]` and no other setting. Built as the default: it listens on three ports (7820 public, 7821 Flight SQL, 7822 management); the public listener answers `/health/live` and 404 for everything else. Before, it started only when a module was mounted on it, so the same Gateway listened on two ports. Option B: it starts only when a module is on it (or, later, a root app is bound), so a Gateway of Flight SQL alone has two ports. Recommendation: the default, since the goal says three listeners by default and a load balancer has one address to probe.

### ENDPOINTS-P13: the container image sets `listen` for Flight SQL and management (default, awaiting the owner)

Scenario: `docker run -p 8815:8815 dbdatagateway` has to answer on 8815, yet no listener has a fixed default. Built as the default: `DbDataGateway/Dockerfile` sets `DBDATAKIT_Gateway__Listen=https://0.0.0.0:8815` and `DBDATAKIT_Gateway__Management__Listen=https://127.0.0.1:8816`, which are the container's configured addresses and are never moved. Option B: the image sets nothing and every `docker run` and compose file names the Flight SQL address it publishes. Recommendation: the default; a published port is a decision of the container.

### ENDPOINTS-P14: a written port outside the block is accepted (default, awaiting the owner)

Scenario: an administrator writes `listen: https://0.0.0.0:8815` in `server.yaml`. Built as the default: the Gateway listens there and never moves it; only the clash with another listener is checked, and the block 7820 to 7839 is the range the choosing uses. Option B: a configuration warning says the port is outside the Gateway's block (`scripts/check_default_ports.py` does not read settings). Recommendation: the default, since Docker Compose and existing clients write their own ports.

### ENDPOINTS-P15: `dbdatakit identity run` takes one address (default, awaiting the owner)

Scenario: `dbdatakit identity run --listen "https://localhost:7819;https://localhost:7829"`. Built as the default: the identity home on its own, which is a command of the console's program with its own `--listen`, still takes one address. The console, the Runner and the Workspace read a list. Option B: it reads a list as well. Recommendation: B when the identity home on its own gets a settings file; today it has none.

### ENDPOINTS-P16: the first address of a list is the one a server reports (default, awaiting the owner)

Scenario: the Runner has `listen: [https://127.0.0.1:7841, https://10.0.0.5:7841]`. Built as the default: it reports `https://127.0.0.1:7841`, the first, to the console when it joins, and the console shows that address. Option B: it reports the first address that is not a loopback address. Recommendation: the default, and the administrator writes the address the console reaches first.

### ENDPOINTS-P17: the setting that moves a module is `path` (default, awaiting the owner)

Scenario: an administrator moves the store to `/warehouse`: `store: { path: /warehouse }` (`rest: { path: /odata }` for feeds, `identity`, `apps`, `views` and `mcp` the same). Built as the default, because the survey named it so. Option B: `prefix`. Recommendation: the default; the word names the part of the address people see.

### ENDPOINTS-P18: a module with a `listen` of its own has no path (default, awaiting the owner)

Scenario: `store: { listen: https://0.0.0.0:9443, path: /warehouse }`. Built as the default: the store answers at the root of that listener (`https://gateway:9443/api/v1/models`) and `path` is not read; the identity home on a `listen` of its own has its issuer at that address with no path, as it always had. Option B: the path applies on a listener of its own too. Recommendation: the default; a listener of its own is how a module gets its own address.

### ENDPOINTS-P19: the old identity addresses answer 404, no pointer for one release (default, awaiting the owner)

Scenario: a script reads `https://gateway:7820/.well-known/openid-configuration` after the upgrade. Built as the default: 404 with the listener's JSON body, and the release notes say to read `<issuer>/.well-known/openid-configuration` (`https://gateway:7820/identity/...`); `/invite` and `/api/auth/...` at the root answer 404 the same way. Option B: the discovery path at the root redirects to the new one for one release. Recommendation: the default, a clean break the owner chose.

### ENDPOINTS-P20: a path of two or more segments is accepted (default, awaiting the owner)

Scenario: `store: { path: /data/store }` with `views: { path: /data/views }`. Built as the default: accepted when no other module has the same path or one inside it (`/data` for one and `/data/store` for another is refused naming the later setting). Option B: one segment only. Recommendation: the default.

### ENDPOINTS-P21: the identity home on its own keeps its issuer and its paths (default, awaiting the owner)

Scenario: `dbdatakit identity run` on port 7819. Built as the default: its issuer is `https://localhost:7819`, the provider's endpoints are `/identity/...` and the invitation page and `/api/...` are at the root, as before; only the home the Gateway serves is under a path. Consoles paired with it need no change. Option B: it moves under `/identity` too, so both homes have the same addresses and a console's setting is always `<address>/identity`. Recommendation: B when the home on its own gets its settings file; today it has none.

### ENDPOINTS-P22: at a name bound to an app instance, the default apps path still answers when the apps path is changed (default, awaiting the owner)

Scenario: `apps: { path: /programs }` and an instance bound to `notes.example.com`. Built as the default: `https://notes.example.com/programs/-/callback` is the host's callback at that name, and `/apps/...` at that name is still read as the host's own paths in two places (the shared script and the callback), so `https://notes.example.com/apps/-/callback` also answers. Option B: refuse `/apps/...` at a bound name when the path is another. Recommendation: B, with a test, in the task that decides the bound names' paths (APPHOSTING-4).

### ENDPOINTS-P23: two Gateways that start at the same moment can pick the same default port (awaiting the owner)

Scenario: an administrator starts two Gateways on one machine, each with its own data directory and no `public.listen`, from one script (`dbdatagateway server run --Gateway:DataDirectory=/srv/a &` and the same for `/srv/b`). Each picks the first free port of its block (7820) and writes it down, but binds it a moment later, so both pick 7820, and the second stops with `Port 7820 cannot be bound`. Found when the console's two-Gateway server test failed this way; the test now gives each Gateway its own `--Gateway:Public:Listen`, which hides it, so nothing tests the case itself. Option A (recommendation): the pick takes a machine-wide lock until the listener is bound, or the pick binds the port itself and hands the socket on, so a second start sees 7820 taken and picks 7823. Option B: leave it; the guide says that Gateways on one machine name their public listener, as every Gateway of a group already does. Awaiting the owner.

## Open designs

### The layout and settings of the public listener

Built (ENDPOINTS-P17 to P22 are the defaults taken): every module under a `path`, the identity home's pages, calls and issuer under `/identity`, the apps host's redirect URIs under the apps path. What is still open:

- **Health and version.** Today the public listener answers `/health/live` only; `/health`, `/health/ready` and `/version` are on the management listener. Open: whether all four stay at the root of every listener, which a load balancer in front of the public listener would need.
- **A path change and apps.** `apps: { path: /a }` gives `/a/<instance>/`, and the redirect URIs the host registers at its own home follow it (they are reconciled at each start and each instance save). A redirect URI already registered at a home elsewhere, for an instance or a bound name, is not changed: open, whether the answer of the save lists each `redirect_uri`, as it does for an address.
- **A reverse proxy.** A request through a reverse proxy that sets `X-Forwarded-Host` to a bound name is not claimed by the apps host on the public listener (only the `Host` header is read). APPHOSTING-4 decides it.

### Docs and code that disagree

Found while writing `docs/hosted-pages.md`. Each is a question for a task, not a decision:

- `docs/apps.md` said domain binding is not built, while the same file's section "Names bound to an instance" and the code build it. The three stale sentences were fixed in the docs; nothing else is open there.
- `docs/formats.json` has no entry for `app-instance`, although the definition kind is stored with format 2 when the instance has addresses (format 1 otherwise; `AppInstanceStore.InstanceKind`). `scripts/check_formats.py` does not fail on it. Open: the entry (current 2, previous 1, open or frozen) and its row in `docs/ui-editors.md` (the Gateway's Apps screen edits it). Adding it is a shared-file change that touches a stored format, so it waits for the owner.
- `primary` on an address is stored, shown and checked (at most one per instance), and no routing or redirect reads it. `docs/apps.md` says "the subpath is the primary address when none is". Open: what a primary address is for (the address the shell page redirects to, the one in the catalog's links, the one an app's `redirect_uri` lists first), or removing the field.
- `apps instance-save` has `--name`, `--app`, `--pin`, `--title`, `--setting` and `--bind` and no address flag, so an address is set only through `PUT /api/v1/app-instances/{name}` or the Apps screen. Open: whether the command takes `--address host[,path]`.
- The apps module answers every unmatched path with a plain-text hint ("An app is at /apps/<instance>/"). With a root app (ENDPOINTS-8) an instance address with path `/` makes `/` the app's, so the hint is answered only under the module's prefix, where a path that is no instance keeps it.

## Shared areas touched

- `DbDataGateway.Server` hosting, `DbDataKit.Serving.*` module registration, `DbDataKit.Identity` (issuer and redirect URIs), `DbDataKit.Apps.*` (addresses).
- `scripts/check_default_ports.py`, `docs/suite-listeners-and-auth.md`, `docs/serving-layer.md`, `docs/release-notes-pending.md`.
