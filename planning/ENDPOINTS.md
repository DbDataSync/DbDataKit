# ENDPOINTS: the Gateway's listeners

## Goal

A Gateway answers end users on one public HTTPS listener that routes by path (identity, apps, store, views, REST feeds, MCP), Flight SQL on a port of its
own, and its management API on a port of its own, on the loopback address by default. A module may still be given a port of its own by a setting. A
PostgreSQL wire listener can be added later (5432 when free, else a port of the suite range). A client set to an old port finds nothing and is set again.

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
| ENDPOINTS-2 | Default ports: public, Flight SQL and management defaults fixed (ENDPOINTS-P1) and read by `check_default_ports.py`; the per-module defaults removed | ENDPOINTS-1 | S | `check_default_ports.py` lists three Gateway listeners and passes; a Gateway started with no settings listens on exactly three ports |
| ENDPOINTS-3 | Clean break for clients: the release notes, `docs/suite-listeners-and-auth.md` and `docs/quickstart.md` list each old port and its new address; the app manifest's and the views' absolute addresses, the identity issuer and the redirect URIs follow the public listener | ENDPOINTS-2 | S | a test signs in through the public listener and an app's redirect URI resolves under it; `check_docs_commands.py` passes; no doc names 8817 to 8822 as a current port |
| ENDPOINTS-4 | The identity home behind the firewall: the public listener forwards the identity paths to a home that runs on its own (`identity.forward_to`) | ENDPOINTS-1 | S | the sign-in test passes with the home in another process reached only through the Gateway's public listener |
| ENDPOINTS-5 | Traces through the public listener and a module: the serving contract's `TraceId` is the W3C trace id; a request through the public listener and into a module is one trace | ENDPOINTS-1 | S | a test with an in-memory exporter finds one trace with the listener's span and the module's span as its child, and the contract's id equal to it |
| ENDPOINTS-6 | Reserve the PostgreSQL wire listener: a `pgwire` module entry that takes 5432 when free, else a port of the Gateway's sub-block, and refuses to start while the protocol is not built (the protocol itself is parked) | ENDPOINTS-2 | S | a test: with `pgwire` enabled the Gateway refuses to start naming the setting and that the protocol is not built |

## Needs from other subjects

- INSTALLER-9: the suite range and the Gateway's sub-block (7820 to 7839) the defaults of ENDPOINTS-2 come from.

## Proposed decisions

### ENDPOINTS-P1: the default ports (awaiting the owner)

Option A: 7820 public HTTPS, 7821 Flight SQL, 7822 management (loopback), fixed defaults moved only when taken. Option B: free ports of 7820 to 7839 at
first start (the rule of `docs/install.md`). Scenario: a person opens `https://gateway.example:7820/apps/sales/` and a BI tool connects Flight SQL to `gateway.example:7821`;
under A both addresses are the same on every default install and in the docs; under B they are read from the console's Servers page. Recommendation: A.

### ENDPOINTS-P2: path prefixes (default, awaiting the owner)

`/identity/`, `/apps/`, `/store/`, `/views/`, `/feeds/`, `/mcp/` on the public listener, matching the module names. Scenario: an OData client reads
`https://gateway:7820/feeds/sales/orders`. Recommendation: accept.

## Shared areas touched

- `DbDataGateway.Server` hosting, `DbDataKit.Serving.*` module registration, `DbDataKit.Identity` (issuer and redirect URIs), `DbDataKit.Apps.*` (addresses).
- `scripts/check_default_ports.py`, `docs/suite-listeners-and-auth.md`, `docs/serving-layer.md`, `docs/release-notes-pending.md`.
