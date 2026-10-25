# Plan 17: SSRS and Power BI Report Server on the semantic models

Status: **documented scenarios** (2026-10-25, reviewed by the owner the same day): the routes are written down so that what to build
can be decided when the time comes; section 2a has the owner's assessment of each. From the owner's direction: the first reporting step
is our own visual format plus SSRS and Power BI Report Server on a separate server (plan 16 section 2a). This plan is how those report
servers **pull data from the semantic models** (plan 14) served by DbDataProxy. **Not part of plan 13's work list.**

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

## 2a. The owner's assessment

| # | Assessment (2026-10-25) |
| --- | --- |
| R1 | **Mostly there already**: Build builds views today, so a view a report server reads is an ordinary Build model. What R1 would add is only writing that view's query in semantic SQL. A good recommendation, little to build |
| R2 | **Worth it beyond report servers**: a stable URL that returns a semantic query's result serves many tools (Excel's Power Query, Python, scripts, any web client), not only SSRS and Power BI |
| R3 | Kept as described (verifying a third-party driver) |
| R4 | **The most power and utility**, and a natural extension of semantic SQL, **provided it does not weaken the semantic SQL spec** (see R4 below: the compatibility layer is separate from the spec) |
| R5 | **Only for SSRS with MDX, and only with what the Excel MDX work already gives**: no SSRS-specific MDX work, and no DAX |
| R6 | **Dropped**: too specific (one product's extension point, nothing for Power BI or other tools) |

### R1. Published semantic views (Build)

- **Today's Build already covers the plain case**: a view model is a SELECT over Build's tables, deployed with them, and a report server
  reads it as any SQL Server view. The addition below is only for writing that SELECT in semantic SQL.

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
- **Not only for report servers** (owner): the same feeds serve Excel's Power Query (`Web.Contents`), Python and R, command-line tools and
  any web client, so a feed is a general way to hand a governed result to a tool that cannot speak Flight SQL.

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
- **The semantic SQL spec is not weakened** (owner's condition). The flat views and the reading of `SUM(measure_column)` as a measure are
  a **compatibility layer** in front of the spec, not part of it:
  - semantic SQL keeps its own strict form (`MEASURE()`, model-anchored names, `LIMIT` required, everything else refused by name);
  - the compatibility layer translates a tool's SQL into a strict semantic request, and a tool query it cannot translate into one is
    refused with a message, never answered approximately;
  - the layer's rules (which aggregates a measure accepts, how a flat name maps back to a member) are written in its own document,
    versioned on their own, with their own fixtures recorded per tool; a rule needed by one tool never becomes a rule of semantic SQL;
  - every translated query is shown, in the result's provenance and in Proxy's log, as the canonical semantic SQL it became, so what a
    tool asked for and what was answered can always be compared.

### R5. XMLA, MDX and DAX

- **MDX** comes with Excel's work (plan 14 R7). SSRS's Analysis Services data source sends the MDX its query designer writes, which is a
  different set of shapes from Excel's. **Owner's scope:** SSRS with MDX is supported only as far as the Excel MDX work already reaches; an
  SSRS query the translator does not recognize is refused with a message like any other unknown MDX, and no SSRS-specific MDX work is planned.
  A one-time look at what SSRS's query designer sends (on the Excel VM) is enough to say which SSRS reports work.
- **Power BI's live connection sends DAX**, for multidimensional models as well as tabular. **Not planned** (owner): translating DAX is a
  large project of its own, and R4 gives Power BI live (DirectQuery) access through semantic SQL instead.

### R6. A custom data extension (dropped)

A Reporting Services data processing extension (`IDbConnection`, `IDbCommand` and the rest, .NET Framework) that sends semantic SQL to
Proxy. **Dropped by the owner as too specific**: code installed on every report server, for one product, and nothing for Power BI
reports or other tools. Recorded so the option is not looked at again without a new reason.

## 3. Identity

- **Stored credentials** (a Proxy client identity per report server, or per report) are the simple start: every route supports user and
  password. Every viewer of a report then sees the same rows.
- **Per-user data** needs the viewer's identity at Proxy: Windows authentication from the report server to Proxy with **Kerberos
  constrained delegation** (the report server's service account allowed to delegate to Proxy's service principal). R1 gets it from SQL
  Server's own row-level security; R2 to R5 from Proxy's Windows validation. This is part of the sign-in design conversation (plan 16).

## 4. When the time comes

Nothing here is scheduled; the routes are documented so the choice can be made then. As things stand:

| Route | Standing |
| --- | --- |
| R1 | available today as plain Build views; semantic SQL as a view's query is a small addition, whenever semantic SQL exists |
| R2 | a strong candidate: small, live and federated, and useful to many tools besides report servers |
| R3 | verification of a third-party driver; can be tried whenever Proxy serves semantic SQL over Flight SQL |
| R4 | the most valuable, after Proxy's Postgres wire (plan 14 R8), on the condition that the compatibility layer stays separate from the semantic SQL spec |
| R5 | MDX only, and only what the Excel work gives; DAX not planned |
| R6 | dropped |

## 5. Testing

- **SSAS** already runs in GitHub CI (QueryIntentSpec's Chocolatey setup); whether **SSRS** can be installed the same way on a Windows
  runner is to be checked. **Power BI Report Server** has a free developer mode for testing; it and Power BI Desktop need the Windows VM.
- Each route gets recorded fixtures (the report server's requests and Proxy's answers) and an end-to-end test where a report server is
  available: a paginated report and a Power BI report over the sample semantic model give the same numbers as the semantic query itself.

## 6. Questions for the owner

1. ~~Order~~: no order now; decided per route when the time comes (section 4).
2. Is per-user data (Kerberos delegation) needed in the first version, or are stored credentials enough to start?
3. ~~Power BI live connections (DAX)~~: not planned; R4 is the live route for Power BI.
