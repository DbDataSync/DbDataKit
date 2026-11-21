# Hosted pages and paths

Which pages and paths each service answers, who opens them, how they are set and how an app instance gets its address. The Gateway's listeners are in [serving-layer.md](serving-layer.md), "The public listener", and in [suite-listeners-and-auth.md](suite-listeners-and-auth.md).

## The Gateway's three listeners

| Listener | Default | What it answers |
| --- | --- | --- |
| Public | `public:` in `server.yaml`, address `0.0.0.0`, port 8817, the Gateway's certificate | the HTTP modules `identity`, `apps`, `store`, `views`, `feeds` and `mcp` that are in `modules:` and have no port of their own |
| Flight SQL | `listen:` in `server.yaml`, address `0.0.0.0`, port 8815 | gRPC for Flight SQL clients. No HTML. |
| Management | `management:` in `server.yaml`, address `127.0.0.1`, port 8816 | the API at `/api/v1/...` that the console and the command lines call. No HTML. |

On the public listener:

- `/health/live` answers `Healthy`. `/health`, `/health/ready` and `/version` are on the management listener.
- A path that no module claims answers 404 with a JSON body. `/` is such a path, except at a name bound to an app instance (see "How an app instance is placed").
- A module with a `port` of its own listens there and has no prefix; `https://gateway:9443/api/v1/models` answers and `https://gateway:8817/store/api/v1/models` is a 404.
- A module hands its own application the request with the prefix moved to `PathBase`, so the paths below are the ones a client uses on the public listener.

## Identity

Pages are HTML. The paths are the same on the public listener and on a home that runs on its own (`dbdatakit identity run`, port 8482).

| Path | What | Format | Who |
| --- | --- | --- | --- |
| `/identity/sign-in` | passkey sign-in page | HTML | end user |
| `/invite` | accept an invitation | HTML | invitee |
| `/identity/upstream/entra` and `/identity/upstream/entra/callback` | Entra sign-in, only when `identity.entra.authority` is set; a failure shows the sign-in page with status 403 | redirect, HTML | end user |
| `/identity/upstream/windows` | Windows sign-in, only when `identity.windows.enabled` is true | redirect, HTML | end user |
| `/.well-known/openid-configuration` | discovery document; its issuer is the public listener's address (`https://gateway:8817`) unless `identity.publicUrl` is set | JSON | clients |
| `/identity/jwks`, `/identity/authorize`, `/identity/token`, `/identity/userinfo`, `/identity/end-session` | the OpenID Connect provider | JSON, redirect | clients, browsers |
| `/api/auth/...`, `/api/invites...`, `/api/users...` | sign-in calls, invitations, users | JSON | the sign-in pages, administrators |
| `/api/access-tokens` | personal access tokens | JSON | end user |

## Apps

Every path is under `/apps`. The prefix is fixed. Pages are HTML.

| Path | What | Format | Who |
| --- | --- | --- | --- |
| `/apps/<instance>/` (`?launch`, `?embed`) | the app's shell page, or a portal's home with its tiles; `?embed` leaves out the header | HTML | end user |
| `/apps/<portal>/<mount>/` | an instance mounted in a portal | HTML | end user |
| `/apps/<instance>/_views/<name>` | a custom page of an app | HTML | end user |
| `/apps/<instance>/_about` | the about and approvals page | HTML | people with a role on the app |
| `/apps/<instance>/_app/{path}` | the app's bundle: entry document and assets | HTML, assets | the shell's frame |
| `/apps/<instance>/_token`, `/_storage`, `/_data/...`, `/_views/<name>/_rows` | token broker, app storage, data calls | JSON | the shell, the app |
| `/apps/catalog` | the instances the signed-in person may open | JSON | portals, scripts |
| `/apps/-/shell.js`, `/apps/-/callback` | the shared shell script and the sign-in callback | JavaScript, redirect | the browser |

A path under `/apps` that matches none of these answers a plain-text line saying the app is at `/apps/<instance>/`. A non-production channel is served at `/apps/<name>~<channel>/`.

The module has no administration page. Instances are administered on the console's Gateway screen, `/servers/<id>/apps`.

## Views

Every path is under `/views`. The only HTML is the print page.

| Path | What | Format |
| --- | --- | --- |
| `/views/api/v1/dashboards/{name}/print` and `/views/api/v1/reports/{name}/print` | the page the exporter prints | HTML |
| `/views/api/v1/views/print/{file}` | the print script, styles and fonts | assets |
| `/views/api/v1/dashboards`, `/views/api/v1/reports`, and under each `/{name}`, `/{name}/export`, `/{name}/render`, `/{name}/actions/{link}`, `/{name}/batches` | published documents, their export and actions | JSON, file |
| `/views/api/v1/catalog...`, `/views/api/v1/views/preview`, `/views/api/v1/my/views...`, `/views/api/v1/apps/{instance}/pages/{name}/export` | the catalog, a preview, a person's own views, the export of an app's page | JSON, file |

The module has no viewer page. Dashboards and reports are viewed in the console at `/views/...`, a route of the console on its own host.

## Store, feeds and mcp

None of these modules serves HTML.

| Module | Paths | What |
| --- | --- | --- |
| `store` | `/store/api/v1/models/...` | reads, drafts, changes and change requests (JSON) |
| `feeds` | `/feeds/api/v1/queries/<model>/<name>` and `/feeds/api/v1/query` | saved queries and semantic requests (JSON, CSV, Arrow) |
| `mcp` | `/mcp` and `/mcp/api/v1/mcp/approvals/...` | the agents' JSON-RPC endpoint, and the approvals as JSON; the approvals page is the console's `/approvals` |

## The other services

| Service | Pages a person opens | Notes |
| --- | --- | --- |
| Console (`dbdatakit`) | a web app served from `wwwroot`. `/` is the home. `/invite` accepts an invitation. A signed-out visitor sees the sign-in at any path. Screens: `/instances`, `/upgrades`, `/state/:instance`, `/connections`, `/docs`, `/lineage`, `/quality`, `/setup`, `/deployments`, `/releases`, `/agents`, `/knowledge`, `/backlog`, `/delivery`, `/bindings`, `/monitoring`, `/approvals`, `/runs`, `/tracking`, `/audit`, `/secrets`, `/users`, and `/views`, `/views/dashboards`, `/views/reports`, `/views/catalog` | `/servers/<id>/*` shows the screens of a paired server. Gateway: `/` (and `data-sources`, `identities`, `identity-home`, `secrets`, `apps`, `models`, `saved-queries`, `trust`, `access`, `cache`, `tls`, `activity`, `settings`, `config`). Runner: `/`, `/runs`, `/packages`. Workspace: `/`, `/keys`, `/<project>/<id>`. The console's API is `/api/*` and `/relay/<id>/...`. |
| Workspace server | none of its own | one HTML route, a built app's preview at `/api/v1/projects/{project}/workspaces/{id}/builds/{build}/preview`, which the console frames. Its screens are the console's. |
| Runner | none of its own | `POST /api/v1/webhooks/{package}/{channel}/{source}` takes signed inbound webhooks. Its screens are the console's. |

## How an app instance is placed

The instance's address is `https://<host>/apps/<instance-name>/`. It is derived from the name; no field of the instance holds it. An instance name is `[a-z0-9][a-z0-9_-]{0,63}`, and `api`, `admin`, `console`, `identity`, `apps`, `static`, `health`, `version` and `catalog` are refused.

An administrator can add **bound names** to an instance, each an address of the form `{host, kind, path, certificate?, certificate_password?, primary}`:

- `kind: subdomain`: the host is `<instance>.<apps.domain>`, and `apps.domain` in `server.yaml` names the domain (`apps.example.com`). The Gateway's own certificate serves it.
- `kind: custom`: any other DNS name. `certificate: secret:<name>` names a secret holding a base64 PFX, and `certificate_password: secret:<name>` its password. The save is refused unless the name resolves to the machine or to an address in `apps.addresses`, or the administrator proves the name with a DNS challenge.
- `path`: `/` by default. A request to the bound host is rewritten to `/apps/<instance>/<rest>`, so with `path: "/"` the instance is the root of that host name. A path such as `/planning` takes the longest match.
- `primary`: at most one address per instance may be `true`. The Gateway stores and shows it, and no routing reads it.

Where each is set:

- Bound names: `addresses` in `PUT /api/v1/app-instances/{name}`, or the instance editor of the console's Gateway Apps screen. The command `apps instance-save` has the flags `--name`, `--app`, `--pin`, `--title`, `--setting` and `--bind` (data bindings, not addresses); it cannot set an address.
- `apps.domain`, `apps.addresses`, `apps.trustedProxies` (the addresses whose `X-Forwarded-Host` and `X-Forwarded-Prefix` are believed): `apps:` in `server.yaml`.
- An instance of an app that declares `origin: own` is served only at its bound names, and saving it with no address is refused.
- Each bound name is a redirect URI of the instance's OpenID client: `https://<name>[:<port>]/apps/-/callback`.
