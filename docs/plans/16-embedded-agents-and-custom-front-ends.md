# Plan 16: embedded agents and custom front ends (backlog)

Status: **backlog** (2026-10-24): features the owner wants, not yet scheduled. **Not part of plan 13's work list.** This records what they
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
- **Effect classes decide confirmation.** Reads run; writes are shown and confirmed by the user (per call, or per session where the user
  allows it); approvals stay human: an agent never exercises the Approver role, so its changes to databases are plans and change
  requests for a person to approve.
- **Tool results are data, not instructions.** Rows, documents and messages from tools can contain text written to steer the agent; the
  runtime marks them as data, and a destructive or outward-facing step always needs the user's confirmation, whatever the agent was told.
- **Transcripts are kept** (state store), with the tool calls and their results, so a session can be reviewed and replayed.

### Where it runs

The agent runtime is a shared library (`DbDataKit.Agents`): the provider abstraction, the tool binding to command specs, confirmation,
transcripts and limits. **The console hosts it** for its own chat; **DbDataStore hosts it** for custom front ends (section 2); keys and
model calls stay on the server, never in the browser. **Tests** run against recorded model responses, so CI needs no model API.

## 2. Custom front ends on DbDataStore

**Goal.** Custom user interfaces on top of DbDataStore's standardized storage, permissions and validation, deployable by an **external
agent through MCP** and by the **internal agent** with the same tools.

### The principle: the app is untrusted UI

All rules stay on the server: an app reads and writes only through DbDataStore's API (or its protocols, plan 15 section 11), with the
user's identity, so validation, history, approval and permissions apply whatever the app's code does. An app can make a better screen;
it cannot make a different rule.

### Two kinds of app

| Kind | What it is | For |
| --- | --- | --- |
| **Declarative** | a manifest plus view specs (forms, grids, detail pages, dashboards over semantic queries), rendered by DbDataStore with the shared components | most cases, and the safest thing for an agent to generate: it is data, validated against a schema |
| **Code** | a static web bundle (HTML, JavaScript, CSS) with a manifest | a UX the declarative views cannot express |

Both have a **manifest**: name, version, the models and entities it uses (and with which operations), its routes, the roles it needs,
and for a code app its content security policy.

- **Served by DbDataStore** at `/apps/<name>/`, each version kept, with rollback. A code app runs on **its own origin or in a sandboxed
  frame** with a strict content security policy, and gets **its own scoped token**: the intersection of what the manifest declares and what
  the user may do. It never sees the console's tokens.
- **A typed client per model**, generated from the `model/1` definitions (an OpenAPI description and a TypeScript client), and the
  console's shared components (`@dbdatakit/shell`: grid, form, field, preview) published for app authors, so custom apps look like the
  suite when they want to and validate as the server does.

### Deploying through MCP

- **App tools on the command surface**: `app create`, `app validate` (manifest, view specs, the bundle's size and policy), `app preview`
  (deploy to a preview slot only its author sees), `app deploy`, `app list`, `app rollback`, `app delete`. The same tools for an external
  agent over MCP and for the internal agent (section 1).
- **Deploying is a reviewed change**: `app deploy` creates a request that a person with the right role approves (as Build plans are
  approved), and the approval records who and which version. A preview needs no approval.

### Who signs in to an app

Plan 13 puts users in the consoles; an app's end users may not be console users. **Open question:** recommended direction: the console
is the identity provider for apps (OpenID Connect from the console, so an app signs users in with the console's users and roles), with
external identity providers (Microsoft Entra ID, any OpenID Connect provider) added to the console later; the alternative is a user
directory in DbDataStore for app users only. It depends on DbDataStore's approval and permission model, which is still to be specified
(plan 15 section 9).

## 3. What earlier plans must keep possible

| Plan | Keep possible |
| --- | --- |
| 13 W4a (command surface) | every command has a spec and a schema, suite-wide, tested; the MCP surface is generated from the specs |
| 13 W12 (trust) | a token can carry "acting through an agent" for audit; scoped tokens for apps (a ceiling from a manifest) |
| 13 W13 (secrets) | provider keys as secret references |
| 13 W5 (web) | the shared components publishable outside the console |
| 15 (DbDataStore) | its API is the only write path; models generate OpenAPI; its server can host static apps and the agent runtime |

## 4. Questions for the owner (when it is planned)

1. Provider abstraction: `Microsoft.Extensions.AI` (to be checked against each provider), or one of our own?
2. Write confirmation: per call always, or a per-session allowance the user can give?
3. App end users: the console as identity provider (recommended), or a user directory in DbDataStore?
4. Code apps: allowed from the start, or declarative apps first?
5. App deployment approval: always, or only for apps that write?
