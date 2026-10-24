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
