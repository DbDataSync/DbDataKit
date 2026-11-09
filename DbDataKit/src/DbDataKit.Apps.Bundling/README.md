# DbDataKit.Apps.Bundling

Builds an app's source into the static bundle the apps host serves (plan 16 section 2, plan 20 `apps`). The server needs the *build*, not an app runtime: app code runs in the browser.

* `IAppBundler.BundleAsync(AppBundleRequest)` answers a result and never throws for a bad source.
* `BunAppBundler` runs `bun install --ignore-scripts` (when the source has dependencies) and `bun build` as a child process with a minimal environment and a timeout. The entry point must be inside the source folder; the bundle has a size limit.
* `BunLocator` finds the Bun a build runs: a path the caller names, `DBDATA_BUN`, the pinned one that `scripts/fetch-bun.sh` puts in `.bun/<rid>/` (its hash checked against `bun.pin` on every use), then `bun` on the PATH. When none is found the message names `bun.pin`, its version and the script.
* `AppBundleRequest.Offline` installs nothing: a source whose `package.json` lists packages builds only if its `node_modules` folder is already there, and is refused naming that otherwise. `AppBundleRequest.PackageStore` is the whole of it (plan 22a GP33, QG12).
* `VendoredPackageStore` reads a store (`store.json` and `node_modules`, made by `DbDataKit/web/app-store/build.mjs`) and checks its files against the SHA-256 of each; `AppPackagePolicy` checks a source's imports and `dependencies` against the store's library and the administrator's allow-list, refusing a package by name, file and line. With a store, `BunAppBundler` copies the source and the store into a folder of its own, checks the copy, installs nothing and runs Bun with a cleared environment in a network namespace with no route (`NetworkIsolator`: `unshare --net` on Linux; `NetworkIsolation.Required` refuses a machine that cannot). What that does and does not guarantee is in [docs/apps.md](../../../docs/apps.md#offline-builds-the-package-store-and-the-allow-list).
* `dbdatagateway apps build` is the command line over this library (plan 26 AP23).
* Another bundler (esbuild, for example) is another `IAppBundler`.
