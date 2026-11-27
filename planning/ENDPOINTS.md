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
| ENDPOINTS-9 | Flight SQL's listener is `flightsql: { listen: ... }`, like every other listener of the Gateway; the top-level `listen` of the Gateway is refused with a message naming `flightsql.listen`; the example file and the docs follow | none | S | a Gateway with `flightsql.listen` listens there; one with a top-level `listen` refuses to start naming `flightsql.listen` |
| ENDPOINTS-10 | Two Gateways that start at the same moment on one machine never pick the same free port: the pick holds a machine-wide lock until the listener is bound, or binds the port itself and hands the socket on | none | S | a test starts two Gateways at once with no `public.listen`; each binds its own port of the block |
| ENDPOINTS-11 | The identity home on its own (`dbdatakit identity run`) gets a settings file; with it `--listen` takes a list of addresses, and its issuer, pages and calls move under `/identity` like the Gateway's home | none | M | a home on its own listens on two addresses; its discovery document is at `<address>/identity/.well-known/openid-configuration` |

## Needs from other subjects

- The join hands a group's pinned ports to a further Gateway instance by the settings' names in `server.yaml` (`GatewayListeners.Reported` and `TakeGroup`: `public.listen`, `listen`, `management.listen`).

## Open designs

### The layout and settings of the public listener

Built: every module under a `path`, the identity home's pages, calls and issuer under `/identity`, the apps host's redirect URIs under the apps path. What is still open:

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
