# Release notes: what has changed behaviour since the last beta (2026-11-13)

Each entry says what to do about it. Add to it in the pull request that makes the change, one entry per change, without rewriting other entries; a
release moves the entries into its notes and empties this file.

## The install is four services

- **The Store server is gone (plan 33 SC23); the install is four services (console, Workspace, Gateway, Runner).** `dbdatastore server`, `service`, `health`, `state` and `secret`
  answer with a sentence that names where each feature went, and the `DbDataStore` service, its MSI, its container image and its place in `dbdatakit-full` are gone. Author a model in a
  store project of the Workspace; deploy and contract its tables from the Deploy tab there (Runner tasks of the model's package, run by `dbdatastore --runner`); write rows on the Rows
  tab against a Gateway's store module and decide change requests on the console's Approvals page. The server's state database and secret store are not read any more: nothing is
  imported. `dbdatastore model put|publish|unpublish`, `storage create` and `gateway add|list|remove` stay, on a folder of models (`--repo`). Unregister the old service
  (`sc delete DbDataStore` or `systemctl disable --now dbdatastore`) before you install this release.
- **The Build server is gone (plan 33 SC23); dbdatabuild is a command line on a project folder and the runtime a Runner starts.** `dbdatabuild server` (with `project`, `proxy`
  and `schedule`), `service`, `health`, `state`, `secret` (with `send --to runner`), `lineage` and `semantic publish|rollback` answer with a sentence that names where each feature went
  (exit code 2), and the `DbDataBuild` service, its MSI, its container image, its compose file, its console screens and its place in `dbdatakit-full` are gone. Author a project in
  a build project of the Workspace and publish it from there to a Runner and a Gateway; its refresh plans run on the Runner from the first activation (`--runner-schedules` is
  accepted and changes nothing; nothing is handed back to a Build scheduler); apply a plan, acknowledge a block or load seeds as the manual Runner tasks `apply_plan`, `ack` and
  `seed` of the operations package; bind the logins on the Runner (the package's `build-login` slots). `semantic describe|query|explain|members` take `--address`; `--gateway` and
  `--server-dir` named the server's pairings and now say so. The server's state database, secret store, projects and pairings are not read any more: nothing is imported.
  Unregister the old service (`sc delete DbDataBuild` or `systemctl disable --now dbdatabuild`) before you install this release.
- **The Sync server is gone (plan 33 SC23); dbdatasync is a command line on a configuration repository and the engine a Runner runs.** `dbdatasync server` (with `pair`),
  `service`, `cert`, `secret` (with `send --to runner`), `config`, `state`, `health`, `gateway`, `update`, `launcher` and `internal` answer with a sentence that names where each
  feature went (exit code 2), and the `DbDataSync` service, its MSI, its launcher and updater, its container image, its compose file, its screens in the console
  (`Shared/web/sync`) and its place in `dbdatakit-full` are gone. Author replications in a sync project of the Workspace (or as the files of a repository) and publish them
  with the Workspace or `dbdatasync package publish --to runner`; run now, pause, bulk loads, verifications, resyncs and the `provision` task are Runner tasks; bind the logins
  on the Runner. The checks stored with a replication (`checks:` of `task.yaml`) are kept and published but not evaluated until the Runner evaluates them. The server's state
  database, secret store, `server.yaml` and pairings are not read any more: nothing is imported. Unregister the old service (`sc delete DbDataSync` or
  `systemctl disable --now dbdatasync`) before you install this release.
  The Runner's own screen sets a connection's run login on a runner package's connections card (*Set the login*), in place of
  `dbdatasync secret send --to runner`; a replication set's Apply (provisioning) runs as its `provision` task on the Runner.

## Names

- **Breaking, no migration: every remaining product-prefixed environment variable is renamed to `DBDATAKIT_` (plan 33 SC31a).** No product prefix survives (`DBDATA<PRODUCT>_`, `DBGATEWAY_`, `DBPROXY_`, `DBDATA_`); the old names are **refused, with no alias**: a service or command that finds one set stops and names the new one. Rename them wherever you set them (unit files, `.env` files, compose files, the machine's variables, CI):
  - announcing and pairing: `DBDATAKIT_CONSOLE_URL` is `DBDATAKIT_URL`, `DBDATAKIT_CONSOLE_ANNOUNCE_URL` is `DBDATAKIT_ANNOUNCE_URL`, `DBDATAKIT_CONSOLE_ANNOUNCE_LISTEN` is `DBDATAKIT_ANNOUNCE_LISTEN` (`DBDATAKIT_ADVERTISE_URL` and `DBDATAKIT_INSTANCE_NAME` stay);
  - secrets and keys: `DBDATA_SECRET_<NAME>` is `DBDATAKIT_SECRET_<NAME>` (the rest of the name is the secret's name as before: `DBDATA_SECRET_DBDATA_SYNC_CONNECTION_ORDERS` is `DBDATAKIT_SECRET_DBDATAKIT_SYNC_CONNECTION_ORDERS`, because the secret's name changed too: see the entry on secret names), `DBDATA_SECRETS_KEY_FILE` is `DBDATAKIT_SECRETS_KEY_FILE`, `DBDATA_SIGNING_KEY` is `DBDATAKIT_SIGNING_KEY`, `DBGATEWAY_PASSWORD` is `DBDATAKIT_GATEWAY_PASSWORD`, `DBGATEWAY_WORKER_SECRET` is `DBDATAKIT_GATEWAY_WORKER_SECRET`;
  - connection logins of Build: `DBDATA_BUILD_<CONNECTION>_READ` and `_WRITE` are `DBDATAKIT_BUILD_<CONNECTION>_READ` and `_WRITE` (the same rule, `LoginEnvironment`, serves every product: `DBDATAKIT_<APP>_<CONNECTION>[_READ|_WRITE]`);
  - upgrade: `DBDATA_UPGRADE_CHANNEL`, `_MIRROR` and `_REPO` are `DBDATAKIT_UPGRADE_CHANNEL`, `_MIRROR` and `_REPO`;
  - one service's settings: `DBDATARUNNER_CHANNELS`, `_GROUP`, `_MAX_ARTIFACT_BYTES`, `_ACTIVATION_MAX_WAIT_SECONDS`, `_TASK_ENVIRONMENT` and `_CLIENT_DIR` are `DBDATAKIT_RUNNER_<same>`; `DBDATAGATEWAY_CLIENT_DIR` is `DBDATAKIT_GATEWAY_CLIENT_DIR`; `DBDATASYNC_API_URL`, `_RUNNER_TOKEN` and `_STATE_ENDPOINT` are `DBDATAKIT_SYNC_<same>`; `DBDATABUILD_ACTOR`, `_CHECK_RESULTS`, `_DUCKDB_CLOCK_OVERRIDES`, `_POLYGLOT_PATH` and `_STATEMENT_LOG_DIR` are `DBDATAKIT_BUILD_<same>`;
  - the Gateway's settings: the configuration prefix is `DBDATAKIT_`, so `DBGATEWAY_Gateway__Management__Port` is `DBDATAKIT_Gateway__Management__Port` (the section stays `Gateway`; the old `DBPROXY_Proxy__...` spellings are `DBDATAKIT_Gateway__...`). The other `DBDATAKIT_*` variables of the suite are visible to the Gateway's configuration and ignored, because none is under `Gateway`;
  - what a parent hands a child (not refused, a parent sets them): `DBDATA_CHILD_ENDPOINT` and `_TOKEN` are `DBDATAKIT_CHILD_ENDPOINT` and `_TOKEN`, `DBDATA_PARENT_PID` and `_START` are `DBDATAKIT_PARENT_PID` and `_START`, `DBDATA_BUN` is `DBDATAKIT_BUN`; the `DBDATA_<APP>__A__B` configuration form of the shared `DbDataKit.Config` library is `DBDATAKIT_<APP>__A__B`; a Dockerfile written by `DbDataKit.Hosting.Systemd` sets `DBDATAKIT_DATA` (it set `DBDATA_DATA_DIR`, which nothing read).
  `scripts/check_env_names.py` fails on any product-prefixed name in a tracked file (the variables of tests are in the entry below).
- **Changed: the variables of tests, harnesses and CI are `DBDATAKIT_TEST_<NAME>` (plan 33 SC31b).** `DBDATA_TEST_*`, `DBKIT_TEST_*`, `DBDATASYNC_TEST_*`, `DBDATABUILD_TEST_*`, `DBGATEWAY_TEST_*`, `DBDATA_E2E_*`, `DBDATA_REQUIRE_*`, `DBGATEWAY_REQUIRE_*`, `DBGATEWAY_LIVE_*`, `DBDATA_UPDATE_GOLDEN` and the others of the same kind are now `DBDATAKIT_TEST_<same without the product prefix>` (`DBDATA_TEST_SQLSERVER` is `DBDATAKIT_TEST_SQLSERVER`, `DBKIT_TEST_MSSQL` is `DBDATAKIT_TEST_MSSQL`, `DBGATEWAY_REQUIRE_SQLSERVER` is `DBDATAKIT_TEST_REQUIRE_SQLSERVER`); names that meant the same became one (Sync's `DBDATASYNC_TEST_MSSQL_SERVER` is `DBDATAKIT_TEST_MSSQL`, Gateway's `DBGATEWAY_TEST_SQLSERVER` is `DBDATAKIT_TEST_MSSQL_SERVER`). `testenv/testenv.sh env <suite>` prints the new names; `docs/testing-environment.md` has the whole table to translate a shell profile or a CI secret store. Nothing reads an old test name and nothing refuses one, except that a leftover `DBGATEWAY_` or `DBPROXY_` variable of any kind, `DBGATEWAY_TEST_...` included, in a Gateway's or another service's environment is now refused like the other old Gateway settings.
<!-- secret-names:begin -->
- **Breaking, no migration: secret names are `dbdatakit:<service>:<kind>:<name>[:<part>]` (plan 33 SC31c).** The names of the secrets follow the environment variables (`DBDATAKIT_`): the root `dbdata` is `dbdatakit` and the second segment is the service, `server` (the DbDataKit console), `gateway`, `runner`, `sync`, `build`, `store` or `workspace`. **A store written under the old names is not read, moved or renamed, and there is no alias:** a secret stored as `dbdata:...` is simply not found under its new name, and a reference `secret:dbdata:...` in a file finds nothing. Set each secret again under its new name (`secret set <new name>`, or the screen that stored it) and change each reference you wrote by hand (connection files, `git_credential`, `password_secret`, `connection_string_secret`, `api-key-secret`, a state connection's `Password=secret:...`); remove the old entries with `secret delete <old name>`. The environment variable and the credentials-directory file of a secret follow its name: `DBDATAKIT_SECRET_DBDATA_SYNC_CONNECTION_ORDERS` is `DBDATAKIT_SECRET_DBDATAKIT_SYNC_CONNECTION_ORDERS`, and the file `dbdata_sync_connection_orders` is `dbdatakit_sync_connection_orders`. The Gateway no longer looks for secrets stored as `dbdata:proxy:*` (the check of the rename DbDataProxy to DbDataGateway); the old names of every product are simply not found. `scripts/check_secret_names.py` fails on any tracked file that names a secret `dbdata:...`.

  | Old | New |
  | --- | --- |
  | `dbdata:sync:connection:<name>[:run\|read]` | `dbdatakit:sync:connection:<name>[:run\|read]` |
  | `dbdata:sync:config:<key>` | `dbdatakit:sync:config:<key>` |
  | `dbdata:sync:test:...` | `dbdatakit:sync:test:...` |
  | `dbdata:build:connection:<connection>:read\|write` | `dbdatakit:build:connection:<connection>:read\|write` |
  | `dbdata:build:git:<name>` | `dbdatakit:build:git:<name>` |
  | `dbdata:gateway:ds:...`, `datasource`, `connection`, `identity:...`, `state:session-key`, `managed-<client>:<name>`, `tls:...`, `apps:signing-key` | the same under `dbdatakit:gateway:` |
  | `dbdata:proxy:datasource:...` (a legacy name from before the rename to Gateway) | `dbdatakit:gateway:datasource:...` |
  | `dbdata:workspace:publisher-key:<name>` | `dbdatakit:workspace:publisher-key:<name>` |
  | `dbdata:runner:...` | `dbdatakit:runner:...` |
  | `dbdata:console:state:<name>` (a state connection's password) | `dbdatakit:server:state:<name>` |
  | `dbdata:console:model-provider:<name>:key` | `dbdatakit:server:model-provider:<name>:key` |
  | `dbdata:kit:console:signing-key` and `signing-key-next` (the console's identity) | `dbdatakit:server:identity:signing-key` and `signing-key-next` |
  | `secret list` with no prefix lists `dbdata:` | it lists `dbdatakit:` |
<!-- secret-names:end -->
