# DbData.Hosting.WindowsService

SCM wrapper replacing `sc.exe`, install planning, directory layout and `service doctor`. No package dependencies.

Contract
- `IServiceControlManager` (real: `WindowsServiceControlManager` via advapi32 P/Invoke: CreateServiceW, ChangeServiceConfigW/2W for description, failure actions, delayed auto-start, SID type, required privileges; QueryServiceStatusEx, StartService, ControlService, DeleteService). Fake: `InMemoryServiceControlManager`.
- `ServiceProvisioner`: idempotent install (create / reconfigure on drift / unchanged), dry-run `InstallPlan.Render()`, downgrade refusal, stop-then-delete uninstall, Event Log source registration (`WindowsEventLogSourceRegistrar`, registry based).
- `DirectoryLayout.ForProduct`: versions/current/config/data/secrets/certs/logs/updates with intended ACLs as data (`AclSpec`); `IAclPolicy` (`WindowsAclPolicy`) applies; `IFileSystemAclReader` reads back; `ServiceDoctor` verifies service settings and ACL drift (world-readable key files, writable binaries, uncut inheritance) over those interfaces.

Decisions
- Default identity `NT SERVICE\<Name>`; gMSA (`DOMAIN\name$`, no password); explicit account only with a `SecureString` password (zeroed after the call); LocalSystem only with `AllowLocalSystem`. SYSTEM spellings all map to LocalSystem so the gate cannot be bypassed.
- Service arguments containing "password" are refused. Plans and exceptions never contain secrets.
- ACL helper: this package has its own small `IAclPolicy`/`WindowsAclPolicy` so it does not depend on `DbData.Secrets.Windows` (built in parallel). **Reconcile the two at merge** (keep one implementation behind `IAclPolicy`).
- The virtual-account SID only resolves after the service exists: create the service first, then apply ACLs (the CLI does).
- Real-SCM mutating test runs only with `DBDATA_ADMIN_TESTS=1` in an elevated prompt.
