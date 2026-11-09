# The app store builder

`node app-store/build.mjs --out <folder> [--include <package>[,<package>...]]` makes the **vendored package store** of the app build (plan 22a GP32, GP33): `<folder>/node_modules` with the default library set and everything it needs at run time, and `<folder>/store.json`, which lists every file with its SHA-256. Run it in `DbDataKit/web` after `npm ci`.

* The default library (importable with nobody's permission): `react`, `react-dom`, `@dbdatakit/store-ui`, `@dbdatakit/visuals`, `@dbdatakit/semantic-client`, `@dbdatakit/view-app`. `@dbdatakit/shell` is not in it (the console's shell, with Monaco).
* `--include` adds packages installed in `DbDataKit/web` (an administrator runs `npm install` there first), with their run-time dependencies. Including a package does not let an app import it: the allow-list (`apps.allowedPackages`) does.
* Files are copied and hashed; nothing is run from a package and no network is used. Workspace packages are copied as source (`src`, `schemas`, no tests).
* `dbdatagateway apps store-verify --store <folder>` checks a store against its `store.json`; `dbdatagateway apps build --store <folder>` builds from it. See [docs/apps.md](../../../docs/apps.md#offline-builds-the-package-store-and-the-allow-list).
