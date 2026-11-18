# Code comments

A comment describes the code it sits on: what it does and why, in terms of the code. A reader who has only the file and the docs under `docs/` must be
able to follow it.

## What a comment never cites

Plans, task ids (`INSTALLER-3`), proposed decisions, subjects and work-in-progress notes are temporary: a subject file is deleted when its last task
lands, and a task id then names nothing. A comment does not cite them. The reason a task gave belongs in the comment as the reason itself:

```csharp
// Wrong: the task is gone once it lands, and the reason with it.
// INSTALLER-3: write the failure file before exiting.

// Right: the reason, in terms of the code.
// A service that fails before its log is open writes the failure to a file next to its data, so the person who started it can read why.
```

Ids of the deleted plans that older comments still carry (`plan 33 SC23`) are history (`planning/ROLES.md`, "Old plan ids"); new comments do not add them.

## Pointing at a design

A comment may point at a finished document under `docs/` when the design is longer than a comment should be. It uses one fixed form, so a tool can find
every reference and check that the target still exists:

```
Design: docs/<path>.md#<anchor>
```

inside the comment, on a line of its own or at the end of the comment, with the path from the repository root and the anchor of the heading:

```csharp
// Starts the parts of the server in a fixed order and stops at the first that fails, naming it.
// Design: docs/install.md#the-startup-sequence
```

```python
# Design: docs/branch-cleanup.md#backup-branches
```

Only documents under `docs/` that describe how the product is (not `planning/`, not a wip file) are named this way. When a heading is renamed or a
section moves, the comments that name it change in the same commit.
