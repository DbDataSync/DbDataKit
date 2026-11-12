# {{title}}

Picks up the CSV files that appear in a folder of a share and lands their rows in a staging table. A template: the code is yours, change the folders, the columns and the CSV options.

* `package.yaml` the task `pick-up-csv` (every 15 minutes, or by hand) and its two connection slots: `share` (kind `folder`) and `staging` (a database connection).
* `src/pickup.csx` the job: a file still being written is left for the next run, a file whose content came before is skipped (`Files.ClaimAsync`, by hash, whatever it is called), the rows are read by `DelimitedReader` and landed by `StagingLander` as one batch under the file's hash, and the file is moved to `/done`. A record that does not fit its column is collected (its number and reason, never its value).
* `tests/` run with no share and no database: the share is a `FakeFileShare`, the table a `MemoryWarehouse`.

The job talks to a share through `IShare`, three calls. `LocalFolderShare` is a folder the Runner's machine can read (a mounted share). SFTP, FTPS, SMB and object storage have no client in the kit yet (plan 27a IX04 to IX06): when they exist, a class of yours implements `IShare` over them and `Outside.OpenShareAsync` returns it.

**Not done for you:** opening the database connection of the `staging` slot (it needs your engine's driver: see the message `Outside.OpenStagingAsync` gives).
