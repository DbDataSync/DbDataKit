# Listeners, protocols and authentication across the suite

What each service listens on and how each way of proving who you are is used. It is read from the code as of 2026-10-31 (the file and the setting that decide each cell are named), not from the plans: a thing a plan schedules and nobody built is in the last section, not in the matrices.

"Service" means a process the suite runs. The libraries (`DbDataKit.*`) have no listeners of their own; they are mounted into these.

## 1. Services, listeners and protocols

| Service | Listener (default) | Protocol | TLS | Who may connect, and what it carries |
| --- | --- | --- | --- | --- |
| **DbDataKit** (`dbdatakit server run`) | `https://localhost:7800`, or the next free port of 7800 to 7819, kept in `settings.yaml` (`--listen`) | HTTPS (HTTP/1.1 and 2): the web app, the REST-style API behind it, the sign-in controllers, `/health*` and `/version` | the console's own certificate | People (browsers); servers asking for the console's public key; the relay below |
| | same listener, `/relay/<server>/api/...` and `/relay/<server>/hubs/...` | HTTPS in, then HTTPS out to the managed server with its certificate pinned; SignalR (WebSocket) for the hubs | in and out | The console calls a managed server as the signed-in person (a signed token) and relays the answer |
| | `--announce-listen` (off unless set; plain `http`, a port of its own, for example 7801) | HTTP, one `POST /announce` (body capped at 4 KB) | none, by design | Servers that are not paired yet announce their name, address and pairing code; the console lists them as waiting |
| **Identity home on its own** (`dbdatakit identity run`, in the console's project) | `https://localhost:7819` (`--listen`) | HTTPS: OpenID Connect provider (discovery, JWKS, `authorize`, `token`, `userinfo`, `end-session`), the shared sign-in (passkeys, invitations), the Users screens when `wwwroot` is installed | its own certificate | People and the OpenID clients (consoles) |
| **DbDataGateway** (the Flight listener) | `listen` (every address by default; a free port of the Gateway's block 7820 to 7839, chosen at the first start) | gRPC over HTTP/2: Arrow Flight SQL (`Handshake`, `GetFlightInfo`, `DoGet`, prepared statements, and `DoAction` for the standard Flight SQL actions only) | `Tls.Mode`: `SelfSigned`, `File`, `Store`, or `None` (credentials then travel as Basic) | Database clients (ADBC, JDBC, ODBC bridges, Sync, Build) |
| | same listener, source `semantic:<model>` | Flight SQL carrying a semantic request (query, explain, members, describe) or semantic SQL | as above | the same clients |
| **DbDataGateway** (management listener) | `management.listen` (the loopback address; a free port of the Gateway's block, chosen at the first start), on by default (plan 33 D15; `management.enabled: false` turns it off; it needs TLS) | HTTPS (HTTP/1.1 and 2): `/api/v1/...` (with `/api/v1/state` and the maintenance mode), pairing, key rotation, `/health*`, `/version`. A management path on the Flight port is a 404 and the reverse | the same certificate | A console (a signed token) or a paired Build server (a publishing client) |
| | local channel (`management.localChannel: true`) | one request per message on a named pipe (Windows) or a Unix socket (`channel.sock`) | none; the operating system's access list or file mode decides | The command line on the same machine, as an administrator |
| **DbDataGateway 32-bit worker** (`dbdatagateway-worker`) | `127.0.0.1:<random port>` | gRPC (Flight) over HTTP/2 | none (loopback) | Only the Gateway that started it, with a per-launch secret |
| | runner state endpoint, `127.0.0.1:<State.Port>` (0 picks a free port) | HTTP: the work queue and config the per-run TaskRunner children poll | none; IPv4 loopback only | Only the children this process started (the runner token) |
| **DbDataSync TaskRunner** (a Runner's task; the Sync server was deleted, plan 33 SC23) | none; it speaks the runner protocol to the Runner that started it | | | |
| **DbDataBuild web page** (the local `web` command) | `127.0.0.1:<free port>` | HTTP, one page and its calls | none | The person who started it: the page carries a random token |
| **DbDataBuild MCP** (`mcp`) | none: standard input and output | MCP (JSON-RPC) | n/a | The process that started it (an agent host), no network |
| **DbDataBuild TUI** (`tui`) | none: the terminal | | | |
| **Child processes under `DbDataKit.Hosting.Children`** (a parent that starts a child) | a named pipe (Windows), a Unix socket (elsewhere), or loopback TCP when asked | the child's own message protocol | the operating system | The parent that started it |
| **XMLA forwarder** (a test tool, `DbDataKit.Tools.XmlaForwarder`) | a TCP port it is told | raw TCP forwarding to SQL Server Analysis Services, with a recording | none | Test runs only; not shipped |

Every server that a console manages also answers the same four things on its management listener: `GET /health/live`, `GET /health` and `/health/ready`, `GET /version` (all without a token), and the pairing and key-rotation endpoints (no token; the pairing code is the proof).

### Default-port clashes to know about

* The console, the Runner and the Workspace listen on the suite's ports, 7800 to 7899, each in a sub-block of its own: the console 7800 to 7819 (default 7800), the Runner 7840 to 7859 (default 7840), the Workspace 7860 to 7879 (default 7860); the identity home on its own listens on 7819. With no `--listen`, a first start takes the default or the next free port of the sub-block and writes it to the server's settings file (`listen:`), and later starts use it (`docs/install.md`, "Ports"). Until this release they listened on 8480 (the console), 8482 (the identity home), 8495 (the Runner) and 8471 (the Workspace).
* No two services share a default port, and no default is one of Windows' default ports (`scripts/check_default_ports.py` fails when one is): the Gateway's three listeners (the public listener, where identity, apps, store, views, feeds and MCP answer by path, Flight SQL and management) take free ports of its block, 7820 to 7839, at the first start. A module with a `listen` of its own listens there and is not on the public listener.
* The console (7800) and its announce port (7801 in the examples) are different listeners on purpose: the announce port is plain HTTP and takes nothing but the announcement.

### Safe defaults to know about

* **The Gateway's management API in a container.** On a machine it is bound to the loopback address, on a port of the Gateway's block (7822 on a Gateway that found 7820 to 7822 free). The container image keeps that (`DBDATAKIT_Gateway__Management__Listen=https://127.0.0.1:7822` in `DbDataGateway/Dockerfile`, no `EXPOSE` for it), so a plain `docker run` makes it unreachable from outside the container until someone widens it. The compose files set `https://0.0.0.0:7822` inside the container so a console on the same compose network can pair, and publish only the Flight port; `quickstart-apps.yml` publishes 7822 on the host's loopback (`127.0.0.1:7822:7822`) and nowhere else. `dist/tests` fails when a compose file publishes 7822 on any other address. It is the administration API: every call needs a token a pairing issued, but keep the port off public interfaces.
* **Health probes that accept any certificate, on loopback only** (plan 33 D19). The probe a service's `health check` uses (the shared `HealthProbeClient` with `TrustLoopbackCertificate`, the console's and the Runner's clients) and the helper's `VersionHealthProbe` (`update --apply`, readiness of the new version) accept the self-signed certificate when the address is this machine's own: they send a GET and read a status code and a version, never a secret, and an address anywhere else is validated normally. Sync's `health check` goes further and pins the `.cer` beside its own certificate in the data folder; the others cannot until each product tells the probe where its certificate is.

## 2. Authentication methods and the services that use them directly

"Direct" means the service itself checks it on the listener named. A console calling a server through the relay is not a second method: the server sees the signed token.

Columns: **Con** console; **Home** identity home on its own; **Px-F** Gateway Flight listener; **Px-M** Gateway management listener; **Sync**; **Build**; **Store**; **Wkr** Gateway worker; **Local** local-only listeners (Build `web`, MCP, Sync runner state, Gateway local channel).

| Method | What it is | Con | Home | Px-F | Px-M | Sync | Build | Store | Wkr | Local |
| --- | --- | :-: | :-: | :-: | :-: | :-: | :-: | :-: | :-: | :-: |
| **Passkey sign-in, session cookie** | WebAuthn passkey after an invitation; an opaque session id in a cookie; the role is read from the user row on every request | yes | yes | | | no (`PasskeyOptions.Disabled`) | | | | |
| **Invitation** | A single-use link (`/invite#<code>`) that registers a passkey; the first administrator's comes from `FIRST-RUN.txt` | yes (writes the first) | yes | | | no (the console's users and invitations; X05 removed Sync's own) | | | | |
| **Windows sign-in (Negotiate / NTLM)** | The shared sign-in controller trades a negotiated Windows identity for a session; the SID is the subject; roles come from two configured Windows groups, evaluated on every request | shared controller | shared controller | | | yes, only when Windows groups are configured (or HTTP.sys) | | | | |
| **OpenID Connect, relying party** | Authorization code with PKCE; the console keeps the home's refresh token protected and asks again for the person's roles | yes, when a `home` is set | n/a (it is the provider) | | | | | | | |
| **OpenID Connect, provider** | Discovery, JWKS, ES256 id and access tokens, refresh tokens, groups mapped from Entra and Windows groups | the console is the home by default | yes (`identity run`) | | | | | | | |
| **Console token** (`X-DbData-Token`) | A short-lived (default 5 minutes) Ed25519-signed token naming the person, their roles and a ceiling; audience is the server's instance id; a token id is spent once | issues it | | | yes | yes (the session handler accepts it before a cookie) | yes | yes | | |
| **Pairing code** | A single-use, 100-bit code (10 minutes; five wrong codes cancel all); the code never crosses the wire, both ends prove they hold it, and the client pins the server's certificate | yes (client) | | | yes | yes | yes | yes | | |
| **Key rotation proof** | A new public key signed by the old key and by itself over the server's instance id, a time and a nonce | yes (client) | | | yes | yes | yes | yes | | |
| **Announcement with a code** | A server not yet paired POSTs its name, address and a pairing code to the console's private port; a person confirms in the console, which then pairs normally | yes (receives) | | | announces | announces | announces | announces | | |
| **Flight handshake, identity home users** | Basic credentials in (a home user name and a personal access token), an opaque bearer token out; a home token alone as `Bearer` works without the handshake; the data sources a person may use are the ones whose files name their home groups (P13; Gateway has no users of its own); throttled per user and client address | | | yes | | | | | | |
| **Flight handshake, Windows (UPN)** | The same handshake with the person's UPN as the user name, the password checked by the operating system, resolved to the home user who holds that verified address (`Auth: windows: true`) | | | yes (Windows) | | | | | | |
| **Pass-through database credentials** | The client's user and password are written into the data source's connection and the database is the authority; a rejected login counts in the throttle | | | yes (per data source) | | | | | | |
| **JWT bearer / OIDC token** | `authorization: Bearer <jwt>` on each call, validated against an authority's keys or a shared HMAC key; `Mappings` turn claims into data-source access | | | yes | | | | | | |
| **Network trust fallback** | With no cookie, a request from loopback is an administrator and one from the network a viewer, only when the network-trust settings (`Admin`, `Viewer`; both off by default) say so | | | | | yes (the shared session handler; both off by default) | | | | |
| **Runner token + loopback** | A random token this process made for its children, and a loopback-only check, on `/runner-state` | | | | | yes | | | | yes |
| **Per-launch worker secret** | A random secret handed to the 32-bit worker in `DBDATAKIT_GATEWAY_WORKER_SECRET` | | | | | | | | yes | |
| **Page token + loopback** | A random token in the page `web` serves | | | | | | | | | yes (Build `web`) |
| **Operating-system access** | File mode or pipe access list on the local channel, a pipe or socket to a child, standard input of an MCP server, the terminal | | | | | | | | | yes |

### Roles

Servers a console manages use one set of five roles, carried in the console token: **Viewer, Operator, Author, Approver, Admin** (`ServerRole`). A paired client's **ceiling** caps what a token may claim; a token that claims more is refused, not trimmed. Admin does not approve plans, and Author does not either. The Gateway's Flight clients have no roles: a client may use the data sources its user, its Windows account, its token mappings or its pass-through setting name, and a Gateway publisher adds the publishing role on management.

### Credentials the services use outward

| Service | To | How |
| --- | --- | --- |
| Gateway | the databases | the data source's connection (mapped account, pass-through, integrated, token) with secrets resolved through `secret:` references |
| Sync | the databases, and a Gateway | the connection's run login (on a Runner, its binding of the set's slots); a Gateway as an Arrow Flight SQL source with the Flight handshake; no server since plan 33 SC23 |
| Build | the databases | the `DBDATAKIT_BUILD_<TARGET>_<READ|WRITE>` logins (on a Runner, its binding of the package's slots); no server since plan 33 SC23 |
| Console | every paired server | its signing key (kept in the secret store) and the server's pinned certificate |
| Console | an identity home | the client id and secret of its OpenID client, over the home's HTTPS |

## 3. Planned, not built

These are in the plans and are not in the matrices above:

* **A REST listener for semantic queries and a REST API generally** (J28): waits for one design that holds REST, authentication and apps together. Flight keeps its own endpoints.
* **The serving layer as a host** that mounts the identity home, semantic models, saved queries and apps (plans 20 and 19): the pieces exist as libraries and are mounted in the Gateway.
* **Postgres wire protocol listener** for semantic and saved queries: asked for, not built.
* **Entra and Windows group feeds in a home** (U18's host side), and the serving layer forwarding a home that runs on its own (O07).
* **Passkeys on Sync**: Sync takes its people from the console (the token or the home), not its own passkeys.
