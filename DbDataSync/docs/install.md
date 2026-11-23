# Installing dbdatasync

[DbDataSync](../README.md) · **Install** · [Configuration](configuration.md) · [Getting started](getting-started.md) · [Replication concepts](replication-concepts.md) · [Drivers and libraries](drivers-and-libraries.md) · [Building from source](development.md)

`dbdatasync` is a command line on a configuration repository and the engine a Runner runs. The Sync server was deleted (plan 33 SC23): there is no
service, no Windows installer and no container image of Sync any more. The install of the suite is four services (the dbdatakit console, the Workspace,
the Gateway and the Runner); a Runner runs the replications, with the engine installed on it as the package `dbdatasync-runtime`.

## Prerequisites

- **.NET 10 runtime**, to run `dbdatasync` from a zip.
- **.NET SDK**, only to restore driver libraries (`dbdatasync library install`, `driver install` and `package publish`, which restore them with `dotnet publish`).

## Zip install (install script or Scoop)

Every release carries `dbdatasync-v<version>-<win-x64|linux-x64>.zip` and the repository's standard install scripts
([`dist/README.md`](https://github.com/DbDataSync/DbDataKit/blob/main/dist/README.md)). Every installer checks the zip against the release's `SHA256SUMS`.

```powershell
irm https://github.com/DbDataSync/DbDataKit/releases/download/dbdatasync/dist/install.ps1 | iex
scoop install https://github.com/DbDataSync/DbDataKit/releases/download/dbdatasync/dist/dbdatasync.json
```

```bash
curl -fsSL https://github.com/DbDataSync/DbDataKit/releases/download/dbdatasync/dist/install.sh | bash
```

This installs into your own profile (`%LOCALAPPDATA%\Programs\DbDataSync`, `~/.local/share/dbdatasync` with a link in `~/.local/bin`) and puts it on your
`PATH` (open a new terminal). `-Version` / `--version` picks a release other than the newest stable one.

## Machine-wide

To make `dbdatasync` available to every user of a machine, unpack the zip into `$env:ProgramFiles\DbDataSync` on Windows or `/opt/dbdatasync` on Linux
(`/usr/local/dbdatasync` on macOS) and run `dbdatasync tool install` from an elevated prompt (or with `sudo`): it puts that folder on the machine `PATH`
(a `/usr/local/bin` link, an `/etc/paths.d` entry on macOS, the machine `PATH` on Windows). `dbdatasync tool uninstall` takes it off again.

## Updating

Install the newer release the same way you installed this one: the install script again (or with `-Version`), `scoop update`, or an unpacked zip over the
old files. The engine a Runner runs is updated by installing a newer `dbdatasync-runtime` package on the Runner.

## Running replications

`dbdatasync` runs no replication itself, and it does not send anything to a Runner. Author the replications as a sync project in the Workspace and
publish it from there: the console deploys the package to a Runner, which runs the replications on their schedules. Each slot of the package is a
connection of the set, bound on the Runner to a connection it holds. See [Getting started](getting-started.md).

The command line makes the package and says what it holds, without sending it:

```sh
dbdatasync package publish --to runner --check
dbdatasync package publish --to runner --out <directory>
```

`--server` and `--code`, which sent the package to a Runner, exit with code 2 and say that the console deploys.

## Next: Configuration

See [Configuration](configuration.md) for what configures the command line and the engine.
