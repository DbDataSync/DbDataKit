# DbDataKit.Metadata.Abstractions

Contract package for Plan 04 Part B. Depends only on `DbDataKit.Dialect.Abstractions` (for `CanonicalType` and `ISqlDialect`).

## Public contract
- `TableRef(Catalog?, Schema?, Name, Kind)`, `TableKind`.
- `ColumnInfo(Name, Ordinal, CanonicalType Type, NativeType, Nullable, DefaultExpr?, IsIdentity, IsComputed, Collation?)`, `KeyInfo`, `ForeignKeyInfo` (+ `ReferentialAction`), `IndexInfo`/`IndexColumn`.
- `TableShape` (columns, primary key, foreign keys, indexes, row estimate) carrying a `ShapeSupport` that says which parts the source could answer. **False means unknown, never "none"**: a driver without `SQLPrimaryKeys` yields `PrimaryKeys = false` and no key. `Warnings` carries ODBC truncation signals and partial failures.
- `QueryShape(Columns, Strategy)` with `DescribeStrategy` (`SqlServerDescribeFirstResultSet | PreparedStatement | OdbcPrepare | WrappedZeroRowProbe`).
- `CatalogCapabilities`, `ServerFacts` (product, version, quote characters, case rules, max identifier length, row-limit/batch support; everything nullable).
- `ICatalogReader`: `ListCatalogsAsync`, `ListTablesAsync(TableFilter)`, `GetShapeAsync`, `CrawlAsync` (default: list then shape; ODBC overrides with one session), `DescribeQueryAsync`, `Capabilities`, `GetServerFactsAsync`.
- `TableFilter` + `LikePattern`: SQL LIKE with a `\` escape, matched in memory by a linear algorithm (no regex, no backtracking blow-up).
- `CatalogNames`: Proxy S-9 name validation. Catalog-supplied names are always quoted through the dialect; when the driver reports no quote character, names with control characters, unterminated quotes/brackets, `;` or comment openers are refused (`CatalogNameException`).
- `ICatalogCache` (fingerprint + epoch keys, `Invalidate` bumps the epoch) and `ConnectionFingerprint.Of` (secret-free hash of a connection string).

## Decisions
- No execution API here. Readers work against `System.Data.Common.DbConnection` or ODBC catalog functions only.
- Row estimates, identity and computed flags are optional per engine; consumers read `Capabilities`/`Support` instead of guessing.
- The fingerprint drops password-like keys so rotating a secret does not invalidate the cache, and sorts keys so ordering does not either.
