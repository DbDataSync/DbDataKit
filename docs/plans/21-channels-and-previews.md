# Plan 21: channels, previews and sandboxed writes

Status: **two decisions made (owner, 2026-10-26); the rest is open and needs design conversations** (section 4). This plan records what
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

## 3. What this changes now

| Slice | Change |
| --- | --- |
| Plan 20 O03 (published definitions) | the definition store's key includes the channel from the start; one channel (`prod`) is enough until channels are built, but the key, the publish command and the switch take it |
| Plan 19 Z05 (DbDataStore history) | the history design (native temporal tables and app-managed) leaves room for a draft layer per channel or preview: draft rows are kept apart from history, never become history unless promoted, and reads can be asked to include one draft layer; the conformance suite gets a placeholder case that fails until the draft layer is built |

## 4. Open, for design conversations

1. **What is promoted?** One product's change at a time, or a change set across products (a Build model, the semantic package on it,
   and the app that reads it, moving together)?
2. **Who approves a promotion,** and is it per product or per change set?
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
