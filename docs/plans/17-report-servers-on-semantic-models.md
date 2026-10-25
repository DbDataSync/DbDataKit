# Plan 17: SSRS and Power BI Report Server on the semantic models

Status: **proposed** (2026-10-25), a small plan from the owner's direction: the first reporting step is our own visual format plus SSRS
and Power BI Report Server on a separate server (plan 16 section 2a). This plan is how those report servers **pull data from the
semantic models** (plan 14) served by DbDataProxy. **Not part of plan 13's work list.**

## 1. What the report servers can read

Two kinds of report live on these servers, and each reads data differently.

**Paginated reports (`.rdl`)**, on SSRS and on Power BI Report Server: the Reporting Services data extensions (SQL Server, Analysis
Services, Oracle, Teradata, ODBC, OLE DB, XML, SharePoint lists, and custom data extensions written in .NET Framework). The report server
runs the query each time the report runs.

**Power BI reports (`.pbix`)** on Power BI Report Server, from Microsoft's table of supported sources (checked 2026-10-25):

| Source | Import (cached) | Scheduled refresh | DirectQuery or live | Refresh sign-in |
| --- | --- | --- | --- | --- |
| SQL Server | yes | yes | yes | user and password, Windows |
| SQL Server Analysis Services | yes | yes | yes (live connection) | user and password, Windows |
| PostgreSQL | yes | yes | **yes** | user and password |
| ODBC | yes | yes | no | anonymous, user and password, Windows |
| OData feed | yes | yes | no | anonymous, key, user and password, Windows |
| Web (only `Web.Contents`), JSON, XML, CSV | yes | yes | no | anonymous, user and password, Windows |

Every source in a `.pbix` must support scheduled refresh, or the report cannot be scheduled. OAuth is not supported for refresh or
DirectQuery. Row-level security at the source works for DirectQuery to SQL Server (and a few others) and for live connections, with
Kerberos.

## 2. The routes, from least to most work

| # | Route | Paginated (SSRS, PBIRS) | Power BI (PBIRS) | Live, federated semantic queries | Work |
| --- | --- | --- | --- | --- | --- |
| R1 | **Published semantic views** on the warehouse: Build renders a named semantic query as a SQL view and deploys it | SQL Server data source | SQL Server: import, refresh, **DirectQuery** | no: one source, a fixed grain | small |
| R2 | **Result feeds** on Proxy: a saved, parameterized semantic query served over HTTPS as CSV, JSON or XML | XML data source | Web, JSON or CSV: import, refresh | **yes** | small to medium |
| R3 | **ODBC**: the Arrow Flight SQL ODBC driver on the report server, connected to Proxy, with semantic SQL as the query text | ODBC data source | ODBC: import, refresh | **yes** | medium (mostly testing a third-party driver) |
| R4 | **Postgres wire** on Proxy (plan 14 R8), with a flat view of each semantic model | ODBC (psqlODBC) | PostgreSQL: import, refresh, **DirectQuery** | **yes** | large |
| R5 | **XMLA**: MDX (plan 14 R7) and, for Power BI's live connection, DAX | Analysis Services data source (MDX) | SQL Server Analysis Services live connection (DAX) | **yes** | MDX: large (shared with Excel); DAX: very large |
| R6 | A **custom data extension** for Reporting Services (a .NET Framework assembly installed on the report server) | the extension | none (Power BI reports cannot use it) | **yes** | medium, plus an install on every report server |

### R1. Published semantic views (Build)

- A Build project names semantic queries to publish (`semantic/published/*.sql`, in semantic SQL, plan 14 R6). `project compile` checks
  that each has **one source** (no federation) and a fixed set of dimensions; Build lowers it to the target's SQL (it is a query over
  Build's own tables, so the lowerer and its rules apply) and `connection deploy` creates the view beside the tables.
- What the report server sees is an ordinary SQL Server view: every mode works, including DirectQuery and row-level security through
  SQL Server, with nothing installed on the report server.
- **The catch is re-aggregation.** A view is at a fixed grain; Power BI and SSRS will happily `SUM` a column again at a coarser grain.
  Each column's description says how it may be combined (from the measure's rollup rule), and measures that must not be summed (ratios,
  distinct counts) are published only in views at the grain they are reported at. This route gives governed *numbers*, not a governed
  model.

### R2. Result feeds (Proxy)

- A **published query** on Proxy: a semantic query (semantic SQL or JSON), its parameters (typed, with defaults and allowed values), and a
  stable URL: `https://proxy/feeds/<model>/<query>.csv?region=West`, and `.json`, `.xml`. Parameters become query-string values, bound as
  parameters of the semantic request, never as text.
- **SSRS and PBIRS paginated** use the XML data extension on the feed's `.xml` URL; **Power BI** uses `Web.Contents` on `.csv` or `.json`,
  which Power BI Report Server can refresh on a schedule.
- **Sign-in** is Windows (Negotiate) or Basic over TLS, mapped to Proxy's client identities, so each feed request is a Proxy query with its
  limits, audit and failure model. A feed is read-only and is defined through Proxy's management API (and the console).
- This is the quickest route to **live, federated** semantic results in both kinds of report, with nothing installed on the report server.

### R3. ODBC through the Arrow Flight SQL ODBC driver

- The **Arrow Flight SQL ODBC driver** (open source, LGPL 2; distributed by Dremio for any Flight SQL endpoint; Windows 10 and later, 64-bit
  only; latest found 0.9.7, August 2025) on the report server, pointed at Proxy's Flight SQL listener with the semantic data source
  (`x-datasource: semantic:<model>`).
- The report's query text is **semantic SQL**, with `?` parameters from the report. Paginated reports run it on every execution; Power BI
  imports and refreshes it.
- Work for us is mostly verification: the driver's behaviour with Proxy (metadata calls, parameter binding, types, TLS, Windows or basic
  sign-in), on SSRS and PBIRS, recorded as tests. Whether a 64-bit-only driver suits every report server is a fact to check per install.

### R4. Postgres wire with DirectQuery

- After Proxy has the Postgres wire protocol (plan 14 R8): each semantic model is also published as **a flat view** for SQL tools (plan 14
  R6's flattened names): dimensions as columns, measures as columns, no joins.
- Power BI's PostgreSQL connector generates SQL itself (`SELECT customer_region, SUM(net_revenue) ... GROUP BY customer_region`). Proxy
  reads an aggregate over a measure column as `MEASURE(net_revenue)` (the approach Cube's SQL API takes), so DirectQuery gets governed
  numbers whatever aggregate Power BI asked for, and an aggregate that contradicts the measure (summing a ratio) is refused with a message.
- The work is Power BI's SQL: its query shapes and its catalog questions, recorded from Power BI Desktop against a real PostgreSQL first,
  then answered by Proxy (the same cataloguing approach as Excel and MDX).

### R5. XMLA, MDX and DAX

- **MDX** comes with Excel's work (plan 14 R7). SSRS's Analysis Services data source sends the MDX its query designer writes, which is a
  different set of shapes from Excel's: catalogued the same way, on the same VM, against the SSAS that already runs in CI.
- **Power BI's live connection sends DAX**, for multidimensional models as well as tabular. Translating DAX to semantic requests is a large
  project of its own; it is listed so the decision is explicit, not planned.

### R6. A custom data extension

A Reporting Services data processing extension (`IDbConnection`, `IDbCommand` and the rest, .NET Framework) that sends semantic SQL to
Proxy. It gives the most control (a query designer, parameters, identity), but it is code installed on every report server and it does
nothing for Power BI reports. Kept as a fallback if R3's driver does not hold up.

## 3. Identity

- **Stored credentials** (a Proxy client identity per report server, or per report) are the simple start: every route supports user and
  password. Every viewer of a report then sees the same rows.
- **Per-user data** needs the viewer's identity at Proxy: Windows authentication from the report server to Proxy with **Kerberos
  constrained delegation** (the report server's service account allowed to delegate to Proxy's service principal). R1 gets it from SQL
  Server's own row-level security; R2 to R5 from Proxy's Windows validation. This is part of the sign-in design conversation (plan 16).

## 4. Recommended order

1. **R1**, published semantic views: the smallest change, every mode of both servers, and it reuses Build's lowering.
2. **R2**, result feeds: live and federated semantic results for both kinds of report, nothing installed on the report server.
3. **R3**, the ODBC driver: verify it against Proxy; if it works, semantic SQL is usable directly in report queries.
4. **R4** after Proxy's Postgres wire, for Power BI DirectQuery on the semantic models.
5. **R5**: MDX with Excel's work; DAX only if Power BI live connections are needed.

## 5. Testing

- **SSAS** already runs in GitHub CI (QueryIntentSpec's Chocolatey setup); whether **SSRS** can be installed the same way on a Windows
  runner is to be checked. **Power BI Report Server** has a free developer mode for testing; it and Power BI Desktop need the Windows VM.
- Each route gets recorded fixtures (the report server's requests and Proxy's answers) and an end-to-end test where a report server is
  available: a paginated report and a Power BI report over the sample semantic model give the same numbers as the semantic query itself.

## 6. Questions for the owner

1. The order in section 4: R1 and R2 first?
2. Is per-user data (Kerberos delegation) needed in the first version, or are stored credentials enough to start?
3. Power BI live connections (DAX): wanted at some point, or never?
