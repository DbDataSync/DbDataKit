# FABRICSERVING: Microsoft Fabric warehouses served through the Gateway

## Goal

A Fabric warehouse that Build writes to can be a data source of the Gateway, so semantic models, saved queries, pages and Flight SQL clients read it
like a SQL Server or PostgreSQL source. Close to working, and low priority until the product is consistent (owner, 2026-11-18).

## What exists today

- Build has a Fabric target (`engine: fabric`, the `fabric` dialect, ScriptDOM's newest grammar for validation, a column in the strategy matrix), written
  and never run against a real Fabric warehouse: every Fabric row of the matrix is `unverified` (`DbDataBuild/docs/progress/OPEN-ITEMS.md` lists what a
  real warehouse must confirm: `MERGE`, `ALTER`, `TRUNCATE` and rename, `nvarchar(max)` and constraints in the tracking tables,
  `sp_describe_first_result_set`, trailing spaces and `LEN`, collations, `GENERATE_SERIES`, `HASHBYTES` with a UTF-8 collation).
- The engine registry has a `fabric` descriptor (`Shared/src/DbDataKit.Connections.Descriptor/engines/fabric.engine.yaml`: SqlClient, port 1433, the roles
  `build_target` and `build_origin` `unverified`); `docs/engine-support.md` says no for the Gateway.
- The Gateway refuses `engine: fabric` as a data source (`DbDataGateway/README.md`, what is not supported).
- No test tenant exists.

## Open designs

### Fabric as a Gateway data source

**What.** A Fabric warehouse's SQL endpoint speaks TDS but accepts only Entra ID sign-in, which the Gateway cannot make today. **How, as the Gateway's
README records the default:** reference `Microsoft.Data.SqlClient.Extensions.Azure` from the Gateway's server; map `engine: fabric` onto the SQL Server
provider with the `fabric` dialect; put the Entra credential in the connection string (`Authentication=Active Directory Service Principal;User
Id=...;Password=env:...`, or a managed identity), with no pass-through of the caller's credentials; apply the read-only classifier as for SQL Server; let
the Workspace's publish of a Build project to a Gateway accept a Fabric connection as a data source. A token source inside the Gateway (`auth: {mode:
token}`, the Gateway renewing the token itself) was the other option, not chosen because the provider already renews it. **Proven** when a Fabric data
source answers a query with the classifier applied and its publish is tested, on a tenant.

**Open questions.**

- A test tenant: who provides it, and whether a CI job can reach it (a by-hand check otherwise, as the AD CS checks are).
- Whether Build's Fabric target is verified first (its matrix rows), since a served model is only as good as what Build wrote there.
- The semantic layer on Fabric: the semantic engine writes SQL Server text; which constructs differ on Fabric (the same list as Build's matrix) and
  whether the engine needs a `fabric` dialect of its own.
- The registry: a `gateway_source` role for `fabric` and its status once it runs.

## Shared areas touched

- `DbDataGateway.Server` (data sources), `DbDataKit.Connections.Descriptor` (`fabric.engine.yaml`), `DbDataKit.Authoring.Server` (publish to a Gateway),
  `DbDataKit.Semantic` (dialect), `docs/engine-support.md`, `DbDataGateway/README.md`.
