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

The products are `dbdatabuild` (`DbDataBuild/dist.json`: the self-contained zip, the `DbDataBuild` service, machine scope; the service runs `server run` over `%ProgramData%\DbDataKit\build` and hosts no project until `server project add`), `dbdatasync`, `dbdatagateway` and the console, `dbdatakit` (`DbDataKit/dist.json`: the zip needs the .NET 10 runtime, the MSI registers the `DbDataKit` service stopped, and its program folder is `DbDataKit`). The Gateway ships two zips (plan 33 SC01): **win-x64** (`DbDataGateway/build/package.ps1`: self-contained, with the 32-bit ODBC worker; it feeds the MSI, Scoop and `install.ps1`) and **linux-x64** (`DbDataGateway/scripts/package.sh`: framework-dependent like the Runner's, so it needs the .NET 10 runtime, `requiresDotnet` on that platform in `DbDataGateway/dist.json`; it carries the DuckDB engine `libduckdb.so` and the views runtime and **no worker**; `install.sh` installs it and `dbdatagateway service unit` prints the systemd unit). `dbdatagateway-release.yml` builds the Windows zip on a Windows runner and the Linux one on Ubuntu, and one release holds both under one `SHA256SUMS`. **Windows-only on the Gateway:** Analysis Services (`ssas`) sources, Windows password sign-in (`Auth: { windows: true }`) and `Bitness: x86` ODBC sources; a Linux server refuses a configuration that asks for any of them at start, naming the setting. Everything else, ODBC drivers installed on the machine aside, runs on both. The runner, `dbdatarunner`, is described the same way (`DbDataRunner/dist.json`: the .NET 10 runtime, the `DbDataRunner` service, the task host in a `taskhost` folder beside the program); its zips (win-x64 and linux-x64, `DbDataRunner/scripts/package.sh`) and MSI come from `dbdatarunner-release.yml`, which `suite-release.yml` calls. The Store, `dbdatastore` (`DbDataStore/dist.json`, the `DbDataStore` service) and the Workspace server, `dbdataworkspace` (`DbDataWorkspace/dist.json`, the `DbDataWorkspace` service; its notes say a Workspace with no `apps.packageStore` and bun does not build apps, neither is bundled) are packaged the same way (`dbdatastore-release.yml`, `dbdataworkspace-release.yml`, plan 33 SC16 and SC17). Each has its own MSI: a product's MSI does not embed the console. To pair them after installing both, make a code on the product (`<program> server pair`) and run `dbdatakit instance add <https address> --code <code>`; `dist-msi-ci.yml` does that on a Windows runner (`dist/tests/msi-pairing-e2e.ps1`: the console's MSI installed, a Build server built from the same commit paired with it).

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

## The export component

A host that exports PDFs (the Gateway, which hosts the views module and the subscription task) needs the pinned Chromium, the bundled fonts and the print page. They are not in any product zip (the browser alone is about 420 MB installed): `dist/export_component.py stage` builds the folder from a checkout and `verify` checks it against `chromium.pin`, `fonts.pin` and its own manifest (`dist/tests/test_export_component.py`); `dist/export/Dockerfile` is the same folder as an image layer over the Gateway's image. See `docs/views.md`, "The export component". Not yet in a release workflow.

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
| register the Windows service | `-InstallService` | n/a (the unit is printed by `<program> service unit`) |
| join a console by announcement | `-AnnounceUrl <url>` | `--announce-url <url>`; with `--system` (root) it writes the systemd drop-in `/etc/systemd/system/<product>.service.d/announce.conf` (`--systemd-dir <d>` for another place) |
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

**What the install does** (`dist/suite-scripts.ps1`, bound by `dist.py` into the manifest's `post_install` and `pre_uninstall`; plan 33 SC12 and SC13). Only `scoop install -g` from an elevated prompt: a per-user install is refused with a message that says to use `-g`, and so is an install while a member's service is registered from another folder (a product's own MSI, `install.ps1` or manifest). Nothing is changed before those checks pass. Then:

1. the machine variables of `globalEnv` are set (as before), so a command run by hand reads the folder the services use;
2. the program files of this version are copied to `%ProgramData%\DbDataKit\bin\<version>` (Scoop refuses to update an app while a process runs from its folder, so no service runs from there), then each member's `service install` from that copy (registered stopped, the same as the MSI), in the order of `dist/suite.json`; a service registered from another version's copy is stopped, unregistered and registered again from this one, with the same data;
3. the console is started and its health check awaited;
4. for each other member: `<program> server pair --max-role <roles>` prints a single-use code, the member's service is started, `dbdatakit instance add <default address> --code <code> --name <product>` uses the code, and nothing else sees it: it is read into a variable, never written to a file, the log or the notes, and scrubbed from an error message. This is not auto-approval: the person who runs `scoop install -g` is the elevated administrator who would have typed those commands. A member with no default address (`_MANAGEMENT_ADDRESS` in `dist.py`) is registered and left unpaired;
5. it says where the console is and **prints the setup link** for the first administrator (read from `FIRST-RUN.txt` in `%ProgramData%\DbDataKit\server`, where the console wrote it whole; plan 23a FQ19), and names `dbdatakit setup link` for a new one; a console that has a user already made no link and the script says so. No state settings are written.

It is idempotent: a registered service is not registered again, a member the console already lists (`instance list --offline`) is not paired again, and a service that runs is not started again, so a repeat after a failure converges. A failure at one member is collected, the rest go on, and the install ends with an error naming each member and the reason; the notes of the manifest list the commands by hand for every step.

**Uninstall and update.** `scoop uninstall -g` goes through the members in reverse order: `instance remove <product>` in the console, `server unpair` of the console's pairing on the member, stop, `service uninstall`; a step that fails is a warning and the next member goes on. The machine variables are removed as before (all but `DBDATAKIT_DATA`). Settings and data under `%ProgramData%\DbDataKit` stay; a purge is the administrator's, by hand. `scoop update` only stops the services that run from the old version's copy; the script tells the two apart by Scoop's `$cmd` (`uninstall` or `update`) and, when `$cmd` says anything else or nothing (it was never seen from a `pre_uninstall` script), by the app's folder: an update has already extracted a newer version beside this one (compared by the version in the folder name, or by creation time when a name is no version), a real uninstall has none, and an older version Scoop kept does not make an uninstall an update; the new version's `post_install` copies its files, re-registers the services from the copy (they are paired already), starts them and removes the old copy. `scoop uninstall -g` also removes the copy of that version.

The job `suite-install-windows` of `suite-release.yml` (a dry run) runs `dist/tests/suite-scoop-e2e.ps1` on a Windows runner: it installs Scoop, serves the dry run's zips from the machine (`dist.py bind --suite --base-url`), checks a per-user install and an install over another route's service are refused, installs globally, checks every service runs and answers its health check and the console lists each member as ready, runs the install script again, updates to a second version and uninstalls.

Use **one route per machine**: the package installs under Scoop's folder, while a product's own MSI, `install.ps1` or manifest uses its own and registers the same service names. A member that folds into another server leaves `dist/suite.json` in the same commit.

### The whole install on one server, one command

The commands the script runs, one by one, for a machine that runs only the Console, the Gateway and the Runner (or to finish a step by hand), are in `docs/suite-single-server-install.md`.

In an elevated PowerShell, with [Scoop](https://scoop.sh) installed:

```powershell
scoop install -g https://github.com/<owner>/DbDataKit/releases/download/dbdatakit-full/dist/dbdatakit-full.json
```

That is all: when it returns, every member's service is registered, running and paired with the console, and the console's address and the setup link for the first administrator are printed (the link is also in `%ProgramData%\DbDataKit\server\FIRST-RUN.txt`; `dbdatakit setup link` makes a new one). Open the link once, register a passkey and the members' screens are there. The install is idempotent, so running it again after a failure (or `scoop update dbdatakit-full`) finishes what is missing.

**What it changes on the machine:**

| where | what |
| --- | --- |
| `%ProgramData%\scoop\apps\dbdatakit-full` | the members' programs, one folder each (`console`, `gateway`, `runner`, ...), where the shims point; no service runs from here |
| `%ProgramData%\DbDataKit\bin\<version>` | the copy of those programs the services run from; replaced by an update, removed by an uninstall |
| the machine `PATH` (Scoop's shims) | `dbdatakit`, `dbdatagateway`, `dbdatarunner`, `dbdatasync`, `dbdatabuild`, `dbdatastore`, `dbdataworkspace` |
| the Service Control Manager | one service per member (`DbDataKit`, `DbDataGateway`, `DbDataRunner`, `DbDataSync`, `DbDataBuild`, `DbDataStore`, `DbDataWorkspace`), set up by the member's own `service install` (its start mode and account) |
| machine variable | `DBDATAKIT_DATA=%ProgramData%\DbDataKit`, under which each service keeps a folder (`server`, `gateway`, `runner`, ...) with its state, certificate and settings |
| the console's and each member's data folder | the pairings (the console pins each member's certificate; each member lists the console as a paired client) |

`scoop uninstall -g dbdatakit-full` removes the pairings and the services and the files; `%ProgramData%\DbDataKit` (settings, data, `DBDATAKIT_DATA`) stays until the administrator deletes it. **One route per machine:** the install is refused when a member's service is already registered from another folder, so a machine that has a product from its own MSI, `install.ps1` or manifest uninstalls it first. For a machine that runs one product, or products that join by announcement (plan 33 SC10), use the product's own route above.

### Joining a console by announcement (separately installed products)

Where products are installed one by one on separate machines, nobody types a pairing code: give each server the console's private announce address and an administrator confirms it. (The single-server case of Console, Gateway and Runner is `docs/suite-single-server-install.md`: the one Scoop command, or the same steps by hand with the commands the install script runs, which pair with codes and need no announce port.)

1. Console: `dbdatakit server run --announce-listen http://<console address>:8481` (its private port; keep it on the network the servers share).
2. Each server: install with the address, `msiexec /i dbdatagateway-v<version>-win-x64.msi ANNOUNCEURL=http://<console address>:8481 /qn` (the MSI sets the machine variable `DBDATAKIT_ANNOUNCE_URL` before it registers the service and refuses an address that is not http or https), or `install.ps1 -AnnounceUrl ...`, or on Linux `sudo install.sh --system --announce-url ...`, which writes the systemd drop-in `<product>.service.d/announce.conf` (`Environment="DBDATAKIT_ANNOUNCE_URL=..."`, a drop-in is read whether or not the unit exists yet, so write the unit with `<program> service unit` before or after) and reloads systemd; without `--system` it only prints the `Environment=` line.
3. Administrator: `dbdatakit instance pending`, then `dbdatakit instance confirm <id or name>` (or the console's screen). A server that announces is **not** paired and not trusted until then; the announcement is never a confirmation. A removal leaves the variable in place, as it leaves the data folders (another product on the machine reads it).

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
| `announces` | `true` for a server that announces itself to a console (every product with a `service` except the console): the MSI takes `ANNOUNCEURL=<console announce address>` and sets the machine variable `DBDATAKIT_ANNOUNCE_URL`, `install.ps1 -AnnounceUrl` sets it for the machine or the user, `install.sh --announce-url` writes the systemd drop-in for the product's unit when run with `--system` as root (the unit is named for the product: `dbdatagateway`, `dbdatarunner`, `dbdatakit`, `dbdatasync`), and prints the line otherwise; the address must be http or https with no white space, quotes, backslash, `%` or `$` |
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
