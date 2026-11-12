# Installing the suite on one server

The single-server install is the three service types of plan 33, **DbDataKit (the console), the Gateway and the Runner**, on one machine, each paired with the console. There are two ways, and the second is the first written out: use one route per machine, because both register the same service names.

| | Route | When |
| --- | --- | --- |
| A | `scoop install -g` of `dbdatakit-full`: one command installs, registers, starts and pairs every member | a Windows machine that runs the suite |
| B | the same steps by hand, with the commands the script runs | to finish or repeat a step the script stopped at, to install only some members, or on a machine where Scoop is not wanted |

Windows is the only platform with route A. The Linux zips of the Gateway and the Runner are installed with `install.sh`, see "Linux" below. Nothing here auto-approves a pairing: the person who runs the commands is the elevated administrator of the machine, a pairing code is made, used once and never written to a file, and the console pins each member's certificate.

## Route A: one command

In an elevated PowerShell, with [Scoop](https://scoop.sh) and the .NET 10 runtime (ASP.NET Core) installed:

```powershell
scoop install -g https://github.com/<owner>/DbDataKit/releases/download/dbdatakit-full/dist/dbdatakit-full.json
```

When it returns, every member's service is registered, running and paired; the console's address and the setup link for the first administrator are printed. Open the link once and register a passkey; it works once and ends after 24 hours, and `dbdatakit setup link` on the machine makes a new one. The package installs every member of `dist/suite.json` (DbDataSync, DbDataBuild, the Store and the Workspace besides the three); for only the three, use route B. The services run from a copy of the program files in `%ProgramData%\DbDataKit\bin\<version>`, not from Scoop's app folder (Scoop refuses to update an app while a process runs from its folder). `scoop update -g dbdatakit-full` is the update command: the new version's install copies its files, stops the old services, registers them again from the new copy with the same data and pairings, starts them and removes the old copy. A repeat of the install command finishes what is missing. `scoop uninstall -g dbdatakit-full` removes the pairings, the services and the copy of the program files, and leaves `%ProgramData%\DbDataKit`. What the script does in each case is in `dist/README.md` ("One package for a single server").

## Route B: the steps by hand

These are the steps `post_install` runs (`dist/suite-scripts.ps1`), in its order, for the three members. Run them in an elevated PowerShell. The members' programs are `dbdatakit`, `dbdatagateway` and `dbdatarunner`; with the Scoop package they are on the machine path, otherwise use the install folder of each product (the MSI or `install.ps1`).

Open a new terminal after installing, so `DBDATAKIT_DATA` (the machine variable that names `%ProgramData%\DbDataKit`, under which each service keeps a folder) is in the environment: a command run by hand reads the folder the service uses only when it is set.

**1. Register the services** (each is registered stopped, as the MSI does):

```powershell
dbdatakit service install
dbdatagateway service install --name dbdatagateway
dbdatarunner service install
```

**2. Start the console and wait for its health check:**

```powershell
Start-Service DbDataKit
dbdatakit health check
```

**3. Pair each other member.** The code is made on the member, the member is started (it must answer for the console to pair with it), then the console uses the code:

```powershell
dbdatagateway server pair --max-role viewer,operator,admin      # prints: Pairing code: <code>
Start-Service dbdatagateway
dbdatagateway health check
dbdatakit instance add https://localhost:8816 --code <code> --name dbdatagateway

dbdatarunner server pair --max-role viewer,operator,admin
Start-Service DbDataRunner
dbdatarunner health check
dbdatakit instance add https://127.0.0.1:8495 --code <code> --name dbdatarunner
```

The code is single-use and expires in ten minutes; five wrong codes cancel all of a server's codes. The addresses are the members' default management listeners (`docs/suite-listeners-and-auth.md`): the Gateway's management API is on by default and answers on this machine only, https on 8816 with its generated certificate; the Runner's is `https://127.0.0.1:8495`. A member moved to another address by its own setting is paired by that address. The ceilings (`--max-role`) are what a console administrator may use on that server, never what anyone holds; the Store takes `viewer,operator,author,approver,admin` and the Workspace `viewer,operator,author,admin`.

**4. Check:** `dbdatakit instance list` shows each server with its product, version and whether it answers. The first administrator opens the setup link (`dbdatakit setup link --url https://localhost:8480` prints it again; the console also keeps it in `%ProgramData%\DbDataKit\server\FIRST-RUN.txt`). No state settings are written: every service starts on its SQLite default; `<program> state init` puts it in a shared database later.

Repeating is safe. `service install` says a service is registered already, `instance list --offline` shows which members the console has, and a member that is listed is not paired again.

**Removing** goes the other way, in reverse order: `dbdatakit instance remove <product>`, `<program> server unpair <id>` (the id is the console's entry of `<program> server clients`), `Stop-Service`, `<program> service uninstall` (the Gateway with `--name dbdatagateway`). Settings and data under `%ProgramData%\DbDataKit` stay.

## Servers on other machines: announcement

Where the members run on other machines, or are installed one by one from their own MSIs, nobody types a code: each server is given the console's private announce address and an administrator confirms it in the console. The console listens for announcements on a plain-http port of its own (`dbdatakit server run --announce-listen http://<address>:8481`); a console that has `DBDATAKIT_ANNOUNCE_URL` set takes the port from it and listens on every address, so keep that port on the network the servers share and never publish it. A server announces when `DBDATAKIT_ANNOUNCE_URL` is set for it (`msiexec ... ANNOUNCEURL=...`, `install.ps1 -AnnounceUrl`, or `install.sh --announce-url`), and then waits in `dbdatakit instance pending` until `dbdatakit instance confirm <id or name>`. The announcement is never a confirmation. On one machine, prefer route A or B: the announce port would only be one more listener.

## Linux

The Gateway and the Runner have `linux-x64` zips (the console's zip is framework-dependent too). `install.sh --system` installs one under `/opt/<product>`; each runs as a systemd unit that the product prints (`<program> service unit`), and its folder is `/var/lib/dbdatakit/<service>`. The pairing steps are step 3 above with the Linux addresses and without `Start-Service` (`systemctl start`). There is no single-command package on Linux. A server that joins by announcement is installed with `sudo ./install.sh --system --announce-url http://<console address>:8481`, which writes `/etc/systemd/system/<product>.service.d/announce.conf` (the unit is named for the product: `dbdatagateway`, `dbdatarunner`, `dbdatakit`) so the service starts with `DBDATAKIT_ANNOUNCE_URL` set; write the unit itself with `<program> service unit`, then `systemctl daemon-reload`, `systemctl enable --now <product>` and confirm the server in the console (`dbdatakit instance pending`, `instance confirm`).
