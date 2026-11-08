# Sample apps

Three small apps to publish while you try app publishing end to end ([docs/apps-testing.md](../../../docs/apps-testing.md) is the script; [docs/apps.md](../../../docs/apps.md) is the design).

| Folder | Kind | What it shows |
| --- | --- | --- |
| `territory-notes/` | a code app: `index.html`, `app.js`, `app.css`, no build step | a sandboxed document: a token from the host's shell over `postMessage`, data calls to the Store model `territories` through the host (`/apps/<instance>/_data/...`), app storage, and no outside request |
| `front-portal/` | a declared portal (`kind: portal`): data the host renders, no script | a home page from `views/home.yaml` (text, a link, a tile for each mounted app the person may open), a menu and a mount of the instance `notes` |
| `territory-views/` | an app of views only (`view-spec/1`, no `entry`, no script) | a grid with a filter and sortable columns, an add form and an edit form (the Store's refusals are drawn against the field), a detail page and a dashboard (two figures, a bar chart, a table) over the model `territories`; the host draws all of it (AP25) |
| `territory-notes-src/` | the same code app as a source tree: `src/main.ts` (TypeScript), `index.html`, `app.css` | what `apps build` takes: it bundles `src/main.ts` into `main.js` (a plain script, which is what the sandboxed page loads) and copies the rest |
| `territories.yaml` | a Store `model/1` | the model the code app uses; `storage: {connection: mdm}` is the connection the quickstart's Store has |

`apps init --template <territory-notes|territory-notes-src|front-portal|territory-views> --dir <new folder> [--name <app>]` copies a sample (the four are inside the program, so no repository or network is needed) and `apps build --src <folder> --out <folder>` builds a source tree; `publish-samples.sh` runs `apps validate`, `apps publish`, `apps instance-save` and `apps nav-save` for the three that need no build (instances `notes`, `front` and `views`). `territory-notes` has no build step on purpose, so a tester needs nothing but the Gateway; `territory-notes-src` is the one to build (needs Bun: `scripts/fetch-bun.sh` fetches the pinned, hash-checked one).

`territory-views` needs the host's views runtime (`apps-views` beside the Gateway, the web build of `DbDataKit/web/view-app`; the Dockerfile and the Windows publish script include it). The instance's people need the `author` role to add or edit and any role to read.
