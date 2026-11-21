# INSTALLER-5 (work in progress)

## Audit against the code (2026-11-21)

- Nothing of the task existed: `suite-install-windows` (`suite-release.yml`, dry runs only) ran one leg with the hosted runner's Windows Server default.
- Since the row was written, the Gateway allows a configuration repository another account owns when its account can write it
  (`docs/install.md`, "The Gateway's configuration repository"), and the folder step (INSTALLER-8) does not check the owner on Windows
  (INSTALLER-P32). So on today's code the workstation leg is expected to start every service; what shows the workstation's conditions is the owner
  of the folders the install made (the user, not Administrators) and the Gateway's `git/.gitconfig` with its `safe.directory` entry.

## Built so far

- `suite-scoop-e2e.ps1 -WorkstationOwner`: sets `nodefaultadminowner=1` under `HKLM\SYSTEM\CurrentControlSet\Control\Lsa`, and puts the same default
  owner on the script's process token (Windows reads the value at logon only; the runner's session logged on before), then asserts a new folder is
  owned by the user. Both legs print the owner of every item under the data folder after the install, and whether the Gateway allowed its repository.
- `suite-release.yml`: input `workstation_owner`; the job `suite-install-windows` runs a matrix `server` / `workstation`.
- Real-time scanning is left out on the owner's word (2026-11-21).

## Defaults taken

- The token's default owner is set in the test's process as well as the registry value (example: without it, `scoop install -g` on the runner
  still makes `C:\ProgramData\DbDataKit\gateway` owned by `BUILTIN\Administrators`, since the value is read when an account logs on). The other
  option is a new local administrator logged on after the value is set, running the whole test.

## Open questions

- none yet
