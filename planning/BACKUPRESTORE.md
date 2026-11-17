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
| BACKUPRESTORE-1 | One backup set: the console sets one location and passes it to every paired server; each snapshots into its own subfolder; Monitoring shows each snapshot's age | INSTALLER-12 | M | servers suite: after one interval the four subfolders hold a snapshot and Monitoring lists their ages |
| BACKUPRESTORE-2 | The console backs up the keys file once (the console's and the group keys it holds) under one passphrase | BACKUPRESTORE-1, INSTALLER-15 | S | a key backup restores on a new console and opens the groups' secrets |
| BACKUPRESTORE-3 | The recovery page on the console, the `health` lines (no backup in N days), and the CI drill that restores all four services from one set | BACKUPRESTORE-1 | M | the drill in the servers suite: four services restored from one set serve their published definitions and runs; Playwright of the recovery page |
| BACKUPRESTORE-4 | `git.remote` on Workspace projects (one remote per project) with its `health` line; then the Gateway's and the console's configuration repositories | none | M | a commit in a project is pushed to a test remote; a failing push shows on the health line and the commit stands |
| BACKUPRESTORE-5 | A group key change as BACKUPRESTORE-P1 says | BACKUPRESTORE-P1, INSTALLER-15 | M | after the change the old key opens nothing and every instance serves with the new one |

## Needs from other subjects

- INSTALLER-12: servers joined to the console (the console tells each where to snapshot).
- INSTALLER-15: the console holds every group key.

## Proposed decisions

### BACKUPRESTORE-P1: a group key change after a stolen machine (awaiting the owner)

Option A: the console makes a new key, hands it to every live instance, re-encrypts the group's secrets, and the stolen instance's link and pairing are
revoked; the old key is kept only for restoring old backups. Option B: no key change; the administrator rotates every secret the group holds. Scenario: a
Gateway laptop instance is stolen. Under A the administrator clicks "change group key" and the thief's copy decrypts nothing new; database passwords the
thief may have read are still rotated by hand. Under B the administrator rotates each login at its database. Recommendation: A, with a reminder that values
read before the change must be rotated.

## Shared areas touched

- `DbDataKit.State.Engines` (housekeeping), `DbDataKit.Secrets.*`, `DbDataKit.Config.Git`, the console's Monitoring.
- `docs/backup-and-recovery.md`, `docs/ui-editors.md`.
