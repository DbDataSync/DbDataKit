# Plan 26: how apps are published, navigated and instanced

Status: **proposed** (written 2026-11-02 at the owner's request; nothing here is built). **Owner direction (2026-11-02): support every address form and every isolation option listed here; subpath apps get storage; some Store models are shared and split by instance id, others are separate copies; some apps get their own domains.** Sections 2a and 6.2 carry that direction. **Owner direction (2026-11-02, later): a special kind of app, a *portal*, customises the host and the shared navigation that subpath apps are mounted into** (section 4a). **Owner direction (2026-11-02, later still): sandboxing is not required in every case. A portal may need access to and control of APIs on behalf of users, and administrators and code reviewers decide whether such an app has been properly audited. The one kind of sandboxing to keep for everything is a ban on outside requests: external scripts, JSONP, CORS and other requests to outside domains** (section 2b). It builds on plan 16 section 2 (an app is untrusted UI), plan 20 (the `apps` module of the serving layer, published definitions, groups), plan 21 (channels) and plan 22 section 1 (app authoring). Where this plan and those differ, this plan says so in section 9.

Three questions are answered:

1. **Where does a published app live?** A subpath, a subdomain, a custom domain, inside a portal.
2. **Can apps share navigation?**
3. **Can one app, or one DbDataStore model, be published several times as isolated instances?**

## 1. Terms

| Term | Meaning |
| --- | --- |
| **App** (definition) | The source, the manifest and the built, versioned bundles (plan 22). It has no address. |
| **Instance** | One publication of an app (or of a Store model): a name, the version it runs, the data it is bound to, its settings, its addresses. Two instances of one app share code, nothing else unless said. |
| **Channel** | The lifecycle stage of an instance (plan 21): preview, test, prod. An instance exists in each channel it has been promoted to. Instances are not channels: a channel is *when*, an instance is *who for*. |
| **Navigation set** | A named, ordered list of instances (with titles and icons) shown as one menu. |
| **Host** | A serving group running the `apps` module (plan 20). |

## 2. The rule that decides the rest: where does the app's code run?

This section is the safe default. Section 2b says when an administrator may approve something else, and what every app keeps regardless.

An app is untrusted UI (plan 16): whatever its code does, the servers it calls check everything. What still needs protecting is the **user's session on the host**: an app's script must not be able to read the console's or another app's cookies and storage, or call the host's APIs as the user without the scoped token.

The browser gives that protection per **origin**. So:

* **Declarative views** (specs rendered by the host with the shared components) are the host's own code, so they run on the host's origin. Any address form works.
* **Code apps** (an author's JavaScript) must not run on the host's origin. Two ways, and both are supported:
  1. **A sandboxed document** (recommended default). The host serves the app's pages with `Content-Security-Policy: sandbox allow-scripts allow-forms allow-popups` (and a strict `connect-src`). The document gets an opaque origin: no cookies, no storage, no access to the host's pages. The app has no credentials of its own; the host's **shell page** (section 4) frames it and hands it a short-lived scoped token over `postMessage`. This works on a subpath, so a plain install needs no DNS and no certificates.
  2. **An origin of its own** (a subdomain or custom domain per instance). The app can use its own cookies and storage, and `localStorage` works. This is for apps that need those, and it costs DNS and certificates.

An app's manifest says which it needs (`origin: sandbox | own`; default `sandbox`). Declaring `own` on a host without a domain bound to the instance fails publication with a message naming the setting.

## 2b. Trust, and the one restriction every app keeps: no outside requests

Section 2 describes the safe default. It is not the only way an app may run. Some apps, and above all portals, legitimately hold APIs on behalf of the people using them; sandboxing such an app would defeat its purpose. Whether that is acceptable is an **administrative and review decision**, not something the host can derive. So there are two separate settings, and they are not the same thing.

### Trust: who vouches for the code

| Level | What the app can do | Who decides |
| --- | --- | --- |
| **Sandboxed** (default) | runs in an opaque origin with no credentials; it reaches the servers only through the shell's token broker, with a scoped token (section 2) | nobody: it is the default |
| **Trusted** | runs on the host's origin (or the instance's own), with direct access to the APIs and the person's session as far as its approval says; can control APIs on the user's behalf | an administrator, after review; recorded below |

* **The manifest asks; an administrator grants.** `trust: sandbox | trusted` in the manifest is a request. An instance runs as `trusted` only when an administrator has approved *that exact version*. Without it, publication of a trusted app is refused with a message that names the missing approval.
* **An approval is a record, not a flag**: who approved, when, which version (the bundle's hash and the manifest's), what it was approved for (the data operations and APIs it may use, the outside origins it may reach, section 2b below), and a note (a link to the review). It lives in the group's state next to the published definition.
* **A new version needs a new approval.** The default is that a change of any byte of the bundle ends the approval; an administrator may choose to let the same reviewer's approval carry to later versions of a named app whose reviewed source is in the suite's own repository and whose bundle reproduces from it (the bundler is deterministic), so a trusted app can still be updated without a review per release.
* **Everything a trusted app does is audited**: its calls to the suite's APIs carry the approval's id as well as the person, so the servers' audit records say "Ada, through the sales portal v7, approved by Grace on 2026-11-03". A trusted app's own token is no wider than the person's: it acts as them and never above them.
* **Who can approve** is a role: the administrator, or an approver role for apps if an organisation wants a split between those who run the host and those who review code. An author cannot approve their own app.
* **The servers do not change.** Trusted or sandboxed, an app is a client: the servers check every call. Trust changes what the *browser* lets the app do, and what the audit says; never what the servers permit.

The manifest of AP03 gains `trust` and `egress` keys with AP14 and AP15 (both are requests; the approval is not in the manifest).

### Egress: no outside requests, for every app

Independent of trust, **an app may not make requests to outside domains unless an administrator allowed that domain**. This is the sandboxing the owner wants kept in all cases.

* **What it stops**: external scripts and stylesheets (`script-src`, `style-src`), JSONP (it is a script load), fetch, XHR, WebSocket and EventSource to other origins, which includes CORS requests (`connect-src`), images, fonts, media, frames and objects from other origins (`img-src`, `font-src`, `media-src`, `frame-src`, `object-src 'none'`), form posts to other origins (`form-action`), and a changed `<base>` (`base-uri 'self'`).
* **How**: the host serves every app document with a `Content-Security-Policy` built from the instance's effective policy, starting from `default-src 'self'`, and the same policy covers a portal and its slots. The browser enforces it.
* **Exceptions are allow-lists, per instance, per directive**, from two sources: the manifest *requests* them (`egress: {connect: ["https://api.contoso.com"], img: ["https://cdn.contoso.com"]}`, or `frame` for an embedded report server, plan 16 "Sign-in decisions" item 10) and the administrator *approves* them (the same record as a trust approval, or on its own for a sandboxed app). A request nobody approved is not in the policy. The host's own origins and, for a sandboxed app, the token broker are always allowed; the suite's APIs are reached through them.
* **At build time**, the bundler's output is scanned for URLs to other origins (an `http(s)://` literal in script, style or HTML, an `import` from a URL, a `<script src>`), and `app validate` reports each one that the manifest does not request, so a surprise is found before a review. This check is advice for the author; the policy in the browser is what enforces.
* **What it does not do**: a content security policy is enforced by the browser for that document. It stops accidents, hidden dependencies and the ordinary routes for sending data out, and it is the right control for policy and compliance. It is **not** a barrier against hostile code that runs on the same origin with credentials: such code can still leak through channels a policy does not govern (navigating the window, a link the person clicks, timing). For a trusted app the control is the review and the audit above; the egress policy backs them up. For a sandboxed app, the opaque origin and the broker do the work, and the egress policy adds the ban on outside requests.
* **A report of the policy in force**: each instance's page in the console shows its effective policy and what was requested but not approved.

## 2a. App storage on every address, including a subpath

A sandboxed document has an opaque origin: **no `localStorage`, no cookies, no IndexedDB**. A subpath app would therefore have nowhere to keep anything. So the host provides storage, and every app gets it whatever its address; an app with `origin: own` may use the browser's storage as well.

| Tier | What it holds | Scope | How an app reaches it |
| --- | --- | --- | --- |
| **App storage, per user** | preferences, saved filters, drafts, UI state | one person in one instance | `storage.user` |
| **App storage, per instance** | shared settings, small lookup data, counters | everyone in one instance | `storage.instance` |
| **Store models** | structured, validated, audited business data (section 6.2) | per the model's isolation | the Store client (`@dbdatakit/store-ui` and the typed client) |
| **Documents** | files and versioned documents | per the documents module (plan 20) | not planned here |

* **App storage** is a key-value store of JSON values in the group's shared state (`DbDataKit.State`, so every instance of a group sees it). Limits are part of the manifest and enforced by the host: a value up to 256 KB, a key up to 128 characters, an instance up to a size the administrator sets (default 50 MB), and `storage.user` cannot be read by another person. Writes are last-writer-wins with an optional version check (`if_version`) for an app that needs one.
* **A sandboxed app reaches it through the shell** (the same `postMessage` bridge that carries the token): `storage.get(scope, key)`, `set`, `delete`, `list(prefix)`. The shell calls the host's storage API with the app's scoped token. An app with its own origin can call the same API directly.
* **Isolation is by instance.** Two instances of one app have separate app storage, always. (The next section's "shared" option is for Store models, where it is a deliberate choice.)
* App storage is **not** the place for business data. The manifest declares `storage: {user: true, instance: true, quota_mb: 10}`; an app that declares nothing gets none.
* Previews (plan 21) get a throwaway copy that is dropped with the preview.

## 3. Addresses

An instance has one **primary address** and any number of others (redirects). Every address kind is a *binding* the administrator adds to an instance; none changes the app.

| Form | Example | Needs | Use it for |
| --- | --- | --- | --- |
| **Subpath** (always on) | `https://serving.example.com/apps/sales-planning/` | nothing beyond the host | the default; every instance has one, derived from its name |
| **Subdomain** | `https://sales-planning.apps.example.com/` | a wildcard DNS record and a wildcard certificate, once | code apps that need an origin of their own; instances that should look separate |
| **Custom domain** | `https://planning.contoso.com/` | the customer's DNS (CNAME to the host), a certificate for the name (supplied through the secret store, file or reference), the name verified | a branded or tenant-facing instance |
| **Portal** | a tile or an embed in the organisation's portal (SharePoint, Teams, an intranet) | section 5 | people who start from the portal |

How it is served:

* **One listener, routing by `Host` and path.** The `apps` module looks the request up in a table of bindings (host name, path prefix, instance) read from the group's shared state, so every instance of a group routes the same way and a binding change is a published change (plan 20 principle 1), not a restart. TLS picks the certificate by name (SNI). Behind a reverse proxy it trusts `X-Forwarded-Host` and `X-Forwarded-Prefix` only from addresses the administrator lists.
* **Apps must not assume where they are.** The bundler (AP01) builds with relative asset paths, and the shell tells the app its base path at start. A bundle that hard-codes `/` is caught by `app validate`.
* **Names are validated.** Instance names are the Store's name rule (`[a-z0-9][a-z0-9_-]{0,63}`); a subdomain label is the name; reserved names (`api`, `admin`, `console`, `identity`, `apps`, `static`) are refused; a name is never reused for a different app while its old address still redirects.
* **Sign-in follows the address.** Each binding is registered as a redirect URI of the instance's OpenID client when it is added, and removed when it is removed. A session cookie is per origin, so a person signs in once per origin; with the same identity home the sign-in is silent.

Recommendation: ship **subpath first** (AP05), then **subdomain** (AP08) then **custom domain** (the same code plus certificate and verification). A portal is section 5.

## 4. Shared navigation: a shell page around sandboxed apps

Recommendation: **the host's shell page owns navigation; apps do not draw it.**

* Every instance is served inside a **shell page** on the host's origin: a header (the suite's look, the signed-in person, a menu), and the app below it, in a sandboxed frame (code apps) or rendered in place (declarative). The shell obtains the scoped token, passes it to the frame, and keeps the address bar in step with the app's own routes (the app reports route changes by `postMessage`; the shell updates the URL, so links and the back button work and can be shared).
* **Navigation sets.** An administrator (or an author with the role) makes a set: a title, and an ordered list of instances with an optional label and icon. A set is a published definition like any other (versioned, in channels). The shell shows a set's menu in every member instance. A person sees only the members they may open (the instance's roles decide), so a menu never advertises what leads to a refusal.
* An instance can be in several sets, or none. `chrome: none` in the manifest (or an `?embed` request, section 5) serves the app without the header, for use inside another page.
* **Across origins.** A set rendered on one origin links to an instance bound to a different origin as a normal link (full page load, silent sign-in). It does not frame it.
* **Within an app**, the app's own routes and tabs are the app's business (declarative views have a standard layout; a code app draws what it likes below the header).

Not recommended: letting each app include a nav component. It cannot be enforced (the app is untrusted), versions drift, and a menu that differs per app is the problem sets exist to remove.

## 4a. Portals: the app that is the shell

The shell page of section 4 is a fixed page of the suite's. Organisations will want it to be theirs: their name and colours, their header and footer, their menu, a home page of their own, apps arranged the way they think of them, a sign-in page that says who they are. That is a **portal**: *a special kind of app whose job is to be the host that other apps are mounted into.* (An external portal such as SharePoint or Teams is something else, section 5: the suite's apps go into it. A portal in this section is the suite's own, and it can sit inside an external one.)

**A portal is declared by default, and may be trusted code.** A portal is the page that holds the person's session, hands out tokens and frames the apps, so what it is allowed to do matters more than for any other app. There are two kinds, and the manifest says which:

* **Declared** (default): the portal is *data the host's own shell renders* and contains no script of its own. It needs no approval beyond the usual publish, and it is what most organisations want: a look, a menu and a home page. Where it wants something the declarative parts cannot say (a live widget in the header, a status banner), it names a **slot** and the content is an ordinary app mounted there, sandboxed or trusted like any app.
* **Trusted**: a portal with code of its own, for the cases where a portal must hold or control APIs on behalf of users (a portal that creates records for people, signs them in to other systems, or brokers calls between apps). It is a trusted app (section 2b): an administrator has approved that exact version after review, and every call it makes is audited with the approval. It still keeps the egress policy.

The shell never runs the code of a portal that is not approved; it falls back to the default portal and says why.

What a portal declares (`kind: portal` in the manifest, plus a `portal:` section):

| Part | What it says |
| --- | --- |
| **Identity** | name, logo, favicon, the title of the page, the text of the sign-in page and the footer, a support link |
| **Theme** | design tokens (colours, fonts, spacing, radius) for light and dark; they become the CSS variables the suite's controls already read (`--sui-*`, the console's own), so every app that uses the shared controls follows the portal without knowing it |
| **Layout** | where the menu is (top, side), whether there is a footer, how wide the content is, what the header shows (search, the person's menu, a switcher for portals or instances) |
| **Navigation** | the menu, as a tree of entries: an app instance, a link, a group, a divider, each with a label, an icon and the role that shows it. A portal *owns* a navigation set (section 4); a standalone set is the same thing without the rest |
| **Home** | the landing page: declarative views (text, a catalog of the apps the person may open, links, a dashboard from the semantic layer) |
| **Mounts** | which instances this portal mounts and where: a path under the portal's own address (`/sales/`, `/hr/`), or the instance's own address with the portal's frame around it |
| **Slots** | `header`, `footer`, `home`, `aside`: places a sandboxed app may fill |
| **Addresses** | the portal's own: a subpath, a subdomain or a custom domain, like any instance. The domain decides which portal a request gets |

How it fits with the rest of the plan:

* **The host has a default portal** (the suite's own shell, always there, so nothing is mounted into nothing). A portal is an *instance* like any other, versioned, promoted through channels, and published more than once (one per customer, one per department): section 6 applies. An app instance can be mounted in several portals; its address and its roles do not change with the portal around it.
* **Which portal frames a request** is decided by the address (a domain or a path prefix the portal is bound to) and then by the instance's setting; an instance with `chrome: none` is served without any portal.
* **A portal changes how the apps look and are reached, never what they may do.** Roles, tokens, the manifest's data operations and the servers' own rules are untouched; a portal cannot grant an app anything. A person sees in a portal's menu only what they may open.
* **Branding applies to the controls, not only the frame**: because the theme is the variables the shared controls read, `@dbdatakit/store-ui` and the other shared components take the portal's look with no change by the app's author.
* **Portals are edited like apps** (plan 22 section 1: workspace, preview, merge, publish), by people with the author role; a portal's preview is read-only like any other (plan 21). Publishing a portal that mounts an instance the author may not see is refused.

Not in a portal: code on the host's origin unless the portal is trusted and approved; per-person layout customisation (a person's preferences are app storage, section 2a); mounting apps from other hosts (a link, not a mount).

## 5. External portals

The organisation's portal is a place people start from; the suite does not replace it. Three supported forms, all optional (section 4a is the suite's own portal; this section is about the organisation's, which the suite's apps go into):

1. **Link or tile.** Each instance has a stable launch URL (`<primary address>/?launch`) that signs the person in (silently, with the same OpenID provider) and opens the app. `GET /apps/catalog` returns the instances the caller may open, as JSON (name, title, description, icon URL, launch URL, channel), for a portal that builds tiles from a feed.
2. **Embed.** `?embed` serves the app without the header; the instance setting `embed.allowed_origins` becomes the response's `frame-ancestors`, and nothing else may frame it. The default is that no origin may.
3. **Single sign-on.** The portal and the suite use the same identity (Entra ID through the console or home, plan 16), so the person is already signed in. Teams and SharePoint wrappers are *the portal's* configuration of 1 or 2, not new suite features.

Not planned: mounting an app as a web component or ES module inside the portal's own page (it would give the app the portal's origin; section 2 forbids that for code apps).

## 6. Many instances of one app, and of one Store model

**Yes, both.** The two are separate questions.

### 6.1 Instances of an app

An instance is a row: `(name, app, version pin or "latest in channel", settings, data bindings, addresses, roles)`. Publishing the same app twice is two rows pointing at one bundle. Two instances share *code only*:

* **Settings** (title, logo, theme variables, feature flags the manifest declares) are per instance.
* **Roles** are per instance (`app:<instance>` in the identity home, which already supports `identity assign --app`). A person can be an editor in one instance and a viewer in another.
* **Tokens** are scoped to one instance; a token for instance A is refused by instance B.
* **Versions** move per instance: promoting version 7 to `acme` does not touch `globex`. A release (plan 21) can list several instances.

### 6.2 Instances of a Store model: shared and split, or separate copies

A model definition says, once, how its instances are kept apart. **Both ways are supported, per model**, because the right answer depends on the data (owner direction, 2026-11-02):

```yaml
storage:
  connection: mdm
  instances: column     # column | schema | connection;  omit for a model that has one copy only
```

| `instances:` | What it means | Use it for | Cost |
| --- | --- | --- | --- |
| *(omitted)* | one copy; the model has a single `default` instance | most models today | none |
| **`column`** (shared, split by instance id) | one set of tables for all instances; every table carries an instance id, which is part of the key | reference data that many tenants hold in small amounts; cross-instance reporting is wanted; thousands of instances | isolation is only as good as the server's filtering (see below) |
| **`schema`** (separate copy) | each instance has its own tables in a schema of its own, `<model>__<instance>`, on the model's connection | tenants that must be backed up, reset or dropped alone | N schemas to migrate |
| **`connection`** (separate copy, own database) | each instance names its own connection | data that must be physically separate (another database, server or region) | N connections, N credentials |

How each works:

* **A model instance** is `(definition version, instance id, connection, schema)`. The management API takes an instance in the path (`/api/v1/models/{m}/instances/{i}/...`); the existing paths mean the model's `default` instance, so nothing breaks.
* **`column` mode is enforced by the server, never the client.** The writer stamps `_instance` on every inserted row and adds `AND _instance = @instance` to every read, update, delete and rule check (unique, reference, sum, overlap, cycle); the instance comes from the request's path and must be one the caller's token is scoped to; a body that names another instance, or an `_instance` value, is refused. The key of every entity is `(_instance, <declared key>)`, so two instances may hold the same business key; `references:` resolve inside the instance; audit and history rows carry the instance. A test seeds two instances with colliding keys and proves no read, write or rule ever sees the other's rows. `no_overlap`, `no_cycle` and `sum_equals` are evaluated within one instance.
* **A model can offer a `shared` slice** (not built yet; Z12b has the instance split without it): an entity marked `scope: shared` in `column` mode (for example a list of countries) is held once and readable by every instance, writable only through the `default` instance. Everything else is per instance.
* **`schema` and `connection` modes** need no change to the rules: the writer already works against one set of tables; the instance only decides which.
* **Structure changes are per instance for `schema` and `connection`, and once for `column`.** The definition has versions; each separate instance records which one it is at. A new version is applied instance by instance through the plans and approvals that exist, with plan 21's expand-then-contract rule (an instance still on version N keeps working while others move to N+1). A migration report lists every instance and where it stands. In `column` mode there is one set of tables, so one migration moves every instance together.
* **Changing the mode of a model that holds data** is a migration, planned, previewed and approved like any other (add the instance id, backfill `default`; or split into schemas), never a silent change.
* **Roles are per instance**, as for apps.
* **Semantic models and lineage** see a `schema` or `connection` instance as a distinct source (its tables are different objects) and a `column` instance as a filter on one source. Whether the semantic layer parameterises by instance is a semantic-layer decision outside this plan.
* **Creating a `schema` or `connection` instance is a plan** (create schema, create tables, seed), dry-run then applied and approved; creating a `column` instance only registers it. Deleting one is an administrator action that names what is dropped or deleted.

### 6.3 Binding an app instance to data

The manifest names the models it uses (plan 16). The instance supplies the **model instance** for each, by name, when it is published. Two app instances may name the same model instance (shared data) or different ones (separate data); in `column` mode both can also name the same model with different instance ids. An app that needs a different model instance per signed-in user's organisation (true multi-tenancy inside one app instance) is a different feature, not planned: use one app instance per tenant, which this section makes cheap.

## 7. Channels and instances together

Plan 21's channel is part of a published version's key. Every instance belongs to a channel at each stage: `acme` in `prod` and in `test`. The subpath of a non-prod channel is derived (`/apps/acme~test/`, preview `/apps/acme~preview-<id>/`), never the prod address, and a preview is removed with its draft. A promotion carries the app version; the model instance a channel binds to is channel configuration (plan 21 section 4, question 3). A model instance's draft layer (sandboxed writes) is per instance and per channel.

## 8. Slices

| Id | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| AP03 | The app manifest and `app validate`: name, version, models used, roles, `origin`, `chrome`, relative-base check | AP01 | M | a valid manifest passes; each bad field is refused with a message naming the file and line; a bundle with absolute asset paths is refused |
| AP04 | Instances in the group's state: definition, instance, version pin, settings, bindings, per-instance roles | AP03, O03 | M | two instances of one app run different versions and settings; a token for one is refused by the other | **Status: the library done; the apps host's use of it not** (branch `suite/ap04-app-instances`, stacked on O03). `DbDataKit.Apps.Instances`: an app is the published definition `app/<name>` (content: an `AppPackage`, the manifest and the built files, read strictly before it is stored), an instance `app-instance/<name>` (app, version pin or the channel's current, title, settings, bindings), both in `DefinitionStore`, so they version, switch by channel, audit and roll back. `Save` refuses an unpublished app, a pin that is not a version, a binding to a model the manifest does not use, a reserved or malformed name and a malformed setting. `Resolve` gives the instance, the app version it runs and its manifest; promoting a version moves only the unpinned instances. A token's audience for an instance is `app:<name>` (`AppInstanceAudience`), and a validator for another instance refuses it (the console token validator, tested). Tests: two instances run different versions and settings, promotion, rollback, refusals, the token. **Not done:** per-instance roles (they live in the identity home as `app:<instance>`; the host reads them with the token, AP05/U19), addresses (AP08), the host adding `DefinitionSchema` to the Proxy's state.
| AP05 | The `apps` module serves instances on subpaths, declarative views in place and code apps in the shell page with a sandboxed frame and the token broker (`postMessage`) | AP04, O11, 13a U19 | L | Playwright: a code app reads and writes through the store module with the sandbox attribute set and no access to the host's cookies; an app that tries `document.cookie` of the host fails | **Status: AP05a, the request handler, done; the rest not** (branch `suite/ap05-apps-module`). `DbDataKit.Apps.Host.AppSite` answers `Get(instance, subpath)` from the stored package with no web framework: the root and any route without a file extension get the entry document with `<base href="/apps/<instance>/">`, a file gets its content type, a path that leaves the app is 400, an unknown instance or missing file 404 (the message names no other instance), and every answer carries the egress `Content-Security-Policy` from the approved origins and, for `origin: sandbox`, the `sandbox` directive, plus `nosniff` and `no-referrer`. 10 tests. **Left:** mapping it onto the Proxy as the `apps` module (`ProxyModules.Known`, `DefinitionSchema` in the Proxy state), OIDC sign-in and `app:<instance>` roles (AP05b), the shell page with the sandboxed frame and `postMessage` token broker, and the Playwright test (AP05c) |
| AP06 | Navigation sets and the shell header; the URL follows the app's route | AP05 | M | a person sees only the instances they may open; the back button and a pasted link work |
| AP07 | Portal: launch URL, `GET /apps/catalog`, `embed.allowed_origins` as `frame-ancestors` | AP05 | S | a page on an allowed origin frames an instance; one on another origin is refused by the browser |
| AP08 | Subdomain and custom-domain bindings: the routing table, SNI certificates, name verification, redirect URIs registered with the identity home | AP05 | L | one host serves two names with two certificates; removing a binding removes its redirect URI; an unverified name is refused |
| AP09 | App storage: `storage.user` and `storage.instance` in the group's state with the manifest's limits, the storage API, and the shell bridge for sandboxed apps | AP05 | M | a sandboxed app stores and reads a preference per person and a setting per instance; one person cannot read another's; two instances do not share; a value over the limit is refused |
| AP10 | The portal kind: `kind: portal` in the manifest and the `portal:` section (identity, theme, layout, navigation, home, mounts, slots, addresses) validated with positions; a portal contains no script | AP03 | M | tests: a complete portal passes; each bad part is refused with its reason; a portal that names a script, a mount that does not exist or a slot with no app is refused |
| AP14 | The egress policy: the manifest's `egress` requests, the per-instance approved allow-lists, a `Content-Security-Policy` built from them (`default-src 'self'` and the directives of section 2b), the bundle scan for URLs to other origins in `app validate`, and the policy shown on the instance's page | AP03, AP05 | M | tests: a page that loads an outside script, calls an outside API with fetch, XHR or WebSocket, loads a JSONP script, or posts a form to another origin is blocked by the browser (Playwright); the same call to an approved origin works; an unapproved request is not in the policy; `app validate` reports an outside URL in a bundle |
| AP15 | Trust levels, approvals and audit: `trust: sandbox \| trusted` as a request, the approval record (who, when, the version's hash, what it was approved for, a note), a new version ends an approval (with the reproducible-source exception), the approver role, the approval's id on every API call a trusted app makes and in the servers' audit | AP04, AP05 | L | tests: a trusted app does not publish without an approval for that exact version; a changed bundle ends it; an author cannot approve their own app; the Store's audit row of a write made through a trusted app names the person, the app version and the approver |
| AP16 | Trusted portals: a portal with code of its own runs on the host's origin only with an approval; without one the shell falls back to the default portal and says why | AP11, AP15 | M | a trusted portal calls an API for the user and every call is audited with its approval; the same portal unapproved is not run |
| AP11 | The shell renders a portal: theme tokens become the controls' variables, header, footer, menu and layout from the declaration; the default portal is the suite's own | AP06, AP10 | L | Playwright: two portals on two addresses give the same app two looks, and a person sees only the menu entries they may open |
| AP12 | Portal home and catalog: declarative landing views and a catalog of the apps the person may open | AP11 | M | the home lists exactly the instances the caller may open |
| AP13 | Slots: a sandboxed app mounted in a portal's header, footer, aside or home, with the same token broker | AP05, AP11 | M | an app in the header reads its own data and cannot reach the portal's page or session |
| AP17 | Report servers embedded with Windows sign-in (plan 16, "Sign-in decisions" item 10): an app embeds an SSRS or Power BI Report Server report by its URL; the report server's origin is an approved `frame` exception of the instance (section 2b); off the domain the frame is replaced by a link that opens the report on the report server | AP05, AP14 | M | Playwright: an unapproved report origin is blocked by the policy and an approved one is framed; with Windows sign-in failing, the person sees the link, not a password prompt; on a domain-joined Windows host with a report server (the Windows CI job, or recorded on the Excel VM) the report renders signed in as the person |
| AP18 | An app's about page shows its data access (plan 16, "Sign-in decisions" item 7): the models and operations its manifest uses, who published it and who approved it; no consent prompt | AP04, AP15 | S | test: the page lists exactly the manifest's models and operations and the publication and approval records; a person who opens the app is never asked to consent |
| Z12 | Store model instances, separate copies: `storage.instances: schema \| connection`; `(definition, instance, connection, schema)`, per-instance tables, history, audit and roles; the instance in the API path | Z09, AP04 | L | two instances of one model on one database hold different rows, a row in one is invisible in the other, a reference across them is refused; tests on both engines |
| Z12b | Store model instances, shared and split: `storage.instances: column`; `_instance` in every table and key, stamped and filtered by the writer, `scope: shared` entities | Z12 | L | two instances with colliding business keys; no read, write or rule (unique, reference, sum, overlap, cycle) sees the other's rows; a body naming another instance is refused; the instance must be in the caller's token; tests on both engines |
| Z13 | Per-instance migration: apply a new definition version to one instance, a report of all instances, expand-then-contract respected | Z12, plan 21 | M | an instance on N keeps working while another is at N+1; the report names both |
| Z14 | The Store controls (`@dbdatakit/store-ui`) take an instance; the console's Store module picks one | Z12 | S | the console edits rows of either instance; the app's controls use the instance its binding names |

## 9. What this changes in other plans

* **Plan 16 section 2** says an app is served "at `/apps/<name>/`", "on its own origin or in a sandboxed frame". This plan makes it precise: the sandbox is a CSP `sandbox` on the app's documents, framed by the shell page; "own origin" is a subdomain or custom domain.
* **Plan 22 section 1** says builds run offline from vendored packages with an allow-list. AP01 as built runs `bun install --ignore-scripts` (it fetches from the registry). Before the apps host ships, the bundler gets an `offline` mode that installs only from an allow-listed, vendored mirror; AP01's README says so.
* **Plan 20** gets the routing table of AP08 as a published definition; the `apps` module's slice O11 stays the base.
* **Plan 21** gets instances as a second axis beside channels (section 7).
* **Plan 15 / 19 (Store)** gets model instances (Z12 to Z14). `ModelStorage` already names tables `<model>.<entity>`; the instance's schema changes the first part only, so the writer and the controls need an instance argument, not a rewrite.

## 10. Decisions, and what is only an option

**Accepted by the owner, 2026-11-02:** decisions 1 to 6 as written below. The apps host is built as the `apps` module of the serving layer (plan 20, O11) on this order: O01 (modules), O02 (shared state), O03 (published definitions), AP04 (instances), AP05 (the module, the shell and the token broker).

**Decisions** (one way, hard to change later):

1. **The default for a code app is a sandboxed document under the host's shell page**, not a separate origin per app; because the sandbox has no browser storage, **the host provides app storage** (section 2a). **Sandboxing is a default, not a rule:** an administrator may approve an app as *trusted* (section 2b).
2. **The shell page owns navigation** (navigation sets); apps do not draw menus.
3. **A Store model's isolation is declared by the model** (`storage.instances`): not at all, `column`, `schema` or `connection`. The server enforces it; the client never does.
4. **A portal is declared data by default, and may be trusted code** with an administrator's approval of that exact version (section 4a).
5. **Trust is an administrative, reviewed and audited decision**: the manifest asks, an administrator approves a version, a new version needs a new approval, and every call a trusted app makes carries the approval in the audit. The servers' rules do not change with trust.
6. **No outside requests for any app, trusted or not**, unless an administrator allowed that domain (a content security policy built from per-instance allow-lists). This is advice to the browser, not a barrier against hostile same-origin code; for trusted apps the review and the audit are the control.

**Supported options** (the design carries all of them; each is chosen per app, per instance or per model; a portal can be published more than once, like an app): `trust: sandbox | trusted`; the egress allow-lists; subpath, subdomain and custom domain addresses for an instance; `origin: sandbox | own`; `chrome: shell | none`; app storage (user, instance, quota); the three instance modes of a Store model, including both a shared-and-split model and separate copies; portal launch links, the catalog feed and an allow-listed embed; navigation set membership.

**Not planned:** several tenants inside one app instance (use an instance per tenant); mounting an app as a web component inside a portal's own page.
