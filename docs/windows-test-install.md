# Trying the 2026.11.6.837 release on a Windows test server

A step-by-step for installing the console (`dbdatakit`) and DbDataSync from the first suite release, on a Windows machine that is not
your live Sync server. It is written from the release's files and the code on `main`; **nothing here has been run on Windows yet**.
Each step marked *untested* is the first time anyone does it, so write down what happens.

The release is `dbdatakit/v2026.11.6.837` and `dbdatasync/v2026.11.6.837` (Proxy and Build have the same version; they are not needed
for this test). Every release file is on <https://github.com/DbDataSync/DbDataKit/releases>.

## 0. What you need

- Windows with an elevated PowerShell (a global Scoop install and a service both need it).
- The **.NET 10 runtime** (ASP.NET Core for the console). The zips do not carry it. Sync also needs the .NET 10 **SDK** if you install
  drivers from the console.
- [Scoop](https://scoop.sh), or use the MSIs in step 1.
- A name for the machine that other machines can use (`test-sync.example.com` below). **Use that name, not `localhost`, in every
  `--url` and `instance add`:** the self-signed certificate Sync makes is issued for the host in its URL.

## 1. Install

Pick one route per machine and do not mix them (they share a folder).

**Scoop, global** (*untested*):

```powershell
scoop install -g https://github.com/DbDataSync/DbDataKit/releases/download/dbdatakit/dist/dbdatakit.json
scoop install -g https://github.com/DbDataSync/DbDataKit/releases/download/dbdatasync/dist/dbdatasync.json
dbdatasync version
```

Open a new terminal afterwards so the PATH change is seen; both programs should then be found.

Each manifest is pinned to `2026.11.6.837` and checks the zip's SHA256. A global install puts the programs under
`C:\ProgramData\scoop\apps\` and shims on the machine PATH. Both manifests have no `persist` entries; settings and data live in
`%ProgramData%` (step 2 and 3), not in the Scoop folder, so `scoop uninstall` leaves them.

> **Running either one as a Windows service from a Scoop install is not proven.** Scoop keeps each version in its own folder and a
> service registered against that folder points at a path that `scoop update` replaces. For this test run both in a console window
> (steps 2 and 3). If you want services, use the MSI instead.

**MSI** (the documented route for a service): `dbdatakit.msi` and `dbdatasync.msi` from the `dbdatakit/dist` and `dbdatasync/dist`
releases, then `msiexec /i dbdatakit.msi /qn` and the same for Sync. Each installs to `C:\Program Files\<Name>` and registers its
service **stopped**: `DbDataKitConsole` and `DbDataSync`. They are unsigned, so Windows will say "Unknown publisher".

## 2. The console

> **Every `dbdatakit` command that touches the console's data needs the same folder** (`server run`, `user`, `instance`). A command
> without `--repo` and without `DBDATAKIT_CONSOLE_DIR` uses `%LOCALAPPDATA%\dbdatakit\console` instead, which is not the folder
> `server run` serves, so a pairing made there never shows in the web app. Either pass `--repo` every time, as below, or set the
> variable once for the account: `[Environment]::SetEnvironmentVariable('DBDATAKIT_CONSOLE_DIR', 'C:\ProgramData\DbDataKit\console', 'User')`
> (a new terminal picks it up).

```powershell
dbdatakit server run --repo C:\ProgramData\DbDataKit\console
```

It listens on `https://localhost:8480`, makes a self-signed certificate in that folder, and with no user yet writes an invitation to
`FIRST-RUN.txt` there and to the log. Open `https://localhost:8480/invite#<code>` **once** on the machine and register a passkey: that
person is the first administrator. (`dbdatakit user invite --repo C:\ProgramData\DbDataKit\console` prints a new invitation if the first has expired.) To reach it from
another machine add `--listen https://test-sync.example.com:8480`.

## 3. Sync, started for the first time

Use a **new, empty** folder, not a copy of your live install. Sync 2026.11 reads `server.yaml` and nothing else; see step 5 for what
that means for an existing install.

```powershell
dbdatasync server run --repo C:\ProgramData\DbDataSync --url https://test-sync.example.com:5080
```

Because the URL is `https://` and no certificate is configured, Sync makes a self-signed one in `tls\dbdatasync.pfx` under the
folder and binds it. (Sync's default address is plain `http://localhost:5080`, which a console cannot pair with.) Browsing to Sync's
own address now gives a plain-text message: Sync has no web app, its screens are the console's.

## 4. Pair them

On the Sync machine, in a second window:

```powershell
dbdatasync server pair
dbdatakit instance add https://test-sync.example.com:5080 --code <the code it printed> --repo C:\ProgramData\DbDataKit\console
dbdatakit instance list --repo C:\ProgramData\DbDataKit\console
```

The code is single-use and lasts a few minutes. The console pins Sync's certificate when it pairs. Then open the console, **Servers**,
and open Sync from there (`/servers/<id>`). Check that you can sign in, see the replications screens, and save a connection.

## 5. What an existing install will hit

Do these against a **copy** of your old folder, never the live one. The full list, with the reason for each, is
[`docs/plans/RELEASE-NOTES-PENDING.md`](plans/RELEASE-NOTES-PENDING.md) (the DbDataSync section); the ones that stop you first:

1. **Settings are `server.yaml`.** `dbdatasync.config.yaml` and any `appsettings*.json` in the folder are refused, naming the file.
   Set the server up again from `docs/configuration.md` (many key names changed).
2. **Every config file needs a `schemaVersion`**, and unknown keys are errors. Run `dbdatasync config check --repo <copy>` and fix
   what it names.
3. **Passwords are not carried over.** Connection and state-database passwords moved to an encrypted store (`secrets\` in the folder,
   `dbdata:sync:...` names). Set each again with `dbdatasync secret set dbdata:sync:connection:<name> <value>`.
4. **Scripts are `.csx`.** `config\scripts\<name>.cs` files with an `entryType` are no longer read; rename them, drop `entryType`,
   and end the script with an expression with no semicolon.
5. **Users start fresh.** Nothing is read from Sync's old users, sessions or invitations; the console's first administrator is the
   one you made in step 2.
6. **The first start cancels queued work** (the work queue tables are replaced) and an on-demand reload that had not started must be
   asked for again.
7. **Some loads now fail where they used to store a wrong value** (for example a date sent into a text column on SQL Server). The
   message names the table, the column and the types; the fix is a Transform on the mapped column.
8. **MySQL, MariaDB and Oracle connections verify TLS by default.** A server with a private certificate needs the connection's
   `SSL Mode` or `Encrypt` property set.

## 6. Known gaps in this release

- **Everything above is untested on Windows**, including the Scoop routes and both certificate steps.
- **The container images for the console and Proxy were not published** (the registry refused the push). Windows installs do not use
  them.
- **One Build test is skipped on SQL Server** (`docs/plans/13c-follow-ups.md`, T16): a plan run with `--parallel 1` took about 7.4 s
  where three 3-second pauses should take at least 9 s. It is not known whether Build or the test is wrong.
- The installers are not signed.
