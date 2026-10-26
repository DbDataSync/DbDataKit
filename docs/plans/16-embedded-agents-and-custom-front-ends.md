# Plan 16: embedded agents and apps (backlog)

Status: **backlog** (2026-10-24, revised 2026-10-25 after the owner's review): features the owner wants, not yet scheduled. **Not part of plan 13's work list.** This records what they
need and what the earlier plans must not make hard, so the work can be planned in slices when it comes up.

## 1. Embedded agents

**Goal.** An agent inside the console (or another front end, section 2) that can build or query **every type of object** the platform
supports, on standard model APIs: the OpenAI API, the Anthropic API, and **Microsoft Foundry** as one of the first hosted options.

### What makes "every object" possible

**The agent's tools are the shared command surface** (plan 13 W4a): every command has a spec with an effect class and a JSON document
with a schema, and the same specs already generate the MCP tools that an *external* agent uses. An embedded agent gets the same tools,
so the internal and external agents can do the same things, and a feature an agent cannot reach is a missing command, not an agent problem.

- **Rule for every product: no feature without a command spec.** Build enforces this today for its own commands (a test fails for a
  command without a spec and schema). Make it a suite-wide test, so the agent's reach grows with the platform: Sync (connections,
  replications, runs), Build (projects, models, plans, deploys, semantic models, authoring files), Proxy (data sources, semantic queries,
  semantic SQL), DbDataStore (models, rows, change requests, documents, extractions, apps), the console (servers, users, pairings).
- **Context as data**: the semantic model, Build's metadata documents and DbDataStore's models are what the agent reads to know the
  platform; Build's `project agent-kit` skills and QIS's clarify and unsupported documents are the pattern for what an agent is told.

### Model providers

- **One provider abstraction: `Microsoft.Extensions.AI`'s `IChatClient`** (owner decision, for now), as long as it covers the four
  provider kinds the owner needs. Microsoft's Agent Framework documentation lists `IChatClient` implementations for OpenAI, Azure OpenAI,
  Microsoft Foundry, Anthropic, Ollama and Foundry Local, with function tools supported for each:

| Provider | Package (to be pinned when built) | Notes |
| --- | --- | --- |
| OpenAI | `Microsoft.Extensions.AI.OpenAI` over the OpenAI SDK | |
| Microsoft Foundry | the Azure OpenAI and Foundry SDKs | Foundry hosts OpenAI models and models from other providers; which a deployment uses is configuration |
| Anthropic | the official Anthropic C# SDK (its `IChatClient` support has been described as beta: check when built) | a community adapter exists as a fallback |
| Local models | OllamaSharp (Ollama), Foundry Local | |

  The first slice of this work proves tool calls and streaming against each of the four with the same test, before anything is built on
  the abstraction.
- **Configured by an administrator**, not by each user: which providers and models are allowed, their endpoints and keys (secret
  references, plan 13 W13), per-user and per-console limits on use and cost, and which provider a deployment must use (Foundry for an
  organization that requires its data to stay in its tenant).

### Safety (the agent acts as the user, never more)

- **The user's identity and roles on every tool call**: the agent's calls go to the servers with the signed-in user's token (plan 13 W12),
  so it cannot do what the user could not. Its actions are audited as the user *through the agent* (`via: agent <provider/model>`).
- **Every action is approved by the user as it happens** (owner decision, to be refined later). The agent *generates* an action: the
  command and its arguments, shown as the command's own preview (what it reads or changes, its effect class, and for a plan or a change
  request, the plan itself). The user approves it, and only then does it run, under the user's identity. One action, one approval;
  nothing runs ahead of the user. A user's approval of an action that needs a role (an Approver applying a Build plan) is that user's own
  act, recorded as such, through the agent. Allowing some reads to run without asking is a later refinement, not part of the first version.
- **Tool results are data, not instructions.** Rows, documents and messages from tools can contain text written to steer the agent; the
  runtime marks them as data, and a destructive or outward-facing step always needs the user's confirmation, whatever the agent was told.
- **Transcripts are kept** (state store), with the tool calls and their results, so a session can be reviewed and replayed.

### Where it runs

The agent runtime is a shared library (`DbDataKit.Agents`): the provider abstraction, the tool binding to command specs, confirmation,
transcripts and limits. **The console hosts it** for its own chat; **the app host hosts it** for app assistants (section 2); keys and
model calls stay on the server, never in the browser. **Tests** run against recorded model responses, so CI needs no model API.

## 2. Apps

**Goal** (revised by the owner): apps are a **standalone feature**, not a part of DbDataStore. An app is a custom front end that can
**view and edit DbDataStore data** under DbDataStore's standard rules for storage, permissions and validation, and can also carry
**dashboards with Vega-Lite visuals**, **reports**, and **embedded agent assistants** (section 1). External agents deploy apps through
MCP, and the internal agent has the same tools.

### Where apps run

**Amended (2026-10-26) by [plan 20](20-serving-layer.md):** the app host is proposed as the `apps` module of the serving layer, which
calls DbDataStore and the semantic layer in process as the signed-in user. The standalone-host text below is the earlier design.

A standalone app host, **DbDataApps** (`dbdataapps`; the owner's name for now), the same shape as the other products: a server with a management API,
paired with consoles (plan 13 W12), its own CLI on the shared nouns. It holds no data of its own beyond the apps' definitions and
versions; it **reaches data as a client** of the servers that own it:

| An app part | Reads or writes through | Rules come from |
| --- | --- | --- |
| Data views and forms over managed data | DbDataStore's API (reads, validated writes, change requests) | DbDataStore: storage, validation, history, permissions |
| Dashboards and visuals | semantic queries and semantic SQL on Proxy (plan 14); the presentation specs (matrix grids, Vega-Lite) | Proxy and the semantic model |
| Reports | section 2a | Proxy, DbDataStore, or a report server |
| Agent assistants | the agent runtime (section 1), scoped to the app's objects | the user's identity and approvals |

The app host calls DbDataStore and Proxy **as a paired server client** (plan 13 W12, "Server-to-server clients"), with tokens naming the
signed-in user, so DbDataStore's and Proxy's rules apply exactly as they do to anyone else.

### Code apps first

**Decided (owner):** code apps come at the same time as declarative apps, or before them. The first version is the code-app path (the
bundle, the manifest, the scoped token, the sandbox, the typed clients, deploy through MCP); declarative views are built on the same
host and clients, and can be the same components a code app uses, so they come cheaply after it.

### The principle: the app is untrusted UI

All rules stay on the servers that own the data: an app reads and writes only through their APIs, with the user's identity, so
validation, history, approval and permissions apply whatever the app's code does. An app can make a better screen; it cannot make a
different rule.

### Two kinds of app

| Kind | What it is | For |
| --- | --- | --- |
| **Declarative** | a manifest plus view specs (forms, grids, detail pages, dashboards, reports, an assistant), rendered by the app host with the shared components | most cases, and the safest thing for an agent to generate: it is data, validated against a schema |
| **Code** | a static web bundle (HTML, JavaScript, CSS) with a manifest | a UX the declarative views cannot express |

Both have a **manifest**: name, version, the DbDataStore models and entities and the semantic models it uses (and with which operations),
its routes, the roles it needs, and for a code app its content security policy.

- **Served by the app host** at `/apps/<name>/`, each version kept, with rollback. A code app runs on **its own origin or in a sandboxed
  frame** with a strict content security policy, and gets **its own scoped token**: the intersection of what the manifest declares and what
  the user may do. It never sees the console's tokens.
- **A typed client per DbDataStore model**, generated from the `model/1` definitions (an OpenAPI description and a TypeScript client), a
  client for semantic queries, the Vega-Lite and matrix renderers, and the console's shared components (`@dbdatakit/shell`: grid, form,
  field, preview), published for app authors, so custom apps look like the suite when they want to and validate as the servers do.

### Deploying through MCP

- **App tools on the command surface**: `app create`, `app validate` (manifest, view specs, the bundle's size and policy), `app preview`
  (deploy to a preview slot only its author sees), `app deploy`, `app list`, `app rollback`, `app delete`. The same tools for an external
  agent over MCP and for the internal agent (section 1), and an agent's deploy is approved by its user as it happens, like any action.

### Who signs in to an app

**Decided direction (owner):** Windows authentication, OpenID Connect, and Microsoft Entra ID, with **the console acting as an OpenID
Connect provider**. **The spec needs design conversations with the owner**; the topics to settle:

1. ~~Who the users are~~: **decided (owner)**: app users are **console users who administer nothing**: they sign in like any console
   user, hold app roles only, and never see the console's administration. Open with it: whether identity stays inside the console or
   becomes a component of its own (topic 6 and section 2b).
2. **The console as provider.** Which flows (authorization code with PKCE for browser apps; client credentials for server clients?),
   token lifetimes, consent, and how an app registers as a client of the console.
3. **Federation.** Entra ID and other OpenID Connect providers signing in to the console (the console then issues its own tokens to apps),
   or apps trusting Entra ID directly. Group and role mapping from Entra to the suite's roles.
4. **Windows authentication.** Negotiate (Kerberos, NTLM) at the console and the app host for intranet installs; how a Windows identity
   becomes a suite user and roles (groups); what happens off-domain.
5. **From app to data.** How the app host turns a signed-in app user into the tokens DbDataStore and Proxy accept (plan 13 W12), and how
   the manifest's scope narrows them.
6. **Several consoles.** Plan 13 allows several consoles per server; an app needs one identity provider. Which console it is, and what a
   second console means for an app's users. **Decided**: one identity home per install (section 2b).

## 2a. Shared visuals and reports

### Visuals are a shared feature

Agent assistants must be able to show a chart or a table as part of their help, in the console's chat and in apps, so visuals are not an
app feature but a **shared one**, used by the console, apps, assistants and reports alike:

- **One view format**: QIS's presentation view documents (plan 14: matrix grids and a Vega-Lite subset over a result), extended where
  dashboards need more. A view always names a **result** (a semantic query, a DbDataStore read) and never carries a computation of its own,
  as QIS already requires; totals come from the result.
- **One renderer**, a front-end package (`@dbdatakit/visuals`: Vega-Lite through `vega-embed`, the matrix grid, a table), used by the
  console, by apps and by the chat panel of an assistant. An assistant's answer can contain a view document; the chat renders it inline,
  and the user can open it as a query in the console or pin it to a dashboard.
- **Safe by construction**: views are data validated against a schema; the Vega-Lite subset allows inline data only (no URLs, no data
  loaded by the spec itself) and no custom expressions beyond the subset, so a view an agent writes cannot fetch anything or run code.
- **Rendering outside a browser** (a PDF report, an image in a transcript): through a headless browser on the server (Chromium, already
  used by the suite's UI tests) or a Vega-Lite-to-SVG converter; to decide when reports are built.

### Reports

**Decided first step (owner, 2026-10-25):** our own format for visuals and reports on the shared visuals, and **integration with SSRS
and Power BI Report Server running on a separate server**: embedding their reports in apps (below), and letting them **pull data from
the semantic models** ([plan 17](17-report-servers-on-semantic-models.md)). An RDL engine of our own stays a documented option.

| Option | What it is | Status |
| --- | --- | --- |
| **A report spec of our own** on the shared visuals | a declarative document: parameters, sections, text, tables and matrices, Vega-Lite charts, each over a semantic query or a DbDataStore read; rendered to HTML, and to PDF through the headless browser | **first step**: agents can write it, it uses the same views as dashboards and assistants, and nothing new is licensed or hosted |
| **Embedding SSRS and Power BI Report Server reports** | the report server stays where it is; an app shows its reports by URL (`rs:Embed=true` for the viewer, `rs:Format=PDF` and others for exports), and the Report Server REST API (`/reports/api/v2.0`) lists reports, folders and parameters | **first step**: a "report server" source in an app, with the catalogue read through the REST API; the report server reads the semantic models as in plan 17 |
| **Majorsilence Reporting** | an open-source RDL engine (details below) | **documented option**: for RDL reports without a report server, if that is wanted later |
| **FastReport Open Source** (MIT; .NET Core and .NET 10 supported, actively released) | a banded report engine with sub-reports and a script engine, a desktop designer; it does **not** read RDL (only the commercial edition does) | documented alternative for banded, pixel-perfect documents (invoices, statements, printed lists) |
| **RDL on .NET Core from Microsoft's code** | no Microsoft-supported ReportViewer or RDLC engine exists for .NET Core; `ReportViewerCore.NETCore` is an unofficial build of the SSRS engine, decompiled and recompiled, not maintained for years | **not recommended**: a licence and maintenance risk |
| Hosting SSRS reports ourselves on .NET | no solution found that runs RDL on .NET Core besides Majorsilence Reporting and the unofficial port | not an option for now |

**Embedding a report server: the hard part is sign-in.** Community reports show the iframe still prompting for the report server's
credentials, and Power BI's cloud embedding APIs do not apply to the on-premises Report Server. The workable paths are Windows
authentication end to end (the browser signs in to the report server directly with the user's Windows identity, on an intranet) or the
app host as a reverse proxy using Kerberos constrained delegation to the report server. Both need a test environment with a report server;
this belongs to the sign-in design conversation (section 2).

### Documented option: Majorsilence Reporting

Reviewed at `24c6580` (committed 2026-10-23): github.com/majorsilence/Reporting, formerly My-FyiReporting. An RDL engine of its own,
**not Microsoft's code decompiled** (as `ReportViewerCore` is); about 237,000 lines of C#; Apache 2.0, with some parts also offered under
MIT or BSD 2-Clause; targets .NET 8 and .NET 10.

**Pros**

- **Licence and platform fit.** Open source, permissive, actively developed, .NET 10, Linux, macOS and Windows through SkiaSharp; even
  Native AOT compatible.
- **It speaks RDL**, the format SSRS users know: the "hosted SSRS reports on .NET Core" gap, at least partly.
- **The export formats people ask for:** PDF, HTML, MHTML, Word (RTF), Excel (xlsx, plus a data-only table form), CSV, XML, TIFF.
- **A web side:** an ASP.NET Core viewer API (render, parameters) and a Blazor web designer.
- **Agent-friendly already:** an MCP server (`report_formats`, `report_inspect`, `report_lint`, `report_render`, `report_scaffold`) and
  a compatibility linter (`rdl-doctor`), which fit plan 16's agents.
- **Data can be pushed in.** Reports can be given rows (`DataSet.SetData`) instead of opening their own connections, so a semantic query
  or a DbDataStore read made under the user's identity feeds them and the suite's limits, permissions and audit still apply.
- **Embedded code is off by default.** A report's VB `<Code>` runs on .NET only if a compiler is registered.

**Cons**

- **RDL coverage is partial.** It models RDL 2005; newer reports are rewritten to that shape only for a Tablix with static columns (a
  plain table). Refused, loudly rather than silently: matrices (a Tablix with dynamic columns), nested hierarchies on both axes, gauges,
  indicators, sparklines, maps and the `Lookup` functions. Real SSRS reports use matrices often, so a sample of real reports has to go
  through `rdl-doctor` before anything is promised.
- **Its own data providers duplicate ours** (provider strings and connection strings inside the report). They stay off; data is pushed
  in from the suite, or reports would bypass Proxy's and DbDataStore's rules.
- **Report extensions can load code**: besides `<Code>`, `<Classes>` and `<CodeModules>` load .NET types; all three are refused for
  reports from users or agents. The expression language needs a review for reflection before agents' reports are rendered.
- **Two chart systems.** RDL charts render through its own chart code, not the shared Vega-Lite views; a report that must match the suite's
  visuals uses our report spec, or embeds a rendered view as an image.
- **A large dependency** with its own UI framework underneath (Majorsilence.Forms); development comes in bursts ("nothing happens for
  weeks or months", its README). Pinned versions, our own tests over the reports we rely on, and fixing things upstream are the price.
- **The designer is Blazor, the console React**: it would be embedded (its own page or a frame), not merged into the console.

**How it would be used, if chosen.** Beside our own report spec, as the engine for RDL and pixel-perfect documents, rendered server-side by
the app host, with data pushed in from the suite and the code-loading features refused. FastReport would then be only the alternative if a
spike fails.

**The spike that decides it:** run a sample of real SSRS reports through `rdl-doctor`, and render the ones it accepts with data pushed from
a semantic query, on Linux and Windows.

## 2b. Identity with several consoles

**Amended (owner, 2026-10-26) by [plan 20](20-serving-layer.md):** the identity home is hosted by the serving layer by default, not by
a console, so end users can sign in without reaching the private console. Running it on its own stays the option.

**The tension.** Plan 13 W12 lets a server be paired with several consoles, and each console keeps its own users and role assignments.
Apps (and the agents and report servers after them) need **one** answer to "who is this user and what may they do". With users per
console, the same person is two users in two consoles, and an app has no single provider to trust.

**The owner's concern:** not too much complexity, but not an architecture so rigid that several consoles become confusing to reconcile.

| Option | What it is | For | Against |
| --- | --- | --- | --- |
| A. Users per console (plan 13 as written) | each console its own directory and provider | nothing new | the tension above: two directories to reconcile, apps cannot pick a provider, audit names differ per console |
| B. An identity service of its own | a separate server (user directory, roles, OpenID Connect provider, Windows and Entra federation); every console, app host and agent signs users in through it | one directory, one provider, consoles become interchangeable views | one more service in every install, including the smallest; its uptime gates every sign-in |
| **C. Identity as a shared component, hosted by one console by default** | the same component as B (`DbDataKit.Identity`), but it runs inside a console unless an install chooses to run it on its own. Each install has **exactly one identity home**; every other console, DbDataApps and the agents are relying parties of it through OpenID Connect | B's single answer without B's extra service in small installs; a large install moves it out without changing anything for consoles or apps | the console that hosts it is special; moving identity out later is an operation to document and test |

**Decided (owner, 2026-10-25): C**, the same pattern as the protocols (plan 15 section 11): shared code, a host chosen per install. Carried into plan 13 (W8, W12, D20; slices U13 to U15).

- **One identity home per install.** It holds users, groups, role assignments and the OpenID Connect provider, and federates Windows
  (Negotiate) and Entra ID. A standalone install has one console, which is the home; nothing changes for it.
- **Other consoles hold no users.** A second console signs people in through the home (it is an OpenID Connect client of it) and keeps
  only its own settings (which servers it shows, its ceiling on each server). The same person is the same user everywhere, and audit
  names agree.
- **Roles are assigned once, at the home**, scoped as plan 13 W12 has them (per server, per Build project; app roles per app). A console
  still has a **ceiling** per server from its pairing, so an operations console can stay read-only whatever a user's roles are.
- **Tokens to servers are unchanged in shape** (plan 13 T01): a console still signs them, but the subject is the home's stable user id,
  and the roles come from the home. App users (decided: console users who administer nothing) are ordinary users of the home with app
  roles only.
- **Running it on its own** (`dbdatakit identity run`, or a deployment option) is for installs that want sign-in independent of any one
  console, or high availability; the component, its data and its protocol are the same.

**What it changes in plan 13:** W12's rule "users are per console" becomes "users live in the install's identity home";
the console's user stores (U04, U05, already built as shared code) become the identity component's; T04 (console side of pairing), U07
(the console host) and U10 (first run) gain the choice of "be the home" or "use a home". T01, T02, T03 and U04, U05 (done) need no
change; T04 (in progress) only takes the subject and roles from the home; new slices U13 to U15.

## 3. What earlier plans must keep possible

| Plan | Keep possible |
| --- | --- |
| 13 W4a (command surface) | every command has a spec and a schema, suite-wide, tested; the MCP surface is generated from the specs |
| 13 W12 (trust) | a token can carry "acting through an agent" for audit; scoped tokens for apps (a ceiling from a manifest) |
| 13 W13 (secrets) | provider keys as secret references |
| 13 W5 (web) | the shared components publishable outside the console |
| 15 (DbDataStore) | its API is the only write path; models generate OpenAPI; it accepts a paired app host as a server client |
| 14 (semantic layer) | the presentation specs (matrix, Vega-Lite) usable outside the console, as the one view format (section 2a); semantic queries callable by a paired server client |
| 13 W5 (web) | the renderer of views as its own package (`@dbdatakit/visuals`), not code inside a console page |

## 4. Decisions and open questions

| # | Question | Status |
| --- | --- | --- |
| 1 | Agent actions | **decided** (to be refined later): every action is generated by the agent and approved by the user as it happens, then run under the user's identity |
| 2 | Apps | **decided direction**: a standalone feature (an app host of its own), with DbDataStore data views and editing, Vega-Lite dashboards, reports and agent assistants |
| 3 | Sign-in | **decided direction**: Windows, OpenID Connect, Entra ID; the console as an OpenID Connect provider; **spec to be designed with the owner** (section 2) |
| 4 | Provider abstraction | **decided** (for now): `Microsoft.Extensions.AI`'s `IChatClient`, proven against OpenAI, Foundry, Anthropic and a local model (Ollama) first |
| 5 | Code apps | **decided**: at the same time as declarative apps, or before them |
| 6 | The app host's name | **decided** (for now): DbDataApps |
| 7 | Visuals | **decided direction**: a shared feature (one view format, one renderer) for the console, apps, assistants and reports |
| 8 | Reports | **decided first step**: our own report spec on the shared visuals, and SSRS and Power BI Report Server on a separate server, embedded in apps and reading the semantic models (plan 17); Majorsilence Reporting a documented option, FastReport a documented alternative |
| 9 | Identity with several consoles | **decided**: one identity home per install, a shared component hosted by a console by default (section 2b) |
