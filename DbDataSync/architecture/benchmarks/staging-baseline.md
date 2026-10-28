# Staging baseline

Plan 13, A13. How fast and how heavy it is to stage a batch of changed rows into the target database through each of Sync's staging providers, before
A14 (SQL Server) and A15 (PostgreSQL) move the bulk paths onto `DbDataKit.Transfer.*`. Their done-when is "within 5% of this".

Run with `DbDataSync/tools/benchmarks-staging` (or the manual workflow *DbDataSync benchmarks*, which prints the same table in the run summary). 200,000 rows x 20 columns
(4,000,000 cells: int, nvarchar/varchar(50), decimal(18,2), datetime2(3) in a 5-column cycle), median of 3 runs, each run in its own process against a scratch database
on the shared `testenv/` engines (SQL Server 2022, PostgreSQL 17, in containers on the same machine). Every run checks that the staging table holds exactly the rows it was given.

Machine: Linux 7.0, 24 cores, .NET 10.0.13.

| provider | ms | rows/s | alloc MB | peak heap MB | peak working set MB | gen0 / gen1 / gen2 | GC pause ms |
| --- | ---: | ---: | ---: | ---: | ---: | --- | ---: |
| SQL Server, `SqlBulkCopy` (`MsSqlStagingTableProvider`) | 1,424 | 140,449 | 248.3 | 17.0 | 104.7 | 15 / 0 / 0 | 4 |
| SQL Server, batched `INSERT` (`BatchInsertStagingProvider`) | 111,847 | 1,788 | 2,302.2 | 22.3 | 110.4 | 144 / 80 / 2 | 126 |
| PostgreSQL, binary `COPY` (`PgCopyStagingProvider`) | 1,292 | 154,799 | 147.1 | 17.1 | 100.4 | 9 / 0 / 0 | 2 |
| PostgreSQL, batched `INSERT` (`BatchInsertStagingProvider`) | 11,034 | 18,126 | 2,370.5 | 107.0 | 194.6 | 189 / 178 / 57 | 1,431 |

What it says:

- The bulk paths (A14, A15) are the ones that matter: about 1.3 to 1.4 seconds for 200,000 rows, and a small, flat heap. The batched `INSERT` fallbacks are 8x (PostgreSQL) to 79x (SQL Server) slower and allocate ten to fifteen times as much, which is why they are only the fallback.
- SQL Server's batched insert is slow because a 20-column row leaves room for about 100 rows per statement under the 2,100-parameter limit; PostgreSQL's limit is 65,535.
- Numbers depend on the machine and on how busy it is. Compare a later run only with one from the same machine, run back to back.

## After A14 and A15 (bulk paths on `DbDataKit.Transfer.SqlServer` and `DbDataKit.Transfer.Postgres`)

The table above was measured on a 24-core machine and is the reference for the *shape* of the numbers. A14 and A15 were measured against it by running the same tool, built from `main` and from the branch,
back to back on one machine: 4 cores shared with other jobs (load average 7 to 24 throughout), SQL Server 2022 (Developer, from the `mssql/server:2022-latest` image's files, run directly) and PostgreSQL 16.15 on the same host,
.NET 10.0.13, 200,000 x 20, three interleaved rounds of "before, after", each cell the median of 5 runs (the tool's own). The absolute numbers are therefore about 1.7x the baseline's; only the before/after pairs mean anything.

| provider | before (`main`), ms per round | after, ms per round | median before | median after | change |
| --- | --- | --- | ---: | ---: | ---: |
| SQL Server, `SqlBulkCopy` | 2,325 / 2,365 / 2,159 | 2,492 / 2,177 / 2,151 | 2,325 | 2,177 | -6.4% (faster; noise) |
| PostgreSQL, binary `COPY` | 2,292 / 2,240 / 1,928 | 1,931 / 2,010 / 2,065 | 2,240 | 2,010 | -10.3% (faster; noise) |

Allocation is unchanged (SQL Server 248.4 -> 250.4 MB, PostgreSQL 147.0 -> 142.7 MB), peak managed heap +0.5 to +0.2 MB (17.6 -> 18.2, 17.4 -> 17.6), peak working set +1 MB, GC counts identical (15 / 0 / 0 and 8 / 0 / 0).
The run-to-run spread on this machine (about 15%) is larger than any difference between the two builds, so the honest reading is "no measurable change", not "faster". An earlier "after" with 2,048-row staging batches
(rather than 128) was *not* within noise: the held batches were promoted to generation 1 and 2 and cost 20 to 90 ms of GC pause, which is why `StagingBatches.BatchRows` is 128.
