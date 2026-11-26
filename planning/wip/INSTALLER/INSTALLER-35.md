# INSTALLER-35 (work in progress)

Built so far: nothing yet. Started from `backup/installer-16`, `origin/main` merged.

Plan:
- `package promote` (every form; each sends a version between runner groups) answers `RemovedCommands.ConsoleDeploys` (`RunnerCli.Removed`).
- Its callers move: help snapshot, `DbDataRunner/README.md`, `DbDataRunner/CLAUDE.md`, `docs/publishing.md`, the csproj comment,
  `docs/release-notes-pending.md` (new entry), `planning/INSTALLER.md` (the pairing-by-code line).
- A test in `RunnerCliTests` shows the answer; `ConsolePromotionTests` and the story `runner-promote.spec.ts` show the console's Promote.

Open questions: none.
Defaults: none yet.
