# DbDataKit.Update.Service

Side-by-side layout, the `update-plan.json` journal, and the privileged helper's state machine. No Windows SCM calls here (Plan 03 supplies `IServiceControl`).

## Layout
```
<root>\versions\<ver>\   current -> versions\<ver>   config\   updates\{staging\<ver>\,backup\<planId>\,update-plan.json,update-result.json}
```
`InstallLayout` is the only place that turns plan strings into paths (`IsSafeVersion`, `ResolveUnderUpdates`). `ICurrentPointer`: `LinkCurrentPointer` (junction on Windows via `mklink /J`, symlink with atomic rename elsewhere) and `MarkerFileCurrentPointer` (`current.txt`, portable/tests).

## Flow (`UpdateHelper.RunAsync`)
1. Service side: `UpdateStager.Stage` copies manifest + signature + artifact into `updates\staging\<ver>\` and writes the plan (no trust decisions).
2. Helper: load + `PlanStore.Validate` (product, safe versions, paths under `updates\`, loopback health URL) -> `RecoveryPlanner.Decide`.
3. **Verify** (re-done by the helper): signature with the helper's own `TrustStore`, manifest hash equals plan, version equals target, installed == plan.from, `UpdateChecker.Evaluate` (revocation, downgrade, channel, min-safe), artifact size+sha256, then the helper itself extracts to `versions\<to>.extracting` and renames. A verify failure leaves the machine untouched (service never stopped) -> `Rejected`.
4. Stop -> Backup (config copy; product `IPreUpdateBackup` hook) -> Migrate (`IMigrationRunner`) -> Repoint -> Start -> HealthCheck (`IHealthProbe`) -> Commit (prune, retain N).
5. Any failure after Stop -> rollback: stop, restore config, repoint to `from`, restart if it was running, delete new version -> `RolledBack`, `update-result.json`.

## Journal and recovery
Every step state is persisted atomically (temp + rename). On the next run `RecoveryPlanner` decides: before Repoint -> `Resume`; after Repoint but not committed -> `Rollback` (new version never proved healthy); `RollingBack` -> continue rollback; terminal -> nothing. `OperationCanceledException` models an interruption and deliberately leaves the journal untouched.

## Irreversible migrations
`IMigrationRunner.HasIrreversibleSteps` requires a verified `IPreUpdateBackup` (else the update is refused before any change). Once such a migration starts, `IrreversibleMigrationStarted` is journaled: later failures yield `NeedsOperator` instead of rollback and recovery only goes forward. Migration steps must be idempotent.

## Decisions / threat notes
- The plan is untrusted input: a compromised service can at worst request install of a *signed, not-older* release. `AllowDowngrade` is a helper option (admin CLI flag), not a plan field.
- Health URL must be loopback so the plan cannot make SYSTEM call out.
- Updater console `dbdata-updater` (src/DbDataKit.Update.Updater) wires this to real pointer + loopback health probe; its `IServiceControl` is a stub that throws until Plan 03.
- Not done: Authenticode check, Event Log entry, Scheduled Task/systemd triggering, file locking against two helpers running at once (add a named mutex in the host).
