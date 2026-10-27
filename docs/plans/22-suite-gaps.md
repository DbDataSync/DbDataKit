# Plan 22: filling the suite's major gaps

Status: **direction decided by the owner (2026-10-27)**, one question at a time. Not sliced yet; each section becomes slices once the
plans it depends on (20, 21) have their first slices merged.

The gaps came from a review of plans 13 to 21 against the data-flow diagram (`docs/suite-data-flow.md`). The seventh gap,
environments and promotion, is plan 21.

## 1. App authoring

Plan 16 described how an app is served, not how it is written, built, tested and versioned.

1. **Shared authoring.** The authoring machinery Build already has moves into a shared library (`DbDataKit.Authoring`), and both Build
   projects and app projects use it. That machinery is plan 13a's W02–W10:
   - git workspaces on the server, one per user (a branch);
   - the console's file tree and editor with diagnostics;
   - diff, commit and merge;
   - the Author role.

   The W slices still to come build it in the shared library instead of in Build.
2. **An apps back-end server** (DbDataApps) owns app repositories, builds and versions. It publishes built versions to the serving
   layer's `apps` module (plan 20), following the same pattern as Build and DbDataStore.
3. **The framework.**
   - Every app starts from one template.
   - The default library set is fixed: `@dbdatakit/shell`, `@dbdatakit/visuals`, and typed clients for DbDataStore and semantic
     queries.
   - Builds run on the server in a child process, offline, from vendored packages.
   - An **allow-list** lets an administrator add further packages. No other `npm install` runs.
4. **Declarative views are part of the framework, not a second kind of app.** The template ships components that render spec files
   (grid, form, detail page, dashboard). A new app can be nothing but spec files, and an author drops into code one page at a time.
   This replaces plan 16's "two kinds of app".
5. **One workflow for people and agents:**
   - create a workspace (a branch);
   - edit;
   - build and preview (plan 21: previews are read-only by default, with sandboxed writes on request);
   - commit and merge;
   - publish.

   A person does this in the console's editor. An agent does the same through MCP, with every step approved by its user as it happens.

## 2. Row- and column-level access rules

Plan 14 deferred row-level security ("not needed now"). With one serving layer, there is now one place to enforce it.

1. **Enforced in the serving layer**, by one policy engine applied to every module: Flight SQL, semantic, store, apps, MCP and feeds.
   Rules are added to the query before it runs and to the result's columns before it is returned. A module cannot bypass them.
2. **Rules are defined with the data's owner and published with it.**
   - Build defines rules on its models and semantic packages.
   - DbDataStore defines rules on its entities.
   - The rules travel with the published definition (plan 20, principle 1), so a promotion moves rules and data together.
3. **Rules key on user attributes,** which come from two sources:
   - **The directory:** claims and groups from Entra ID, Active Directory or Windows sign-in, mapped to attributes in the identity home.
   - **A grants entity in DbDataStore**, defined by the suite.
     - Each row is a subject (a user or a group), an attribute, a value and effective dates.
     - A user or group can hold many values (a manager over three regions), and a new attribute needs no model change.
     - Each attribute is declared once, naming the entity that holds its valid values (for example `region`), so a grant is validated
       like any other write.
     - Grants are maintained by business owners, with DbDataStore's history, approval and audit. "Who could see what, when" is therefore
       answerable.
4. **An example rule:** `region IN user.attributes('region')`, where the values come from the directory and the grants entity together.
5. **Masking:** a column rule can hide a column, mask it, or replace it with null, again keyed on attributes.
6. **Later:** limits on non-production channels reading production data (plan 21 section 2b) use this same engine.

## 3. Catalog and search

1. **Built on the lineage graph** (plan 18). Every node is searchable: datasets, columns, jobs, semantic models and members, entities,
   apps.
2. **Descriptions and owners come from the definitions:** Build's model and column descriptions, DbDataStore model text, semantic
   member descriptions, app manifests. Nothing is typed twice.
3. **The glossary is a DbDataStore entity:** terms, definitions, owners and links to nodes, with history and approval like any
   managed data.
4. **Agents use the same search** through the command surface (`catalog search`, `catalog show`), so "which table has customer
   churn" is a tool call.

## 4. Backup and recovery

| Part of the suite's state | How it is protected |
| --- | --- |
| Definitions (Sync configuration, Build projects, serving-layer configuration, connection definitions without secret values, app source, DbDataStore models) | **git remotes.** Each configuration repository can push to a remote the owner chooses (GitHub, Azure DevOps, any git server) on every commit. Published definitions can be republished from them |
| The secrets store's key | documented as a separate backup (plan 13 section on secrets); never in git; a DPAPI-bound key is machine-specific, so the docs say how to re-key on restore |
| Identity home, pairing and trust, audit, approvals, change requests, run history | on SQL Server or PostgreSQL (required for serving groups), **the database's own backups**. For single-instance SQLite installs, a small command (`<product> state backup` and `state restore`) archives the state database, with a restore test in CI |
| DbDataStore's data, the warehouse | the customer's database backups; Build can also rebuild the warehouse |

## 5. Delivery: alerts and subscriptions

1. **One delivery service**, shared code (`DbDataKit.Delivery`), used by both alerts and report subscriptions.
2. **Channels:** email (SMTP), Teams and Slack webhooks, and a generic signed webhook. Each channel is configured once per install,
   with its secrets in the secrets store.
3. **Alerts** come from monitoring (plan 13 W10): failed runs, late data, approvals waiting, expiring previews.
4. **Subscriptions** deliver a report or a view (plan 16 section 2a) on a schedule, through the shared scheduler (`DbDataKit.Scheduling`).
   They are rendered under the subscriber's own identity, so access rules (section 2) apply to what is sent.

## 6. External monitoring

1. **OpenTelemetry** from every server and the serving layer: metrics, traces and logs.
   - Off by default, and turned on by one setting.
   - Exported to any OpenTelemetry collector; Prometheus scraping goes through the OpenTelemetry exporter.
2. **Traces follow a request across servers,** for example a query from an app through the semantic module to a database. Identities
   in traces are the user's id, never secrets or query parameters.
3. **The console's own monitoring stays.** OpenTelemetry is for operations teams who already have their own tools.

## 7. What this changes in other plans

| Plan | Change |
| --- | --- |
| 13a W02–W10 | built in `DbDataKit.Authoring` rather than in Build, so app projects can use them; the W slices' tests stay as written |
| 14 concern 5 (row-level security) | superseded by section 2: enforced in the serving layer, keyed on user attributes |
| 16 section 2 | app authoring (section 1); declarative views become framework components; builds use the allow-list |
| 18 | the catalog (section 3) builds on the lineage graph |
| 20 | the policy engine (section 2) becomes a principle of the serving layer |
