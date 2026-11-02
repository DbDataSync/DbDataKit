# Plan 26: how apps are published, navigated and instanced

Status: **proposed, with a recommendation for every question** (written 2026-11-02 at the owner's request; nothing here is built). It builds on plan 16 section 2 (an app is untrusted UI), plan 20 (the `apps` module of the serving layer, published definitions, groups), plan 21 (channels) and plan 22 section 1 (app authoring). Where this plan and those differ, this plan says so in section 9.

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

An app is untrusted UI (plan 16): whatever its code does, the servers it calls check everything. What still needs protecting is the **user's session on the host**: an app's script must not be able to read the console's or another app's cookies and storage, or call the host's APIs as the user without the scoped token.

The browser gives that protection per **origin**. So:

* **Declarative views** (specs rendered by the host with the shared components) are the host's own code, so they run on the host's origin. Any address form works.
* **Code apps** (an author's JavaScript) must not run on the host's origin. Two ways, and both are supported:
  1. **A sandboxed document** (recommended default). The host serves the app's pages with `Content-Security-Policy: sandbox allow-scripts allow-forms allow-popups` (and a strict `connect-src`). The document gets an opaque origin: no cookies, no storage, no access to the host's pages. The app has no credentials of its own; the host's **shell page** (section 4) frames it and hands it a short-lived scoped token over `postMessage`. This works on a subpath, so a plain install needs no DNS and no certificates.
  2. **An origin of its own** (a subdomain or custom domain per instance). The app can use its own cookies and storage, and `localStorage` works. This is for apps that need those, and it costs DNS and certificates.

An app's manifest says which it needs (`origin: sandbox | own`; default `sandbox`). Declaring `own` on a host without a domain bound to the instance fails publication with a message naming the setting.

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

## 5. Portals

The organisation's portal is a place people start from; the suite does not replace it. Three supported forms, all optional:

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

### 6.2 Instances of a Store model, and the isolation choices

An app that writes data usually needs the data isolated too. A Store **model definition** (`model/1`) can be published as several **model instances**. Three levels, chosen per instance, one mechanism:

| Level | What differs | Use it for | Cost |
| --- | --- | --- | --- |
| **Shared data** | nothing: two app instances bind the same model instance | the same data seen through two skins (staff and public) | none |
| **Own schema** (default for a new instance) | the tables live in a schema of their own, `<model>__<instance>`, on the model's connection; own history and audit rows; own approvals | many tenants, a customer each; each can be backed up, reset or dropped alone | N schemas to migrate |
| **Own database** | the instance names a different `connection` | a tenant whose data must be in its own database, server or region | N connections, N credentials |

How it works:

* A model instance is `(model definition version, instance name, connection, schema)`. The management API gets an instance in the path (`/api/v1/models/{m}/instances/{i}/...`, with the existing paths meaning the model's `default` instance, so nothing breaks).
* **Structure changes are per instance.** The definition has versions; each instance records which one it is at. A new version is applied instance by instance through the plans and approvals that exist, with plan 21's expand-then-contract rule (an instance still on version N must keep working while others move to N+1). A migration report lists every instance and where it stands.
* **Nothing crosses instances.** Reads, writes, references (`references:` stays inside the instance), change requests, history and audit are per instance. A rule cannot span instances.
* **Roles are per instance**, as for apps.
* **Semantic models and lineage** see an instance as a distinct source: its tables are different objects. A semantic model bound to a model definition is bound to an instance; "the same report for every tenant" is one semantic definition published once per instance (or parameterised by the instance, which is a semantic-layer decision outside this plan).
* **Creating one is a plan.** Creating an instance is a dry-run-then-apply action (create schema, create tables, seed rows), approved like any structural change, and deleting one is an administrator action that names what is dropped.

### 6.3 Binding an app instance to data

The manifest names the models it uses (plan 16). The instance supplies the **model instance** for each, by name, when it is published. An app that needs a different model instance per signed-in user's organisation (true multi-tenancy inside one app instance) is a different feature, not planned: use one app instance per tenant, which this section makes cheap.

## 7. Channels and instances together

Plan 21's channel is part of a published version's key. Every instance belongs to a channel at each stage: `acme` in `prod` and in `test`. The subpath of a non-prod channel is derived (`/apps/acme~test/`, preview `/apps/acme~preview-<id>/`), never the prod address, and a preview is removed with its draft. A promotion carries the app version; the model instance a channel binds to is channel configuration (plan 21 section 4, question 3). A model instance's draft layer (sandboxed writes) is per instance and per channel.

## 8. Slices

| Id | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| AP03 | The app manifest and `app validate`: name, version, models used, roles, `origin`, `chrome`, relative-base check | AP01 | M | a valid manifest passes; each bad field is refused with a message naming the file and line; a bundle with absolute asset paths is refused |
| AP04 | Instances in the group's state: definition, instance, version pin, settings, bindings, per-instance roles | AP03, O03 | M | two instances of one app run different versions and settings; a token for one is refused by the other |
| AP05 | The `apps` module serves instances on subpaths, declarative views in place and code apps in the shell page with a sandboxed frame and the token broker (`postMessage`) | AP04, O11 | L | Playwright: a code app reads and writes through the store module with the sandbox attribute set and no access to the host's cookies; an app that tries `document.cookie` of the host fails |
| AP06 | Navigation sets and the shell header; the URL follows the app's route | AP05 | M | a person sees only the instances they may open; the back button and a pasted link work |
| AP07 | Portal: launch URL, `GET /apps/catalog`, `embed.allowed_origins` as `frame-ancestors` | AP05 | S | a page on an allowed origin frames an instance; one on another origin is refused by the browser |
| AP08 | Subdomain and custom-domain bindings: the routing table, SNI certificates, name verification, redirect URIs registered with the identity home | AP05 | L | one host serves two names with two certificates; removing a binding removes its redirect URI; an unverified name is refused |
| Z12 | Store model instances: `(definition, instance, connection, schema)`, per-instance tables, history, audit and roles; the instance in the API path | Z09, AP04 | L | two instances of one model on one database hold different rows, a row in one is invisible in the other, a reference across them is refused; tests on both engines |
| Z13 | Per-instance migration: apply a new definition version to one instance, a report of all instances, expand-then-contract respected | Z12, plan 21 | M | an instance on N keeps working while another is at N+1; the report names both |
| Z14 | The Store controls (`@dbdatakit/store-ui`) take an instance; the console's Store module picks one | Z12 | S | the console edits rows of either instance; the app's controls use the instance its binding names |

## 9. What this changes in other plans

* **Plan 16 section 2** says an app is served "at `/apps/<name>/`", "on its own origin or in a sandboxed frame". This plan makes it precise: the sandbox is a CSP `sandbox` on the app's documents, framed by the shell page; "own origin" is a subdomain or custom domain.
* **Plan 22 section 1** says builds run offline from vendored packages with an allow-list. AP01 as built runs `bun install --ignore-scripts` (it fetches from the registry). Before the apps host ships, the bundler gets an `offline` mode that installs only from an allow-listed, vendored mirror; AP01's README says so.
* **Plan 20** gets the routing table of AP08 as a published definition; the `apps` module's slice O11 stays the base.
* **Plan 21** gets instances as a second axis beside channels (section 7).
* **Plan 15 / 19 (Store)** gets model instances (Z12 to Z14). `ModelStorage` already names tables `<model>.<entity>`; the instance's schema changes the first part only, so the writer and the controls need an instance argument, not a rewrite.

## 10. Decisions this plan makes, to confirm or change

1. **Subpath is always on; code apps default to a CSP-sandboxed document under a shell page**, not to a subdomain each. (Cheapest to run; own origin is opt-in.)
2. **The shell page owns navigation**, through navigation sets; apps do not draw menus.
3. **Instances are cheap and first-class**; multi-tenancy inside one app instance is out of scope.
4. **A Store model instance defaults to its own schema**; its own database is the same mechanism with another connection.
5. **Portals are supported by link, catalog feed and embed with an allow-list**; no web-component mounting.
