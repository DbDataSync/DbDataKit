# DbDataKit.Hosting.Cli

Plain command classes, no CLI framework: `Execute(string[] args, IConsole console)` returns an exit code (0 ok, 1 failure, 2 usage, 3 not elevated).

- `ServiceCommand`: `install|uninstall|start|stop|restart|status|check|unit|run`. `check` checks the registration, the account and the directory permissions, and prints the startup-failure file of a suite service. Options: `--account`, `--password-stdin`, `--allow-localsystem`, `--data-dir`, `--dry-run`, `--allow-downgrade --installed-version`. `--password` on the command line is rejected. Mutating verbs need elevation (`IElevationChecker`, default `Environment.IsPrivilegedProcess`); dry run does not.
- `CertCommand`: `status`/`show` (read-only; reports an unreadable certificate instead of throwing), `ensure` (create/rotate if needed), `renew` (replace the managed self-signed certificate now), `check` (exit 1 when rotation is due).
- Products provide a `ServiceCommandContext` (name, binary, layout, principal resolver, optional systemd unit) and inject the real `WindowsServiceControlManager`, `FileSystemAclService` and a `WindowsPrincipalResolver`.

UAC relaunch is not implemented (message only).

`ServiceCommandContext` also takes `StartMode`, `Failure` (restart delays and reset period), `Dependencies`, `SidType`, `RequiredPrivileges`, and `DataDirectoryArgument` (how `--data-dir` reaches the service command line when the host reads its data directory from configuration rather than a `--data-dir` option).

`HostingNouns.Service(ServiceCommand)` and `HostingNouns.Cert(CertCommand)` expose these commands as the shared `service` and `cert` nouns of `DbDataKit.Cli`'s `NounRouter`, so a product's command line has
`<program> service install` and `<program> cert show` under the same names as every other product's.


`SuiteServiceRegistration` is how each of the four services is registered: `Context(service, program, data folder, ...)` gives the Windows service (its
command line, start mode, failure actions and folders) and `UnitOptions(service, program)` the systemd unit. Each server's `service install` and
`service unit` call it, and so does `dbdatakit setup single-server`, so a service is registered the same way by either.

`SingleServerSetup` is `dbdatakit setup single-server` and `--stop` (`docs/install.md`, "Setting up one machine, or inviting another"). `SuiteDiscovery`
finds the programs installed beside `dbdatakit`; `WindowsSuiteServiceHost` (the service control manager and the folders' access lists) and
`SystemdSuiteServiceHost` (the system user, the unit, `systemctl`, the folders' owners) register and start them; the console supplies the join
requests and says which servers it lists (`ISingleServerConsole`, in `DbDataKit.Server`). Each service is set up in start order: registered when it is
not registered as it should be, its folders set, a join request written for a server that never started or that listens and is not listed, started,
then watched until it listens and is listed, or until its startup-failure file says why it stopped. Example: a Runner whose port another program holds
stops at `registered · started`; the run prints its `startup-failure-dbdatarunner.txt` and ends with exit code 1, and the next run, after the port is
freed, starts it and leaves the other three alone.
