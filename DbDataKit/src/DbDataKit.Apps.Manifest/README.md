# DbDataKit.Apps.Manifest

The `app/1` manifest of an app (plan 26) and the checks `app validate` makes. `AppManifestLoader.Load(yaml, file)` reads it strictly: every problem is a diagnostic with the file, line and column, and nothing throws on a damaged file.

```yaml
schemaVersion: 1
name: sales-planning        # lower-case letters, digits, '-' and '_'; becomes the instance's default address
title: Sales planning
version: 1.2.0
entry: src/main.ts          # a code app's entry point (built by DbDataKit.Apps.Bundling)
views: [views/home.yaml]    # declarative view specs; an app has an entry, views, or both
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

## The ban on outside requests

`EgressPolicy.Build(effective, sandboxed, frameAncestors)` makes the `Content-Security-Policy` the host sends with every document of an app: `default-src 'self'`, `object-src 'none'`, `base-uri 'self'`, `frame-src 'none'`, and the origins in force added to the directive they were asked for (`connect-src` covers fetch, XHR, WebSocket and CORS; `script-src` covers external scripts and JSONP). `EgressPolicy.Effective(requested, approved)` keeps what was both asked for and approved, `Unapproved` lists the rest, and `Covers` is the match (the same origin, or a host under an approved `*.host`). `AppChecks.OutsideUrls(bundleFolder, egress)` lists the addresses of other hosts in a built bundle that the manifest did not ask for: advice for the author, since the browser enforces the policy. A content security policy stops accidents and the ordinary routes for sending data out; it is not a barrier against hostile code on the host's origin (plan 26 section 2b).

`AppApprovals` holds the rules for a manifest that asks `trust: trusted` (plan 26 section 2a). `Approve` makes an `AppApproval` record (who, when, the bundle's and the manifest's SHA-256, the operations and outside origins approved, a note) and refuses an approver who is the author, an app that did not ask to be trusted, and an approval that would allow more than the manifest asked for. `Gate` is the publish check: a sandboxed app always passes; a trusted one passes only with an approval for those exact hashes (a revoked one, or another app's, does not count), and the refusal names the missing approval. An approval an administrator marked `CarriesOver` also stands for a later version when the caller says the bundle reproduces from the reviewed source, and never for a request for more than was approved. `AppApproval.AuditLabel` is the text a server's audit row carries for a call made through the app. Where approvals are kept, the approver role, and the approval's id on each API call come with the apps host and AP04/AP05.
