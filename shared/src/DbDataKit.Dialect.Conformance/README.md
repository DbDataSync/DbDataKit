# DbDataKit.Dialect.Conformance

Abstract xunit.v3 suites for `ISqlDialect` implementations.

## Public contract
- `DialectConformanceTests` (offline, in-process): derive and implement `CreateDialect()`. Checks quoting with hostile names (round trip through `SplitQualifiedName`, lexes as exactly one quoted identifier), `IsSafeUnquoted` vs the lexer, string literals lex as one string, scalar literals, row limits per supported style (tie-safe honesty), parameter marker rewriting, a total and honest type table, and the read-only policy corpus. The lexer is the oracle.
- `LiveDialectConformanceTests` + `ILiveEngine`: same claims against a real server (hostile column aliases, hostile string literals, numeric literals). Every test is skipped (visibly, via `Assert.SkipUnless`) unless the env var named by the subclass holds a connection string (convention `DBDATA_CONFORMANCE_<ENGINE>`).

## Adding an engine
Add its `dialect.yaml`, derive `DialectConformanceTests` (two lines), optionally a live subclass with a `Connect` implementation.

## Status
No database driver ships in `shared/`; the live subclasses in the test project are stubs whose `Connect` throws until a product repo or docker fixture supplies one. DDL smoke, type round trip against a live engine and tie-safe row-limit execution are not yet live-tested.
