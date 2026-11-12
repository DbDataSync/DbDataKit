# DbDataKit.Apps.Manifest

The `app/1` manifest of an app (plan 26) and the checks `app validate` makes. `AppManifestLoader.Load(yaml, file)` reads it strictly: every problem is a diagnostic with the file, line and column, and nothing throws on a damaged file.

```yaml
schemaVersion: 1
name: sales-planning        # lower-case letters, digits, '-' and '_'; becomes the instance's default address
title: Sales planning
version: 1.2.0
entry: src/main.ts          # a code app's entry point (built by DbDataKit.Apps.Bundling)
views: [views/home.yaml]    # declarative view specs (`view-spec/1`, below); an app has an entry, views, or both
origin: sandbox             # sandbox (default): a sandboxed document under the host's shell page; own: an origin of its own (a subdomain or custom domain)
chrome: shell               # shell (default): the host draws its header and navigation; none: the app only
models:                     # the Store models and entities it uses, and what it does with each
  - model: territories
    entities: {territory: [read, write]}
semantic: [sales]           # the semantic models it queries
roles: [viewer, editor]     # the roles the app defines
trust: sandbox              # sandbox (default) or trusted: a request; an administrator approves the exact version (plan 26 section 2b)
egress:                     # outside origins the app asks to reach; everything else is refused by the browser. Origins only (https, or wss for connect), `*.` allowed before a host
  connect: [https://api.contoso.com]
  img: [https://cdn.contoso.com]   # also script, style, font, media, frame, form
```

The app's scoped token is the intersection of what the manifest declares and what the signed-in person may do; the servers still check every call (plan 16: the app is untrusted UI).

`AppChecks.Files` reports the entry and view files the manifest names that are missing; `AppChecks.AbsolutePaths` reports built HTML and CSS that point at a path from the server's root, which breaks under a subpath.

**Custom views and documents (plan 32 DBR30, DBR13).** A mapping under `views` declares a custom view (`CustomView`: a `widget` or a `page`, its `export`, `params`, declared `queries`, a page's `formats` and `page`), checked by `AppChecks.CustomViews` (`DBD-APP-019` to `022`). A dashboard or report file under `views` is detected by `ViewSpecLoader.IsDocument` and handed to an `IViewDocumentChecker` (`DbDataKit.Views.AppViewDocuments` implements it; `AppChecks.ViewSpecs(..., documents:)` and `AppValidator.Validate(..., documents:)` take it). See `docs/apps.md`.

## The ban on outside requests

`EgressPolicy.Build(effective, sandboxed, frameAncestors)` makes the `Content-Security-Policy` the host sends with every document of an app: `default-src 'self'`, `object-src 'none'`, `base-uri 'self'`, `frame-src 'none'`, and the origins in force added to the directive they were asked for (`connect-src` covers fetch, XHR, WebSocket and CORS; `script-src` covers external scripts and JSONP). `EgressPolicy.Effective(requested, approved)` keeps what was both asked for and approved, `Unapproved` lists the rest, and `Covers` is the match (the same origin, or a host under an approved `*.host`). `AppChecks.OutsideUrls(bundleFolder, egress)` lists the addresses of other hosts in a built bundle that the manifest did not ask for: advice for the author, since the browser enforces the policy. A content security policy stops accidents and the ordinary routes for sending data out; it is not a barrier against hostile code on the host's origin (plan 26 section 2b).

`AppApprovals` holds the rules for a manifest that asks `trust: trusted` (plan 26 section 2a). `Approve` makes an `AppApproval` record (who, when, the bundle's and the manifest's SHA-256, the operations and outside origins approved, a note) and refuses an approver who is the author, an app that did not ask to be trusted, and an approval that would allow more than the manifest asked for. `Gate` is the publish check: a sandboxed app always passes; a trusted one passes only with an approval for those exact hashes (a revoked one, or another app's, does not count), and the refusal names the missing approval. An approval an administrator marked `CarriesOver` also stands for a later version when the caller says the bundle reproduces from the reviewed source, and never for a request for more than was approved. `AppApproval.AuditLabel` is the text a server's audit row carries for a call made through the app. Where approvals are kept, the approver role, and the approval's id on each API call come with the apps host and AP04/AP05.

`kind: portal` makes the manifest a portal (plan 26 section 4a): the host other apps are mounted into. Its `portal:` section declares `identity`, `theme` (tokens for light and dark that become the controls' `--sui-*` variables; a value cannot refer to an image or an address), `layout`, `navigation` (app, link, group and divider entries; an `app` entry opens only an instance the portal mounts), `home` (view files), `mounts` (`app` at a one-segment `path` such as `/sales/`), `slots` (header, footer, home, aside, each filled by a mounted app) and `addresses` (a subdomain and custom domains). A portal is declared data: with `trust: sandbox` an `entry` or `views` is refused (`DBD-APP-009`), because code in a portal runs only when it asks `trust: trusted` and an administrator approves that version (`AppApprovals`). Every other part is validated with the file and line, as for an app.

`HomeViewLoader` reads a portal's home view file (AP12): `schemaVersion: 1` and `blocks` of `text` (markdown subset, no raw HTML), `links` and `tiles` (`all` or a list of mounted instances), `dashboard` (`app`, `view`, `height`: a mounted app drawn as a frame), `image` (`file` of the package, `alt`, `width`) and `table` (`columns`, `rows`); every block may carry `roles`. Problems carry the home file's path and line (`DBD-APP-011` shape, `-012` a tile or dashboard for an unmounted instance, `-013` HTML or an unsafe link in text, `-026` a role the manifest does not declare, `-027` an image that is not in the package). `AppChecks.HomeViews` runs it over the files a portal names.

`AppApprovals` addresses an approval by `ContentHash` (the bundle's and the manifest's hashes together; AP15), so it can be given before publishing.

## Declarative views: `view-spec/1`

Each file under `views` in the manifest is a view spec: a grid, a form, a detail page or a dashboard over the entities of a Store model the app uses. It is data the host draws with its own components, never script. `ViewSpecLoader.Load(yaml, file, app?)` reads one file strictly (unknown keys, wrong types, limits and duplicates are refused), every problem is a diagnostic with the file, line and column, and nothing throws on a damaged file. A portal's `portal.home` files are a different format (home blocks, above).

```yaml
schemaVersion: 1                # the format number of view-spec; required
kind: grid                      # grid | form | detail | dashboard
title: Territories
model: territories              # a model listed under `models` in the manifest
entity: territory               # an entity of it the manifest lists (read for grid, detail and dashboard; write for a form)
params:                         # what the view takes from its address or from a link: name, type (string|int|bool|date), required, default
  - {name: region, type: string}
columns:                        # grid: names, or {attribute, label, sortable}
  - name
  - {attribute: region, label: Sales region, sortable: false}
filters: [region]               # grid
sort: [{attribute: name, direction: desc}]
pageSize: 25                    # 1 to 500, default 50
links:                          # navigation to other views of the app
  - {label: Open, to: views/detail.yaml, row: true, with: {id: $id}}
```

- **grid**: `entity`, `columns` (at most 50), `filters`, `sort`, `pageSize`.
- **form**: `entity`, `fields` (names, or `{attribute, label, required, readonly, help}`), `mode` (`create`, the default, or `edit`), `key` (an edit form only: the name of a declared parameter with `required: true` that carries the row's key), `submit` (the button's label).
- **detail**: `entity`, `key` (as for an edit form, required), `sections` (each a `title` and its `attributes`).
- **dashboard**: `widgets`, each `kind` (`metric`, `bar`, `line`, `table`), `title`, `entity`, and a `measure` (`count`, or `{sum|avg|min|max: attribute}`) for a metric, a bar or a line, `by` (an attribute) for a bar or a line, `columns` for a table, `limit` (1 to 100, default 10). A dashboard names no `entity` of its own: each widget does, in the view's `model`.
- **links** (any kind, at most 20): `label`, `to` (a view file of the app, `.yaml` or `.yml`, relative, no `..`), `with` (values for the target's parameters) and, in a grid, `row: true` to put the link on every row. A `with` value is text, `$attribute` (an attribute of the row: only a grid row's link, a detail page's or a form's) or `$params.name` (a parameter of this view). A literal that starts with `$` cannot be written.

`schemaVersion` is the format number of `view-spec`, numbered from 1, in the rule of formats of plan 24a: a file in a newer format is refused with `DBD-APP-015`, naming the kind, the file, the app (when read through a manifest) and both numbers (`the app 'sales' holds the view-spec 'views/grid.yaml', which is in format 2, and this program reads format 1 of view-spec`), and a missing or non-numeric one is refused the same way. `AppInstanceStore.PublishApp` runs the same check over the package's files, so a version whose views do not read is never stored. Format 1 is open (unreleased); once a release holds it, a change is a new number and N-1 stays readable. The id of the format is `ViewSpec.FormatId` (`view-spec/1`) and the JSON Schema of it is `schemas/view-spec.schema.json`, an embedded resource (`ViewSpecLoader.Schema()`); a test keeps its keys, choices and limits equal to the loader's. The loader is the authority: the schema cannot say what the next paragraph checks.

Codes, in the manifest's series: `DBD-APP-014` the shape or a value of a spec, `-015` its format number, `-016` a model or entity the manifest does not list for the view (or a form that needs `write` where the manifest asks only `read`), `-017` an attribute the model does not have, `-018` a link that does not lead to a view named under `views`, that gives a parameter the target does not take, omits one it requires, or gives a literal of the wrong type. A missing or unknown key is `DBD-105` and `DBD-104` as everywhere.

`AppChecks.ViewSpecs(manifest, read, modelAttributes?)` reads every spec the manifest names and checks it against the manifest (the models and entities, the operations, the links and the target's parameters) and, when `modelAttributes` is given (model, entity, then the attribute names, from the Store's schema), every attribute a view names: a column, filter, sort key, field, section attribute, widget measure, `by` or column, and a `$attribute` in a link. `AppValidator.Validate(..., modelAttributes: ...)` calls it, so `app validate` reports `views/grid.yaml:11:20: error DBD-APP-017: The view names `territory.regoin`, which the model `territories` does not have.` Without `modelAttributes` the attributes cannot be known and are not checked; everything else is.

The apps host renders a spec (plan 26, AP25; [docs/apps.md](../../../docs/apps.md), "Apps of views only"): `ViewsApp.Json` hands the specs to `Shared/web/view-app`, which draws each kind with `@dbdatakit/store-ui` and `@dbdatakit/visuals` and reads and writes through the app's data route with the operations the manifest declared, drawing a field's error where the Store's validation names it. Not built: a semantic-layer widget (a measure of a semantic model the manifest lists under `semantic`), which stays a further widget kind, an additive key, for the same reason `HomeBlock` leaves room for one; and views mixed with code pages in one app (plan 22a GP34).

`AppValidator.Validate(sourceDirectory, bundleDirectory?, modelEntities?, manifestFile, modelAttributes?)` is the whole of `app validate` as one call: the manifest text, the files it names (a portal's home, logo and favicon too), the view specs (`view-spec/1`), the models and entities it uses against a Store's schema (`modelEntities`, from the Store's schema endpoint; skipped when null), and, for a built bundle, root-relative paths (error) and addresses of other hosts the manifest did not ask for (warning `DBD-APP-010`). `AppValidation.Render()` prints one `file:line:column: severity CODE: message` line per finding and a summary; `Ok` is true when there are no errors. A command line, the apps host and the publish step each call this one function.
