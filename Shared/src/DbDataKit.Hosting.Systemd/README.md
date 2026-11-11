# DbDataKit.Hosting.Systemd

Deterministic (LF) generators, golden-tested (`tests/.../Golden`; `UPDATE_GOLDEN=1` rewrites them).

- `SystemdUnitGenerator`: `DynamicUser=yes` by default or a dedicated user; `LoadCredential=` for secrets; `ProtectSystem=strict`, `ProtectHome`, `NoNewPrivileges`, `PrivateTmp`, kernel/cgroup protections, empty capability set unless listed, `ReadWritePaths`, `Restart=on-failure`, optional `WatchdogSec`. Newlines in any value are rejected (directive injection); paths must be absolute.
- `DockerfileGenerator`: numeric non-root `USER` (uid 0 refused), `HEALTHCHECK` via the app's `--healthcheck` (see `HealthProbeClient`), data volume, optional `@sha256` pin. Runtime image is the default; the SDK image is an explicit `Dev` flavour with a warning header.

Decision: set `WatchdogSec` only when the host really pings the watchdog, otherwise systemd kills a healthy service.
