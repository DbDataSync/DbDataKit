# INSTALLER-14 work in progress

## Built so far

- Nothing yet: the branch is started from origin/main 241e235b7.

## Plan

- Shared registration of the four services (`SuiteServiceRegistration` in `DbDataKit.Hosting.Cli`), used by each product's `service install` and by setup.
- `SingleServerSetup` in `DbDataKit.Hosting.Cli`: discovery, registration by library call, folders, start, wait, failure files, summary, `--stop`;
  a Windows host (SCM, access lists) and a systemd host (users, units, owners).
- The console's half in `DbDataKit.Server` (tokens, join requests, joined check, setup link) and the verb `dbdatakit setup single-server`.
- Tests: fakes in `DbDataKit.Hosting.Cli.Tests`, real programs in `DbDataKit.Server.Servers.Tests`; CI legs in `suite-release.yml` (Windows zips, Linux systemd).

## Open questions

## Defaults taken
