# Backup and recovery

What to back up, with which command, and how to get a lost install back. Each part of the suite's state has one answer; the recovery drill at the end is the order to do them in. (Plan 22 section 4, slices GP19 to GP22 of plan 22a.)

| Part of the suite's state | How it is protected | Command or setting |
| --- | --- | --- |
| **Definitions**: Sync configuration, Build projects, serving-layer configuration, connection definitions without secret values, app source, DbDataStore models | A **git remote** per configuration repository: every commit is pushed in the background, a remote that is down never fails a commit and catches up when it returns | the library `GitRemotePusher` (`DbDataKit.Config.Git`); **no product reads a `git.remote` setting yet** (see "Git remotes") |
| **The secrets store's key** | A separate backup of the data keys under a passphrase. **Never in git**, and the passphrase is kept apart from the file | `secret key-backup <file>`, `secret key-restore <file>` |
| **The state database**: identity home, pairing and trust, audit, approvals, change requests, run history, delivery outbox | On SQL Server or PostgreSQL: the database's own backups **and** `state backup`. On SQLite: `state backup` | `state backup --to <folder>`, `state restore --from <folder>`; the console's `state-backup` system task does it on a schedule (`runner.backup_directory`) |
| **DbDataStore's data, the warehouse** | The customer's database backups; Build can rebuild the warehouse | not the suite's |

## The state backup

`state backup --to <folder>` writes a snapshot of the product's state to `<folder>/state-<UTC time>` **while the service keeps running**. It is the snapshot `state export` writes (format `state-snapshot/1`: a manifest with every module's version and each file's size, row count and SHA-256), so there is one format to know. It reads in one transaction on SQLite and PostgreSQL, so it is consistent as of its start while writers go on; on SQL Server it reads committed rows table by table (a row written while it runs can be in one table and not yet in another, so take it at a quiet time or use the database's own backup there). It is written to a `.partial` folder and renamed only after it was re-hashed against its own manifest: **a folder named `state-<time>` is a backup that verified**. The secrets in it are as stored, encrypted; the keys that decrypt them are not in it (that is the key backup).

`state restore --from <folder>` restores into the **empty** database the product's settings name (the service stopped):

- given a folder of backups it takes the newest; given one backup it takes that one;
- the backup is checked against its manifest **before the target is touched**: one changed byte stops the restore and leaves the database as it was;
- a database that already holds a module is refused naming it (restore never merges into live data), and a backup made by a newer program than this one is refused;
- a failed import empties the target again, so it can be retried; afterwards the modules are migrated to this program's versions.

`state verify --snapshot <backup> --db` then compares the restored database with the backup, table by table.

The console runs the same snapshot as its system task `state-backup` (`runner.backup_directory`, `backup_every_hours`, `backup_keep`); the other servers' state is not backed up by it, so run `state backup` for each on a schedule of your own.

## The secrets key

The secrets file is useless without the key that encrypted it, and a key bound to a machine (Windows DPAPI) cannot be read on another. So the key is backed up on its own:

```
secret key-backup D:\vault\dbdatakit-secret-key.yaml          # asks for a passphrase of at least 12 characters, twice
secret key-restore D:\vault\dbdatakit-secret-key.yaml         # on the new machine: asks for the passphrase
```

- The file holds each data key sealed with AES-256-GCM under a key derived from the passphrase (PBKDF2-SHA256, 600,000 rounds), and the id of the active key; it says nothing about the machine. It is the format `secret-key/1` (`docs/formats.json`).
- **Restoring stores each key under the new machine's own protection**: a key that DPAPI bound to the old machine or account is thereby re-keyed to this one. A key the ring already has is kept, never replaced; the backup's active key becomes the active one only in a ring that has none (`--activate` makes it so in one that has).
- A wrong passphrase, a changed file or a key moved to another id is refused before anything is stored.
- **Without a backup** a key file that cannot be read on this machine says so and names the command; if there is no backup the secrets it protected cannot be read, and the way on is to set them again with `secret set` under a new key.
- Keep the file and the passphrase in different places, and out of git. The secrets themselves can also be exported (`secret export`, under a passphrase) to move a server.

## Git remotes

A configuration repository is pushed after each commit by `GitRemotePusher`: the commit is made first and never waits for, or fails because of, the remote. A push that fails (the remote down, a refused credential) is recorded with the remote's words, the token taken out, and tried again every 30 seconds; the next successful push carries everything committed meanwhile. The credential is a secret reference read for each push and given to git only through its credential callback: it is not in the remote's address, in `.git/config`, in a log or in a file of the repository (an address with a login in it is refused). `GitRemotePusher.Status` is what a product shows in `health`: the remote, whether a push is pending, when the last one worked, and the last failure. **Not wired yet:** the library is built and tested, but the products (Sync, the serving layer, DbDataStore, the console) do not yet read a `git.remote` setting or show the status in `health`; until they do, set the remote on the repository yourself (`git remote add origin ...`) and push from a schedule of your own.

## The recovery drill

Practise this once on a spare machine; the steps are the order that works.

1. **Install** the same version of the suite on the new machine (`docs/releases.md`).
2. **Restore the secrets key**: `secret key-restore <file>`.
3. **Restore the state**: create or point the settings at an empty state database, then `state restore --from <folder>`; `state show` lists the modules at this program's versions.
4. **Restore the definitions**: clone each configuration repository from its remote (the console's repositories, Sync's, the serving layer's, the DbDataStore models'); published definitions can be republished from them.
5. **Start** the services, **sign in**, open the Health page and read the data you backed up; run `state verify --snapshot <folder> --db` if you want proof.

A drill that fails at a step is a finding: fix it before you need it.
