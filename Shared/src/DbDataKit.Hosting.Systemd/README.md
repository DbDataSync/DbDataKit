# DbDataKit.Hosting.Systemd

Deterministic (LF) generators, golden-tested (`tests/.../Golden`; `UPDATE_GOLDEN=1` rewrites them).

- `SystemdUnitGenerator`: `DynamicUser=yes` by default or a dedicated user; `LoadCredential=` for secrets; `ProtectSystem=strict`, `ProtectHome`, `NoNewPrivileges`, `PrivateTmp`, kernel/cgroup protections, empty capability set unless listed, `ReadWritePaths`, `Restart=on-failure`, optional `WatchdogSec`. Newlines in any value are rejected (directive injection); paths must be absolute.
- `DockerfileGenerator`: numeric non-root `USER` (uid 0 refused), `HEALTHCHECK` via the app's `--healthcheck` (see `HealthProbeClient`), data volume, optional `@sha256` pin. Runtime image is the default; the SDK image is an explicit `Dev` flavour with a warning header.

Decision: set `WatchdogSec` only when the host really pings the watchdog, otherwise systemd kills a healthy service.

**`PrivateTmp` and integrated authentication.** The four services' units use the full sandbox, which includes `PrivateTmp`: the service sees its own
`/tmp`, so a Kerberos ticket cache another account left in `/tmp` (the default `FILE:/tmp/krb5cc_<uid>`) is invisible to it. A Linux Runner whose tasks
sign in to SQL Server with integrated authentication therefore needs its ticket cache where the service can read it: a keytab the service uses itself
(`KRB5_CLIENT_KTNAME`, renewed by the service's own `kinit`), or a `KRB5CCNAME` pointing into the service's state directory. For example, a Runner
unit with `Environment=KRB5CCNAME=FILE:/var/lib/dbdatakit/dbdatarunner/krb5cc` and a timer that runs `kinit -k` as the service's user keeps a ticket
the tasks can use, without lifting the sandbox. The same holds for `UMask=0077`: files the service writes are readable by its user only.
