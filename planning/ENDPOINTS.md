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

## Needs from other subjects

- INSTALLER-15: the join that hands a group's pinned ports to a further Gateway instance.

## Proposed decisions

### ENDPOINTS-P2: path prefixes (default, awaiting the owner)

`/identity/`, `/apps/`, `/store/`, `/views/`, `/feeds/`, `/mcp/` on the public listener, matching the module names. Scenario: an OData client reads
`https://gateway:7820/feeds/sales/orders`. Recommendation: accept.

### ENDPOINTS-P3: the setting that gives a module a port of its own is its `port` (default, awaiting the owner)

The subject said "a module with its own `listen:`". The modules already have `address` and `port` settings, so a module has a listener of its own when its `port` is set and none
when it is empty. Scenario: `store: { address: 127.0.0.1, port: 9443 }` puts the store on 9443 and a request to `/store/api/v1/models` on the public listener is a 404; with no
`store:` section the store is at `https://gateway:8817/store/api/v1/models`. Option: a `listen:` object per module in place of `address` and `port`, which renames settings every
install has. Recommendation: keep `port`.

### ENDPOINTS-P4: the public listener's port until ENDPOINTS-2 (default, awaiting the owner)

`public.port` is 8817 (the identity home's old port) with `public.address` 0.0.0.0, so an install that reached the identity home at 8817 still does. Scenario: `modules: [identity,
apps]` with no `public:` section serves sign-in at `https://gateway:8817/identity/sign-in` and apps at `https://gateway:8817/apps/<instance>/`; the console's sign-in to that home needs no
change, and an app's redirect URI is `https://gateway:8817/apps/-/callback` (it was 8818). ENDPOINTS-2 replaces the default with a pinned port of 7820 to 7839. Recommendation: accept.

### ENDPOINTS-P5: which paths each module keeps on the public listener (default, awaiting the owner)

The identity home and the apps host keep the paths they already have, because the home's issuer is the listener's address and an app's address is `/apps/<instance>/`: the identity
claim is `IdentityHome.IsHomePath` (`/.well-known/openid-configuration`, `/identity/...`, `/invite`, `/api/auth/...`, `/api/invites`, `/api/users`), the apps claim is `/apps/...` and
every path of a host name bound to an instance. Store, views, feeds and MCP are under `/store`, `/views`, `/feeds`, `/mcp` with the prefix removed for the module, so
`/store/api/v1/models` is the store's `/api/v1/models` and the MCP endpoint is `/mcp`, the same as on a port of its own (its approvals are `/mcp/api/v1/mcp/approvals`). Option: move the
identity home under `/identity/` entirely (issuer `https://gateway:8817/identity`), which changes the issuer every console and app has stored. Recommendation: accept.
A request through a reverse proxy that sets `X-Forwarded-Host` to a bound name is not claimed by the apps host on the public listener (only the `Host` header is read); APPHOSTING-4 decides it.

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

## Shared areas touched

- `DbDataGateway.Server` hosting, `DbDataKit.Serving.*` module registration, `DbDataKit.Identity` (issuer and redirect URIs), `DbDataKit.Apps.*` (addresses).
- `scripts/check_default_ports.py`, `docs/suite-listeners-and-auth.md`, `docs/serving-layer.md`, `docs/release-notes-pending.md`.
