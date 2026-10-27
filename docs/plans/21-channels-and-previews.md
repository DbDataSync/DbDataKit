# Plan 21: channels, previews and sandboxed writes

Status: **channels, sandboxed writes and promotion decided (owner, 2026-10-26 and 2026-10-27); the rest is open and needs design conversations** (section 4). This plan records what
is decided so that the slices now being built leave room for it. It is not sliced yet, beyond the two amendments in section 3.

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

## 3. What this changes now

| Slice | Change |
| --- | --- |
| Plan 20 O03 (published definitions) | the definition store's key includes the channel from the start; one channel (`prod`) is enough until channels are built, but the key, the publish command and the switch take it. Each published version also records what it depends on (name and version of each package, model or data source it reads), so dependency checks (section 2a) need no format change later |
| Plan 19 Z05 (DbDataStore history) | the history design (native temporal tables and app-managed) leaves room for a draft layer per channel or preview: draft rows are kept apart from history, never become history unless promoted, and reads can be asked to include one draft layer; the conformance suite gets a placeholder case that fails until the draft layer is built |

## 4. Open, for design conversations

1. ~~What is promoted~~ and 2. ~~who approves~~: **decided**, section 2a.
3. **Bindings per channel.** Do connections resolve per channel (`sales` points to a test database in the test channel)? Or do all
   channels read production data, with only writes sandboxed?
4. **Build's outputs in non-production channels.** Warehouse tables are expensive to duplicate. Options:
   - test channels read production warehouse data;
   - a Build project chooses its own test target;
   - a channel-scoped schema.
5. **Sync in channels.** Replication is continuous and has no "preview". Does Sync take part in channels at all, or only as a source
   for the others?
6. **Draft layers and the other products.** Can a semantic query in a preview see that preview's DbDataStore draft, so a report shows
   what the app just wrote? Can a Build preview read it?
7. **Lifetimes.** How long previews and their drafts live, and what promoting a draft would mean (a change request with its own
   approval, or never).
