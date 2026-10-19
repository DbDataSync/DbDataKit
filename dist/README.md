# Standard distribution

Every product is distributed the same way, so there is one baseline to maintain and one thing to learn. A product may add routes of its
own (DbDataSync is also a .NET tool package), but these always exist.

| | |
|---|---|
| **Package** | `<product>-v<version>-<rid>.zip`, the program files at the zip root (no wrapping folder), one per platform (`win-x64`, `linux-x64`) |
| **Checksums** | `SHA256SUMS` on the release, listing every zip and the installer |
| **Installer** | `<product>-v<version>-win-x64.msi`: double-click to install (Windows) |
| **Scoop** | `<product>.json`, a manifest pinned to one release (Windows) |
| **PowerShell** | `install.ps1`: Windows PowerShell 5.1 and PowerShell 7 |
| **bash** | `install.sh`: Linux and macOS |

Releases are tagged `<product>/v<version>` in this repository (`<version>` is CalVer `YYYY.M.D.HHmm`, `-beta` for a prerelease).

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

A prerelease attaches its own `install.ps1` / `install.sh` to its release and leaves the fixed release alone. To pass options through a pipe:
`... | bash -s -- --version 2026.10.19.1200` and, in PowerShell,
`& ([scriptblock]::Create((irm <url>))) -Version 2026.10.19.1200`.

## What the installers do

Both find the newest **stable** release whose tag starts with `<product>/v` (GitHub's own "latest" is one marker for the whole repository
and is not used), download that platform's zip and `SHA256SUMS`, refuse to continue on a mismatch, replace the program files, and keep any
file the product lists as `preserve` (none today: DbDataProxy keeps its settings in `%ProgramData%`, away from the program files). Installing again is how you upgrade.

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

A product with a Windows service (`service` in `dist.json`: DbDataSync's `DbDataSync`, DbDataProxy's `DbDataProxy`) is handled for you: a running
service is stopped before the files are replaced and started again afterwards, including when the copy fails, and `-InstallService` registers
it (and implies `-Machine`, since a service belongs in Program Files). A service that was stopped stays stopped.

Defaults: per-user (`%LOCALAPPDATA%\Programs\<Name>` + user PATH; `~/.local/share/<product>` + a symlink in `~/.local/bin`), unless the
product's `windowsScope` is `machine` (`%ProgramFiles%`, elevated), as for a product that is a Windows service.

## Adding or changing a product

Edit `<Product>/dist.json`:

| key | |
|---|---|
| `product`, `displayName`, `description`, `homepage`, `license` | identity; `product` is the tag prefix |
| `platforms` | `{ "<rid>": { "exe": "<file in the zip>" } }`. `win-*` feed install.ps1 and Scoop, the rest install.sh |
| `windowsScope` | `user` or `machine` |
| `preserve` | files an upgrade never overwrites |
| `service` | `{ "name", "register" }`: the Windows service, and the arguments that register it (`{name}` is substituted) |
| `requiresDotnet` | warns after install when that runtime is missing |
| `msi.upgradeCode` | the installer's fixed GUID, **never changed** (it is how a newer version finds the older one) |
| `msi.addToPath` | the installer puts its folder on the machine PATH |
| `service.unregister` | the command the installer runs on removal (with `register`, `{name}` is substituted) |
| `requiresVcRuntime` | the Windows program needs the Visual C++ Redistributable: `install.ps1` warns when `vcruntime140.dll` is missing and the Scoop manifest suggests `extras/vcredist2022` |
| `notes` | printed after install and shown by Scoop |

The release workflow builds the zips and `SHA256SUMS`, then calls `.github/actions/publish-dist`, which runs `dist/dist.py bind` to bake
`dist.json` into copies of the templates here (the installers refuse to run unbound) and uploads them. Tests are in `dist/tests`: they run
both installers against a local fake of the GitHub releases API (`dist-ci.yml`).

If a `<product>/dist` release is deleted or loses its files, run the `dist-republish.yml` workflow for that product
(`gh workflow run dist-republish.yml --ref main -f product=dbdataproxy`): it re-binds the files for the newest stable release and uploads them.

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

* Zip installs of DbDataSync cannot `dbdatasync update` in place (only the tool package can); re-run the installer to upgrade.
* No macOS packages are built yet; `install.sh` already knows `osx-*` and `linux-arm64` and uses them once a product lists them.
