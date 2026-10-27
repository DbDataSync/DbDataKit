# Plan 24: upgrades, compatibility and caching

Status: **direction decided by the owner (2026-10-27)**, one question at a time. Not sliced yet.

## 1. Upgrades

1. **The console drives upgrades.**
   - The console shows each paired server's version and the versions available.
   - It upgrades servers one at a time, using each product's own installer (MSI, package or image; plan 13 section L).
   - The shelved self-updater (`DbDataKit.Update.*`) stays shelved.
2. **Serving groups upgrade rolling, draining first,** one instance at a time:
   - stop taking new work;
   - finish the queries already running;
   - upgrade;
   - pass the health checks (plan 20, health per module);
   - rejoin the group.

   If an instance fails its health check, the upgrade stops there, and the rest of the group keeps serving on the old version.
3. **State changes during an upgrade** follow plan 21's expand-then-contract rule for the suite's own state databases:
   - a release only adds what the previous release can ignore;
   - it removes what is no longer used one release later.

## 2. Compatibility

1. **N and N-1.** Every part works with the current and the previous release of every other part. This covers the console, the
   back-ends, the serving layer, the identity home and the published definition formats, so parts can be upgraded one at a time.
2. **Checked at pairing.**
   - A pairing, or a reconnection after an upgrade, exchanges release numbers.
   - A pair further apart than N-1 is refused, with a message naming both versions and what to upgrade.
3. **Checked in CI.** A compatibility job runs the previous release of each part against the current one: management API, pairing, publish
   and the serving protocols.
4. **Formats:** a published definition carries its format version. A serving instance reads the current and the previous format and
   refuses anything newer with a message, the same clean-break rule as configuration files.

## 3. Caching

1. **Results are cached in the serving layer, opt-in per dataset.**
   - The cache is off by default.
   - An owner turns it on per semantic model or data source, in its definition, so the setting is published and promoted with it.
2. **The cache key keeps rules and channels safe.** It includes:
   - the query;
   - the values of the access-rule attributes that apply (plan 22 section 2), so users with the same effective access share entries and
     no one sees another's rows;
   - the channel and any preview draft (plan 21);
   - the published version.
3. **Expiry on data change, with a time-to-live as a backstop.**
   - An entry is cleared when something it depends on changes: a Build load, a Sync run, a DbDataStore write, or a new publish. Lineage
     (plan 18) says which entries depend on what.
   - Each cached dataset also has a time-to-live, for changes the suite cannot see (a source written outside it).
4. **Where the cache lives:** in memory per instance, with an optional shared store for serving groups. An instance that misses fetches
   from the database as usual, so a cold or lost cache never changes results.
