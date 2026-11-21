# ENDPOINTS: the Gateway's listeners

## Goal

A Gateway answers end users on one public HTTPS listener that routes by path (identity, apps, store, views, REST feeds, MCP), Flight SQL on a port of its
own, and its management API on a port of its own, on the loopback address by default. A module may still be given a port of its own by a setting. A
PostgreSQL wire listener can be added later. Every listener takes a free value of the Gateway's sub-block, pinned, and every instance of a group
listens on the same ports (`docs/install.md`, "Ports"). A client set to an old port finds nothing and is set again.

## What exists today

- One public HTTPS listener (`PublicListener`, `public:` in `server.yaml`, port 8817 until ENDPOINTS-2) serves the identity home, apps, store, views, feeds and MCP
  modules by path (`docs/serving-layer.md`, "The public listener"); a module with a `port` set listens there instead and is not under its prefix.
- Flight 8815 and management 8816 (on `127.0.0.1` by default) are fixed ports; `scripts/check_default_ports.py` reads the three defaults.
- The apps host routes by path and host name for app instances (`AppAddress`, `docs/apps.md`); on the public listener it also answers every path of a bound name.
- The identity home is hosted by the Gateway by default; `dbdatakit identity run` hosts it on its own (port 8482), and nothing forwards a Gateway's public
  identity paths to a home elsewhere.
- Traces: the console's relay passes `traceparent` to every host; the serving contract's `TraceId` and the hop through a module are not traced
  (`docs/observability.md`).

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| ENDPOINTS-2 | Three listeners by default (public, Flight SQL, management), none with a fixed port: each takes a free port of the Gateway's block 7820 to 7839 at the first start of its group's first instance and pins it, with the port choosing and pinning the console, Runner and Workspace already use (`ListenPorts` in `DbDataKit.Hosting.Startup`; `docs/install.md`, "Ports"); a configured port is never moved; the per-module defaults removed; `check_default_ports.py` gives the Gateway's entries the block `Gateway` in place of a default per listener; if the pin adds a key to the Gateway's `server.yaml`, its `docs/formats.json` entry is bumped in the same task and its `docs/ui-editors.md` row says where it is edited | none | S | `check_default_ports.py` passes with the Gateway's block and no per-listener default; a Gateway started with no settings listens on exactly three ports, each inside 7820 to 7839, the same three after a restart; with 7820 held the listeners take other free ports of the block; a configured port that is held fails the ports step naming it |
| ENDPOINTS-3 | Clean break for clients: the release notes, `docs/suite-listeners-and-auth.md` and `docs/quickstart.md` list each old port and where its new one is read (the console's Servers page, or the Gateway's pinned settings); the app manifest's and the views' absolute addresses, the identity issuer and the redirect URIs follow the public listener | ENDPOINTS-2 | S | a test signs in through the public listener and an app's redirect URI resolves under it; `check_docs_commands.py` passes; no doc names 8817 to 8822 as a current port |
| ENDPOINTS-4 | The identity home behind the firewall: the public listener forwards the identity paths to a home that runs on its own (`identity.forward_to`) | none | S | the sign-in test passes with the home in another process reached only through the Gateway's public listener |
| ENDPOINTS-5 | Traces through the public listener and a module: the serving contract's `TraceId` is the W3C trace id; a request through the public listener and into a module is one trace; spans carry the person's user id (never a name, a secret or a parameter value); the console's status and pairing calls to a server carry `traceparent` like its relayed calls | none | S | a test with an in-memory exporter finds one trace with the listener's span and the module's span as its child, and the contract's id equal to it; a span names the caller's user id and no parameter value; a status call's server span shares the console's trace id |
| ENDPOINTS-6 | Reserve the PostgreSQL wire listener: a `pgwire` module entry whose listener takes a free value of the Gateway's sub-block like the others (no fixed port, 5432 included), and refuses to start while the protocol is not built (the protocol itself is parked) | ENDPOINTS-2 | S | a test: with `pgwire` enabled the Gateway refuses to start naming the setting and that the protocol is not built |
| ENDPOINTS-7 | Every module under a prefix on the public listener, each prefix set by the administrator: `/identity`, `/apps`, `/store`, `/views`, `/feeds`, `/mcp` by default, each with a setting of its module that moves it. Identity's pages and calls move under `/identity` (`/identity/invite`, `/identity/api/auth/...`, `/identity/api/invites...`, `/identity/api/users...`, `/identity/api/access-tokens`), and its issuer becomes the listener's address with `/identity` and the discovery document is read at `<issuer>/.well-known/openid-configuration`; the apps host's redirect URIs follow the apps prefix; a console, app or client that stored the old issuer is set again and a clean break says so naming the setting. HTML pages are allowed under any prefix. `/` answers 404 on the public listener (a root app is not part of this task). It reworks the public listener as built (`IdentityHome.IsHomePath`, the `PublicPrefix` constants, `docs/serving-layer.md`, `docs/hosted-pages.md`) | none | M | a test with the defaults: the sign-in page is at `/identity/sign-in`, the invitation page at `/identity/invite`, the discovery document at `/identity/.well-known/openid-configuration` with the issuer `https://<host>:<port>/identity`, and `/invite`, `/api/auth/...` and `/.well-known/openid-configuration` answer 404; a sign-in through the public listener succeeds with the full issuer URL; with a module's prefix set to another value the module answers there and 404 under the default; an app's redirect URI is `<apps prefix>/-/callback` under the listener; `docs/hosted-pages.md` lists the new paths |

## Needs from other subjects

- INSTALLER-15: the join that hands a group's pinned ports to a further Gateway instance.

## Proposed decisions

### ENDPOINTS-P4: the public listener's port until ENDPOINTS-2 (default, awaiting the owner)

`public.port` is 8817 (the identity home's old port) with `public.address` 0.0.0.0, so an install that reached the identity home at 8817 still does. Scenario: `modules: [identity,
apps]` with no `public:` section serves sign-in at `https://gateway:8817/identity/sign-in` and apps at `https://gateway:8817/apps/<instance>/`; the console's sign-in to that home needs no
change, and an app's redirect URI is `https://gateway:8817/apps/-/callback` (it was 8818). ENDPOINTS-2 replaces the default with a pinned port of 7820 to 7839. Recommendation: accept.

### ENDPOINTS-P6: the ports step lists the public port (awaiting the owner)

The Gateway's `ports` start step checks the Flight and management ports before the host starts (`StartupPorts.BindAsync` in `Cli.cs`). The public listener's port is not in that list,
because the Gateway's start belongs to INSTALLER-12 while the public listener was built: a held public port fails the start with the host's own "address already in use" error instead of
the step's message naming the port and the excluded ranges. Scenario: port 8817 is held by another program; `dbdatagateway server run` fails without the ports step's file. Recommendation:
ENDPOINTS-2 adds the public port to the step with the port choosing.

### ENDPOINTS-P7: the Gateway settings form shows 8821 for the feeds port (awaiting the owner)

The console's Gateway settings form (`Shared/web/gateway/src/settings.ts`) has a number box for `rest.port` with 8821 as its starting value. The module now has no port unless set, so the
box offers a value that is not the setting. Only a changed box is saved, and its hint now says so. Scenario: an administrator opens Settings, leaves the box at 8821 and saves: nothing
changes and feeds stays under `/feeds`; typing 9444 gives feeds its own listener. Option: allow an empty box (a form change in the Gateway app and its test). Recommendation: allow the
empty box in ENDPOINTS-2, which removes the other fixed defaults.

### ENDPOINTS-P8: the apps quickstart sets the module ports (default, awaiting the owner)

`dist/compose/quickstart-apps.yml` publishes 8817 and 8818 and its scripts register the redirect `https://localhost:8818/apps/-/callback`. It now sets `identity.port` 8817 and `apps.port`
8818, so the quickstart behaves as before. Scenario: `docker compose ... up` serves the identity home at `https://localhost:8817` and apps at `https://localhost:8818/apps/<instance>/`.
ENDPOINTS-3 moves it to the public listener (one published port). Recommendation: accept.

### ENDPOINTS-P9: one `listen` setting everywhere (awaiting the owner)

Scenario: an operator wants the console on `https://0.0.0.0:8800`, the Gateway's Flight listener on all addresses, and the store module on `https://127.0.0.1:9443` only. Today that is
`listen: { address, port }` on the Gateway, `public: { address, port }`, and `port` and `address` on each module. Proposal: every service and every Gateway listener takes one `listen`
setting, a Kestrel-style URL or a list of URLs (`https://0.0.0.0:8817`, `[https://127.0.0.1:9443]`); `https` only where the Gateway's certificate is used. `port` and `address` are removed
with no alias and no version bump; an old shape fails with a message naming the file and the setting. Validation of clashes and of the 7820 to 7839 ranges reads the parsed URLs.
Recommendation: build it in ENDPOINTS-2 together with the pinning; split out the suite-wide rename, with its test and doc changes, only if that makes ENDPOINTS-2 simpler.

### ENDPOINTS-P10: the root app (awaiting the owner)

Scenario: an administrator wants `https://gateway.example.com:8817/` to open the sales app, while `/store/api/v1/models` stays the store's. All binding information stays with the app instance; the server's configuration only offers host names (`apps.domain` for subdomains, the Gateway's own TLS host names). Proposal: the root app is an address of the instance, `{host: gateway.example.com, path: "/"}`, the existing `AppAddress` record. The rule that a host and path pair belongs to one instance already gives one root per name. The instance behaves as at any bound name with `path: "/"`: its pages are `/`, `/_app/...` and `/_token`, and it keeps working at `/apps/sales/`.

- Module prefixes answer before the root app, so a path under a module prefix is the module's and never the app's. An instance whose name equals a module prefix (`store` and the like) is refused when it is saved.
- Open point: the Gateway's own host names have no address kind. `subdomain` needs `apps.domain` and `custom` needs a certificate of its own. A third kind for a name the server offers (no certificate of its own; the Gateway's certificate serves it) would fill the gap. Option 1: add that kind, named by the owner. Option 2: allow `custom` without a certificate when the host is one of the Gateway's TLS host names. Recommendation: option 1, because the save check can then say which names the server offers.
- A setting `apps.root` of the server, and a flag `root: true` on the instance, are rejected (owner).

## Open designs

### The layout and settings of the public listener

What the owner has answered (ENDPOINTS-7 builds it): every module sits under a prefix, each prefix is set by the administrator, HTML pages are allowed under any prefix, only an app may be served at `/` (through an address of its instance, ENDPOINTS-P10), and identity's HTML pages are under `/identity`. What is not settled, and what a design has to answer before the tasks that follow:

- **The setting.** Each module would take a `path` setting, with the defaults `/identity`, `/apps`, `/store`, `/views`, `/feeds`, `/mcp`:

  ```yaml
  public:
    listen: https://0.0.0.0:8817
  modules: [identity, apps, store, views, feeds, mcp]
  identity: { path: /identity }
  apps:     { path: /apps }
  store:    { path: /store }
  ```

  The name `path` is the survey's, not the owner's. A module with a `listen` of its own has no prefix unless one is set: open, because ENDPOINTS-P9 (one `listen` setting) is not answered.
- **Identity's paths.** Under the answer the identity home's calls move too: `/identity/invite`, `/identity/api/auth/...`, `/identity/api/invites...`, `/identity/api/users...`, `/identity/api/access-tokens`, and the discovery document at `/identity/.well-known/openid-configuration`. The issuer becomes `https://gateway:8817/identity`. Scenario: the console, an app or a script that stored the issuer `https://gateway:8817` finds a 404 at `/.well-known/openid-configuration` and must be set to the full issuer URL. A client that builds the discovery address from the host root alone cannot find the home. Open: whether the Gateway also answers the old discovery path with a redirect or a pointer to the new one for one release, or the break is clean.
- **Health and version.** Today the public listener answers `/health/live` only; `/health`, `/health/ready` and `/version` are on the management listener. Open: whether all four stay at the root of every listener, which a load balancer in front of the public listener would need.
- **A prefix change and apps.** `apps: { path: /a }` gives `/a/<instance>/`. The OpenID redirect URIs the module registers (`<prefix>/-/callback`, `https://<name>/apps/-/callback` for a bound name) follow it, and a bound name's rewrite target (`/apps/<instance>/<rest>`) follows it. Open: what happens to a redirect URI already registered at a home elsewhere when an administrator changes the prefix (the answer of the save lists each `redirect_uri`, as it does for an address).
- **A reverse proxy.** A request through a reverse proxy that sets `X-Forwarded-Host` to a bound name is not claimed by the apps host on the public listener (only the `Host` header is read). APPHOSTING-4 decides it.
- **Store, views, feeds and mcp.** The module sees the path with its prefix removed (`/store/api/v1/models` is the store's `/api/v1/models`), the same as on a port of its own. A prefix of two segments (`/data/store`) is open: nothing says it is refused.

### Docs and code that disagree

Found while writing `docs/hosted-pages.md`. Each is a question for a task, not a decision:

- `docs/apps.md` said domain binding is not built, while the same file's section "Names bound to an instance" and the code build it. The three stale sentences were fixed in the docs; nothing else is open there.
- `docs/formats.json` has no entry for `app-instance`, although the definition kind is stored with format 2 when the instance has addresses (format 1 otherwise; `AppInstanceStore.InstanceKind`). `scripts/check_formats.py` does not fail on it. Open: the entry (current 2, previous 1, open or frozen) and its row in `docs/ui-editors.md` (the Gateway's Apps screen edits it). Adding it is a shared-file change that touches a stored format, so it waits for the owner.
- `primary` on an address is stored, shown and checked (at most one per instance), and no routing or redirect reads it. `docs/apps.md` says "the subpath is the primary address when none is". Open: what a primary address is for (the address the shell page redirects to, the one in the catalog's links, the one an app's `redirect_uri` lists first), or removing the field.
- `apps instance-save` has `--name`, `--app`, `--pin`, `--title`, `--setting` and `--bind` and no address flag, so an address is set only through `PUT /api/v1/app-instances/{name}` or the Apps screen. Open: whether the command takes `--address host[,path]`.
- The apps module answers every unmatched path with a plain-text hint ("An app is at /apps/<instance>/"). With a root app (ENDPOINTS-P10) an instance address with path `/` makes `/` the app's, so the hint is answered only under the module's prefix, where a path that is no instance keeps it.

## Shared areas touched

- `DbDataGateway.Server` hosting, `DbDataKit.Serving.*` module registration, `DbDataKit.Identity` (issuer and redirect URIs), `DbDataKit.Apps.*` (addresses).
- `scripts/check_default_ports.py`, `docs/suite-listeners-and-auth.md`, `docs/serving-layer.md`, `docs/release-notes-pending.md`.
