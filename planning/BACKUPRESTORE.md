# BACKUPRESTORE: one backup set, one restore drill

## Goal

Every server snapshots its own state into one location the console sets; the console backs up the keys once, under one passphrase; Monitoring shows each
snapshot's age; one documented drill restores all four services, and CI runs it. Workspace projects push to their git remote.

## What exists today

- `StateHousekeeping` in every server snapshots its own state when `state.housekeeping.backup_directory` is set; each server's folder is set by hand.
- `state backup` and `state restore` (every product), `secret key-backup` and `key-restore` (`secret-key/1`).
- `docs/backup-and-recovery.md` lists the commands; no console page, no `health` line for a stale backup, no end-to-end drill.
- `GitRemotePusher` (`DbDataKit.Config.Git`) is built; no product has the `git.remote` setting or its health line.
- No group key change exists: a stolen machine keeps a group key that still opens the group's secrets.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| BACKUPRESTORE-1 | One backup set: the console sets one location and passes it to every paired server; each snapshots into its own subfolder; Monitoring shows each snapshot's age | none | M | servers suite: after one interval the four subfolders hold a snapshot and Monitoring lists their ages |
| BACKUPRESTORE-2 | The console backs up the keys file once (the console's and the group keys it holds) under one passphrase | BACKUPRESTORE-1 | S | a key backup restores on a new console and opens the groups' secrets |
| BACKUPRESTORE-3 | The recovery page on the console, the `health` lines (no backup in N days), and the CI drill that restores all four services from one set | BACKUPRESTORE-1 | M | the drill in the servers suite: four services restored from one set serve their published definitions and runs; Playwright of the recovery page |
| BACKUPRESTORE-4 | `git.remote` on Workspace projects (one remote per project) with its `health` line; then the Gateway's and the console's configuration repositories | none | M | a commit in a project is pushed to a test remote; a failing push shows on the health line and the commit stands |
| BACKUPRESTORE-5 | A group key change after a stolen machine: the console makes a new key, hands it to every live instance, re-encrypts the group's secrets and revokes the stolen instance's link and pairing; the old key is kept only to restore old backups; the screen reminds that values read before the change must be rotated at their source | none | M | after the change the old key opens nothing and every instance serves with the new one |
| BACKUPRESTORE-6 | The console's setup checklist gets its git-remote step: today it lists the git remote as "not available"; the step opens the screen where a Workspace project's `git.remote` is set and is finished when a project has a remote whose last push succeeded (never by a click), skippable like the other steps | BACKUPRESTORE-4 | S | vitest of the checklist: the step is open with no remote, finished once a project's health line shows a successful push; Playwright: the step opens the remote's screen |

## Needs from other subjects

- The console holds every Gateway and Runner group's key, by the group's instance id (`ConsoleGroupKeyRing`, `dbdatakit:server:group-key:<instance id>` in its secret store), and hands it at the join (`ConsoleJoinGroups`).

## Shared areas touched

- `DbDataKit.State.Engines` (housekeeping), `DbDataKit.Secrets.*`, `DbDataKit.Config.Git`, the console's Monitoring.
- `docs/backup-and-recovery.md`, `docs/ui-editors.md`.
