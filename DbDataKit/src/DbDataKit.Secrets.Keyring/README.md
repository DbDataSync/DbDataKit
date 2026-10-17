# DbDataKit.Secrets.Keyring

Interface-compatible stub. `KeyringSecretProvider` implements `ISecretProvider` and `ISecretStore` over an
`IKeyringBackend` (get/set/delete/list by service + name). Without a backend it is inert (handles nothing).

## Phase 0 decision (open)
Plan 02 Phase 0 must review `ClrKernel.Core.Secrets` (fallback encryption, memory handling) and decide whether to
**wrap** it or **fold** it into this repo. This package deliberately has **no dependency** on `ClrKernel.Core.Secrets`.
If the decision is "wrap": add a separate adapter class implementing `IKeyringBackend` over `SecretStore` (a
service+name pair maps to the store key `<service>/<name>`; `dbdatasync:connection:<name>` items stay readable via the
Sync rule in `LegacyEnvProvider` or a keyring alias). If "fold": implement `IKeyringBackend` per OS here.
Either way consumers keep using `KeyringSecretProvider` unchanged.

Caveat to document wherever this is used: the OS keyring is per user profile, so it suits interactive CLI use, not
Windows services (use `DbDataKit.Secrets.Windows` DPAPI machine store) or containers (`file:`/`env:`).
