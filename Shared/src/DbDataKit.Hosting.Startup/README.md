# DbDataKit.Hosting.Startup

The steps a server runs before it serves, and the file that says why a start failed. The design is in
[docs/install.md](../../../docs/install.md), "One startup sequence" and "Logs and the startup-failure file".

- `StartupSequence` runs the steps a server fills in, always in this order: `folder`, `keys`, `certificate`, `state`, `ports`, `join`. It stops at
  the first step that throws and returns a `StartupResult`; the server's `server run` returns its `ExitCode` (1 after a failed start).
- `StartupStepException` has one factory per step, so a failed step names the same things in every server: `Folder` the folder, its owner and the
  account; `Keys` the key file and the unexpected grant; `Certificate` the file and the account; `State` the engine, the database and the migration;
  `Ports` the port, who holds it and Windows' excluded port ranges; `Join` the console's address and its answer (`JoinNotMade` when the console
  gave none: it could not be reached, or another certificate answered at its address; `JoinFailed` picks one of the two). A step may throw any other
  exception; the failure file then names the step and the exception chain.
- `StartupFailureFile` is `startup-failure-<program>.txt` in the shared logs folder. Before the first step the sequence makes the service's own logs
  subfolder (`logs/<program>`) and writes the file's first line. Each step rewrites that line, for example
  `starting since 2026-11-17T09:12:03Z, step: certificate, not finished`. A start that succeeds deletes the file. A failed start writes the account,
  the folders, what the step names and the exception chain. Only the service's account, SYSTEM and Administrators can open the file; on Linux it is
  mode 0600.
- `StartupPorts.BindAsync` is the ports step of a server whose host binds its listeners when it starts (Kestrel): a port another program holds, or
  one Windows refuses, fails the step as `StartupStepException.Ports`, with the port read from the host's message. The host starts its background
  services in the same call, so a failure of one of them is also reported at the ports step, with its own message.
- `ListenPorts.Resolve` gives the address a server listens on: `--listen` when given, else the `listen:` line of its settings file, else the first
  port of the service's sub-block that is outside Windows' excluded ranges and can be bound. The server resolves it before it builds its host, in the
  folder step. The ports step, `StartupPorts.BindAsync` with that `ListenChoice`, fails as `StartupStepException.NoFreePort` when no port of the
  sub-block could be bound (naming the excluded ranges that cover it), and writes a chosen port as the `listen:` line once the server listens on it
  (`ListenPorts.Pin`), so the next start uses the same port. A port from `--listen` or the `listen:` line is never moved: another program holding it
  fails the ports step naming it. The design is [docs/install.md](../../../docs/install.md), "Ports".
- `StartupContext.CheckFolderPermissions` is the folder step's check of the data folder (`FolderPermissions.Problems`). A folder that another account
  could change fails the step as `StartupStepException.FolderPermissions`, which names the folder, its owner, the account, what is wrong and the
  switch that skips the check. `SkipFolderPermissions.Read` reads the switch: the flag `--skip-folder-permissions-check` or the setting
  `startup.skip_folder_permissions_check: true` in the server's settings file (`SuiteService.SettingsFile`, read strictly by
  `FolderPermissions.SettingIn`). With the check skipped, the start goes on and writes a warning at every start (`StartupOptions.Warning`; for a
  process under a service manager also `StartupOptions.ServiceWarning`, the Application event log). The design is
  [docs/install.md](../../../docs/install.md), "Skipping the folder permissions check".
- A failed start also goes to the Application event log when the server runs as a Windows service (`StartupOptions.Failed`).
  `StartupResult.Summary()` is the line a server writes to standard error: the step, the reason and where the file is.
- `StartupFailureFile.DescribeLastStart` is what `service check` prints: the file's lines under a heading, or that there is none.
- `SharedLogsFolder` gives the shared logs folder its protection: on Windows each service account may create files and folders in it and owns what
  it creates; on Linux the folder is mode `1770`.
- `SuiteServices` is the table of the four services: program, Windows service name and account, description, data folder and start order. It also
  holds the suite's ports, 7800 to 7899 (`SuitePorts`), and each service's sub-block (`ConsolePorts` 7800 to 7819, `GatewayPorts` 7820 to 7839,
  `RunnerPorts` 7840 to 7859, `WorkspacePorts` 7860 to 7879, `SparePorts` 7880 to 7899); `scripts/check_default_ports.py` reads them.

Example, in a server's `server run`:

```csharp
var result = await new StartupSequence(new StartupOptions { Service = SuiteServices.Runner, DataFolder = dataFolder })
    .Step(StartupStep.Folder, step => step.CheckFolderPermissions(dataFolder, SkipFolderPermissions.Read(flagGiven, settingsFile), settingsFile))
    .Step(StartupStep.State, (_, ct) => state.MigrateAsync(ct))
    .Step(StartupStep.Ports, (_, ct) => app.StartAsync(ct))
    .RunAsync(ct);
if (!result.Succeeded) return result.ExitCode; // the reason is in result.FailureFile
```

A port the Runner cannot bind leaves `C:\ProgramData\DbDataKit\logs\startup-failure-dbdatarunner.txt`:

```
Startup of dbdatarunner failed at the ports step, at 2026-11-17T09:12:05Z (starting since 2026-11-17T09:12:03Z).

Port 7841 cannot be bound: it is inside the excluded port range 7840-7939.

Step: ports
Account: NT SERVICE\DbDataRunner (not elevated)
Data folder: C:\ProgramData\DbDataKit\runner
Logs folder: C:\ProgramData\DbDataKit\logs\dbdatarunner
Port: 7841
Excluded port ranges: 5357, 7840-7939 (`netsh int ipv4 show excludedportrange protocol=tcp`)

What to check: Check that no other program holds the server's ports ...

Exception chain:
  DbDataKit.Hosting.Startup.StartupStepException: Port 7841 cannot be bound: ...
  System.Net.Sockets.SocketException: An attempt was made to access a socket in a way forbidden by its access permissions.
```
