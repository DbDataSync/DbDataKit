# SSRS and Power BI Report Server on the semantic models

How a report server can read governed data from the suite, route by route, and where each route stands (read on 2026-11-09). It replaces plan 17. The suite's own reports and dashboards are plan 32; showing a report server's reports in an app is plan 32's `embed` widget and report-catalog slices (RC rows) with plan 26 AP17.

## What the report servers can read

**Paginated reports (`.rdl`)** on SSRS and Power BI Report Server run a Reporting Services data extension (SQL Server, Analysis Services, Oracle, ODBC, OLE DB, XML, ...) every time the report runs.

**Power BI reports (`.pbix`)** on Power BI Report Server (Microsoft's table of supported sources, checked 2025-10): every source must support scheduled refresh; OAuth is not supported for refresh or DirectQuery.

| Source | Import | Scheduled refresh | DirectQuery or live | Refresh sign-in |
| --- | --- | --- | --- | --- |
| SQL Server | yes | yes | yes | user and password, Windows |
| SQL Server Analysis Services | yes | yes | yes (live) | user and password, Windows |
| PostgreSQL | yes | yes | yes | user and password |
| ODBC | yes | yes | no | anonymous, user and password, Windows |
| OData feed | yes | yes | no | anonymous, key, user and password, Windows |
| Web (`Web.Contents`), JSON, XML, CSV | yes | yes | no | anonymous, user and password, Windows |

## The routes and where they stand

| Route | What | Standing |
| --- | --- | --- |
| **R1** published semantic views | Build renders a named semantic query as a SQL view beside its tables; the report server sees an ordinary SQL Server view (every mode, DirectQuery, row-level security through SQL Server, nothing installed on the report server). A view is at a fixed grain, so a column says how it may be combined and ratios and distinct counts are published only at the grain they are reported at | **Plain case works today**: a view model is a SELECT deployed with Build's tables. Writing that SELECT in semantic SQL (`semantic/published/*.sql`, one source, a fixed set of dimensions) is not built and not sliced until a report-server user asks (plan 14a Q15) |
| **R2** result feeds | a saved, parameterised semantic query at a stable URL returning JSON, CSV or XML, as the caller, so SSRS (XML data source) and Power BI (`Web.Contents`) read **live, federated** results with nothing installed; the same URL serves Excel's Power Query, Python and scripts | **Built** (plan 14a SL31): `GET|POST /api/v1/queries/{model}/{name}?<parameter>=<value>&limit=&format=json|csv|arrow` and `POST /api/v1/query` on the `feeds` module's listener (`modules: [feeds]`, `rest:`, port 8821), as the person behind a personal access token, an access token of the home or Basic (the address and the token as the password); CSV has a header row (what SSRS's and Power BI's CSV readers take); XML is not built; no report server has read it yet (the tests read it as a client does); `docs/serving-layer.md` |
| **R3** ODBC | the Arrow Flight SQL ODBC driver (Dremio, LGPL 2, Windows 64-bit only) on the report server, pointed at the Gateway's Flight SQL listener with `x-datasource: semantic:<model>`, semantic SQL as the query text with `?` parameters | Works as far as Flight SQL works; **not verified** with a report server. Parked (`plans/README.md`, "Parked"): verification is metadata calls, parameter binding, types, TLS and sign-in on SSRS and PBIRS, recorded as tests |
| **R4** Postgres wire | each model as a flat view; Power BI's PostgreSQL connector sends its own SQL and a compatibility layer reads an aggregate over a measure column as `MEASURE()` and refuses one that contradicts the measure | **Deferred, low priority** (owner, 2026-11-09): plan 14a SL50 to SL53. Condition that stays: the compatibility layer is separate from the semantic SQL spec, which is not weakened; every translated query is shown as the canonical semantic SQL it became |
| **R5** XMLA, MDX, DAX | SSRS with MDX only as far as the Excel MDX work reaches, no SSRS-specific MDX work; DAX (Power BI live connection) **not planned** | MDX deferred with the Excel work (plan 14a SL40 to SL47) |
| **R6** custom Reporting Services data extension | a .NET Framework assembly installed on every report server | **Dropped** (owner, 2026-10-25): too specific, nothing for Power BI or other tools. Not to be looked at again without a new reason |

## Identity

**Decided (owner, 2026-10-25): stored credentials to start.** A report server (or a report) signs in as one identity of the identity home, a **service account** with a personal access token as the password (every route supports a user name and password). Every viewer then sees the same rows; the account's access (which models, which data sources) is what limits the report. Per-user data is a later decision.

**Per-user data through Kerberos, for later review.** The report server's service account is allowed to delegate to the Gateway's service principal (constrained delegation on the report server's account, or resource-based on the Gateway's, usually easier to administer); the Windows identity becomes a home account (UPN) with roles from the directory groups; the serving layer applies the person's row filters (plan 22a access rules) before planning, or delegates again to the database. By route: R1 yes without the Gateway (SQL Server's row-level security applies); R2 yes (Negotiate over HTTP/1.1 on the REST listener); R3 not as it stands (Flight SQL is gRPC over HTTP/2, where Negotiate is not available in Kestrel; the driver uses a user and password or a token); R4 not for Power BI (refresh supports only user and password for PostgreSQL); R5 yes. Once results differ per viewer every cache key includes the viewer (plan 24a). Testing needs an Active Directory lab on the Windows VM; nothing of it runs on hosted CI runners.

## Testing

SSAS runs in CI (the `semantic-ssas` job); whether SSRS installs on a Windows runner the same way is to be checked; Power BI Report Server has a free developer mode and, with Power BI Desktop, needs the Windows VM. Each route gets recorded fixtures (the report server's requests, the Gateway's answers) and an end-to-end test where a report server is available: a paginated report and a Power BI report over the sample semantic model give the same numbers as the semantic query itself.
