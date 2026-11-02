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
```

The app's scoped token is the intersection of what the manifest declares and what the signed-in person may do; the servers still check every call (plan 16: the app is untrusted UI).

`AppChecks.Files` reports the entry and view files the manifest names that are missing; `AppChecks.AbsolutePaths` reports built HTML and CSS that point at a path from the server's root, which breaks under a subpath.
