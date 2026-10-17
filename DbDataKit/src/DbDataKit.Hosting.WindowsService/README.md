# DbDataKit.Hosting.WindowsService

SCM wrapper replacing `sc.exe`, install planning, directory layout and `service doctor`. Depends on `DbDataKit.Security.Acl` for all ACL work.

Contract
- `IServiceControlManager` (real: `WindowsServiceControlManager` via advapi32 P/Invoke: CreateServiceW, ChangeServiceConfigW/2W for description, failure actions, delayed auto-start, SID type, required privileges; QueryServiceStatusEx, StartService, ControlService, DeleteService). Fake: `InMemoryServiceControlManager`.
- `ServiceProvisioner`: idempotent install (create / reconfigure on drift / unchanged), dry-run `InstallPlan.Render()`, downgrade refusal, stop-then-delete uninstall, Event Log source registration (`WindowsEventLogSourceRegistrar`, registry based).
- `DirectoryLayout.ForProduct`: versions/current/config/data/secrets/certs/logs/updates with intended ACLs as data (`AclSpec`); the shared `IAclService` (`FileSystemAclService`, from `DbDataKit.Security.Acl`) applies (`LayoutAcl.Apply(layoutEntry, resolver)` converts the layout to an `AclSpecification`) and reads back; `ServiceDoctor` verifies service settings and ACL drift (world-readable key files, writable binaries, uncut inheritance) over those interfaces.

- `DirectoryLayout.Create(product, entries)` builds a product-specific layout (for example one flat data directory) instead of `ForProduct`. `ServiceSid.FromName` computes `S-1-5-80-...` for `NT SERVICE\<name>` without a lookup; `WindowsPrincipalResolver` is the real `IPrincipalResolver`.

Decisions
- Default identity `NT SERVICE\<Name>`; gMSA (`DOMAIN\name$`, no password); explicit account only with a `SecureString` password (zeroed after the call); LocalSystem only with `AllowLocalSystem`. SYSTEM spellings all map to LocalSystem so the gate cannot be bypassed.
- Service arguments containing "password" are refused. Plans and exceptions never contain secrets.
- ACLs: one implementation, `DbDataKit.Security.Acl` (shared with `DbDataKit.Secrets.Windows`). This package only owns the layout data and the mapping to `AclSpecification`; the doctor and CLI take a single `IAclService`.
- The virtual-account SID only resolves after the service exists: create the service first, then apply ACLs (the CLI does).
- Real-SCM mutating test runs only with `DBDATA_ADMIN_TESTS=1` in an elevated prompt.
