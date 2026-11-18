# INSTALLER-30 (work in progress)

Scope (owner, 2026-11-18): only the Gateway's open of its configuration repository; install and the other startup steps are unchanged (INSTALLER-6).

## Built so far

- `GitRepositoryOwnership` and `GitSafeDirectory` in `Shared/src/DbDataKit.Config.Git`; the Gateway opens `config` through it (`GatewayManagementHost.Map`).
- Tests in `Shared/tests/DbDataKit.Config.Git.Tests/GitRepositoryOwnershipTests.cs`, one of them with real libgit2 and a second owner (root or sudo).
- `docs/install.md`, "The Gateway's configuration repository".

## Defaults taken

- The allow is written as a `safe.directory` entry in a git settings file of the Gateway's own, `<data>/git/.gitconfig`, made the process's global git
  settings (LibGit2Sharp `GlobalSettings.SetConfigSearchPaths`), with the account's earlier global file included first. libgit2 reads `safe.directory`
  only from global and system settings files, and LibGit2Sharp's only other switch (`SetOwnerValidation(false)`) turns the check off for every path.
  Example: the service starts, git refuses `C:\ProgramData\DbDataKit\gateway\config` (owned by `CONTOSO\ada`), the probe writes and removes a file in
  `config` and `config\.git`, the file `C:\ProgramData\DbDataKit\gateway\git\.gitconfig` lists that folder and nothing else, and the open succeeds.
- Writability is a probe (create and delete a file) in the folder and in its `.git`; both must pass.
- The owner in the message is read on Windows from the folder's security descriptor; on Linux from `stat -c %U` (falls back to "another account").
