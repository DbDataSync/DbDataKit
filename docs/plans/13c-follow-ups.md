# 13c. Follow-ups from the work of 2026-10-28 to 2026-10-30

Status: **open** (written 2026-10-30). Open items that came out of the pull requests of those days and that no plan row or
other document records: decisions that are the owner's, tests that failed once without a known cause, and paths that
could not be run where the work was done. Each line says where it came from, what is needed and what is known. "Verified"
means run or read in the code; "inferred" means reasoned and worth a second look. Close an item by deleting its line in
the pull request that settles it.

## 1. Decisions for the owner

| # | Item | Source | What is needed | What is known |
| --- | --- | --- | --- | --- |
| D4 | Does Sync's server keep its own invite and sign-in remnants? | U11 (#260) | Decide whether `dbdatasync invite`, the bootstrap invite file and `Auth:Passkeys:*` stay. | Nothing redeems the invite URL now that Sync has no screens; marked in `DbDataSync/docs/configuration.md`, not removed. |
| D5 | Retake or drop the committed screenshots in `DbDataSync/screenshots` | U11 (#260) | Decide. They show Sync's own frame and the suite no longer regenerates them. | `DBDATASYNC_E2E_SCREENSHOTS=repo` retakes them with the console's frame. `getting-started.md` shows them. |
| D6 | Where the product docs live: carried by the module and served by the console, or served by the paired server through the relay | U11 (#260) | Re-evaluate (decided "for now": module-carried). Many docs are about configuration, not screens. | The viewer reads a `DocsSource`, so a relay-backed source needs no viewer change. Docs then match the console's screens, not the paired server's version. |
| D7 | Extend the docs check | X03 (#244) | Decide: the README-completeness rule for Sync, Proxy and Build (their READMEs would need filling in first); a rule for `.json` files, which are skipped now. | `scripts/check_docs_commands.py` reads commands from the sources. |
| D8 | Show when a waiting server first announced | L03 (#254) | Decide. It needs a console API change: the pending entry is rewritten on every announcement, so only the last time is stored. | The list shows "last announced N s ago". |
| D9 | The compose files for Proxy, Sync and Store (L03) | L03 (#254) | Slices to define. Proxy's management listener needs enabling and a stable address; Sync's image serves plain http, which pairing refuses; Store has none. | `dist/compose/build.yml` works and runs in CI. |
| D10 | Remove Sync's legacy migrations | X01 (#245) | Decide (E04 is the likely place). `LegacyConfigMigration.cs` and `LegacyRootMigration.cs` migrate old installs, which contradicts the plan's clean break. | They are not in plan 13 section 6. |
| D11 | A slice for moving Build's CLI onto the shared router | X01 (#245) | A plan row. Build's `CliApp` (603 lines) has no C-lane slice, so that part of "three CLI parsers" waits on nothing. | Sync's own dispatch for `tool`, `update`, `launcher`, `invite`, `internal` and `version` also remains. |
| D12 | Move relationships onto models as `references:` | J08 (#199) | Decide. Relationships are written in the semantic file between models; moving them is a model-schema change of its own. | Not done in J08. |
| D13 | Validate a PostgreSQL connection as PostgreSQL in Build's semantic layer | J08 (#199), J05 (#198) | A small follow-up now that J05's emitter has landed. J08 validates a PostgreSQL connection as SQL Server and `project show semantic` shows the real engine. | |
| D14 | The SSAS recording run for J20, and the transport questions | J20 (#197) | Run the SSAS job once with `ssas_record` from Actions and read the recording. The owner confirmed HTTP (msmdpump) is not used by Excel. | Not verified: whether ADOMD connects through the forwarder (host name, NTLM on loopback), whether SSAS compresses its XML, Kerberos SPN behaviour. If the recording is unreadable: decode frames in the tool, or try `Transport Compression=None` (an unverified property). |
| D15 | Decisions already recorded elsewhere, listed so they are not lost | | | Datetime on SSAS (review later; `docs/semantic/ssas-gaps.md`); Arrow path type decisions, caps and replay, and the run record that still writes rows as JSON (`docs/semantic/open-questions.md`, J24). |
| D16 | Pin script package hashes in a tracked lock file | S07 | Decide, then a plan row if wanted. A `.csx` script's `#r "nuget: Id, 1.2.3"` packages are resolved into the script cache's package store and fetched when the script is first validated; nothing in the config repository records their hashes, so the store trusts the first download and two machines (or a later re-fetch) can get different bytes under the same id and version. | S07's row in `13a-suite-slices.md` (its "Not done"); S02's resolver already caches by hash with a lock file in `DbDataKit.Scripting`, which is the likely mechanism to write into the repository. Libraries (S05) were not checked in this review: say whether they have the same gap. |
| D17 | Apply the shared ODBC connection-string policy to Build's ODBC logins | B02 (fourth part, `suite/b02d-odbc-flight-logins`) | Decide, then a release note. Build now reads an ODBC login through the shared `OdbcConnectionString`, but still sends the value to the driver manager as written, because the shared `OdbcProvider` composer applies the strict policy (ambiguous strings refused) to a string Build users wrote whole: a behaviour change. `FlightSqlLogin`'s `grpc://host:port;option=value` grammar stays Build's own format and maps onto `FlightSqlSettings`. | Same shape as B02c, which did the move for SQL Server and PostgreSQL (the connection file's `tls:` block now applies over the string). The decision is whether to refuse ambiguous ODBC strings in Build and say so in a release note. |
| D18 | Write the Windows and Entra group-to-role mapping row for the identity home | U13 (#263) | A plan row. U13's row says the mapping "is a row of its own, written when this slice starts"; U13 is built and no such row exists. Also not built there: refresh tokens. | A serving layer on a domain-joined Windows host federates Windows (Negotiate) itself; one on Linux signs in through Entra ID or another OpenID Connect provider (plan 20). The mapping maps Windows and Entra groups to suite roles. |

## 2. Tests that failed without a known cause (audit, do not re-run)

| # | Test | Source | What happened | What is needed |
| --- | --- | --- | --- | --- |
| T1 | Console `first-run.spec.ts` | U11 (#260) | Failed once, then passed on two reruns; the failing run's output was lost. The story takes about 37 s against a 45 s local timeout, so a timeout is likely (a guess). | A timing audit: where the seconds go (passkey step, server start), then raise the timeout or split the story. |
| T2 | Sync `runs-watermarks-refresh` 02 | U08 (#253) | `NaN` for the countdown seconds in one direct run; passed since and passed through the console. | An audit of how the countdown is read. |
| T3 | Sync golden-path 04 | U08 (#253) | The first SQL Server connection of a Sync process takes about 15 s on a busy machine and the step waits exactly 15 s. The console setup now pays that cost first; the direct run passed narrowly at 17.0 s (the direct run is gone since U11). | Check the console run for the same race, and make the wait independent of first-connection time. |
| T5 | Sync TaskRunner integration tests | A14/A15 (#202) | Four tests failed in one run on a busy shared machine (each about 34 s), then the node died; they passed on three reruns. The evidence points to the environment but it is not proven. The tests now fail with the failed run's own log (`AssertSucceeded`). | Watch CI; if it recurs, the log now says why. |
| T6 | Build `WebServerTests.A_request_without_the_token_for_another_host_or_from_another_origin_is_refused` | B06 (#213) | Failed once in the full Build unit suite; passes alone (9 of 9, twice). | An audit of what in the full run it depends on. |
| T8 | `pending-servers.spec.ts` reject case | L03 (#254) | A rejected server re-announces within 30 s, so there is a very small window where it could reappear before the assertion. | Close the window or assert after the next announcement. |

## 3. Paths that could not be run where the work was done

- **A14a (#215):** MySQL, MariaDB and Oracle staging (the value gate covers SQL Server and PostgreSQL targets only); Oracle, JDBC and Flight reader types (documentation or code only).
- **L03 (#254):** pairing a Sync server by announcement (its image serves plain http); the Postgres deploy part of the console story (`DBDATA_E2E_PG` not set).
- **Library-install specs:** `admin-library-install` cannot reach nuget.org in the sandbox. That is the machine, not a defect, but it means the specs have not run there.
- **U13 (#263):** the identity home's SQL Server and PostgreSQL schemas render from the same templates as SQLite but have not run (its 18 tests ran on SQLite).
- **J07 to J09 (#199):** the calendar template's T-SQL lowering was only checked by compile before CI; the `release` group, the slow engines and the Windows/Wine run were not run.
