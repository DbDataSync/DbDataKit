# DbDataKit.Hosting.Startup

The steps a server runs before it serves, and the file that says why a start failed. The design is in
[docs/install.md](../../../docs/install.md), "One startup sequence" and "Logs and the startup-failure file".

- `StartupSequence` runs the steps a server fills in, always in this order: `folder`, `keys`, `certificate`, `state`, `ports`, `join`. It stops at
  the first step that throws and returns a `StartupResult`; the server's `server run` returns its `ExitCode` (1 after a failed start).
- `StartupStepException` has one factory per step, so a failed step names the same things in every server: `Folder` the folder, its owner and the
  account; `Keys` the key file and the unexpected grant; `Certificate` the file and the account; `State` the engine, the database and the migration;
  `Ports` the port, who holds it and Windows' excluded port ranges; `Join` the console's address and its answer. A step may throw any other
  exception; the failure file then names the step and the exception chain.
- `StartupFailureFile` is `startup-failure-<program>.txt` in the shared logs folder. Before the first step the sequence makes the service's own logs
  subfolder (`logs/<program>`) and writes the file's first line. Each step rewrites that line, for example
  `starting since 2026-11-17T09:12:03Z, step: certificate, not finished`. A start that succeeds deletes the file. A failed start writes the account,
  the folders, what the step names and the exception chain. Only the service's account, SYSTEM and Administrators can open the file; on Linux it is
  mode 0600.
- `StartupPorts.BindAsync` is the ports step of a server whose host binds its listeners when it starts (Kestrel): a port another program holds, or
  one Windows refuses, fails the step as `StartupStepException.Ports`, with the port read from the host's message. The host starts its background
  services in the same call, so a failure of one of them is also reported at the ports step, with its own message.
- A failed start also goes to the Application event log when the server runs as a Windows service (`StartupOptions.Failed`).
  `StartupResult.Summary()` is the line a server writes to standard error: the step, the reason and where the file is.
- `StartupFailureFile.DescribeLastStart` is what `service doctor` prints: the file's lines under a heading, or that there is none.
- `SharedLogsFolder` gives the shared logs folder its protection: on Windows each service account may create files and folders in it and owns what
  it creates; on Linux the folder is mode `1770`.
- `SuiteServices` is the table of the four services: program, Windows service name and account, description, data folder and start order.

Example, in a server's `server run`:

```csharp
var result = await new StartupSequence(new StartupOptions { Service = SuiteServices.Runner, DataFolder = dataFolder })
    .Step(StartupStep.Folder, _ => folders.Check())
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
