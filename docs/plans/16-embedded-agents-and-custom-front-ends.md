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

- **One provider abstraction**, with implementations for the OpenAI API, the Anthropic API, Microsoft Foundry, and any OpenAI-compatible
  endpoint (local models). Candidate: `Microsoft.Extensions.AI`'s `IChatClient`, which several providers implement; check each provider's
  support for tool calls and streaming before choosing it.
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

A standalone app host (working name **DbDataApps**, to be decided), the same shape as the other products: a server with a management API,
paired with consoles (plan 13 W12), its own CLI on the shared nouns. It holds no data of its own beyond the apps' definitions and
versions; it **reaches data as a client** of the servers that own it:

| An app part | Reads or writes through | Rules come from |
| --- | --- | --- |
| Data views and forms over managed data | DbDataStore's API (reads, validated writes, change requests) | DbDataStore: storage, validation, history, permissions |
| Dashboards and visuals | semantic queries and semantic SQL on Proxy (plan 14); the presentation specs (matrix grids, Vega-Lite) | Proxy and the semantic model |
| Reports | the same queries, laid out as a document (sections, tables, charts, parameters), exported to PDF | Proxy, DbDataStore |
| Agent assistants | the agent runtime (section 1), scoped to the app's objects | the user's identity and approvals |

The app host calls DbDataStore and Proxy **as a paired server client** (plan 13 W12, "Server-to-server clients"), with tokens naming the
signed-in user, so DbDataStore's and Proxy's rules apply exactly as they do to anyone else.

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

1. **Who the users are.** Console users only, or app end users who are not console users (a team that fills in a form and never administers
   anything)? Where those users and their roles live if not in a console.
2. **The console as provider.** Which flows (authorization code with PKCE for browser apps; client credentials for server clients?),
   token lifetimes, consent, and how an app registers as a client of the console.
3. **Federation.** Entra ID and other OpenID Connect providers signing in to the console (the console then issues its own tokens to apps),
   or apps trusting Entra ID directly. Group and role mapping from Entra to the suite's roles.
4. **Windows authentication.** Negotiate (Kerberos, NTLM) at the console and the app host for intranet installs; how a Windows identity
   becomes a suite user and roles (groups); what happens off-domain.
5. **From app to data.** How the app host turns a signed-in app user into the tokens DbDataStore and Proxy accept (plan 13 W12), and how
   the manifest's scope narrows them.
6. **Several consoles.** Plan 13 allows several consoles per server; an app needs one identity provider. Which console it is, and what a
   second console means for an app's users.

## 3. What earlier plans must keep possible

| Plan | Keep possible |
| --- | --- |
| 13 W4a (command surface) | every command has a spec and a schema, suite-wide, tested; the MCP surface is generated from the specs |
| 13 W12 (trust) | a token can carry "acting through an agent" for audit; scoped tokens for apps (a ceiling from a manifest) |
| 13 W13 (secrets) | provider keys as secret references |
| 13 W5 (web) | the shared components publishable outside the console |
| 15 (DbDataStore) | its API is the only write path; models generate OpenAPI; it accepts a paired app host as a server client |
| 14 (semantic layer) | the presentation specs (matrix, Vega-Lite) usable outside the console; semantic queries callable by a paired server client |

## 4. Decisions and open questions

| # | Question | Status |
| --- | --- | --- |
| 1 | Agent actions | **decided** (to be refined later): every action is generated by the agent and approved by the user as it happens, then run under the user's identity |
| 2 | Apps | **decided direction**: a standalone feature (an app host of its own), with DbDataStore data views and editing, Vega-Lite dashboards, reports and agent assistants |
| 3 | Sign-in | **decided direction**: Windows, OpenID Connect, Entra ID; the console as an OpenID Connect provider; **spec to be designed with the owner** (section 2) |
| 4 | Provider abstraction | open: `Microsoft.Extensions.AI` (checked against each provider), or our own |
| 5 | Code apps | open: from the start, or declarative apps first |
| 6 | The app host's name | open: DbDataApps is a working name |
