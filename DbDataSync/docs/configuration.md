# Configuration

[DbDataSync](../README.md) · [Install](install.md) · **Configuration** · [Getting started](getting-started.md) · [Replication concepts](replication-concepts.md) · [Drivers and libraries](drivers-and-libraries.md) · [State database](state-database.md) · [Building from source](development.md)

Every CLI flag and environment variable that configures a running DbDataSync, across every way it can
be started. There are two separate entry points into the same host, with different defaults — that
distinction matters more than any individual setting below, so it's worth reading first.

## Two entry points, one host

- **`dotnet run --project src/DbDataSync.Api`** — the raw ASP.NET Core project. This is the dev-loop
  path documented in [Building from source](development.md).
- **`dbdatasync`** — a dotnet global tool (`src/DbDataSync.Cli`, `PackAsTool=true`,
  `ToolCommandName=dbdatasync`, [on NuGet](https://www.nuget.org/packages/DbDataSync)). This is the
  actual product distribution: `dbdatasync server setup`, `dbdatasync server run`,
  `dbdatasync service install|uninstall|status`, `dbdatasync cert ...`, `dbdatasync invite`,
  `dbdatasync health check`, `dbdatasync version`. It's also what the Docker image runs. See
  [Install](install.md) for every way to install and run it.

Both ultimately call the same `DbDataSyncHost.Build(args)` composition root
(`src/DbDataSync.Api/DbDataSyncHost.cs`), so a setting documented under "DbDataSync.Api process config"
below applies no matter which entry point launched it — the CLI just supplies its own flags and
defaults, then forwards them as the same `DbDataSync:*` configuration keys.

**The three entry points default to three different ports** — the most common source of "why can't
the console's web app reach the API" confusion:

| how it's started | default port |
| --- | --- |
| `dotnet run --project src/DbDataSync.Api` (dev loop) | `5183` (`http`), plus `7186` (`https`) |
| `dbdatasync server run` | `5080` |
| the Docker image | `8080` |

The dev-server proxy of the Sync screens (`DBDATASYNC_API_URL`, below) defaults to `5183` — i.e. it assumes the raw
`dotnet run` dev loop, not a CLI-launched backend. Point it at `5080` if you're running against
`dbdatasync server run` instead.

## `dbdatasync` CLI (`src/DbDataSync.Cli`)

Parsing is a simple `--flag value` scan (`CliOptions.Read`/`Has`) — no positional magic, no `=`
syntax. Run `dbdatasync --help` or any subcommand's `--help` for the same text.

### Repo root resolution (`serve`, `invite`, `health`, `setup`, `config check`)

These commands resolve their repo root the same way, via a shared resolver
(`DbDataSyncRoot.Resolve`):

1. An explicit `--repo <path>` wins outright.
2. Otherwise, walk upward from the current directory — the same shape `git` itself uses to find
   `.git` — looking for `dbdatasync.config.yaml` at each parent in turn.
3. Otherwise, `DbDataSync__App__RepoRoot` — the environment-variable form of the `DbDataSync:App:RepoRoot`
   config key, not a name invented for this resolver.
4. Otherwise, fall back to one documented, **machine-wide** directory per platform
   (`CliOptions.DefaultRoot`):

   | platform | default |
   | --- | --- |
   | Windows | `%ProgramData%\DbDataSync` |
   | macOS | `/Library/Application Support/DbDataSync` |
   | FreeBSD | `/var/db/dbdatasync` |
   | Linux (and any other Unix-like OS) | `/var/lib/dbdatasync` |

   Machine-wide, not per-user, so an interactive `serve`/`setup` and a registered service agree on one
   repo with no `--repo` needed on either side — in earlier versions this was a per-user location
   (`%LOCALAPPDATA%\DbDataSync` / `~/.local/share/DbDataSync`), which put a Windows service (running
   as `LocalSystem`, a different profile than a person's) on a different repo than an interactive run.
   An operator upgrading from an earlier version whose only configuration is still at that old location
   sees a one-time message naming both paths (`LegacyRootMigration`) rather than silently finding
   nothing at the new default.

`dbdatasync service install` does **not** use this resolver for `--repo` — it's passed straight through
to the registered service's `binPath` at install time. `--url` is different: it is **not**
baked into `binPath` — only `--repo` is — so the registered service always resolves `App:Url` the same
way `serve` does at every start (flag, absent here; then env var; then the config file). An explicit
`--url` given to `service install` instead seeds `App:Url` into `dbdatasync.config.yaml` once (if the
file doesn't already have one), the same value `setup`'s General tab would write. This is what makes
editing `App:Url` through the Admin screen actually take effect on the next restart — baking it into
`binPath` would have made the install-time value win forever.

### `dbdatasync.config.yaml`

A git-tracked file at `<RepoRoot>/dbdatasync.config.yaml` — the same `DbDataSync:*` settings
this document describes, in the format ASP.NET Core's own `appsettings.json` uses conceptually but
written as YAML:

```yaml
DbDataSync:
  App:
    Url: http://localhost:5080
  State:
    Engine: MsSql
    ConnectionString: "Server=sql01;Database=DbDataSyncState;"
```

It sits in the *same* precedence slot `appsettings.json` occupies — behind environment variables and
`--DbDataSync:Key`/CLI arguments, so either still overrides the file for a one-off run, and ahead of
nothing but the defaults baked into code. `serve --repo <fresh-root>` writes a starter copy the first
time it `git init`s a repo root (never touching one that already exists) with every key present but
commented out, as a reference rather than a default silently taking effect.

**No credential is ever written into this file.** `StateConnectionString` is validated the same way
`ConnectionConfig.ConnectionString` already is (`ConfigValidation.RejectEmbeddedCredential`) — a
`password`/`pwd`/`passwd`/`secret`/`accountkey`/`apikey` key in the string is rejected outright. The
password for a non-SQLite state store goes through the secret store instead, under the one fixed ref
`dbdata:sync:config:stateConnectionString` (see "Secrets," below) — the starter file's own comment names
this, and it and the connection string are spliced together at connect time.

### `dbdatasync server run`

Starts the API and the scheduler in one process. The server serves no web app: its screens are the
dbdatakit console's, paired with the server once (`dbdatasync server pair`, see
[Installing → Connecting a console](install.md#connecting-a-console)). A browser pointed at the server's address gets a plain-text
message saying so, and naming the console when `DbDataSync:Console:Url` (environment `DbDataSync__Console__Url`, or
`DBDATAKIT_CONSOLE_URL`) holds its address.

| flag | default |
| --- | --- |
| `--repo <path>` | see "Repo root resolution," above |
| `--state-db <path>` | `<repo>/state.db` |
| `--url <url>` | `--url` flag, else `DbDataSync__App__Url` env var, else the resolved config's `DbDataSync:App:Url`, else `http://localhost:5080` |

`--repo` git-inits the config repository if it isn't one already. Internally the resolved values
become `--DbDataSync:App:RepoRoot`, `--DbDataSync:State:DbPath` and `--urls`, so it's the exact same
configuration surface as running the API directly (see below) — just with CLI-flavored names and
defaults.

### `dbdatasync service install|uninstall|status|start|stop|restart|doctor`

Windows or Linux (systemd); `install` needs an elevated prompt (Windows) or root (Linux,
`sudo`). macOS keeps bailing with a pointer to `dbdatasync server run` / the container.

| flag | applies to | default |
| --- | --- | --- |
| `--repo <path>` | `install` | `CliOptions.DefaultRoot` — **not** the walk-up resolver; see above |
| `--url <url>` | `install` | `http://localhost:5080` |
| `--account <account>` (Windows) | `install` | `LocalSystem`; also `NT SERVICE\DbDataSync`, `DOMAIN\gmsa$`, or `DOMAIN\user` |
| `--password-stdin` (Windows) | `install` | none; the password of a named user account is read from standard input, never from the command line |
| `--user <user>` (Linux) | `install` | `dbdatasync` — created as a system user if it doesn't exist |

Windows: registers the tool's own installed executable with the Service Control Manager (the shared
`DbDataKit.Hosting.WindowsService` provisioner), with `serve --repo ...` as its command line; running `install`
again updates the registration instead of failing. `start`, `stop` and `restart` wait until the service reaches the state
(so a script or the installer can run `dbdatasync service start`), `status` shows state, account, start mode and command
line, and `doctor` checks the registration (account, command line, start mode, recovery actions) and the data directory.
The service starts automatically at boot (not delayed) and, as before, has no restart-on-failure actions; `doctor` says so. Linux: writes `/etc/systemd/system/dbdatasync.service`
(`ExecStart=` carries the same resolved values), `systemctl daemon-reload`s, and `enable`s it —
`install` does not start it; run `sudo systemctl start dbdatasync` for that, so the first start's own
output is visible rather than scrolling by during `install`. Either way, **a connection using
integrated authentication connects/authenticates as this account** — worth deciding `--account`/
`--user` deliberately rather than accepting the default.

### `dbdatasync cert use-pem|use-pfx|status|list|new-self-signed|enroll|renew|retrieve|templates|discover|bind`

`use-pem`/`use-pfx`/`status` work on any platform; everything else is Windows-only
— issues, installs, binds, renews and reports on the certificate Kestrel serves TLS with,
entirely from the CLI so setting up HTTPS never depends on HTTPS already working. Run
`dbdatasync cert` with no subcommand for the full flag list. A walk-through of each method is in
[certificates](certificates.md).

| command | does |
| --- | --- |
| `use-pem --cert <path> --key <path>` | points Kestrel at a PEM certificate + unencrypted private key file — the Linux/macOS answer; an encrypted key is not supported yet |
| `use-pfx --pfx <path>` | as `use-pem`, for a PFX/PKCS#12 file with no password |
| `status` | on any platform, whatever is configured — a file (`use-pem`/`use-pfx`) reports path, SANs, `NotAfter`, days remaining; a Windows-only store binding reports the same plus thumbprint and whether the resolved service account can read the private key |
| `list [--location LocalMachine\|CurrentUser]` | server-authentication certificates in `<location>\My` |
| `new-self-signed --dns <names> [--days] [--account]` | issues and installs a self-signed certificate, in-box .NET (`CertificateRequest.CreateSelfSigned`), no PowerShell |
| `enroll --dns <names> [--ca] [--template] [--account]` | submits a CSR to an enterprise CA over COM (`ICertRequest`); a template requiring approval reports a request id and exits — not an error |
| `renew [--ca] [--template] [--account] [--days]` | re-enrolls with the bound certificate's subject and SANs (a fresh request, not a signed AD CS renewal) — works even if the bound certificate has already expired |
| `retrieve --request-id <id> [--account]` | collects a pending enrollment from `enroll`/`renew` |
| `templates [--ca]` | best-effort: templates published on the configured CA; empty-plus-a-reason on any failure (not domain-joined, LDAP unreachable, access denied, `CaConfig` unset), never blocking — `--template` stays free text regardless |
| `discover` | finds the enterprise CA(s) and the templates they publish in Active Directory, unelevated. Exactly one CA (or one template) is saved to `DbDataSync:Certificates:CaConfig` (`Template`) automatically; several are printed with the `config set` command that saves one. A value already set is never overwritten |
| `bind --thumbprint <thumbprint> [--location] [--allow-invalid\|--no-allow-invalid]` | binds an already-installed certificate by writing the four `Kestrel:Certificates:Default:*` keys (below) into `dbdatasync.config.yaml`, committed the same way every other config write is |

`--account` on the issuing commands, because issuing and granting private-key read access are one
operation from the operator's point of view — resolved as an explicit `--account`, else the account the
installed `DbDataSync` service runs as (read back via `sc.exe qc`), else `LocalSystem` (which needs no
grant, since `LocalMachine\My`'s default ACL already covers it).

**Nothing takes effect until the process restarts** — `bind` only writes config; Kestrel's certificate
is resolved once at startup, the same as every other `DbDataSync:*`/`Kestrel:*` setting.

### `dbdatasync config get|set`

```
dbdatasync config get <key> [--repo <path>]
dbdatasync config set <key> <value> [--repo <path>]
```

Reads or writes one `DbDataSync:*` setting in `dbdatasync.config.yaml` — the same settings, and the same commit, as
**Admin → Configuration** on the server's screens in the dbdatakit console; `dbdatasync server setup` edits some of the same ones. `<key>` is the name from the
tables below, with or without the `DbDataSync:` prefix (`config set Notes:MarkdownRenderer rich`), and can be nested
(`config set Auth:Windows:AdminGroup "DbDataSync Admins"`) — any depth works; only
`App:RepoRoot` (how the file itself is found) is refused. A setting with a caution — a mode that widens what an
unauthenticated or unverified caller can reach — prints it before it is turned on, as that screen and `setup` show it beside
the control. The change waits for a restart of the service; `config get` reports what the file says, not what a running
process is using (the **Running** column of that screen compares the two).

### `dbdatasync invite`

Prints a fresh single-use invitation URL — for when the first-run one has scrolled off-screen, or the
process is a service with no terminal to print to in the first place. `--email <address>` is required: the invitation is
for that person, and the address is how the same person is one account whether they sign in with a passkey or with Windows
(the user principal name the domain gives is matched to it, ignoring case).

> **Since the web app moved to the console, no screen of this server redeems that URL**: people are made and sign in at the console
> (`dbdatakit user invite`). The command and the server's own sign-in remain for the API; a server that is only used through a console needs neither.

| flag | default |
| --- | --- |
| `--repo <path>` | see "Repo root resolution," above |
| `--state-db <path>` | same as `serve` |
| `--url <url>` | same as `serve` |
| `--email <address>` | required: the address the invitation is for |
| `--role Admin\|Viewer` | `Admin` |

Opens the state database directly rather than calling a running API — the situation it exists for is
"nobody can sign in," and an endpoint that needs a session is no help there. Resolves
`DbDataSync:State:Engine`/`DbDataSync:State:ConnectionString` the same way `serve` does (the resolved
config file, then `DbDataSync__*` environment variables), so it works against a `MsSql`- or
`Postgres`-backed deployment, not only the SQLite default — earlier versions opened SQLite
unconditionally and had no way to reach the other two. For SQLite, requires the state database file
to already exist (i.e., DbDataSync has been started at least once); for the other two engines, it
connects to whatever `State:Engine`/`State:ConnectionString` (plus the secret store, if needed) name.

### `dbdatasync health check`

| flag | default |
| --- | --- |
| `--repo <path>` | see "Repo root resolution," above — used only to find a config file's `DbDataSync:App:Url`, nothing else |
| `--url <url>` | `--url` flag, else the resolved config's `DbDataSync:App:Url`, else `http://127.0.0.1:8080` (the *container's* port, not `serve`'s 5080) |

Hits `{url}/api/health`; exits `0` on success, `1` otherwise. This is what the container's
`HEALTHCHECK` runs — inside the image there's no config file to find, so it always falls through to
the hardcoded default unless `--url` is passed (which the image's own `HEALTHCHECK` does).

### `dbdatasync secret set|list|remove`

Thin wrappers over the encrypted secret store connections use (see "Secrets," below) — reachable without
going through a connection's own save flow. Never prints a stored value back.

| command | does |
| --- | --- |
| `dbdatasync secret set <ref> <value>` | stores `value` under `ref` |
| `dbdatasync secret list [<ref> ...]` | reports whether each given ref is currently set (given none, checks the one fixed ref this build defines: `dbdata:sync:config:stateConnectionString`) |
| `dbdatasync secret remove <ref>` | deletes a stored value |

Example — setting the state store's password for a `dbdatasync.config.yaml`-configured deployment:

```
dbdatasync secret set dbdata:sync:config:stateConnectionString "Password=..."
```

### `dbdatasync version`

No flags.

## DbDataSync.Api process configuration

Read through ASP.NET Core's standard configuration chain — so every key below is settable
interchangeably as a `--DbDataSync:Key value` CLI argument, a `DbDataSync__Key` environment variable
(double underscore), under `"DbDataSync": { "Key": ... }` in `appsettings.json` /
`appsettings.Development.json`, or under `DbDataSync:` in `dbdatasync.config.yaml` at the repo
root. Neither shipped `appsettings*.json` sets any `DbDataSync:*` key today — every default below lives
in code. `dbdatasync.config.yaml` sits in the same precedence slot `appsettings.json` occupies — behind
environment variables and the command line — so a CLI flag or env var still overrides a value the file
sets; see "`dbdatasync.config.yaml`," above, for the file itself.

### `DbDataSync:*`

The key surface is grouped by area, and every bare boolean has been replaced with a named mode string
(`enabled`/`disabled`, or something richer where a setting genuinely has more than two states) — a mode
self-documents in `config get`/the Admin screen the way `true`/`false` never did, and leaves room for a
setting to grow a third state without another schema change. An existing `dbdatasync.config.yaml` using
the old flat names is migrated automatically the first time `server run`/`server setup` runs against it (see
"Migrating an existing `dbdatasync.config.yaml`," below).

#### `DbDataSync:App:*`

| key | env var | default | notes |
| --- | --- | --- | --- |
| `App:RepoRoot` | `DbDataSync__App__RepoRoot` | `<cwd>/dbdatasync-repo` under raw `dotnet run`; the CLI's `--repo` default under `dbdatasync server run` | git-tracked config store root |
| `App:Url` | `DbDataSync__App__Url` | `http://localhost:5080` | `dbdatasync server run`/`dbdatasync health check` read this themselves (see above) and translate it to `--urls`/Kestrel's bind address; also this deployment's own always-trusted passkey origin (see `Auth:Passkeys:*`, below) |
| `App:Server` | `DbDataSync__App__Server` | `kestrel` | `kestrel` or `httpsys`. `httpsys` is Windows-only and exists so the port can be shared with other processes — see "Sharing a port with HTTP.sys," below. Anything else refuses to start. Takes effect on restart |
| `App:AlternateUrls` | `DbDataSync__App__AlternateUrls` | none | every other origin this deployment is also reached at, beyond `App:Url` — comma- or semicolon-separated. Purely additive: `App:Url`'s own origin is always trusted for passkeys without needing to be listed here too |
| `App:TrustedProxies` | `DbDataSync__App__TrustedProxies` | none | proxies (IP addresses or CIDR ranges such as `10.0.0.0/8`, comma- or semicolon-separated) whose `X-Forwarded-For` and `X-Forwarded-Proto` headers are believed. Set it when a TLS-terminating reverse proxy sits in front of a plain-HTTP bind; see [Plain HTTP](#plain-http-and-reverse-proxies) |
| `App:AllowInsecureHttp` | `DbDataSync__App__AllowInsecureHttp` | `false` (`true` in the container image) | serve plain HTTP to every client. Credentials and data then cross the network in clear text |
| `App:TaskRunnerDllPath` | `DbDataSync__App__TaskRunnerDllPath` | none | start replication workers as `dotnet exec` of this `DbDataSync.TaskRunner.dll`, see below |
| `App:CliDllPath` | `DbDataSync__App__CliDllPath` | none | start the deep, connection-scoped library-validation check as `dotnet exec` of this `DbDataSync.Cli.dll`, see below |

Normally neither is set. The server starts a replication's worker, and a library validation, as **itself again**: `dbdatasync internal worker ...`
and `dbdatasync library validate ...`, using the same executable that is running the server (the launcher under an installed service,
a published executable, or `dotnet DbDataSync.Cli.dll serve`). Nothing then needs `dotnet` on the path, which is what lets Sync be published
self-contained. The two settings are for a host that is not `DbDataSync.Cli`, such as the test host, or `DbDataSync.Api.dll` run directly
in this repository's working tree. When neither is set and the running program is not `DbDataSync.Cli`, the files beside the running
assembly are used (or this repository's `DbDataSync.TaskRunner/bin` and `DbDataSync.Cli/bin`).

#### Plain HTTP and reverse proxies

A plain `http://` request is served only when it comes from the same machine (loopback), or when it arrived over HTTPS at a
TLS-terminating proxy that this deployment trusts. Anything else gets `400` with a message saying how to fix it. `/api/health`
is always answered, so a load balancer's probe works.

To run behind a proxy that terminates TLS, bind plain HTTP (the container image does: `http://0.0.0.0:8080`), set
`DbDataSync:App:TrustedProxies` to the proxy's address, and have the proxy send `X-Forwarded-Proto: https` (and `X-Forwarded-For`).
Those headers are believed only from the addresses listed; from anywhere else they are ignored. A proxy on the same host that
talks to a loopback bind needs nothing. With nothing trustworthy in front, bind HTTPS (see [certificates](certificates.md)).
`App:AllowInsecureHttp: true` turns the check off; the container image sets it, so set it back to `false` there when a proxy fronts the container.

#### `DbDataSync:State:*`

| key | env var | default | notes |
| --- | --- | --- | --- |
| `State:DbPath` | `DbDataSync__State__DbPath` | `<App:RepoRoot>/state.db` | the SQLite file; ignored when `State:Engine` is `MsSql` or `Postgres` |
| `State:Engine` | `DbDataSync__State__Engine` | `Sqlite` | which database backs the state store — `Sqlite`, `MsSql` or `Postgres`; see below |
| `State:ConnectionString` | `DbDataSync__State__ConnectionString` | none | how to reach that engine; required unless `State:Engine` is `Sqlite`; never put a password in it — see "Secrets," below |
| `State:Port` | `DbDataSync__State__Port` | `0` (ephemeral) | loopback-only runner-state listener; set to a fixed port if you'd rather firewall a known one than trust the loopback binding |
| `State:Retention:RunDays` | `DbDataSync__State__Retention__RunDays` | `90` | finished runs older than this are pruned hourly; `0` = keep forever |
| `State:Retention:RunMaxPerMapping` | `DbDataSync__State__Retention__RunMaxPerMapping` | `1000` | most recent N finished runs kept, *per table mapping* (not global); `0` = no cap |
| `State:Retention:PruningIntervalMinutes` | `DbDataSync__State__Retention__PruningIntervalMinutes` | `60` | how often the retention sweep runs |
| `State:Retention:ChangeCheckDays` | `DbDataSync__State__Retention__ChangeCheckDays` | `7` | how long the scheduler's change-check history is kept; `0` = keep forever |

### State store engine (`State:Engine`/`State:ConnectionString`)

SQLite by default — a file at `State:DbPath`, nothing to configure. `State:Engine` can instead be set to
`MsSql` or `Postgres` to run the state store (run history, the work queue, watermarks, users and
sessions) on infrastructure a deployment already operates and backs up, with `State:ConnectionString`
saying how to reach it. Two separate keys rather than one connection string carrying a provider hint,
so a typo in the engine name fails as "you named an engine that doesn't exist" rather than as a
driver-level parse error.

The schema is created on first open regardless of engine, and all three are meant to behave
identically — this is API-process-only config; `DbDataSync.TaskRunner` never reads it, since a runner
always reaches state over the loopback state endpoint rather than opening the store directly.

Two things worth being explicit about:
- **No cross-engine migration.** Pointing an existing deployment at a different `State:Engine` starts an
  empty store — it does not move anything. Choose once, at stand-up.
- **An unrecognized `State:Engine` value refuses to start**, naming the three built-in engines in the
  error — a typo here is worth surfacing loudly rather than silently landing on `Sqlite`.
- **`MsSql`/`Postgres` need their library installed.** Neither ships as a package this build carries by
  default — each resolves its connection through the same restorable-library mechanism
  `config driver install` uses. Run `dbdatasync library install microsoft-data-sqlclient` (for
  `MsSql`) or `dbdatasync library install npgsql` (for `Postgres`) once; a `State:Engine` set to
  either without its library installed fails at startup naming that exact command. See
  [Drivers and libraries](drivers-and-libraries.md) for the general mechanism — the same one a
  replication's own `MsSql`/`Postgres` connections use, and the one a descriptor driver's own `library:`
  field points at.

A run failing *either* retention cap is pruned along with its log lines; a run that hasn't finished is
never pruned regardless of age. Verification results are **not** covered by this pruning — a
verification run's `TaskRuns` row is pruned, but its result file and index entry are not (a known gap).

#### `DbDataSync:Nuget:Search:*`

| key | env var | default | notes |
| --- | --- | --- | --- |
| `Nuget:Search:Mode` | `DbDataSync__Nuget__Search__Mode` | `enabled` | whether the Libraries screen's search box may call the public NuGet index; `disabled` for an air-gapped or locked-down deployment |

#### `DbDataSync:Notes:*`

| key | env var | default | notes |
| --- | --- | --- | --- |
| `Notes:MarkdownRenderer` | `DbDataSync__Notes__MarkdownRenderer` | `basic` | `basic` or `rich` — whether Notes render tables, task lists and strikethrough with the full Markdown renderer instead of the small one they use by default. Basic by default: a note is written by one operator and shown in other people's sessions, and a richer renderer is a wider surface — raw HTML is never interpreted and only `http(s)`/`mailto` links are followed, but the risk is not zero. Images in a note show as links, not inline. Change it in Admin → Configuration, on `dbdatasync server setup`'s General tab, or with [`dbdatasync config set Notes:MarkdownRenderer rich`](#dbdatasync-config-getset); takes effect on restart |

#### `DbDataSync:Updates:*`

| key | env var | default | notes |
| --- | --- | --- | --- |
| `Updates:Mode` | `DbDataSync__Updates__Mode` | `disabled` | `manual` or `disabled` — whether the Updates screen looks up the releases available, which calls GitHub. The screen never applies an update; it gives the commands to run on the server either way. See [Installing → Updating](install.md#updating) |
| `Updates:Channels` | `DbDataSync__Updates__Channels` | `stable` | which channels the Updates screen lists, comma-separated: `stable`, `beta`, `snapshot`. A snapshot is a development build, checked only against a checksum published beside it |

### `DbDataSync:Auth:Network:*`

| key | env var | default | meaning |
| --- | --- | --- | --- |
| `Auth:Network:Admin` | `DbDataSync__Auth__Network__Admin` | `disabled` | `loopback` or `disabled` — trusts an unauthenticated request from loopback as Admin, no sign-in at all. There is no "from anywhere" option: unlike `Auth:Network:Viewer`, Admin trust never widens past loopback |
| `Auth:Network:Viewer` | `DbDataSync__Auth__Network__Viewer` | `disabled` | `remote`, `loopback`, or `disabled` — trusts an unauthenticated request as Viewer; `remote` trusts any origin, `loopback` restricts that to loopback only |

These replace the old blanket `Auth:Disabled` (which trusted *every* request, from anywhere, as
Admin) with these two role-scoped, network-trust settings — deliberately narrower for Admin, and able to
act as a fallback *alongside* Windows/Passkeys auth being configured, not only as a replacement for it:
a real session cookie always wins first if present.

**A real security consideration, not a formality**: on a shared host, loopback does not distinguish the
operator from any other local account — anyone who can reach `127.0.0.1` gets whatever `Auth:Network:*`
grants, with no sign-in. Leave both `disabled` unless the deployment is a single-operator box, or the
loopback caller is genuinely trusted (a local health check, a CI runner talking to its own scratch
instance).

### `DbDataSync:Auth:Windows:*`

| key | env var | default | meaning |
| --- | --- | --- | --- |
| `Auth:Windows:Mode` | `DbDataSync__Auth__Windows__Mode` | `enabled` | `enabled` or `disabled` — lets an operator configure a group name and still turn Windows auth off without clearing it |
| `Auth:Windows:AdminGroup` | `DbDataSync__Auth__Windows__AdminGroup` | none | Windows group whose members are admins |
| `Auth:Windows:ViewerGroup` | `DbDataSync__Auth__Windows__ViewerGroup` | none | Windows group whose members are viewers |

Windows authentication (`Negotiate`) is registered automatically when the process is running on
Windows *and* at least one group is configured — it's additive to session-cookie auth, not a mode
switch. If nothing here or under `Auth:Network:*`/`Auth:Passkeys:*` is configured, the only way in on a
fresh install is the bootstrap invite: printed as a startup warning whenever no users exist yet, and
always reprintable with `dbdatasync invite --email <address>`.

### `DbDataSync:Auth:Passkeys:*`

| key | env var | default |
| --- | --- | --- |
| `Auth:Passkeys:Mode` | `DbDataSync__Auth__Passkeys__Mode` | `enabled` |
| `Auth:Passkeys:RelyingPartyId` | `DbDataSync__Auth__Passkeys__RelyingPartyId` | `localhost` |
| `Auth:Passkeys:RelyingPartyName` | `DbDataSync__Auth__Passkeys__RelyingPartyName` | `DbDataSync` |

`RelyingPartyId` is a **bare domain** — no scheme, no port, and it cannot be an IP address (WebAuthn
requires a real domain name). Deliberately independent of `App:Url`, not derived from it: a
relying-party id is cryptographically bound into every passkey at the moment it's created, so silently
tracking `App:Url`'s hostname would invalidate every already-registered passkey the moment that URL
changed. Moving a deployment to a different host name therefore means re-registering its passkeys.

The set of origins a ceremony may come from (this server's own allow-list, separate from the browser's
own relying-party check) is **not** a setting of its own any more — `App:Url`'s origin is always
implicitly trusted, and `App:AlternateUrls` adds any others. Each one's host must match
`RelyingPartyId` or a subdomain of it, checked at startup — a misconfiguration logs a warning naming
exactly what's wrong, rather than failing silently inside a browser API the first time someone tries to
enroll a key.

### Migrating an existing `dbdatasync.config.yaml`

An operator upgrading from an earlier version does not need to hand-edit anything: the first time
`server run`/`server setup` runs against a `dbdatasync.config.yaml` still using the old flat key names, it rewrites
each one to its new grouped location (and, for what used to be a bare boolean, to the closest mode
string — `Auth:Disabled: true` becomes `Auth:Network:Admin: loopback`, deliberately narrower than the
old flag's "admin from anywhere") and commits the result, the same one-line-per-change history every
other config edit already produces. Environment variables and CLI flags using the old names are **not**
migrated — update those by hand if you set configuration that way.

### `DbDataSync:Certificates:*`

| key | env var | default | meaning |
| --- | --- | --- | --- |
| `DbDataSync:Certificates:ExpiryWarningDays` | `DbDataSync__Certificates__ExpiryWarningDays` | `30` | how many days before the bound certificate's `NotAfter` the daily expiry check starts raising a `CertificateExpiring` notification; raised at most once a day, and `CertificateExpired` once past `NotAfter` |
| `DbDataSync:Certificates:CaConfig` | `DbDataSync__Certificates__CaConfig` | none | the enterprise CA's `CASERVER\CA Name` string — read by `dbdatasync cert enroll`/`renew`/`templates`, and saved by `dbdatasync cert discover` (a CLI-process concern; the running API never needs to know which CA a certificate came from, only which one is bound) |
| `DbDataSync:Certificates:Template` | `DbDataSync__Certificates__Template` | none | the certificate template name for `dbdatasync cert enroll`/`renew`; `dbdatasync cert discover` lists the ones the CA publishes |

Windows-only end to end — see `dbdatasync cert`, above, for issuance/installation/binding.
The daily expiry check is a hosted service in the API process, registered only when
`OperatingSystem.IsWindows()`, on the same pattern `SchedulerService`/`RunPruningService` already use.

### `Kestrel:Certificates:Default:*`

Not a `DbDataSync:*` key — ASP.NET Core's own Kestrel configuration, read the same way (config file,
environment variable, CLI flag), and what `dbdatasync cert bind` writes into `dbdatasync.config.yaml`:

| key | meaning |
| --- | --- |
| `Kestrel:Certificates:Default:Subject` | the certificate's simple subject name — a store *lookup*, not a file path, so no certificate password is ever persisted by DbDataSync and the private key never leaves the Windows store |
| `Kestrel:Certificates:Default:Store` | always `My`, written by `bind` |
| `Kestrel:Certificates:Default:Location` | always `LocalMachine`, written by `bind` |
| `Kestrel:Certificates:Default:AllowInvalid` | `true` for a self-signed certificate (Kestrel validates the chain on load and refuses one it cannot build otherwise); `bind` reports this and leaves it as configured on every subsequent bind rather than silently clearing it when a CA-issued certificate replaces a self-signed one — use `--no-allow-invalid` to turn it off explicitly |

**Nothing takes effect until the process restarts** — resolved once at startup, same as every other
`DbDataSync:*` setting.

### Sharing a port with HTTP.sys

Opt-in, Windows only: set `DbDataSync:App:Server` to `httpsys` and the server's API is served by HTTP.sys, the
kernel-mode listener, instead of Kestrel. The reason to do it is that HTTP.sys lets several processes
listen on the same port, routed by host name. Kestrel is still the default, and still what Linux, the
container and the systemd unit use. The runner-state listener (`State:Port`) is a separate loopback-only
server and stays on Kestrel either way.

```yaml
DbDataSync:
  App:
    Server: httpsys
    Url: http://dbdatasync.example.com:80
```

What changes, and what you do about it:

| | Kestrel | HTTP.sys |
| --- | --- | --- |
| `App:Url` | any bind address, including `http://0.0.0.0:8080` | a URL *prefix*: `http://dbdatasync.example.com:80` only answers requests whose `Host` is that name. `http://+:80` takes any host and `http://*:80` is the weak wildcard (anything nobody else claimed). `0.0.0.0` is not valid. **Share by host name, not by path** — the console is served from the root of its origin, so a path prefix such as `/dbdatasync/` is not supported (startup logs a warning if you try) |
| Permission to bind | the port is free or it is not | a URL reservation for the service account unless it is `LocalSystem` or an administrator: `netsh http add urlacl url=http://dbdatasync.example.com:80/ user="<the service account>"`. `dbdatasync service install` does not create it yet |
| Certificate for https | `Kestrel:Certificates:Default:*` — what `dbdatasync cert bind`, `use-pem`, `use-pfx` and `new-self-signed` write | **Ignored** (startup logs a warning if it is set). Bind a certificate from the machine store to the port in the OS: `netsh http add sslcert hostnameport=dbdatasync.example.com:443 certhash=<thumbprint> appid={<any guid>} certstorename=MY`. The certificate screens, `cert status` and the expiry notification still read the Kestrel keys, so they do not describe an HTTP.sys binding |
| Windows sign-in | the Negotiate package, inside this process | HTTP.sys authenticates in the kernel and hands over a Windows identity; the same sign-in endpoint turns it into a session, with the same groups and the same rules |
| Request limits | Kestrel defaults | HTTP.sys defaults (its own request-size, timeout and queue limits). The file upload endpoint's 200 MB limit is set per request on both, but has not been verified under HTTP.sys |

Another process on the same port must claim a different host name (or prefix) with HTTP.sys itself —
IIS and anything built on `HttpListener` or `UseHttpSys` do. A Kestrel process cannot share the port.

Known gaps while this is opt-in:

- **Certificates are not integrated.** `dbdatasync cert`, the Admin certificate screen and the expiry
  notification only know about the Kestrel configuration above, so under HTTP.sys they do not create, bind or
  report on the certificate; use `netsh` and the Windows certificate store directly.
- **No URL reservation on install.** `dbdatasync service install` does not create one, so a service account
  that is not an administrator needs the `netsh http add urlacl` command above before it can start.
- **No path prefixes** — see the `App:Url` row above.
- **Startup errors are raw.** A missing reservation or a prefix another process already owns is reported as
  the underlying HTTP.sys error, not as a DbDataSync message naming the fix.
- **Shared-port behaviour has had limited testing** on real Windows hosts. Try it somewhere you can afford to
  find out before relying on it in production.

### Standard ASP.NET Core variables

Not DbDataSync-specific, but relevant: `ASPNETCORE_URLS` (checked directly at startup to decide whether
HTTPS redirection should be enabled — it isn't, unless an `https://` URL is actually bound) and
`ASPNETCORE_ENVIRONMENT` (set to `Development` in `Properties/launchSettings.json` for the dev-loop
`http`/`https` profiles).

## DbDataSync.TaskRunner process (CLI only)

Spawned per run — by `ProcessSupervisor` inside the API, or directly by `dbdatasync`. Hand-rolled
argument parser; an unrecognized flag is a hard error, not a silent ignore.

| flag | required | default |
| --- | --- | --- |
| `--repo-root <path>` | yes | — |
| `--state-db <path>` | yes | — |
| `--replication <name>` | yes | — |
| `--degree-of-parallelism <n>` | no | `4` |
| `--bulk-load-parallelism <n>` | no | `4` |
| `--state-endpoint <url>` | no | falls back to the `DBDATASYNC_STATE_ENDPOINT` environment variable |
| `--state-grace-seconds <n>` | no | `60` |

The worker runs **two independent lanes**, each with its own bounded queue and its own
pool of consumers:

- `--degree-of-parallelism` sizes the **change-processing lane** — incremental (`Primary`) passes.
- `--bulk-load-parallelism` sizes the **bulk load lane** — bulk load segments and verifications.

A long-running reload on the bulk load lane can no longer take a slot an incremental pass needs. When
the API spawns the runner it always passes both, from `changeProcessing.degreeOfParallelism` and
`changeProcessing.bulkLoadDegreeOfParallelism` in `task.yaml` (both on the replication's Schedule
card on the replication's screen); the `4`s above are the fallback for a runner started by hand and for a replication
whose config does not set them. Each number is table mappings processed at once within that lane;
a single mapping never occupies more than one slot in a lane regardless of the number, and the total
concurrency ceiling for the replication is the two added.

Two more environment variables exist here, but they're **internal, process-to-process only** — set by
the parent API when it spawns a runner, never something an operator sets by hand:

- `DBDATASYNC_STATE_ENDPOINT` — the loopback address of the process that owns the state store.
- `DBDATASYNC_RUNNER_TOKEN` — the runner's auth token for that loopback connection.

### The Bulk Load pipeline (`task.yaml` `bulkLoad:`)

A second pipeline beside `changeProcessing:`, used for an on-demand reload (what the
Bulk Load trigger queues) and for an initial load:

```yaml
bulkLoad:
  reader:
    kind: BatchReload
  cache:      # optional — null inherits changeProcessing.cache
  writer:     # optional — null inherits changeProcessing.writer
```

- `reader` defaults to the `BatchReload` Kind every driver is expected to offer for a whole-table
  read, so a replication that writes nothing under `bulkLoad:` still gets a working pipeline.
- `cache` and `writer` default to unset (`null`) and, when unset, inherit the replication's resolved
  `changeProcessing.cache`/`changeProcessing.writer` — a bulk (re)load into the same target table
  naturally uses the same staging and write mechanism as change processing unless an operator wants
  something different (typically an upsert-only writer for a first load).
- Same per-mapping override shape as `changeProcessing:` — a table mapping can set its own
  `bulkLoadReaderOverride`/`bulkLoadCacheOverride`/`bulkLoadWriterOverride`, each independent and each
  atomic (Kind and options together, never merged).
- The writer is warned about, not constrained, when it does not support reconciliation: an upsert-only
  writer is correct and cheaper for a first load into a table that was just created, since there is
  nothing yet to remove. The mapping editor's Pipeline card surfaces the capability; saving does not
  refuse it.
- Segmenting is **not** part of this — it stays on the table mapping's own `defaultSegmenting`, because
  how a table divides is a fact about the table, not about a pipeline.

**Breaking rename, no migration.** `Backfill` was renamed to `BulkLoad` everywhere — the
`RunKind`/`RunLane` enum members, the `BulkLoadBatches` table and `TaskRuns.BulkLoadBatchId` column,
and `changeProcessing.backfillDegreeOfParallelism` (now `changeProcessing.bulkLoadDegreeOfParallelism`,
see above). There is deliberately no schema migration and no old-key fallback: an existing state
database still has a table and column named `Backfill*`, which the renamed code no longer recognizes,
and an existing `task.yaml` carrying the old `backfillDegreeOfParallelism` key simply has that key
ignored by the deserializer. **Every existing state database, and any config using the old key, must
be recreated rather than upgraded.**

## Secrets

Connection credentials and the state database's password are in an **encrypted store** under the repository root: `secrets/secrets.yaml`,
each value AES-256-GCM encrypted under a key that is protected by DPAPI on Windows and kept in a file readable only by its owner
(`secrets/keys`, created on first use) elsewhere. `DBDATA_SECRETS_KEY_FILE` names a key file kept somewhere else, such as a mounted secret.
`secrets/` is added to the repository's `.gitignore` when the store is first opened. The same store is the one every DbData product uses
(`DbDataKit.Secrets.Store`); back up `secrets/` together with its key, or the values cannot be read again.

Names are `dbdata:sync:<kind>:<name>`: a connection named `<name>` has its password at `dbdata:sync:connection:<name>`, and the state
database's at `dbdata:sync:config:stateConnectionString`. Names are lower-cased, so two connections whose names differ only by case share a
secret. The names from before the store (`dbdatasync:connection:...`) are **not read**: the secrets are set again (`dbdatasync secret set`,
or save the connection with its password), and `dbdatasync secret set` with an old-form name is refused with the form to use.

When the store has no value, the environment answers: the name upper-cased, every character other than a letter or digit replaced by `_`,
prefixed `DBDATA_SECRET_`. A connection named `orders-db` resolves to `DBDATA_SECRET_DBDATA_SYNC_CONNECTION_ORDERS_DB`. A file named after the secret in
the directory `CREDENTIALS_DIRECTORY` names (systemd `LoadCredential=`, a container secret mount) is the last place looked.

## Sync screens' dev server (`DbDataKit/web/sync`)

| env var | default | meaning |
| --- | --- | --- |
| `DBDATASYNC_API_URL` | `http://localhost:5183` | target for Vite's `/api` and `/hubs` dev-server proxy |

No `.env` files ship with the project — this is the only variable Vite itself reads. The server serves no web app, so this dev server is a developer's way to work on the
Sync screens against a running API; the product's screens are the console's (`DbDataKit/web/console`, whose own dev server proxies to a console).

The docs are not served by either: the console's build generates them from this folder (`order.txt` lists the pages in order) into `/docs/sync`, and its dev server
does the same from memory, so edits to a page appear on the next load of the console's dev server.

## Container image

```sh
docker run -p 8080:8080 -v dbdatasync-data:/var/lib/dbdatasync ghcr.io/dbdatasync/dbdatasync:latest
```

`ghcr.io/dbdatasync/dbdatasync` is published for `linux/amd64` and `linux/arm64`, in a default and a smaller `runtime` variant —
see [Installing → Running in a container](install.md#running-in-a-container) for the tags.

- `EXPOSE 8080`; entrypoint is `dbdatasync server run --url http://0.0.0.0:8080` — no trailing `--repo` argument; override at `docker run` time (`docker run ... <image> --repo /some/other/path`) to pass different `dbdatasync server run` flags.
- `HEALTHCHECK` runs `dbdatasync health check --url http://127.0.0.1:8080`.
- The image carries no web app and no docs: the screens, and these docs, are the dbdatakit console's (its own image, `DbDataConsole/Dockerfile`) — see
  [Getting started](getting-started.md#reading-these-docs-in-the-console) and [Installing → Connecting a console](install.md#connecting-a-console).
- `VOLUME ["/var/lib/dbdatasync"]` — one mount is a complete deployment: the config repository and the state database live together, so a backup of this directory is a backup of everything that isn't the image itself.
- `ENV DbDataSync__App__RepoRoot=/var/lib/dbdatasync` — the environment-variable form of the `DbDataSync:App:RepoRoot` config key, honoured by the same repo-root resolver every command uses (below). `CliOptions.DefaultRoot` already resolves to the same path on Linux, so this is set explicitly rather than relied on as a coincidence.

## `tools/dev-harness` — development and testing only

Not application configuration — a separate tool for standing up the shared test engines (`testenv/`: two SQL Servers and PostgreSQL),
seeding data, generating live traffic, and verifying/corrupting a running replication for testing.
Run `tools/dev-harness help` for the full, current, self-documented list of verbs and per-verb flags
(`--tables`, `--rows`, `--rate`, `--duration`, `--parallelism`, `--target-engine`, and more).

Its environment variables:

| env var | default | meaning |
| --- | --- | --- |
| `DBKIT_TEST_PASSWORD` | `DbKit_Test_Pw1` | password of the shared test engines in `testenv/` (SQL Server's `sa`, PostgreSQL's `dbdatasync`); the same variable `testenv/testenv.sh up` reads |
| ~~`DBDATASYNC_MSSQL_SA_PASSWORD`~~, ~~`DBDATASYNC_POSTGRES_PASSWORD`~~ | | retired with `DbDataSync/docker-compose.yml`: the harness refuses to start while either is set, and says to use `DBKIT_TEST_PASSWORD` |
| `DBDATASYNC_HARNESS_TARGET_ENGINE` | `mssql` | or `postgres` — must agree across `up`/`verify`/`drift` |
| `DBDATASYNC_HARNESS_TABLES` | `1` | how many generated tables — same rule |

## Next: Getting started

Once DbDataSync is configured, see [Getting started](getting-started.md) to set up your first
replication.
