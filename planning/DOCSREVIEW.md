# DOCSREVIEW: reference docs the owner approves, and every doc and plan checked against them

## Goal

A small set of reference docs says how the suite works, each approved by the owner and changed only with the owner's approval (`planning/ROLES.md`,
"Files only the owner changes"): the architecture and services, the processes, the feature areas, the document types and how they fit together.
Every other doc and every planning subject agrees with them, and says plainly what is built, in progress or planned.

## What exists today

- `docs/architecture.md`: the approved sections (terms, the four services, who calls whom, what runs where, sign-in, state, screens, risky actions,
  agents, command lines, how our code calls our code, planned changes). Not yet reviewed: "Decisions" and the "13c" table, "Why the
  design is safe", "Where the conventions live".
- `docs/formats.md`: how versioned files are read, refused and written, and how upgrades work (approved by the owner).
- The writing rules: the skill `product-docs`.
- Known contradiction: `docs/authoring-metadata.md` line 20 says the console and the Workspace are one place ("Control").

## Tasks

Each task is written as a short draft for the owner, in small batches, and checked by hand: the owner approves it.

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| DOCSREVIEW-1 | Processes: how work is run, from `planning/ROLES.md` | none | S | the owner approves it |
| DOCSREVIEW-2 | Feature areas: apps, semantic models, build models, master data, sync, integrations, agents, dashboards | none | M | the owner approves it |
| DOCSREVIEW-3 | Document types: each feature's files, where they are authored, built, deployed and run (from `docs/formats.json` and `docs/ui-editors.md`) | DOCSREVIEW-2 | M | the owner approves it |
| DOCSREVIEW-4 | How it fits together: one walkthrough from authoring to running | DOCSREVIEW-3 | S | the owner approves it |
| DOCSREVIEW-6 | "Why the design is safe" moves from `docs/architecture.md` to `docs/security-review.md` | none | S | the owner approves it |
| DOCSREVIEW-7 | "Where the conventions live" in `docs/architecture.md`: check each row | none | S | the owner approves it |
| DOCSREVIEW-8 | Every page under `docs/` and every README checked against `docs/architecture.md`: each contradiction listed, then fixed in small batches, each statement marked built, in progress or planned | none | L | the list is empty |
| DOCSREVIEW-9 | Every subject in `planning/` checked the same way | none | M | the list is empty |

## Needs from other subjects

- CODECLEANUP-12: the decision tables of `docs/architecture.md`, rewritten as description.

## Shared areas touched

`docs/` (every page), the READMEs, `planning/` (every subject), `CLAUDE.md` and `planning/ROLES.md` when a reference doc is added to the protected list.
