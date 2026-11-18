# BACKLOGS: DbDataBacklog's serving module and custom attributes for any object

## Goal

Backlog, sprint and work management for teams that build on the suite, where a work item can point at the thing it is about; and custom attributes on
any object type, kept in the Store. Parked (owner, 2026-11-14): future work, not scheduled; what is built stays. These are its open designs, so the
design is not lost while it waits.

## What exists today

- `DbDataKit.Backlog` (`Shared/src/DbDataKit.Backlog/README.md`): work items, epics, sprints, boards, comments, links and attachments on the
  application's own structure, the state module `backlog`; the default workflow and its rules.
- The console serves the library at `/api/backlog` and shows it on its Backlog screen (backlog, board, sprints, the item page); no browser story.
- No serving module, no links' effects on other products' pages, no notifications, no automatic items, no assistant, no custom attributes.

## Open designs

### The backlog's own structure and its serving module

The core data structure is the application's own, never a Store model (owner, 2026-11-06): DbDataBacklog is set up when it is installed, while Store
models are authored after the rest of the configuration, so the backlog cannot depend on them; the Store can only augment it. Work items, epics, sprints,
boards, comments, links, states and transitions are the `backlog` state module, created on install and migrated with each release. Its rules (who may
move an item to which state, required fields, what a transition does) are the backlog library, served by a `backlog` module of the Gateway (its API, as
the signed-in person, audited, with the module's health), so the screens are a client of it like any app: an app shipped with the suite, served by the
apps module, and a portal for teams that want it alone. Attachments go to the payload store, notifications and digests through the delivery service,
automatic items (a failed run, a check that blocks, an approval waiting) are integration tasks on a Runner (a second failure of the same task adds a
comment, not an item), and an assistant (write and split items, estimate, plan and summarise a sprint) is an agent definition with the backlog API as
its tools, asking before it writes. It is not a server of its own.

**What makes it worth having in this suite:** links to suite objects (an item names a Build model, a replication, a semantic model, a Store entity, an
app, a run or a change request by its lineage identity, and those objects show their open items; a dropped object leaves the link flagged, not deleted);
links to Workspace branches (an item opens a workspace and follows its commits, merge and publish) and to GitHub pull requests; a back-end agent that
takes an item, works in its workspace and hands it back for review; a closed item's resolution proposed as a knowledge suggestion. Outside tools (Jira,
Azure Boards) are extremely low priority and may never happen; an import would be an integration template, never a two-way sync.

**Open questions:** whether the console's own serving of the library stays once the module exists; the order of the parts when it is un-parked.

### Custom attributes for any object type

An object's core structure belongs to the product that owns it; the Store can augment any object type with custom attributes (owner, 2026-11-06).

- **An extension model** is a Store model attached to an object type (`work-item`, `semantic-model`, `build-model`, `app`, `connection`, ...) with
  `extends` (an optional key of `model` format 2): its fields are the custom attributes, each record keyed by the object's stable identity, the one the
  catalog and lineage use.
- **The object's own screens show them**, edited with the Store's validation, history and audit under the Store's permissions, through
  `@dbdatakit/store-ui`; first the work item, then the semantic model, Build model, app and connection screens; a reader sees them read-only.
- **They never change what the product does:** a custom attribute cannot make a work item skip a state or a model build differently, and no rule of
  the owning product reads one.
- **An object that goes leaves its attributes orphaned**, listed for their owners, who can delete them, as knowledge items are.
- **Two kinds of attribute, kept apart:** properties written in a definition (Build's `tags` and `properties`, in git, versioned with it) and custom
  attributes kept in the Store (edited by business users at run time); search and generated knowledge show both, labelled.

**Open question:** whether extension models wait for the Gateway's store module to serve them by object identity (the old plan re-planned them on it).

## Shared areas touched

- `DbDataKit.Backlog`, the `backlog` state module, `DbDataKit.Store` and `DbDataKit.DataModel` (`extends`), `@dbdatakit/store-ui`, the console's Backlog
  screen, `docs/formats.json` (`model` if `extends` is added).
