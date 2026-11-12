# {{title}}

Pulls the pages of a REST API into a staging table. A template: the code is yours, change what the API and the table need.

* `package.yaml` the task `pull-orders` (hourly, or by hand) and its two connection slots: `api` (kind `http`) and `staging` (a database connection).
* `src/pull.csx` the job: `OutsideHttp` with cursor paging (retries, a rate limit and the failure classes come with it), `StagingLander` writing each page as one batch under a key (the cursor it was asked with), so a rerun writes nothing twice.
* `tests/` run with no network and no database: the API's answers are `tests/recordings/orders.json` (`http-recording/1`: write it from the API's documentation, never with a secret in it), the table is a `MemoryWarehouse`. A request with no recorded answer fails the test naming it.

Secrets are never in a file: bind the `api` slot to a connection on the Runner (the connection names its secret by reference). Bind a sandbox endpoint in a preview channel.

**Not done for you:** opening the database connection of the `staging` slot (it needs your engine's driver: see the message `Outside.OpenStagingAsync` gives), and the sign-in methods of an http connection beyond a token or a login (the runner hands those over today).
