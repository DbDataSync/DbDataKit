# DbDataKit.Runner.Metadata

The Runner's built-in package `dbdatakit-metadata`. It reads the shape of a database for a person who authors a project, with the login a channel
binds to that project's slot, and answers shape only: never a row, a value or a statistic of values.

- **`discover`** (`DiscoverJob`): the databases the connection reaches with every schema of each (the empty ones too), and the tables and views of the
  schemas in scope with their columns (type text, nullability, primary key), unique keys and foreign keys. The answer is one connection of
  `catalog-cache/2` as JSON (`CatalogCache.FromAnswer` reads it), streamed into the step `discover` keyed by the run's id. The inputs `schemas`
  (a JSON list), `pattern` (SQL `LIKE` over table names) and `table` (one table of the one schema given) narrow it.
- **`describe`** (`DescribeJob`): the columns of the query in the input `sql`. The read-only policy (`QueryProbe.RequireReadOnly`) refuses a query that
  writes before the connection opens; the engine's own describe answers, and nothing is fetched. The answer is the value of the step `describe` keyed
  by the run's id.

Both tasks take the inputs `package` and `slot` (whose binding is read) and `role` (`read` or `default`). The runner resolves the package's one slot,
`connection`, through the binding of that package's slot in the run's channel, so the run never uses a binding of its own; it asks the login of the role
with `Slot<T>.OpenAsync(role, ct)`.

**Shipping.** The runner's server project publishes this library into `builtin/dbdatakit-metadata/` beside its program; the runner installs it in a
channel the first time a read is asked there (`RunnerMetadataApi`), with the manifest `BuiltInPackages.Metadata()` writes. The engines are SQL Server,
PostgreSQL, MySQL and MariaDB, and Oracle.

Design: [docs/authoring-metadata.md](../../../docs/authoring-metadata.md#the-built-in-package-dbdatakit-metadata)
