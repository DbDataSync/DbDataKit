# 13b. Items waiting on the owner's review

Status: **open** (written 2026-10-27). These are the slices of [13a](13a-suite-slices.md) and one design review that Claude has not started because each needs a decision that is the owner's. Each section stands alone: what the slice is, why anyone
would want it, what is actually in the way, the options, a recommendation, what is verified and what is not, and what waits on it. "Verified" means read in the code or run; "inferred" means reasoned and worth a second look.

| # | Item | One line | Waits on it |
| --- | --- | --- | --- |
| 1 | A14 | Sync's SQL Server bulk staging onto the shared writer | A16 |
| 2 | A15 | Sync's PostgreSQL COPY staging onto the shared writer | A16 |
| 3 | A16 | Sync's merge and delete-insert writers onto the shared strategies | B08 |
| 4 | A19 | Build's Flight SQL client and login onto the shared client | nothing |
| 5 | A20 | Three shared packages with no (or only a test) consumer: adopt or delete | nothing |
| 6 | B06 | Build's write gate onto the shared write gate | B07, B08 |
| 7 | B07 | Build's statement log onto the shared log | nothing |
| 8 | B08 | Build's cross-engine copy code onto the shared transfer code | nothing |
| 9 | N02a | not waiting on anyone: reviewed by Claude, see plan 20 section 10.7 | |

Dependencies between them: A14 and A15 come before A16; A16 and B06 come before B08; B06 comes before B07. A19 and A20 are independent. Decisions on A14 and A15 are the same kind of decision, so read them together; B06 is the one that sets the
terms for B07 and B08.

What is *not* here: the scripting slices (S02 to S10) are decided (plan 14, D9) and are Claude's to build; H02 to H05 are done; W05 is done; E01 and E04 are low priority rather than waiting on a decision.

---

## 1. A14: Sync's SQL Server bulk staging through `Transfer.SqlServer`

**What it is.** When Sync applies changes to a SQL Server target it first loads the changed rows into a staging table, then merges from there. `MsSqlStagingTableProvider` (193 lines, `DbDataSync.Drivers.MsSql`) does the load with `SqlBulkCopy`.
The shared library already has `SqlServerBulkWriter` (`DbDataKit.Transfer.SqlServer`), also `SqlBulkCopy`, written for the same job. **No product uses it yet** (verified: only its own tests reference the package; the same holds for `Transfer.Postgres`), so A14 would be its first real consumer and would also be the proof that it is fast enough. A14 would make Sync use the shared one and delete its own bulk code.

**Why do it.** One implementation of "stream rows into SQL Server fast and safely" instead of two; fixes and tuning land once; the shared write gate (checked, logged writes) covers Sync's staging too. It is also a step toward Sync, Build and the planned DbDataStore
all writing through the same code.

**What is in the way (verified).** The two take different inputs. Sync hands `SqlBulkCopy` a `DbDataReader` whose cells are whatever .NET type the *source* driver produced (a SQL Server `datetime2` as `DateTime`, a PostgreSQL `numeric` as `decimal`, an Oracle
`NUMBER` as `decimal` or `double`, ...). The shared writer takes `DataBatch`es whose cells follow a fixed contract (`ClrContract`: for example every canonical date is a `DateOnly`, every unsigned 64-bit integer a `ulong`), and adapts them for `SqlBulkCopy` itself.
So between Sync's reader and the shared writer a conversion layer is needed that turns "whatever the source returned" into the contract. That layer is where behaviour can change: a value that `SqlBulkCopy` accepted loosely today (a `double` into a `decimal` column, a string into a `datetime2`)
would either be converted the same way, converted differently, or refused. Sync also stages two extra things the shared writer does not know about: an `OperationColumn` (insert/update/delete marker) and an identity `ordinal` that the later chunked merge ranges over.

**The bar already set.** A13 measured today's performance (`DbDataSync/architecture/benchmarks/staging-baseline.md`): 200,000 rows by 20 columns in 1,424 ms (140k rows/s) on `SqlBulkCopy`; the slice's done-when is "within 5% of that". The same file shows the
fallback batched `INSERT` path is 79x slower on SQL Server, so the bulk path matters.

**Options.**
1. *Do it with a conversion layer in Sync*, built from the source column's declared type, with the 5% benchmark and a per-type test table (every canonical kind from each source engine) as the gate. Cost: roughly the M the plan says, most of it in the type table and its tests.
2. *Do it later* (after A16, or never): keep Sync's own staging. Cost of waiting: two bulk implementations stay; nothing else is blocked except A16 and B08, which can themselves be skipped.
3. *Narrow it*: only adopt the shared writer for sources whose reader already yields well-typed values (I expect Sync's own SQL Server and PostgreSQL sources do; I have not checked), leave the loose-typed ones on today's path. Smaller, but leaves both paths alive, which defeats most of the point.

**Recommendation.** Option 2 for now. The gain is deduplication, not capability; the risk is silent value-handling changes in a replication tool, where a wrong value is worse than a duplicate class. If you want it done, option 1 with the type table as the first deliverable
(reviewed before any code moves).

**Questions for you.** Is deduplicating Sync's bulk code worth a type-conversion layer and its risk? If yes, should a value that does not fit its column be an error (strict) or converted as today (lenient)?

**Not verified.** I have not read every source driver's reader to list the types they return; the type table would start with that.

---

## 2. A15: Sync's PostgreSQL COPY staging through `Transfer.Postgres`

**What it is.** The same change as A14 for PostgreSQL: `PgCopyStagingProvider` (binary `COPY`, in `DbDataSync.Drivers.Postgres`) onto the shared `Transfer.Postgres` writer.

**Why, what is in the way, options, recommendation.** As A14; the answer should be the same for both. Differences worth knowing: PostgreSQL's binary COPY is *stricter* about types than `SqlBulkCopy` (it needs the exact PostgreSQL type for each value and fails
the whole copy on a mismatch), so the conversion layer matters more here, and the baseline is 1,292 ms (155k rows/s) for the same 200,000 by 20 test. The fallback batched `INSERT` is 8.5x slower.

**Question for you.** Same as A14. If A14 is deferred, defer this one.

---

## 3. A16: Sync's merge and delete-insert writers through the shared strategy builders

**What it is.** After staging, Sync applies the rows to the target with its own SQL writers: `MsSqlMergeWriter` (133 lines), `MsSqlDeleteInsertWriter`, `MsSqlMergeReconcileWriter`, plus the PostgreSQL equivalents. The shared library (`DbDataKit.Transfer.Strategies`) has
six apply strategies that produce the statements for the same jobs: full replace, delete-insert by key, merge by key, delete-insert by range, watermark append and key-reconcile delete (with the same delete-ratio guard Sync uses). A16 replaces Sync's writers with those strategies.

**Why.** The same reasons as A14, and it is the larger deduplication: the SQL for merge and delete-insert per engine is where subtle bugs live, and Build's copy code needs the same statements.

**What is in the way.** *Verified:* it needs A14 and A15 first (the strategies read from a staged table through the shared staging provider interface). *Inferred, not checked line by line:* Sync's writers apply in chunks over the staging table's ordinal and have
Sync-specific behaviours (how deletes are marked, hash comparison, per-table settings) that the shared `StatementSet` may not express. The slice's done-when is "replication integration tests unchanged", which needs Sync's own engine stack (SQL Server, PostgreSQL, MySQL, MariaDB, Oracle
via its compose file) to run locally and in CI.

**Options.** (1) Do it after A14/A15 with Sync's integration suite as the gate. (2) Don't; keep Sync's writers. (3) Do the inverse: let the shared strategies adopt whatever Sync's writers do better (a smaller change that improves the shared code without touching Sync).

**Recommendation.** Decide together with A14/A15. If they are deferred, so is this. Option 3 is worth considering on its own: it needs no change to Sync.

**Question for you.** Is Sync's merge behaviour considered the reference (the shared code should match it), or the shared strategies (Sync should match them)?

---

## 4. A19: Build's Flight SQL client and login onto `Connections.FlightSql`

**What it is.** Build can read from a Flight SQL server (for example a Proxy). That code is `FlightSqlRowSource` (528 lines) and `FlightSqlLogin` (158 lines) in `DbDataBuild.Execution`. Sync's Flight SQL client was moved into a shared library
(`DbDataKit.Connections.FlightSql`, A17, done) and is tested against a real Proxy in CI (A18, done). A19 would make Build use that shared client.

**Why.** A third copy of the Flight SQL client is not wanted; the shared one is already tested against Proxy.

**What is in the way (verified).** They are not the same shape. The shared client is an ADO.NET connection (`FlightSqlConnection`, `Command`, `DataReader`) configured by `FlightSqlSettings`: address, auth `None`/`Basic`/`Bearer`, a resolved credential, skip-certificate-validation, catalog, timeouts,
parameter mode, data source and compression. Build's login is a richer connection string (`grpc+tls://host:port;token=...`) with options the shared settings do not have: `token_env` (token from an environment variable), `token_command` (run a program to get a token, for example `az account get-access-token`), `ca_file`
(trust a specific CA) and arbitrary `header.<name>` call headers. And Build's `FlightSqlRowSource` also does catalog reads through Flight SQL's metadata calls (tables, schemas, primary keys, imported keys), which the shared client (a query client) does not have; Sync's
driver kept its own metadata readers for the same reason.

**Options.**
1. *Extend the shared client* with CA file, token sources and custom headers (useful to Sync and others too), keep Build's login grammar as a thin parser into `FlightSqlSettings`, and leave the metadata calls in Build (as Sync did).
2. *Leave Build's client alone*: it works, is covered by Build's Flight conformance group, and Build is its only user of these options.
3. *Replace Build's login grammar* with the shared connection settings (breaking Build users' login values: a clean break, with a message naming what to change).

**Recommendation.** Option 1 if you want the duplication gone; the extension is small and benefits the shared client. Option 3 is not worth a break. Honest value is low either way: it is a deduplication with a moderate amount of work and a Flight server needed to test (`testenv up flight`).

**Question for you.** Do you want Build's token sources and CA-file options available to Sync and Proxy's clients too? If yes, option 1 pays for itself; if not, option 2.

---

## 5. A20: three shared packages with no real consumer

The slice asks, for each, to "adopt it (a consumer with tests) or delete it from the solution". Each is its own decision. **Verified today:**

**(a) `DbDataKit.Secrets.Cli`: already adopted.** Build and Proxy both reference it (the shared `secret` verbs), and it has tests. Nothing to decide; I would mark it done.

**(b) `DbDataKit.Connections.Descriptor`: only its own tests use it.** It reads `engine/1` YAML descriptors (a database engine described as data: its ADO.NET library, dialect, connection-string key spellings, fixed and overridable properties). It is the port of Sync's `driver.yaml`, but Sync still has its own
`DbDataSync.Drivers.Descriptor`; neither Sync nor anything else uses the shared one. **However:** plan 14 (the semantic layer) names it as the single engine registry (R5), and the J-lane agent's branch (J06, open pull request #185) is extending this package right now. So deleting it
would collide with that work. Options: adopt it through J06 (and later move Sync's reader onto it, or delete Sync's), or delete it and let J06 start something else. **Recommendation:** adopt through J06; decide after seeing #185.

**(c) `DbDataKit.Dialect.Arrow` (canonical type to Apache Arrow mapping): used only by `Transfer.Abstractions`, not by Proxy.** It was ported from Proxy's `ArrowColumnWriter`. Plan 10 (C7) made Proxy adopting it "optional, measure first": Proxy writes Arrow on the hot query path, and the plan said not to switch
unless a benchmark shows no regression. So the open decision is whether Proxy should use the shared mapping (deduplication, but touches Proxy's fastest path) or whether the shared package stays as the mapping for the transfer code only. **Recommendation:** leave Proxy alone, keep the package
(it has a real consumer in `Transfer.Abstractions`), and close the item.

**Questions for you.** (b) adopt through J06, or delete? (c) leave Proxy's own Arrow writer?

---

## 6. B06: Build's `MutationGate` replaced by the shared `WriteGate`

**What it is.** Every write Build sends to a database goes through `MutationGate` (362 lines): it checks the statement is allowed for the command, logs it, runs it, and refuses anything that tries to reach a database any other way (a test, `GateInvariantTests`, scans the source for that).
The shared library has `WriteGate` (267 lines) for the same job, used by the new code. B06 would delete Build's and use the shared one, and move the invariant test into the shared suite so it protects both.

**Why.** Build's gate is the safety-critical part of the tool (nothing writes without it). One gate means one thing to audit, one place to fix, and the same guarantee for Sync, Build and DbDataStore.

**What is in the way (verified, this is the real work).** The gates differ in behaviour, not just in shape:
- *Logging detail.* Build's log keeps statement text *and parameter values* (`StatementLogEntry.Parameters` with `Value`); the shared gate's default keeps hashes and types only, with text and (redacted) values as opt-in levels (`HashAndTypes`, `WithText`, `WithValues`). Build could run the shared gate at `WithValues`,
  but "redacted" is a new behaviour for Build's log.
- *Parallel loads.* Build's refresh runs loads side by side on sibling gates that share one ordinal sequence and one log (`OpenSiblingAsync`, built for step 5 of the refresh plans). The shared gate has no siblings.
- *Row counts.* Build reports the *last* statement's row count of a script; the shared gate counts differently.
- *Bulk copy.* Build's gate also bulk-copies copy rows (`BulkCopyAsync`, loose rows with its own logical types); the shared writers take canonical batches (this is B08's problem).
- *Tests.* `MutationGateTests` (206 lines) use Build's internal executor interface, so "tests unchanged" (the plan's wording) cannot hold; they would be rewritten against the shared gate.

**Options.**
1. *Move Build onto the shared gate*, extending the shared gate with what Build needs (siblings with a shared ordinal; an opt-in full-value log level that keeps the values Build keeps today), and rewrite Build's gate tests. The safest version: keep Build's current tests as the specification and port them one by one.
2. *Keep both gates*; add a test that the invariants hold for both (the shared `GateInvariantTests` run against both). Gets the audit benefit without moving Build.
3. *Move the other way*: make the shared gate a superset of Build's so Build's can later be a thin wrapper.

**Recommendation.** Option 2 now (cheap, keeps the safety guarantee uniform), with option 1 as the follow-up only if B07 and B08 are wanted. Changing a safety-critical gate for deduplication alone is the weakest case for the work.

**Questions for you.** Which log detail should Build keep (values, redacted values, or hashes only)? Is deduplicating the gate worth rewriting Build's gate tests?

---

## 7. B07: Build's statement log onto the shared write gate's log

**What it is.** Build writes a JSON-lines statement log per run (`FileStatementLog`, 48 lines) with retention (`StatementLogRetention`, 24 lines: logs older than N days are removed by the next deploy or refresh). The shared library has its own `IStatementLog`/`FileStatementLog`
with the three detail levels. B07 would drop Build's and use the shared one.

**Why.** Small, mostly mechanical, and it is where Build's *log format* (which users and the console read) would be unified with the other products'.

**What is in the way.** It depends on B06 (the log is written by the gate). The log *format* would change (shared entries have a typed parameter list with value hashes, Build's has name and value), which matters to anyone reading Build's logs, including the console's activity views. *Not checked:* who reads Build's
log today besides people (I did not find a reader in the console).

**Options.** (1) After B06: adopt the shared log, accepting the format change with a release note. (2) Keep Build's file log even if the gate moves (write an adapter).

**Question for you.** Is the Build log format part of what users depend on (scripts reading it), or free to change with a release note?

---

## 8. B08: Build's `Transfer.cs` (copies) onto `Transfer`

**What it is.** A Build model can be a *copy* of a table from another database (`fan_in_fan_out` is the template that shows it). The rows are read from the origin, converted by the column's declared logical type (`TransferValues.Convert`, `Transfer.cs`, 71 lines: DuckDB-flavoured type names to .NET values) and
written by the gate's `BulkCopyAsync` (SQL Server `SqlBulkCopy`, PostgreSQL binary `COPY`, in `MutationGate.cs`). B08 would use the shared `Transfer` conversion, writers and the shared gate's bulk write.

**Why.** The same row-moving code as Sync's staging, written twice; unifying it is part of making Sync, Build and the planned DbDataStore share their data-movement code.

**What is in the way.** It needs B06 (the bulk write is on the gate) and A16 in the plan (the row strategies); it also has the A14/A15 type-conversion problem in a different spelling: Build converts by *declared* DuckDB-style logical types, the shared writers by *canonical* types. A mapping between the two exists in
principle (Build's logical types are in `LogicalTypes`; canonical types are `DbDataKit.Dialect`), but the exact behaviour (what happens on a value that does not fit, how `TIMESTAMP WITH TIME ZONE` is normalised to an offset) is currently specified by Build's tests (the `fan_in_fan_out` template and the copy conformance cases).

**Options.** (1) Do it after B06, with the copy conformance cases as the gate. (2) Don't; Build's copy path is small (71 lines of conversion plus ~60 of bulk write) and works.

**Recommendation.** Option 2 unless B06 is done. It is the smallest duplication of the three Build slices.

**Question for you.** Same as A14: is deduplication here worth changing conversion behaviour?

---

## 9. N02a: the serving contract (design review)

**Not a wait on the owner.** The slice's own first step, a review of the contract against the ten modules, is Claude's work and was done on 2026-10-27: plan 20 section 10.7 has the module-by-module result, and the contract in code gained what the Flight SQL module needs.
N02 (Flight SQL onto the contract) is in progress. An earlier version of this section listed it as waiting on the owner; that was wrong.

---

## Suggested order for the review

1. **B06** first: it sets the terms (log detail, siblings) for B07 and B08, and it is the one that touches safety.
2. **A14/A15** together (one decision), then **A16** (follows from it).
3. **A20** (three quick decisions; (a) needs none).
4. **A19**.
