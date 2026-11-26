# Testing the suite on a Windows test server

How to install every DbDataKit service on one Windows machine with one command, then check that each one does its main job. Use a test
machine, not a live install. Each check in step 4 is pass or fail. If one fails, write down what happened: it becomes a new row of open
work, and the rest of the checks still count.

**What CI already proves.** The suite release runs `dist/tests/suite-scoop-e2e.ps1` on a Windows runner (`suite-install-windows` in
`suite-release.yml`). It installs `dbdatakit-full` with Scoop, starts all four services, waits until each answers its health
check and the console lists it as paired and ready, then repeats the install, updates and uninstalls. Steps 1 to 3 below are that, by hand.
Step 4 is what CI does not check yet: that each service does its main job.

## 0. What you need

- Windows with an elevated PowerShell (a global Scoop install registers services).
- The **.NET 10 runtime with ASP.NET Core** for every account, in `C:\Program Files\dotnet`. The Microsoft installer for ".NET 10 Hosting Bundle" or
  "ASP.NET Core Runtime 10" does this. The services do not read a per-user install.
- [Scoop](https://scoop.sh).
- A SQL Server the machine can reach (Express or Developer is enough), with a login that can create a database. Step 4 uses it.
- A browser that can register a passkey (Windows Hello, a security key or a phone).

## 1. Install

```powershell
scoop install -g https://github.com/DbDataSync/DbDataKit/releases/download/dbdatakit-full/dist-beta/dbdatakit-full.json
```

Use `dist` instead of `dist-beta` for the newest stable release, and `dist-snapshot` for the newest snapshot.

When it returns:

- every service is registered, running and paired with the console: `DbDataKit` (the console), `dbdatagateway`, `DbDataRunner` and
  `DbDataWorkspace`;
- the console's address and a **setup link** for the first administrator are printed. The link is also in
  `C:\ProgramData\DbDataKit\server\FIRST-RUN.txt`.

The services run from a copy of the programs in `C:\ProgramData\DbDataKit\bin\<version>`, and keep their data in a folder each under
`C:\ProgramData\DbDataKit`. Open a **new** terminal afterwards so the programs and `DBDATAKIT_DATA` are on the path.

**Pass:** the command ends without an error and prints the setup link.

## 2. Sign in

Open the setup link (`https://localhost:7800/invite#<code>`) in a browser on the machine and register a passkey: you are the first administrator. The
browser warns about the certificate, because the console made a self-signed one. Accept it for `localhost`. The link works once and ends after 24 hours;
`dbdatakit setup link` makes a new one.

To open the console from another machine, use the machine's name instead of `localhost` both in that command and in the browser.

**Pass:** you are signed in and the console shows its home page.

## 3. Check the servers

```powershell
dbdatakit instance list
Get-Service DbDataKit, DbDataGateway, DbDataRunner, DbDataWorkspace
```

In the console, open **All servers** (under **Servers** in the navigation). **Setup** (under **Settings**) lists the first steps and ticks each one off by what the console actually has.

**Pass:** the three servers are listed as answering, with the same version, and all four services are `Running`.

## 4. One task per service

Make a demo database first (in SSMS or `sqlcmd`):

```sql
CREATE DATABASE demo;
GO
USE demo;
CREATE TABLE dbo.customers (id int PRIMARY KEY, name nvarchar(100) NOT NULL, country nvarchar(2) NOT NULL);
CREATE TABLE dbo.orders (id int PRIMARY KEY, customer_id int NOT NULL REFERENCES dbo.customers(id), ordered_on date NOT NULL, amount decimal(10,2) NOT NULL);
INSERT dbo.customers VALUES (1, N'Ada', N'GB'), (2, N'Grace', N'US'), (3, N'Linus', N'FI'), (4, N'Margaret', N'US');
INSERT dbo.orders VALUES (1, 1, '2026-01-05', 120.00), (2, 2, '2026-01-06', 75.50), (3, 2, '2026-02-01', 210.00), (4, 3, '2026-02-03', 15.25), (5, 4, '2026-03-10', 99.99);
CREATE DATABASE demo_target;
```

Each task below names where its full description lives. The console's **Docs** has the same pages.

| # | Service | Task | Pass when | Details |
| --- | --- | --- | --- | --- |
| 4.3 | Gateway | Add `demo` as a `sqlserver` data source (the Gateway's **Data sources** screen, or `server.yaml` as the Gateway README describes) open to everyone for this test (`everyone: true`; a source that names no group and is not open is refused), make a service account and token (`dbdatagateway identity service-account-add --name me`, then `identity token-create --account me --name test`), and query it | `dbdatagateway query run --insecure --user <account> --password <token> --datasource demo --sql "SELECT count(*) FROM dbo.orders"` prints 5 | `DbDataGateway/README.md` ("Quick start", "Data sources") |
| 4.5 | Workspace | `dbdataworkspace project create hello --kind app`, then open the Workspace from **Servers** and edit, commit and merge a file of a workspace of `hello` | the commit is on the project's main branch | `DbDataWorkspace/README.md` |
| 4.6 | Runner | Open the Runner from **Servers** and look at its packages, channels and runs | the Runner's pages open and say what it serves (none yet) | `DbDataRunner/README.md` |

### 4b. Build and Sync authored in the Workspace

A build project (SQL models) and a sync project (replications) are authored in the Workspace and published to the Runner, which runs them. The
Workspace runs the `dbdatabuild` and `dbdatasync` programs the package installed for its checks and builds.

The console publishes a build to the Runner as you, so you need the publisher role on the Runner. The Workspace never connects to the Runner.

| # | Task | Pass when |
| --- | --- | --- |
| 4.7 | In the Workspace, make a new project of kind **build**. In a workspace of it, add a connection to `demo`, import `dbo.orders` from the catalog, edit the model, run **Check** and **Compile**, commit and merge | the check has no errors and the merge is on the main branch |
| 4.8 | Build the project and publish it to the Runner on the channel `dev` | the Runner lists the package and its refresh task on `dev` |
| 4.9 | Make a new project of kind **sync**. Edit its replication to copy `dbo.orders` from `demo` to `demo_target`, run **Check**, commit, merge, build and publish it to the Runner | the Runner lists the replication set's drain task |
| 4.10 | From each project's page, find its runs on the Runner | you get there without going through **Servers**; if you have to, write down where you got lost |

What these tasks leave out, on purpose:

- **The Runner runs no task here** beyond what 4.8 and 4.9 publish: the release has no sample package of its own.
- **The Workspace does not build apps here.** Building an app needs bun and a package store (`apps.packageStore`), and the package installs neither.
  Editing, committing and merging work without them.
- **Sign-in with Windows accounts or Entra** is not part of this test.

## 5. Update and remove

```powershell
scoop update -g dbdatakit-full      # when a newer release is published: same data and pairings, new programs
scoop uninstall -g dbdatakit-full   # removes the services, the pairings and the programs; keeps C:\ProgramData\DbDataKit
```

**Pass:** after an update, step 3 passes again with the new version; after an uninstall, no DbDataKit service is left.

## 7. Known gaps

- The installers are not signed: Windows shows "Unknown publisher" and SmartScreen may warn.
- Step 4 is checked by hand; CI checks steps 1 to 3 and step 5.
