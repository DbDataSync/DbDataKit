# Standard distribution

Every product is distributed the same way, so there is one baseline to maintain and one thing to learn. A product may add routes of its
own (DbDataSync is also a .NET tool package), but these always exist.

| | |
|---|---|
| **Package** | `<product>-v<version>-<rid>.zip`, the program files at the zip root (no wrapping folder), one per platform (`win-x64`, `linux-x64`) |
| **Checksums** | `SHA256SUMS` on the release, listing every zip |
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
file the product lists as `preserve` (`appsettings.json` for the proxy). Installing again is how you upgrade.

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
| `notes` | printed after install and shown by Scoop |

The release workflow builds the zips and `SHA256SUMS`, then calls `.github/actions/publish-dist`, which runs `dist/dist.py bind` to bake
`dist.json` into copies of the templates here (the installers refuse to run unbound) and uploads them. Tests are in `dist/tests`: they run
both installers against a local fake of the GitHub releases API (`dist-ci.yml`).

## Not covered

* Zip installs of DbDataSync cannot `dbdatasync update` in place (only the tool package can); re-run the installer to upgrade.
* No macOS packages are built yet; `install.sh` already knows `osx-*` and `linux-arm64` and uses them once a product lists them.
