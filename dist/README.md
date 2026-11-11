# Standard distribution

Every product is distributed the same way, so there is one baseline to maintain and one thing to learn. A product may add routes of its
own, but these always exist. There is no .NET tool package for any product (DbDataSync's was dropped on 2026-11-08), and NuGet publishing is paused.

| | |
|---|---|
| **Package** | `<product>-v<version>-<rid>.zip`, the program files at the zip root (no wrapping folder), one per platform (`win-x64`, `linux-x64`) |
| **Checksums** | `SHA256SUMS` on the release, listing every zip and the installer |
| **Installer** | `<product>-v<version>-win-x64.msi`: double-click to install (Windows) |
| **Scoop** | `<product>.json`, a manifest pinned to one release (Windows) |
| **PowerShell** | `install.ps1`: Windows PowerShell 5.1 and PowerShell 7 |
| **bash** | `install.sh`: Linux and macOS |

The products are `dbdatabuild`, `dbdatasync`, `dbdatagateway` and the console, `dbdatakit` (`DbDataKit/dist.json`: the zip needs the .NET 10 runtime, the MSI registers the `DbDataKitConsole` service stopped, and its program folder is `DbDataKit`). The Gateway ships two zips (plan 33 SC01): **win-x64** (`DbDataGateway/build/package.ps1`: self-contained, with the 32-bit ODBC worker; it feeds the MSI, Scoop and `install.ps1`) and **linux-x64** (`DbDataGateway/scripts/package.sh`: framework-dependent like the Runner's, so it needs the .NET 10 runtime, `requiresDotnet` on that platform in `DbDataGateway/dist.json`; it carries the DuckDB engine `libduckdb.so` and the views runtime and **no worker**; `install.sh` installs it and `dbdatagateway service unit` prints the systemd unit). `dbdatagateway-release.yml` builds the Windows zip on a Windows runner and the Linux one on Ubuntu, and one release holds both under one `SHA256SUMS`. **Windows-only on the Gateway:** Analysis Services (`ssas`) sources, Windows password sign-in (`Auth: { windows: true }`) and `Bitness: x86` ODBC sources; a Linux server refuses a configuration that asks for any of them at start, naming the setting. Everything else, ODBC drivers installed on the machine aside, runs on both. The runner, `dbdatarunner`, is described the same way (`DbDataRunner/dist.json`: the .NET 10 runtime, the `DbDataRunner` service, the task host in a `taskhost` folder beside the program); its zips (win-x64 and linux-x64, `DbDataRunner/scripts/package.sh`) and MSI come from `dbdatarunner-release.yml`, which `suite-release.yml` calls. The Store, `dbdatastore` (`DbDataStore/dist.json`, the `DbDataStore` service) and the Workspace server, `dbdataworkspace` (`DbDataWorkspace/dist.json`, the `DbDataWorkspace` service; its notes say a Workspace with no `apps.packageStore` and bun does not build apps, neither is bundled) are packaged the same way (`dbdatastore-release.yml`, `dbdataworkspace-release.yml`, plan 33 SC16 and SC17). Each has its own MSI: a product's MSI does not embed the console. To pair them after installing both, make a code on the product (`<program> server pair`) and run `dbdatakit instance add <https address> --code <code>`; `dist-msi-ci.yml` does that on a Windows runner (`dist/tests/msi-pairing-e2e.ps1`: the console's MSI installed, a Build server built from the same commit paired with it).

Releases are tagged `<product>/v<version>` in this repository (`<version>` is CalVer `YYYY.M.D.HHmm`; `-beta` is a beta and `-snapshot.g<sha>` a snapshot, both prereleases).

## Stable URLs

A stable release replaces the assets of one fixed prerelease per product, `<product>/dist`, so these never change:

```
https://github.com/DbDataSync/DbDataKit/releases/download/<product>/dist/install.ps1
https://github.com/DbDataSync/DbDataKit/releases/download/<product>/dist/install.sh
https://github.com/DbDataSync/DbDataKit/releases/download/<product>/dist/<product>.json
```

```powershell
irm https://github.com/DbDataSync/DbDataKit/releases/download/dbdatabuild/dist/install.ps1 | iex
scoop install https://github.com/DbDataSync/DbDataKit/releases/download/dbdatabuild/dist/dbdatabuild.json
```

```bash
curl -fsSL https://github.com/DbDataSync/DbDataKit/releases/download/dbdatabuild/dist/install.sh | bash
```

### Beta and snapshot

Each channel has its own fixed prerelease per product, replaced by every release of that channel, so "the newest beta" and "the newest snapshot" have URLs that never change either. They carry the Scoop manifest and the Windows
installer only: `install.ps1` and `install.sh` look for the newest *stable* release, so install a beta or a snapshot with `-Version` / `--version` or with Scoop.

```
https://github.com/DbDataSync/DbDataKit/releases/download/<product>/dist-beta/<product>.json          the newest beta
https://github.com/DbDataSync/DbDataKit/releases/download/<product>/dist-beta/<product>.msi
https://github.com/DbDataSync/DbDataKit/releases/download/<product>/dist-snapshot/<product>.json      the newest snapshot
https://github.com/DbDataSync/DbDataKit/releases/download/<product>/dist-snapshot/<product>.msi
```

```powershell
scoop install https://github.com/DbDataSync/DbDataKit/releases/download/dbdatasync/dist-snapshot/dbdatasync.json
```

Every manifest pins one release's zip and its SHA256. Every release, whatever its channel, also carries its own copy of `install.ps1`, `install.sh` and `<product>.json`, so a particular build can always be installed by its own URL:
`.../releases/download/<product>/v<version>/<product>.json`.

A **snapshot** is cut from any branch or commit (`suite-release.yml` or a product's release workflow, dispatched against that branch with `-f snapshot=true`; `-f sha=<commit>` picks a commit of it). It skips every guard of a
release: the commit need not be on `main`, need not have a green CI run, and Build's full conformance suite is not run. The version is `YYYY.M.D.HHmm-snapshot.g<first seven characters of the commit>`, so two snapshots never share a
version and the version names the commit. A suite snapshot builds no container images. It is always a prerelease, and it never touches the stable `<product>/dist` release.

To pass options through a pipe:
`... | bash -s -- --version 2026.10.19.1200` and, in PowerShell,
`& ([scriptblock]::Create((irm <url>))) -Version 2026.10.19.1200`.

## What the installers do

Both find the newest **stable** release whose tag starts with `<product>/v` (GitHub's own "latest" is one marker for the whole repository
and is not used), download that platform's zip and `SHA256SUMS`, refuse to continue on a mismatch, replace the program files, and keep any
file the product lists as `preserve` (none today: DbDataGateway keeps its settings in `%ProgramData%`, away from the program files). Installing again is how you upgrade.

| | `install.ps1` | `install.sh` |
|---|---|---|
| a specific version | `-Version 2026.10.19.1200` | `--version 2026.10.19.1200` |
| allow a prerelease as "latest" | `-IncludePrerelease` | `--prerelease` |
| show what would happen | `-DryRun` | `--dry-run` |
| install for all users | `-Machine` (elevated) | `--system` (root) |
| install directory | `-InstallDir` | `--install-dir` |
| leave PATH alone | `-NoPath` | `--no-link` |
| register the Windows service | `-InstallService` | n/a |
| a fork or mirror | `-Repo`, `-ApiBase` | `--repo`, `--api-base` |

A product with a Windows service (`service` in `dist.json`: DbDataSync's `DbDataSync`, DbDataGateway's `DbDataGateway`) is handled for you: a running
service is stopped before the files are replaced and started again afterwards, including when the copy fails, and `-InstallService` registers
it (and implies `-Machine`, since a service belongs in Program Files). A service that was stopped stays stopped.

Defaults: per-user (`%LOCALAPPDATA%\Programs\<Name>` + user PATH; `~/.local/share/<product>` + a symlink in `~/.local/bin`), unless the
product's `windowsScope` is `machine` (`%ProgramFiles%`, elevated), as for a product that is a Windows service.

## One package for a single server

`dbdatakit-full` is one Scoop manifest that installs every member product of a suite release on one machine (plan 33 section 6a). The members are listed in start order in `dist/suite.json`, each with its `dist.json` and its folder; `dist.py bind --suite dist/suite.json` writes `dbdatakit-full.json` from them. Every member of a suite release has the same version, so the manifest pins one version of all of them: `url` and `hash` are lists of each member's win-x64 zip, `extract_to` a list of folders and `bin` a list of `[<folder>\<exe>, <alias>]` pairs, so every program is on the PATH.

| member | folder | program |
| --- | --- | --- |
| `dbdatakit` (the console) | `console` | `dbdatakit` |
| `dbdatagateway` | `gateway` | `dbdatagateway` |
| `dbdatarunner` | `runner` | `dbdatarunner` |
| `dbdatasync` | `sync` | `dbdatasync` |
| `dbdatabuild` | `build` | `dbdatabuild` |
| `dbdatastore` | `store` | `dbdatastore` |
| `dbdataworkspace` | `workspace` | `dbdataworkspace` |

```powershell
scoop install -g https://github.com/<owner>/DbDataKit/releases/download/dbdatakit-full/dist/dbdatakit-full.json
```

The fixed releases are `dbdatakit-full/dist`, `dbdatakit-full/dist-beta` and `dbdatakit-full/dist-snapshot` (the channel is the version's suffix, as for the products), each holding only `dbdatakit-full.json`; `.github/actions/publish-suite-dist` publishes it.

**This first release only installs programs.** The manifest has no installer script and its only `post_install` and `pre_uninstall` set the machine variables each member's own manifest sets, once each however many members name it (`globalEnv`, global install only, so a command run by hand reads the folder the service uses), and remove all of them except `DBDATAKIT_DATA` (see `globalEnv`): no service is registered, nothing is started and nothing is paired. Its notes list, in member order, what the administrator runs by hand afterwards: `<program> service install` for each member whose `dist.json` has a `service`, starting the console, then `<program> server pair --max-role <roles>` on each other member and `dbdatakit instance add <https address> --code <code>`, with each member's default management address in the note (the Gateway's is `https://localhost:8816`: its management API is on by default and reachable from the machine only; a member whose default the notes cannot name, Sync, stays generic). To remove it, `scoop uninstall -g dbdatakit-full` removes the files; unregister the services first with `<program> service uninstall`.

Use **one route per machine**: the package installs under Scoop's folder, while a product's own MSI, `install.ps1` or manifest uses its own and registers the same service names. A member that folds into another server leaves `dist/suite.json` in the same commit.

## Adding or changing a product

Edit `<Product>/dist.json`:

| key | |
|---|---|
| `product`, `displayName`, `description`, `homepage`, `license` | identity; `product` is the tag prefix |
| `platforms` | `{ "<rid>": { "exe": "<file in the zip>" } }`. `win-*` feed install.ps1 and Scoop, the rest install.sh |
| `windowsScope` | `user` or `machine` |
| `preserve` | files an upgrade never overwrites |
| `service` | `{ "name", "register" }`: the Windows service, and the arguments that register it (`{name}` is substituted) |
| `requiresDotnet` | warns after install when that runtime is missing. A platform may carry its own (`"linux-x64": { "exe": "...", "requiresDotnet": "10.0" }`) for a product whose Windows zip is self-contained and whose Linux zip is not (the Gateway); `install.sh` reads it, the product-level one wins, and install.ps1, Scoop and the MSI read only the product-level one |
| `msi.upgradeCode` | the installer's fixed GUID, **never changed** (it is how a newer version finds the older one) |
| `msi.addToPath` | the installer puts its folder on the machine PATH |
| `service.unregister` | the command the installer runs on removal (with `register`, `{name}` is substituted) |
| `requiresVcRuntime` | the Windows program needs the Visual C++ Redistributable: `install.ps1` warns when `vcruntime140.dll` is missing and the Scoop manifest suggests `extras/vcredist2022` |
| `notes` | printed after install and shown by Scoop |
| `globalEnv` | `[ { "name": "<VARIABLE>", "programData": "<folder under %ProgramData%>" } ]`: the Scoop manifest sets these **machine** variables after `scoop install -g` and removes them (only while they still hold that value) on uninstall; a per-user install sets nothing. **`DBDATAKIT_DATA` is the exception (plan 33 SC31):** every product's manifest sets it to the same `%ProgramData%\DbDataKit`, the root under which each service keeps a folder named for it (`server`, `gateway`, `runner`, `sync`, `build`, `store`, `workspace`), so an uninstall **never removes it**: another product still installed reads it, and the manifest's notes say it is left for the administrator to remove with the last product (a manifest whose only variable is `DBDATAKIT_DATA` has no `pre_uninstall` at all; the suite manifest emits it once). The folder must be the one the product defaults to for a service (`dist/tests` checks it against the source) |

**State settings.** No installer writes state settings: every product's default, a SQLite file in its own directory, is the `service` topology of plan 29 section 5 and needs none, so a fresh install of any package works as it is (the install tests of this folder start the product with no `state` section). An administrator who wants one database for a group or for the whole install runs the product's own verb after installing: `<program> state init --topology group|install --engine mssql|postgres --connection "<connection with Password=secret:NAME>" --login <role> [--admin-connection <reference> --apply]` (`<program> state init --topology service` returns to the default and writes nothing when it already is). `<program>` is `dbdatakit`, `dbdatagateway`, `dbdatabuild` or `dbdatastore`; DbDataSync has no `state` noun yet. Each package's `notes` say so, and `scripts/check_dist_state.py` fails when a product that has the verb has no such note. Neither the MSI nor Scoop runs the verb: they would need a database, an administrator's credentials and a secret, none of which an installer has.

The release workflow builds the zips and `SHA256SUMS`, then calls `.github/actions/publish-dist`, which runs `dist/dist.py bind` to bake
`dist.json` into copies of the templates here (the installers refuse to run unbound) and uploads them. Tests are in `dist/tests`: they run
both installers against a local fake of the GitHub releases API (`dist-ci.yml`).

`suite-release.yml` is the one release of everything from one commit: it builds the console's zips, MSI and image, calls the six product release workflows (build, gateway, runner, store, workspace, sync), and builds and pushes the images (`ghcr.io/<owner>/dbdatakit|dbdatabuild|dbdatagateway`; DbDataSync's image keeps `dbdatasync-publish-image.yml`). `-f dry_run=true` builds all of it, keeps each product's files as the run's `release-<product>` artifacts, checks every one has its zips, MSI and a verifying `SHA256SUMS`, and tags and pushes nothing. Every product of one suite release gets the same version string (the product workflows take it as their `version` input). Each product workflow still releases on its own.

If a `<product>/dist` release is deleted or loses its files, run the `dist-republish.yml` workflow for that product
(`gh workflow run dist-republish.yml --ref main -f product=dbdatagateway`): it re-binds the files for the newest stable release and uploads them. `-f product=dbdatakit-full` binds the single-server Scoop manifest again with the generator of the ref it runs on, for the suite version in `-f version=...` (any channel; empty means the newest stable) from each member's release, and replaces the asset of that channel's fixed `dbdatakit-full` release: for a changed manifest without a new release of every product.

## The Windows installer (MSI)

A single file to copy to a machine and double-click (or `msiexec /i <file>.msi /qn` for a silent install). It copies the package to
`%ProgramFiles%\<Name>`, and for a product with a Windows service it runs the product's own service install (the service is registered
stopped, so it can be configured first). Stable releases also keep the newest one at
`https://github.com/DbDataSync/DbDataKit/releases/download/<product>/dist/<product>.msi`.

* **Upgrading** is installing a newer MSI over the old one: the service is stopped for the file copy, the registration is kept, and the service
  is started again. Installing an older one over a newer one is refused. Settings and data (`%ProgramData%`) are never touched.
* **Removing** is Add/Remove Programs (or `msiexec /x`): the program folder, the service registration and the PATH entry go; settings and data stay.
* **Properties:** `INSTALLSERVICE=0` installs the files and registers no service. `SKIPDOTNETCHECK=1` skips DbDataSync's check for the .NET 10 runtime.
* **PATH:** DbDataBuild and DbDataSync put their folder on the machine PATH (open a new terminal).
* **Built with wixl** (msitools, LGPL; `apt install wixl`), not the WiX toolset, by `dist/msi.py` from `dist.json`. `dist-msi-ci.yml` installs, upgrades,
  refuses a downgrade and removes each product's MSI on a Windows runner, so what is described here is what ran there.
* **Unsigned** today, so Windows shows "Unknown publisher" and SmartScreen may warn. Signing needs a certificate.
* **With the other routes:** all of them install to the same folder, so use one per machine. `install.ps1` leaves no Add/Remove Programs entry;
  installing the MSI over it is fine.
* **Version:** an MSI version has three fields, so CalVer is shown as `YY.M.(day*1440+minute)` (2026.10.19.509 is 26.10.27669). It sorts in release
  order; the real version is in the entry's comments.

## Not covered

* DbDataSync's `dbdatasync update` cannot update any of these installs, and releases carry no `.nupkg` for it to read; upgrade by installing the newer release (MSI over the old one, the installer script again, `scoop update`, or a newer image tag).
* No macOS packages are built yet; `install.sh` already knows `osx-*` and `linux-arm64` and uses them once a product lists them.
