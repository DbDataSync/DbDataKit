# Apps: publishing, navigation and instances

How an app is published, where its code runs, what it may do, how it is addressed and navigated, and how one app or one DbDataStore model is published more than once. The host is the `apps` module of the serving layer ([serving-layer.md](serving-layer.md)); the code is `DbDataKit.Apps.Manifest` (manifest, validation, egress policy), `DbDataKit.Apps.Instances` (apps, instances, approvals, storage) and `DbDataKit.Apps.Host` (the shell, the token broker, the data gateway), hosted by the Proxy's `ProxyAppsModule` (`modules: [apps]`, https, default port 8818).

## Terms

| Term | Meaning |
| --- | --- |
| **App** | the source, the manifest and the built, versioned bundle: the published definition `app/<name>`. It has no address |
| **Instance** | one publication of an app (or of a Store model): a name, the version it runs (a pin, or the channel's current), settings, data bindings, roles. Two instances of one app share code and nothing else: the published definition `app-instance/<name>` |
| **Channel** | the lifecycle stage of an instance (plan 21): preview, test, prod. A channel is *when*, an instance is *who for* |
| **Navigation set** | a named, ordered list of instances shown as one menu (definition `app-nav`) |
| **Portal** | an app of `kind: portal` whose job is to be the shell other apps are mounted into |
| **Host** | a serving group running the `apps` module |

## Where code runs

An app is untrusted UI: the servers it calls check everything. What needs protecting is the person's session on the host, and the browser protects per **origin**.

- **Declarative views** are the host's own code and run on the host's origin.
- **Code apps** run in a **sandboxed document**, the default: the host serves the app's files under `/apps/<instance>/_app/` with `Content-Security-Policy: sandbox allow-scripts allow-forms allow-popups`, an opaque origin with no cookies, storage or access to the host's pages. The host's **shell page** at `/apps/<instance>/` frames it with `sandbox="allow-scripts allow-forms allow-popups"` (never `allow-same-origin`), runs one script of its own (`/apps/-/shell.js`) and hands the app a short-lived token and storage over `postMessage` (`{dbdata:"app/1", type:"token.request"|"storage"|"route"|...}`; the script answers only the app's own frame). The bundle under `_app/` is not behind the sign-in, because an opaque origin sends no cookie; it is code the group published, and data goes through a token.
- **An origin of its own** (a subdomain or custom domain per instance) is the other option. The manifest accepts `origin: sandbox | own`, but no domain binding exists, so `own` is not served on an origin of its own yet (see plan 26).

Sign-in is the identity home's (`OpenIdClient`, a public PKCE client): nobody signed in is sent to the home, a person with no role on `app:<instance>` gets 403. Sessions are in memory per instance (8 hours, an HttpOnly cookie scoped to `/apps/`, `SameSite=None; Secure` so a portal on another site can frame an instance); the home is asked again every `Apps.RolesRefreshSeconds` (300) so a role taken away stops working, and a refusal (`invalid_grant`) ends the session.

## Trust and egress

Two settings, not the same thing.

**Trust: who vouches for the code.** `trust: sandbox | trusted` in the manifest is a **request**. A `trusted` app (on the host's origin, with the person's session as far as its approval says) is served, given a token, given storage and given data calls only while **an approval stands for its exact bytes**. An approval is a record in the group's state (`app-approval/<id>`): who approved, when, the **content hash** (the built files in path order with their hashes, then the manifest: exactly the bytes a published version contains, so it can be given before publishing), what it was approved for, a note. It is **a new approval for a new version**: any changed byte is another hash. The author cannot approve; the approver must hold the Approver role at the identity home; revoking needs the same role and publishes the record again with the time (nothing is deleted). `apps publish` of a trusted app is refused naming the missing approval and the hash; a revoked approval stops the app at once (the data gateway checks on every call, a token lives five minutes). Commands: `dbdataproxy apps approve --path <folder> --by <approver>` (or `--hash <h> --app <app>`, or `--app --version`), `approval-list`, `approval-revoke --id --by`.

**Audit.** The approval's label goes with every data call a trusted app makes (`AppDataCall.Via`), and the group's Store names it in the audit row of a write: "Ada Lovelace, through sales-portal 3, approved by Grace (apr_...)", cut to the audit column's 200 characters from the person's end. A sandboxed app's rows name the person only. The servers do not change with trust: an app's token is no wider than the person's.

**Egress: no outside requests for any app.** Every answer carries a `Content-Security-Policy` from the instance's effective policy, starting from `default-src 'self'`: it stops outside scripts and styles (so JSONP), fetch, XHR, WebSocket and CORS requests, images, fonts, media, frames and objects from other origins, form posts to other origins and a changed `<base>`. Exceptions are allow-lists: the manifest **requests** them (`egress: {connect: [...], img: [...]}`, or `frame`) and the administrator **approves** them as the standing approval's `Egress` (`approve --egress connect=https://api.x.com,img=https://cdn.x.com`); a request nobody approved is not in the policy, and an approval cannot name an origin the manifest did not ask for. `dbdataproxy apps validate --dir <folder>` checks the manifest and lists addresses of other hosts in the built HTML, CSS and script that the manifest does not request, with file and line. A content security policy is enforced by the browser for that document: it stops accidents and the ordinary routes out, and is not a barrier against hostile code on the same origin; for trusted apps the review and the audit are the control. Approvals exist only for trusted apps, so a sandboxed app cannot be given origins yet.

## App storage

A sandboxed document has no browser storage, so the host provides it on every address (manifest `storage: {user, instance, quota_mb}`; none declared, none given). Keys and JSON values in the group's state (`AppStorage`): `storage.user` (one person in one instance, the owner taken from the session, never the request) and `storage.instance` (everyone in one instance). A value up to 256 KB, a key up to 128 characters, an instance up to its `quota_mb` (1 to 1024, default 10, capped at 50 MB); last writer wins with an optional `ifVersion` (0 = only if new). The shell relays `get`, `set`, `delete`, `list(prefix)` to `POST /apps/<instance>/_storage`. Two instances never share storage. It is not the place for business data: that is a Store model.

## Addresses

Every instance has the **subpath** `https://<host>/apps/<name>/` derived from its name (built). Names follow the Store's rule (`[a-z0-9][a-z0-9_-]{0,63}`); `api`, `admin`, `console`, `identity`, `apps`, `static`, `health`, `version` and `catalog` are reserved. A non-prod channel's subpath is derived (`/apps/acme~test/`), never the prod address. **Subdomain and custom-domain bindings are not built**: there is no routing by `Host`, no per-name certificate and no verification of names (plan 26 AP08).

## Navigation

The host's shell page owns navigation; apps do not draw menus. A navigation set (`apps nav-save`, `nav-list`; refused when it names an instance that does not exist or names one twice) is versioned like any definition. The shell header shows, for each set the instance is in, the members the person holds a role on, so a menu never advertises a refusal. The frame reports its route (`postMessage {type:"route", path}`, a path starting with a single `/`, at most 512 characters); the shell puts it in the address as `#<path>` so the back button and a pasted link work. `chrome: none` (or `?embed`) serves no header. A member bound to another origin would be a plain link.

## Portals

A portal is an app of `kind: portal` with a `portal:` section (identity, theme, layout, navigation, home, mounts, slots, addresses), validated with positions by the manifest loader. It changes how apps look and are reached, never what they may do.

- **Declared** (default): data the shell renders, with no script. `/apps/<portal>/` is its home; `/apps/<portal>/<mount>/` shows a mounted instance in the portal's chrome (a role on the mounted instance is required). The theme is CSS custom properties (light on `:root`, dark under `prefers-color-scheme`) and is also posted to each frame as `{type:"theme", tokens}`. The menu is filtered to what the person may open. The home is a list of view files (`views/home.yaml`: `text` in a small markdown subset with no raw HTML, `links`, `tiles` of the apps the person may open; codes `DBD-APP-011` to `013`; `app validate` and `apps publish` refuse a bad one). **Slots** (`header`, `footer`, `aside`, `home`) are sandboxed apps in frames of their own, each with a token for its own instance only.
- **Trusted**: a portal with code (`trust: trusted` and an `entry`), on the host's origin with `frame-src 'self'`, only with an approval for its exact bytes; without one the shell shows the default portal with the reason ("Showing the default portal instead") and answers 403 on its files, token, storage and data. The same guard applies to the files of a trusted app of `origin: own`.
- The host has a **default portal** (the suite's own shell), so nothing is mounted into nothing. A portal is an instance like any other; an instance whose setting `portal` names a portal is framed by it at its own address too.

## External portals

For the organisation's own portal (SharePoint, Teams, an intranet): a stable launch URL `<subpath>/?launch` (signs the person in and opens the app); `GET /apps/catalog` (the signed-in person's session decides; 401 without one) lists the instances she holds a role on with name, title, description, app, version, channel and launch address; `?embed` serves the app without the header, and the instance setting `embed.allowed_origins` (bare origins, no wildcard, validated on save) becomes `frame-ancestors`, `'none'` by default. Browsers that block third-party cookies make a framed instance ask the person to sign in, which the home refuses to frame; such a portal should link with `?launch`. Mounting an app as a web component inside a portal's page is not planned: it would give the app the portal's origin.

## Instances

**An app instance** is a row `(name, app, version pin or latest in the channel, settings, bindings, roles)`. Settings are per instance; roles are per instance (`app:<instance>` in the identity home, `identity assign --app`); a token's audience is `app:<instance>`, so another instance refuses it; versions move per instance (promoting a version moves only the unpinned ones). `apps publish --dir`, `list`, `rollback`, `remove`, `instance-save`, `instance-list`, `instance-remove`, `nav-save`, `nav-list`, `nav-remove` are the command line (a rollback or a removal is a published change, audited, and the record stays; `remove` is refused while an instance runs the app, `instance-remove` while a portal mounts it); the app's about page (`GET /apps/<instance>/_about`) lists exactly the manifest's models and operations, the outside origins by directive, the storage kept, who published this version, and the approval that stands; it asks for no consent.

**Data.** An instance's manifest names the models it uses with `read` or `read and write` per entity; its bindings say which model instance each uses. The data route is `/apps/<instance>/_data/api/v1/models/...`, answered **in the host's process by the models published to the group** (`GroupAppData` over `GroupStoreApi`): the app's token is checked per instance, the scope of the manifest (`model/entity:read|write`, write includes read) and the person's role both decide (a viewer cannot write), an app never acts as an agent, and a model outside the manifest is 403 before the group is asked. CORS answers `*` on this route only (the opaque origin; the token is the credential). The token the shell hands out is made by `AppTokenBroker` (300 seconds, Ed25519, issuer `dbdataproxy-apps`, the signing seed the secret `dbdata:proxy:apps:signing-key` shared by every instance of a group).

**Store model instances.** A model's `storage.instances` says how its instances are kept apart; the server enforces it, never the client.

| `instances:` | Meaning |
| --- | --- |
| omitted | one copy, the `default` instance |
| `schema` | each instance's tables in `<model>__<instance>` on the model's connection |
| `connection` | each instance names its own connection |
| `column` | one set of tables; `_instance` is the first column of every key and of the audit table, stamped on every insert and added to every read, update, delete and rule check (unique, reference, sum, overlap, cycle); a body that names an instance is refused; references resolve inside the instance; no foreign keys are made |

Routes are `/api/v1/models/{m}/instances/{i}/...`; the old paths mean `default`; an administrator registers and forgets instances (forgetting keeps the data). A saved model text is kept under its SHA-256, so a copy runs the version it was made or last migrated at (`GET .../migrations`: `current`, `behind`, `unknown`) and `POST .../migrate` adds what the new version needs and nothing else (see DbDataStore). The Store controls (`@dbdatakit/store-ui`: `StoreClient.withInstance`, `instances`, `InstancePicker`, `EntityEditor instance`) take an instance. A model with instances cannot be published to a group yet (the group serves a model's own copy only).

## Decisions

1. The default for a code app is a sandboxed document under the host's shell page, not a separate origin; because the sandbox has no browser storage, **the host provides app storage**. Sandboxing is a default, not a rule.
2. The shell page owns navigation; apps do not draw menus.
3. A Store model's isolation is declared by the model and enforced by the server.
4. A portal is declared data by default, and may be trusted code with an administrator's approval of that exact version.
5. Trust is an administrative, reviewed and audited decision: the manifest asks, an administrator approves a version, a new version needs a new approval, and every call a trusted app makes carries the approval in the audit.
6. No outside requests for any app, trusted or not, unless an administrator allowed the domain.

Not planned: several tenants inside one app instance (use an instance per tenant).
