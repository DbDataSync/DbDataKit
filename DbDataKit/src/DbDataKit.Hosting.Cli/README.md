# DbDataKit.Hosting.Cli

Plain command classes, no CLI framework: `Execute(string[] args, IConsole console)` returns an exit code (0 ok, 1 failure, 2 usage, 3 not elevated).

- `ServiceCommand`: `install|uninstall|start|stop|restart|status|doctor|unit|run`. Options: `--account`, `--password-stdin`, `--allow-localsystem`, `--data-dir`, `--dry-run`, `--allow-downgrade --installed-version`. `--password` on the command line is rejected. Mutating verbs need elevation (`IElevationChecker`, default `Environment.IsPrivilegedProcess`); dry run does not.
- `CertCommand`: `status`/`show` (read-only; reports an unreadable certificate instead of throwing), `ensure` (create/rotate if needed), `renew` (replace the managed self-signed certificate now), `check` (exit 1 when rotation is due).
- Products provide a `ServiceCommandContext` (name, binary, layout, principal resolver, optional systemd unit) and inject the real `WindowsServiceControlManager`, `FileSystemAclService` and a `WindowsPrincipalResolver`.

UAC relaunch is not implemented (message only).
