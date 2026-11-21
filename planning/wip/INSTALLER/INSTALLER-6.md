# INSTALLER-6 (work in progress, paused)

Paused before CI: the INSTALLER-5 leg did not reproduce the workstation's refusal (docs/ci-lessons.md, run 44950522575), and the session was to be
told before INSTALLER-6 goes on. This branch holds what was written while that run was going, so nothing stays on one machine.

## Audit against the code (2026-11-21)

- Already built: the services make the shared `logs` folder on demand (Windows, `SharedLogsFolder.Ensure` in the failure file's `Prepare`); keys,
  certificate and state are made at first start by every server. Not built: `service install` gave the top folder no explicit owner; the Gateway's
  `service install` wrote `config/server.yaml` (`EnsureStarter`); the units used `DynamicUser=yes`; `install.sh --system` made no user or unit; the
  Linux failure file was in `/var/log/dbdatakit`; the Gateway's `git/.gitconfig` allow list.

## Written so far (local tests only, no CI run yet)

- `service install` of the four servers: the data folder's spec names the Administrators group as owner (`AclSpec.Owner`, `AclSpecification.OwnerSid`,
  `FileSystemAclService` sets it).
- Gateway: no starter file at install; the folder step writes it when `config` is missing; `GitRepositoryOwnership` no longer allows a folder
  (`GitSafeDirectory` and `<data>/git/.gitconfig` removed), it fails naming folder, owner, account and fix; tests rewritten.
- Windows folder check refuses an owner other than the service's account, Administrators, SYSTEM or TrustedInstaller (INSTALLER-P32 option B).
- Linux: `SuiteService.FailureFileIn` puts the failure file in `/var/log/dbdatakit/<program>/`; units run as `User=<program>`, `Group=dbdatakit`,
  `StateDirectoryMode=0700`, `LogsDirectory=dbdatakit/<program>`, `LogsDirectoryMode=0700` (generator options added); `install.sh --system` (bound
  `SERVICE=1`) makes the group, the user and the unit, and says "up to date" on a second run.
- `suite-scripts.ps1`: a member's service starts before `server pair` writes in its folder.
- Tests: `suite-scoop-e2e.ps1` section "Each service creates and owns its data"; `dist/tests/suite-systemd-e2e.sh` with `fake_releases.py`; job
  `suite-install-linux`; both Windows legs in every dry run (input `workstation_owner` removed). docs/install.md, dist/README.md and
  docs/suite-single-server-install.md updated.
- Local: Shared Startup, Systemd, Config.Git, Hosting.Cli, Security.Acl, WindowsService tests pass; console, Runner, Workspace service/start tests pass;
  dist tests pass. Gateway tests not built (DuckDB not fetched here). No CI run.

## Left

- Run the Gateway suite (golden `dbdatagateway.service` to regenerate with UPDATE_GOLDEN=1), the dry run with both Windows legs and the Linux job.
- When main has docs/install-hardening.md: remove its three "In progress" items INSTALLER-6 builds and make the page describe what is built.
- Planning: remove the row, clear INSTALLER-7, -14, -22's dependency, P13's and P32's references; P32 kept as a default saying B is built.

## Defaults taken

- `install.sh --system` writes the unit and runs `daemon-reload` but does not enable or start the service (example: after `sudo ./install.sh --system`
  the person runs `systemctl enable --now dbdatagateway`).
- The Gateway writes its starter `server.yaml` at any start on a data folder without `config` (example: `dbdatagateway server run` on an empty test
  folder now leaves `config/server.yaml` with `schemaVersion: 2`).
