# DbDataKit.Backlog

The library of DbDataBacklog (plan 27 section 5, slice BK01): work items, epics, sprints, boards, comments, links and attachments on the application's **own** structure, the state module `backlog` (`BacklogModule` and `BacklogStore` in `DbDataKit.State`). It is never a DbDataStore model: Store models are authored after install and can only add custom attributes to an item (EA01).

| Part | What it is |
| --- | --- |
| `BacklogService` | the rules over the store: who may do what, which move along the workflow is allowed and what it requires, the checks that keep an epic, a sprint and a link true, one transaction per act with its history rows. A refusal is a `BacklogException` whose `Problem` (`NotFound`, `Forbidden`, `Invalid`, `Conflict`) the host maps to its answer. |
| `WorkflowDefinition`, `WorkflowLoader`, `BacklogWorkflows.Default` | the states, the move between them and who may make it, as the strict `backlog-workflow/1` format. In version 1 only the default is in use (`default-workflow.yml`, embedded); no installation reads a file of its own. |
| `WorkItemLoader`, `BacklogImport` | `work-item/1`: items to make (a person, an agent's proposal or an integration task writes the file) and a backlog exported. |
| `schemas/` | the JSON Schemas of both formats (embedded; the tests check they agree with the readers). |

## Roles

`BacklogRole` is `Viewer` (reads), `Member` (works on the backlog) and `Lead` (also starts and closes sprints, reopens finished items, deletes an item, makes backlogs). The host maps its own roles: the console maps an administrator to `Lead` and a viewer to `Viewer`.

## The default workflow

`backlog` and `ready` (todo), `in-progress` and `review` (doing, need an assignee), `done` and `cancelled` (done, need a resolution). A member moves an item along `backlog <-> ready -> in-progress <-> review -> done`, back from `in-progress` to `ready`, and to `cancelled` from anywhere; only a lead reopens (`done -> in-progress`) or revives (`cancelled -> backlog`). Entering a state of the done category sets the item's closed time; leaving it clears that and the resolution. An epic is not finished while it has open items.

## Rules the service keeps

* An item's number is taken in the transaction that makes it (`DATA-12`); the backlog's order is a spaced `sort_key`, so putting an item before another rewrites one row (the backlog is renumbered only when two items have no room between them).
* An edit made on a stale copy is refused (`Conflict`), never merged over someone's change (`row_version`).
* An item belongs to an epic of its own backlog; an epic belongs to none and is not planned in a sprint.
* A backlog has at most one active sprint. Closing it keeps the finished items in it and sends the others to a planned sprint or back to the backlog, each with a history row.
* A link names another item of the same backlog, a suite object (`build-model`, `sync-replication`, `semantic-model`, `store-entity`, `app`, `run`, `change-request`, by the identity the other side uses), a workspace or a pull request; a link whose target is gone is flagged (`BacklogStore.MarkTarget`), never deleted.
* An attachment's bytes are a payload of the payload store (25 MiB at most by default); the payload's owner row is the attachment's row, so the shared retention job removes a payload whose row has gone.
* Every change writes a history row (who, the name they had then, what, old and new value) in the same transaction as the change.

## Not here

The API over the service as the signed-in person (BK02, the serving layer's `backlog` module, waits for plan 20's contract), the links' effects on other products' pages (BK05, BK06), notifications (BK07), automatic items (BK08), the assistant (BK09), custom attributes (EA01 to EA04).
