# Plan 23: first-run setup and data quality

Status: **direction decided by the owner (2026-10-27)**, one question at a time. Not sliced yet.

## 1. First-run setup

1. **The default install is a single-machine bundle.**
   - One installer puts the console, the serving layer (with the `identity` module, plan 20) and the chosen back-ends (Sync, Build,
     DbDataStore, DbDataApps) on one machine.
   - They are paired with each other automatically (plan 13 W12 pairing, with no codes typed) and keep their state in SQLite.
   - Scaling out later is a documented step: move state to SQL Server or PostgreSQL, add serving instances, move back-ends to other
     machines. It is not a reinstall.
   - Installing a single product on its own stays possible (plan 13: each product is a standalone install).
2. **The first administrator arrives through a one-time setup link.**
   - The installer prints, and logs, a single-use link that expires after a set time.
   - Following it creates the first administrator's account in the identity home, using the same machinery as invitations
     (`DbDataConsole` already has them).
   - After that, the link is dead, and a new one can only be made from the machine itself (`dbdatakit setup link`).
3. **A setup checklist in the console** guides the first steps:
   - the sign-in method (local accounts, Windows, OpenID Connect or Entra ID);
   - the first connections;
   - git remotes for the configuration repositories (plan 22 section 4);
   - delivery channels (plan 22 section 5);
   - a first Build project from a template.

   Each item can be skipped, and the checklist stays reachable later.

## 2. Data quality

1. **One check model, shared** (`DbDataKit.Quality`).
   - **Kinds of check:** not null, unique, accepted values, range, referential (values exist in another dataset or entity),
     freshness (data no older than a target), and row-count change (beyond a set percentage).
   - **Attachment:** a check can be attached to any dataset or entity, including a column of one.
2. **Every product uses it:**
   - Build's tests are expressed as checks, run after a refresh;
   - Sync runs checks after a replication run, freshness above all;
   - DbDataStore runs checks on its entities (beyond its write rules, which stay validation at write time);
   - the serving layer reports freshness for what it serves.
3. **Severity per check: warn or block.**
   - **Warn** alerts through the delivery service (plan 22 section 5) and marks the dataset in lineage and the catalog.
   - **Block** does the same, and also stops what depends on the dataset:
     - downstream Build runs wait;
     - a publish or promotion that reads it is refused, naming the check;
     - in the serving layer, the dataset keeps serving its last good published version.
4. **Results** feed monitoring (plan 13 W10) and lineage (plan 18). A failed check is visible on the node, and impact analysis shows
   what it blocks.
