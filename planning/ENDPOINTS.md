# ENDPOINTS: the Gateway's listeners

## Goal

A Gateway answers end users on one public HTTPS listener that routes by path (identity, apps, store, views, REST feeds, MCP), Flight SQL on a port of its
own, and its management API on a port of its own, on the loopback address by default. A module may still be given a port of its own by a setting. A
PostgreSQL wire listener can be added later. Every listener takes a free value of the Gateway's sub-block, pinned, and every instance of a group
listens on the same ports (`docs/install.md`, "Ports"). A client set to an old port finds nothing and is set again.

## What exists today

- One listener per module in `GatewayOptions.cs`: Flight 8815, management 8816 (on `127.0.0.1` by default), identity 8817, apps 8818, store 8819, views
  8820, REST feeds 8821, MCP 8822; `scripts/check_default_ports.py` reads each default.
- The apps listener already routes by path and host name for app instances (`AppAddress`, `docs/apps.md`).
- The REST feeds module (`feeds`) has its own listener, off unless enabled.
- The identity home is hosted by the Gateway by default; `dbdatakit identity run` hosts it on its own (port 8482), and nothing forwards a Gateway's public
  identity paths to a home elsewhere.
- Traces: the console's relay passes `traceparent` to every host; the serving contract's `TraceId` and the hop through a module are not traced
  (`docs/observability.md`).

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| ENDPOINTS-1 | The public listener: one HTTPS listener with path prefixes for identity, apps, store, views, feeds and MCP; each module mounts its routes under its prefix; `modules:` still enables each; a module with its own `listen:` keeps a listener of its own | none | L | `DbDataGateway.Tests`: one process serves sign-in, an app, a store read, a view, a feed and an MCP call on one port; with `store.listen` set the store answers on its own port and not under the prefix; a module that is off answers 404 under its prefix |
| ENDPOINTS-2 | Three listeners by default (public, Flight SQL, management), none with a fixed port: each takes a free value of the Gateway's sub-block, pinned (`docs/install.md`, "Ports"); `check_default_ports.py` reads the Gateway's sub-block in place of a default per listener; the per-module defaults removed | ENDPOINTS-1 | S | `check_default_ports.py` passes with the Gateway's sub-block and no per-listener default; a Gateway started with no settings listens on exactly three ports, each inside 7820 to 7839, the same three after a restart |
| ENDPOINTS-3 | Clean break for clients: the release notes, `docs/suite-listeners-and-auth.md` and `docs/quickstart.md` list each old port and where its new one is read (the console's Servers page, or the Gateway's pinned settings); the app manifest's and the views' absolute addresses, the identity issuer and the redirect URIs follow the public listener | ENDPOINTS-2 | S | a test signs in through the public listener and an app's redirect URI resolves under it; `check_docs_commands.py` passes; no doc names 8817 to 8822 as a current port |
| ENDPOINTS-4 | The identity home behind the firewall: the public listener forwards the identity paths to a home that runs on its own (`identity.forward_to`) | ENDPOINTS-1 | S | the sign-in test passes with the home in another process reached only through the Gateway's public listener |
| ENDPOINTS-5 | Traces through the public listener and a module: the serving contract's `TraceId` is the W3C trace id; a request through the public listener and into a module is one trace; spans carry the person's user id (never a name, a secret or a parameter value); the console's status and pairing calls to a server carry `traceparent` like its relayed calls | ENDPOINTS-1 | S | a test with an in-memory exporter finds one trace with the listener's span and the module's span as its child, and the contract's id equal to it; a span names the caller's user id and no parameter value; a status call's server span shares the console's trace id |
| ENDPOINTS-6 | Reserve the PostgreSQL wire listener: a `pgwire` module entry whose listener takes a free value of the Gateway's sub-block like the others (no fixed port, 5432 included), and refuses to start while the protocol is not built (the protocol itself is parked) | ENDPOINTS-2 | S | a test: with `pgwire` enabled the Gateway refuses to start naming the setting and that the protocol is not built |

## Needs from other subjects

- INSTALLER-15: the join that hands a group's pinned ports to a further Gateway instance.

## Proposed decisions

### ENDPOINTS-P2: path prefixes (default, awaiting the owner)

`/identity/`, `/apps/`, `/store/`, `/views/`, `/feeds/`, `/mcp/` on the public listener, matching the module names. Scenario: an OData client reads
`https://gateway:7820/feeds/sales/orders`. Recommendation: accept.

## Shared areas touched

- `DbDataGateway.Server` hosting, `DbDataKit.Serving.*` module registration, `DbDataKit.Identity` (issuer and redirect URIs), `DbDataKit.Apps.*` (addresses).
- `scripts/check_default_ports.py`, `docs/suite-listeners-and-auth.md`, `docs/serving-layer.md`, `docs/release-notes-pending.md`.
