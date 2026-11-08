# Sample apps

Two small apps to publish while you try app publishing end to end ([docs/apps-testing.md](../../../docs/apps-testing.md) is the script; [docs/apps.md](../../../docs/apps.md) is the design).

| Folder | Kind | What it shows |
| --- | --- | --- |
| `territory-notes/` | a code app: `index.html`, `app.js`, `app.css`, no build step | a sandboxed document: a token from the host's shell over `postMessage`, data calls to the Store model `territories` through the host (`/apps/<instance>/_data/...`), app storage, and no outside request |
| `front-portal/` | a declared portal (`kind: portal`): data the host renders, no script | a home page from `views/home.yaml` (text, a link, a tile for each mounted app the person may open), a menu and a mount of the instance `notes` |
| `territories.yaml` | a Store `model/1` | the model the code app uses; `storage: {connection: mdm}` is the connection the quickstart's Store has |

`publish-samples.sh` runs `apps validate`, `apps publish`, `apps instance-save` and `apps nav-save` for both. The code app has no build step on purpose, so a tester needs nothing but the Proxy; an app with a source tree is built by
`DbDataKit.Apps.Bundling` (a library: no command line yet, see plan 26 AP23).

An app with no entry and only `views/*.yaml` of `view-spec/1` (grids, forms, detail pages) is not here: the format is validated but no host renders it yet.
