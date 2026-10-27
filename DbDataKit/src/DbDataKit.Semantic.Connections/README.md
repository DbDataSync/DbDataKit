# DbDataKit.Semantic.Connections

The executor of the semantic layer (plan 14, R2; J03): `ConnectionsSourceExecutor` implements `ISourceExecutor` over `DbDataKit.Connections`. A host gives it a map from the catalog's source names to a `SemanticSource` (an `IDbProvider` and the `ConnectionContext` to open it with); each query the engine plans is run on that connection.

- **What runs where**: on SQL Server the emitted T-SQL (`@p1..@pN`, in the order of the DuckDB parameters); on DuckDB the DuckDB twin of the query. A source with another engine, or a dialect the provider cannot run (DAX), goes to the fallback executor, or fails naming the source.
- **The connection's rules apply**: the host passes a read-only context (`LoginRole.Read`), so the provider's read-only recipe runs after open; the command timeout is the definition's `timeouts.command_s`.
- **Failures** are `ConnectionFailureException` (a kind and a code, never the driver's text), as everywhere else in the suite.
- **Agent-written native text** (level 0) is not run here: `RunsNativeText` is false until the host's statement classifier stands in front of it (Proxy).
- No driver package is referenced: the host references `DbDataKit.Connections.SqlServer` and `DbDataKit.Connections.DuckDb` and builds the providers.
