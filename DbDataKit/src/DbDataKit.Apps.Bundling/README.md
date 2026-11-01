# DbDataKit.Apps.Bundling

Builds an app's source into the static bundle the apps host serves (plan 16 section 2, plan 20 `apps`). The server needs the *build*, not an app runtime: app code runs in the browser.

* `IAppBundler.BundleAsync(AppBundleRequest)` answers a result and never throws for a bad source.
* `BunAppBundler` runs `bun install --ignore-scripts` (when the source has dependencies) and `bun build` as a child process with a minimal environment and a timeout. The entry point must be inside the source folder; the bundle has a size limit.
* Another bundler (esbuild, for example) is another `IAppBundler`.
