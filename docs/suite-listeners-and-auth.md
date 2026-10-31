# Listeners, protocols and authentication across the suite

What each service listens on and how each way of proving who you are is used. It is read from the code as of 2026-10-31 (the file and the setting that decide each cell are named), not from the plans: a thing a plan schedules and nobody built is in the last section, not in the matrices.

"Service" means a process the suite runs. The libraries (`DbDataKit.*`) have no listeners of their own; they are mounted into these.

## 1. Services, listeners and protocols

| Service | Listener (default) | Protocol | TLS | Who may connect, and what it carries |
| --- | --- | --- | --- | --- |
| **DbDataConsole** (`dbdatakit server run`) | `https://localhost:8480` (`--listen`) | HTTPS (HTTP/1.1 and 2): the web app, the REST-style API behind it, the sign-in controllers, `/health*` and `/version` | the console's own certificate | People (browsers); servers asking for the console's public key; the relay below |
| | same listener, `/relay/<server>/api/...` and `/relay/<server>/hubs/...` | HTTPS in, then HTTPS out to the managed server with its certificate pinned; SignalR (WebSocket) for the hubs | in and out | The console calls a managed server as the signed-in person (a signed token) and relays the answer |
| | `--announce-listen` (off unless set; plain `http`, a port of its own, for example 8481) | HTTP, one `POST /announce` (body capped at 4 KB) | none, by design | Servers that are not paired yet announce their name, address and pairing code; the console lists them as waiting |
| **Identity home on its own** (`dbdatakit identity run`, in the console's project) | `https://localhost:8490` (`--listen`) | HTTPS: OpenID Connect provider (discovery, JWKS, `authorize`, `token`, `userinfo`, `end-session`), the shared sign-in (passkeys, invitations), the Users screens when `wwwroot` is installed | its own certificate | People and the OpenID clients (consoles) |
| **DbDataProxy** (the Flight listener) | `Listen.Address` (default `0.0.0.0`) : `Listen.Port` (default 8815) | gRPC over HTTP/2: Arrow Flight SQL (`Handshake`, `GetFlightInfo`, `DoGet`, prepared statements, and `DoAction` for the standard Flight SQL actions only) | `Tls.Mode`: `SelfSigned`, `File`, `Store`, or `None` (credentials then travel as Basic) | Database clients (ADBC, JDBC, ODBC bridges, Sync, Build) |
| | same listener, source `semantic:<model>` | Flight SQL carrying a semantic request (query, explain, members, describe) or semantic SQL | as above | the same clients |
| **DbDataProxy** (management listener) | `Management.Address` (loopback) : 8816, off unless `management.enabled` and TLS is on | HTTPS (HTTP/1.1 and 2): `/api/v1/...`, pairing, key rotation, `/health*`, `/version`. A management path on the Flight port is a 404 and the reverse | the same certificate | A console (a signed token) or a paired Build server (a publishing client) |
| | local channel (`management.localChannel: true`) | one request per message on a named pipe (Windows) or a Unix socket (`channel.sock`) | none; the operating system's access list or file mode decides | The command line on the same machine, as an administrator |
| **DbDataProxy 32-bit worker** (`dbdataproxy-worker`) | `127.0.0.1:<random port>` | gRPC (Flight) over HTTP/2 | none (loopback) | Only the Proxy that started it, with a per-launch secret |
| **DbDataSync API** | `Url` (default `http://localhost:5080`; the compose image serves `https://...:8443`); on Windows HTTP.sys can replace Kestrel to share a port | HTTP(S): the web API behind the screens, SignalR hubs, pairing, key rotation, `/version`, `/health*` | optional (a certificate in `Kestrel:Certificates:Default` or the server's own) | The console's relay (a signed token); people on the screens; on loopback, the network-trust fallback |
| | runner state endpoint, `127.0.0.1:<State.Port>` (0 picks a free port) | HTTP: the work queue and config the per-run TaskRunner children poll | none; IPv4 loopback only | Only the children this process started (the runner token) |
| **DbDataSync TaskRunner** (per run) | none; it is a client of the runner state endpoint | | | |
| **DbDataBuild server** (`serve`) | `https://127.0.0.1:8470` (a name that is not `localhost` binds every address) | HTTPS: the management API (projects, workspaces, commands, plans, publish), pairing, key rotation, `/health*`, `/version` | the server's own certificate | A console (a signed token); other servers as paired clients |
| **DbDataBuild web page** (the local `web` command) | `127.0.0.1:<free port>` | HTTP, one page and its calls | none | The person who started it: the page carries a random token |
| **DbDataBuild MCP** (`mcp`) | none: standard input and output | MCP (JSON-RPC) | n/a | The process that started it (an agent host), no network |
| **DbDataBuild TUI** (`tui`) | none: the terminal | | | |
| **DbDataStore server** | `https://127.0.0.1:8490` | HTTPS: `/api/v1/info`, pairing, key rotation, `/health*`, `/version`; HTTPS only | the server's own certificate | A console (a signed token) |
| **Child processes under `DbDataKit.Hosting.Children`** (a parent that starts a child) | a named pipe (Windows), a Unix socket (elsewhere), or loopback TCP when asked | the child's own message protocol | the operating system | The parent that started it |
| **XMLA forwarder** (a test tool, `DbDataKit.Tools.XmlaForwarder`) | a TCP port it is told | raw TCP forwarding to SQL Server Analysis Services, with a recording | none | Test runs only; not shipped |

Every server that a console manages also answers the same four things on its management listener: `GET /health/live`, `GET /health` and `/health/ready`, `GET /version` (all without a token), and the pairing and key-rotation endpoints (no token; the pairing code is the proof).

### Default-port clashes to know about

* The identity home on its own (`IdentityHost.DefaultListen`) and the Store server (`StoreSettings.DefaultListen`) both default to port **8490**. On one machine one of them needs `--listen`. The compose files do not run both.
* The console (8480) and its announce port (8481 in the examples) are different listeners on purpose: the announce port is plain HTTP and takes nothing but the announcement.

## 2. Authentication methods and the services that use them directly

"Direct" means the service itself checks it on the listener named. A console calling a server through the relay is not a second method: the server sees the signed token.

Columns: **Con** console; **Home** identity home on its own; **Px-F** Proxy Flight listener; **Px-M** Proxy management listener; **Sync**; **Build**; **Store**; **Wkr** Proxy worker; **Local** local-only listeners (Build `web`, MCP, Sync runner state, Proxy local channel).

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
| **Flight handshake, local users** | Basic credentials in, an opaque bearer token out; users in `proxy/users.yaml`, PBKDF2-HMAC-SHA256 (210,000 rounds); throttled per user and client address | | | yes | | | | | | |
| **Flight handshake, Windows accounts** | The same handshake, the password checked against the local or domain account | | | yes (Windows) | | | | | | |
| **Pass-through database credentials** | The client's user and password are written into the data source's connection and the database is the authority; a rejected login counts in the throttle | | | yes (per data source) | | | | | | |
| **JWT bearer / OIDC token** | `authorization: Bearer <jwt>` on each call, validated against an authority's keys or a shared HMAC key; `Mappings` turn claims into data-source access | | | yes | | | | | | |
| **Network trust fallback** | With no cookie, a request from loopback is an administrator and one from the network a viewer, only when the network-trust settings (`Admin`, `Viewer`; both off by default) say so | | | | | yes (the shared session handler; both off by default) | | | | |
| **Runner token + loopback** | A random token this process made for its children, and a loopback-only check, on `/runner-state` | | | | | yes | | | | yes |
| **Per-launch worker secret** | A random secret handed to the 32-bit worker in `DBPROXY_WORKER_SECRET` | | | | | | | | yes | |
| **Page token + loopback** | A random token in the page `web` serves | | | | | | | | | yes (Build `web`) |
| **Operating-system access** | File mode or pipe access list on the local channel, a pipe or socket to a child, standard input of an MCP server, the terminal | | | | | | | | | yes |

### Roles

Servers a console manages use one set of five roles, carried in the console token: **Viewer, Operator, Author, Approver, Admin** (`ServerRole`). A paired client's **ceiling** caps what a token may claim; a token that claims more is refused, not trimmed. Admin does not approve plans, and Author does not either. The Proxy's Flight clients have no roles: a client may use the data sources its user, its Windows account, its token mappings or its pass-through setting name, and a Proxy publisher adds the publishing role on management.

### Credentials the services use outward

| Service | To | How |
| --- | --- | --- |
| Proxy | the databases | the data source's connection (mapped account, pass-through, integrated, token) with secrets resolved through `secret:` references |
| Sync | the databases, and a Proxy | the connection's own login; a Proxy as an Arrow Flight SQL source with the Flight handshake |
| Build | the databases, and a Proxy | the `DBDATA_BUILD_<TARGET>_<READ|WRITE>` logins; publishing to a Proxy as a paired client |
| Console | every paired server | its signing key (kept in the secret store) and the server's pinned certificate |
| Console | an identity home | the client id and secret of its OpenID client, over the home's HTTPS |

## 3. Planned, not built

These are in the plans and are not in the matrices above:

* **A REST listener for semantic queries and a REST API generally** (J28): waits for one design that holds REST, authentication and apps together. Flight keeps its own endpoints.
* **The serving layer as a host** that mounts the identity home, semantic models, saved queries and apps (plans 20 and 19): the pieces exist as libraries and are mounted in the Proxy.
* **Postgres wire protocol listener** for semantic and saved queries: asked for, not built.
* **Entra and Windows group feeds in a home** (U18's host side), and the serving layer forwarding a home that runs on its own (O07).
* **Passkeys on Sync**: Sync takes its people from the console (the token or the home), not its own passkeys.
