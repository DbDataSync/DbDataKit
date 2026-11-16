# DbDataKit.DataModel.Runner

A Store model's tables deployed as a manual Runner task (plan 33 SC24, the Store without its server).

- `ModelDeployPackage.Build(modelText, problems)` makes a model's definition package: `package.yaml` with one manual-only task,
  `deploy`, run by the runtime `dbdatastore-runtime` (`DbDataStore/scripts/runtime-manifest.sh`), with one connection slot,
  `storage`, and the model as `files/model/model.yaml`. `Write` puts it in a folder that `dbdatarunner package install` or a
  publish takes.
- `ModelDeployJob` is the task. Its inputs are `dry_run` (default `true`: report, change nothing), `schema` (default the
  model's name) and `model_file`. It opens the `storage` slot with the login the channel binds and creates the model's
  storage when the schema has nothing of it, adds the tables and columns that are missing when it has some (an expand,
  nullable columns), or does nothing. A change that would remove or retype (a contract step) is reported as blocked and
  fails the run without writing. Everything runs in one transaction (`ModelStorage`). The report is the step
  `deploy-report` of the run (`ModelDeployReport`: action `create`, `expand` or `none`, the statements, blocked, notes).
- `GrantConnections.OpenAsync(grant, role, ct)` opens a SQL Server or PostgreSQL connection inside a task from the slot's
  grant: the connection's definition the runner keeps (TLS, timeouts; the grant's `Definition`) with the grant's address and
  login. Nothing of a secret is logged.

`dbdatastore --runner` is the runtime's entry: it runs the task the runner names (`DBDATAKIT_TASK_NAME`).

The Workspace makes these packages from a `store` project's build (`runner/<model>/` of the artifact) and stages one on a Runner from the
**Deploy** tab of a store workspace; that tab runs `deploy` dry first and reads `deploy-report` back (plan 33 SC24).

- `ModelContractJob` is the task `contract` (the contract step of expand-then-contract): without `approve` it records the plan as the step
  `contract-report` (`ModelContractReport`: the hash, the steps, blocked, notes, and the columns it drops as lineage names); with
  `approve=<hash>` it runs exactly that plan in one transaction, or fails and changes nothing. It does not ask who reads a column: the
  Workspace's contract route asks the paired Gateways (`ContractGuard`) before it passes the hash.
