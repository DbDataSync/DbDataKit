# INSTALLER-30 (work in progress)

## Built

- `HealthNoun.Create(probe, defaultUrl, pinned)` (DbDataKit.Cli): `health check` takes `--repo`; with no `--url` it asks the `listen:` address of the
  settings file, with the default URL's path; with no file or line, the default URL.
- The console, the Runner and the Workspace use it (`settings.yaml` / `server.yaml` through `ListenPorts.ReadPinned`); the Gateway's `health check`
  takes `--repo` too (it already read its `listen` setting, but only from the default folder).
- Tests: `SharedNounTests` (pinned address, any-address mapped to localhost, default when nothing is pinned, `--url` wins, unreadable file fails);
  `ServerStartupTests.A_bare_health_check_asks_the_address_the_server_pinned_...` (a real Runner).
- Docs: `docs/install.md` ("Ports"), the four READMEs.

## Defaults taken

- D1: a settings file that cannot be read (no permission, or a `listen:` line that is not an address) fails the check (exit 1, naming the file and
  `--url`), instead of asking the default address. Example: a non-root user runs `dbdatarunner health check` on Linux, where the data folder is 0700
  for the service user: the check says it cannot read `/var/lib/dbdatakit/runner/server.yaml` and suggests `--url`, rather than asking 7840, where
  another program could answer.
- D2: a `listen:` address of `0.0.0.0`, `::` or a host name is asked on `localhost`; a specific address is asked there.
