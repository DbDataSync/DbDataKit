# Plan 21: channels, previews and sandboxed writes

Status: **decided (owner, 2026-10-26 and 2026-10-27)**: channels, sandboxed writes, promotion, connections per channel, Build and Sync in channels, draft visibility and lifetimes. This plan records what
is decided so that the slices now being built leave room for it. It is not sliced yet, beyond the amendments in section 3.

## 1. The problem

The suite has many products, and each has outputs with their own versions: Build projects, Sync configuration, semantic packages,
DbDataStore models, apps. Testing one change often needs several of them together. Two examples:

- an app that writes back to DbDataStore;
- a report that mixes managed data with what the front end shows.

Separate dev, test and prod copies of every product and every output multiply quickly. The owner's view: "the permutations of dev,
test and prod between all of the different products and their outputs becomes unwieldy quickly".

## 2. Decided (owner, 2026-10-26)

1. **Channels in one install, not separate installs per environment.**
   - The serving layer already runs versioned, published definitions (plan 20, principle 1). A **channel** (for example preview, test,
     prod) is part of a published version's key.
   - Promotion publishes the same version to the next channel. The definition does not change between environments; what it is bound to
     can (section 4, question 3).
2. **Sandboxed writes for DbDataStore.**
   - A preview or test channel can write into a **draft layer** that only that channel (or that one preview) sees.
   - Its reads show the draft on top of the real data. Closing the preview, or resetting the channel, discards the draft.
   - This lets an app that writes back be tested without a copy of the data store. It builds on DbDataStore's versioned history
     (plan 15 section 3).
3. **Previews are read-only by default.** A preview reads real data with its author's permissions. Writes go to the draft layer only
   when the author turns them on.

## 2a. Promotion (decided, owner, 2026-10-27)

The options weighed:

| Option | What it is | Why not, or why |
| --- | --- | --- |
| A. Per product only | each product promotes its own outputs | simple and keeps each product's rules, but nothing stops a bad order across products |
| B. Change sets | a bundle across products promoted as one unit | cannot be atomic: Build applies and DbDataStore structure changes run on real databases and cannot be switched or rolled back like a publish |
| C. Whole channel | everything in test moves to prod at once | the same problem as B, larger; unrelated work is forced to move together |
| **D. Per product, with dependency checks and releases** | A, plus refusing a promotion whose dependencies are missing in the target channel, plus an ordered release | **decided** |

What is decided:

1. **The unit is a per-product promotion.** It covers a Build project version, a semantic package, a DbDataStore model version, an app
   version, and so on. Each product keeps its own rules for applying a change (Build's plans, the serving layer's atomic switch).
2. **Dependency checks.** A promotion is refused when something it depends on is not in the target channel at a compatible version, for
   example an app whose semantic package is still in test. Every published definition therefore declares what it depends on; the
   serving layer's published definitions and lineage (plan 18) hold the graph.
3. **Releases.** A release is a named, ordered list of per-product promotions, for example a Build apply, then a semantic package, then
   an app.
   - It runs in order and stops at the first failure.
   - It says exactly where it stopped, and it can be resumed from that point.
   - It is a sequence, not a transaction, and the console says so.
4. **One approval for a release**, given by an approver who holds the approval permission for **every** item in it. An approver
   without permission for one item cannot approve the release; that item's owner approves it, or it is taken out.
5. **Expand, then contract.** A change to a shared structure goes in three steps: add the new structure, move the consumers over, then
   remove the old structure in a later promotion. This is needed because channels share databases: drafts sandbox writes, not
   structure. Where the suite can see the consumers, it refuses the contract step. For example, a Build plan that drops a column still
   read by a published package in any channel is refused, naming the package and the channel.

## 2b. Connections per channel (decided, owner, 2026-10-27)

The options weighed:

| Option | What it is | Why not, or why |
| --- | --- | --- |
| A. Every channel reads production | only DbDataStore writes are sandboxed | simple, but Build in a test channel has nowhere to write except production targets |
| B. Every connection bound per channel | `sales` points to a test server in test | full isolation, but every test database must exist and be kept filled, and test data drifts from production |
| **C. Inherit reads, override where needed** | a channel uses production's binding unless it overrides it, per connection | **decided** |

What is decided:

1. **A channel inherits production's binding for each connection, unless it overrides it.** Overrides are per connection and per
   channel, and data is copied only where an override needs it.
2. **Only reads are inherited.** A non-production channel never gets a production write binding by inheritance:
   - a write binding in a test or preview channel must be an explicit override that points somewhere other than production;
   - without one, the channel has no write access to that connection;
   - DbDataStore writes are the exception, since they go to draft layers (section 2).

   So a test channel cannot write to production by accident.
3. **Overrides live in the connection's own definition.** `connection/1` gets an optional `channels` map, kept with the connection in
   the product that owns it, with the same secrets handling as the rest of the definition.
4. **The console shows one table per install:** connections down the side, channels across, each cell marked inherited or overridden.
5. **Lineage** (plan 18) identifies datasets by their physical connection. A dataset reached through an overridden binding is therefore
   its own node, marked with the channel; an inherited binding shows the production node.
6. **Later:** limits on non-production reads of production data (row caps, masking). They come with the row- and column-level access
   rules.

What follows for Build (section 4, question 4):
- in a non-production channel, a Build project's target must be an override, either a test database or a channel-scoped schema on the
  same server;
- without one, Build cannot apply in that channel.

## 3. What this changes now

| Slice | Change |
| --- | --- |
| Plan 20 O03 (published definitions) | the definition store's key includes the channel from the start; one channel (`prod`) is enough until channels are built, but the key, the publish command and the switch take it. Each published version also records what it depends on (name and version of each package, model or data source it reads), so dependency checks (section 2a) need no format change later |
| Plan 13a E01, E03 and the `connection/1` schema | the schema reserves an optional `channels` map (section 2b). Until channels are built, a file that uses it is refused with a message naming the file and the key, so the format does not change again later |
| Plan 19 Z05 (DbDataStore history) | the history design (native temporal tables and app-managed) leaves room for a draft layer per channel or preview: draft rows are kept apart from history, never become history unless promoted, and reads can be asked to include one draft layer; the conformance suite gets a placeholder case that fails until the draft layer is built |

## 2c. Build, Sync, drafts and lifetimes (decided, owner, 2026-10-27)

The running example is a "returns" feature:
- Sync replicates `orders`;
- Build adds `fact_returns` to the warehouse `dw`;
- the semantic package `sales` gains `return_rate`;
- DbDataStore gains the entity `return_reason`;
- the "Returns desk" app records reasons and shows `return_rate` by reason.

It is tested in the `test` channel.

1. **Test targets for Build are created by an explicit command,** never automatically.
   - For example, `dbdatabuild channel target create dw --channel test` creates `dw_test` with Build's own write login and records the
     override in the connection's `channels` map.
   - The name follows `<schema>_<channel>` unless one is given.
   - When a channel is reset or deleted, the console offers to drop the schema.
   - Where the engine makes this hard (Oracle, where a schema is a user and needs a DBA), the command says so and the schema is made by
     hand. On DuckDB the target is a separate file.
2. **A test target is filled by deferring to production.**
   - Build builds only the models changed in this version, and whatever depends on them, into the test target. Unchanged upstream models
     are read from production's target (an inherited read, section 2b).
   - In the example, only `fact_returns` and its dependents go to `dw_test`; `dim_customer` and `dim_date` are read from `dw`.
   - A sample build (`DbDataBuild.Sample`) is a per-run option for fast iteration.
   - When the test target is on a different server from production, the deferred reads need either a query across servers or a copy
     (Build's copies). The plan says which before it runs.
3. **Sync runs in production only, with trial runs.**
   - Additive changes (a new column such as `return_flag`, a new table) are expand steps and go straight to production replication
     through per-product promotion. Test channels see them through inherited reads.
   - A risky change (a changed key, a new source) gets a trial run first: a one-off run, optionally limited in rows, into an overridden
     target in a test channel. It is never a continuous replication per channel.
4. **Drafts are visible to everything the serving layer answers in that channel or preview,** but not to Build.
   - Everything served there sees the draft: the app, its dashboards, semantic queries (their reads of entities go through the `store`
     module with the same context), and agents.
   - Build does not see drafts by default, since it would write them into tables. A Build run in the same channel, into an overridden
     target, may opt in.
   - A semantic query that joins entities to the warehouse sees the draft only on the entity side.
5. **Previews and drafts have lifetimes.**
   - A preview lives with its workspace and is removed, with its draft, when the branch is merged or abandoned.
   - An idle preview expires after 30 days by default (configurable), with a notice first.
   - A channel's own draft (for example the shared test channel's) lasts until someone resets it; a release may include a reset as a
     step.
   - **Drafts are never promoted.** To keep what was entered, "submit as change requests" turns draft rows into ordinary DbDataStore
     change requests in production, approved like any other.

## 4. Open

Nothing is open in this plan. The next step is to slice it, after plan 20's O03 and plan 19's Z05 have landed.
