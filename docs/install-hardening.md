# Hardening an install
<!-- reviewed: 2026-11-21 -->

What an administrator sets up and checks so that each service's files can be read and changed only by that service, and so that a server joins
the right console. The steps of an install are in [install.md](install.md); the risks the design answers are in [security.md](security.md).

## Each service owns its folder

Each service runs under an account of its own and keeps its data in a folder of its own:

| Service | Windows account | Windows folder | Linux user | Linux folder |
| --- | --- | --- | --- | --- |
| Console | `NT SERVICE\DbDataKit` | `C:\ProgramData\DbDataKit\server` | `dbdatakit` | `/var/lib/dbdatakit/server` |
| Gateway | `NT SERVICE\DbDataGateway` | `C:\ProgramData\DbDataKit\gateway` | `dbdatagateway` | `/var/lib/dbdatakit/gateway` |
| Runner | `NT SERVICE\DbDataRunner` | `C:\ProgramData\DbDataKit\runner` | `dbdatarunner` | `/var/lib/dbdatakit/runner` |
| Workspace | `NT SERVICE\DbDataWorkspace` | `C:\ProgramData\DbDataKit\workspace` | `dbdataworkspace` | `/var/lib/dbdatakit/workspace` |

Who may use the folder:

- On Windows, the Administrators group owns the folder. The service's account may change it, SYSTEM and Administrators have full control, and other
  users have no access.
- On Linux, the service's user owns the folder, with mode `0700`.

The install creates only the folder itself:

- On Windows, `service install` (which the MSI, `install.ps1 -InstallService` and the `dbdatakit-full` Scoop package run) creates the folder, makes the
  Administrators group its owner and sets its access list.
- On Linux, `install.sh --system` makes the service's system user in the group `dbdatakit` and writes the service's unit, which runs as that user.
  systemd creates the folder and the service's logs folder, `/var/log/dbdatakit/<program>`, each time it starts the unit (`StateDirectory=`,
  `LogsDirectory=`).

When the service starts, it creates whatever is missing inside its folder, as its own account: its keys, its certificate, its state database, its
configuration repository and its logs. Each file it creates is owned by the service's account and inherits the folder's access list.

Example: on a new Windows server, after the install and the first start of the Runner, `C:\ProgramData\DbDataKit\runner\state.db` is owned by
`NT SERVICE\DbDataRunner`, and a user who is not an administrator cannot open the folder.

## The service checks its folder at every start

Before a service opens anything, it checks its folder. The start stops when the folder fails the check:

- On Windows: the folder's owner is not the service's account, the Administrators group, SYSTEM or TrustedInstaller, or Everyone, Users,
  Authenticated Users, Anonymous, Guests or Interactive can write to the folder.
- On Linux: the service's user does not own the folder, or every account on the machine can write to it.

When a start stops, the service writes the reason to a failure file and exits:

- Windows: `C:\ProgramData\DbDataKit\logs\startup-failure-<program>.txt`, which only the service's account, SYSTEM and Administrators can open.
- Linux: `/var/log/dbdatakit/<program>/startup-failure-<program>.txt`, mode `0600`.

The file names the folder, its owner, the account the service runs as and what is wrong. `<program> service check` prints it. The file is deleted
when a start succeeds.

To fix it, give the folder back the access list described above, then start the service again.

### Starting on a folder that fails the check

A test, a developer's checkout or a trial install may need a folder that fails the check. For those, start the server with
`--skip-folder-permissions-check`, or set it in the server's settings file:

```yaml
startup:
  skip_folder_permissions_check: true
```

Use it only on machines that hold no real data. A server that skips the check says so in three places, so it is never forgotten:

- a warning at every start, which a Windows service also writes to the Application event log;
- the line `startup.folder-permissions` of `service check`;
- the console's Bindings page, under "Posture choices", which names every server that skips the check.

## Joining a server to the console

A server joins a console by a command that the console generates. The command works once, for one kind of server, with a role limit and a group you
choose. You can make it on the console's **Add a server** page or with `dbdatakit setup invite`:

```
$ dbdatakit setup invite runner --group nightly --minutes 15
Run this on the runner's machine, as an administrator, before 2026-11-21 14:15:00Z. It works once:
    dbdatarunner pair --host https://console.example.com:7800 --token <token> --console-thumbprint sha256:4f1c...
```

On the server's machine, the command writes a small join request into the service's folder and restarts the service. At its next start the service
joins the console and deletes the request.

What protects the join:

- The token works once and expires after 15 minutes unless you choose a longer time, up to 24 hours. Five refused tries cancel it.
- The token never crosses the network. The server sends a proof made from it, and the console keeps only what it needs to check that proof, so a copy
  of the console's database cannot be used to join a server.
- The server checks the console's certificate against the thumbprint in the command before it sends anything, so a machine that pretends to be the
  console is refused.
- After the join, the console and the server each remember the other's certificate and refuse any other.
- Every token made, used, refused or cancelled is recorded in the console's audit, with who made it and never its value.

What you do:

- Choose the smallest role limit the server needs. The command cannot give the server more than that.
- Choose a short time. Make the command when you are ready to run it.
- Treat the command like a password until it is used. Do not paste it into a ticket or a chat. To keep it out of the shell history, give
  `--token -` and paste the token when the command asks for it.
- Cancel a command you will not use. The **Add a server** page lists the commands still waiting, without their tokens, each with a Cancel button.

When a join is refused, the service does not start, and its failure file says why: the token was already used, it expired, it was made for another
kind of server or another group, or the console's certificate does not match the thumbprint.

## Planned changes

- **Planned.** Administration commands such as `secret set` ask the running service to make the change, so an administrator's account never writes a
  file in a service's folder. Opening the files directly becomes a recovery mode, allowed only while the service is stopped.
- **Planned.** A new command, `setup single-server` of the console's program, sets the program folder so that only Administrators and SYSTEM can change it, so no user can put a file
  beside a program that a service runs.
