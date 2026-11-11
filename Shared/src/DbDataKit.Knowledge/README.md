# DbDataKit.Knowledge

Knowledge for people and agents (plan 27, plan 27a). This library holds what needs no server and no model: the item format, the folder defaults, subjects and orphans, scope matching and chunking. The index, the embedding models and the serving module (`KN05` and after) are other slices and build on it.

## The item: `knowledge/1`

A Markdown file whose front matter says what it is for. The reader (`KnowledgeItemLoader`) is strict: an unknown key, a bad role or a bad subject is a diagnostic with the file, line and column and, where there is a close word, "did you mean". Nothing throws on a damaged file.

```markdown
---
knowledge: 1                      # the format number; a newer one is refused naming the file and both numbers
title: Revenue means net revenue  # left out: the first `# ` heading of the text
role: instruction                 # reference | instruction | guardrail
about:                            # any one entry puts the item in scope; an entry may be narrowed once
  - semantic-model: Sales
    measure: revenue
for:                              # each key narrows; a key left out means all
  audience: [front-end, back-end]
owner: finance-data               # a group of the identity home; approves changes
chunking: none                    # references only: none | { split: heading-level-2 }
---
Revenue in the Sales model means **net revenue**: gross sales minus returns minus discounts.
```

- **Roles.** A *reference* is found by search; an *instruction* is always given where its scope applies; a *guardrail* is an instruction marked as policy and given in a section of its own. An instruction or guardrail with no owner is a warning (`DBD-KNW-005`).
- **Subjects** are named as the catalog names them: `build-project` (narrowed by `folder` or `model`), `semantic-model` (`measure` or `dimension`), `store-model` (`field`), `connection`, `app`, `integration`, `tag`, `property` (`name=value`), `product` (one or a list: build, sync, store, integration, apps, serving, runner, console) and `install: true`.
- **Defaults by place.** A folder may hold `_knowledge.yml` (`knowledge: 1`, `defaults:` with `role`, `about`, `for`, `owner`, `chunking`); the nearest wins, key by key, and an item's own key beats every default. A file in a project's `knowledge/` folder needs no `about`: it is about that project (`KnowledgeProject.Load(root, impliedAbout)`). `README.md` and files starting with `_` are not items.
- **The version of an item** is the sha256 of its text with `\n` line endings (`KnowledgeItem.Hash`).
- **Settings are not knowledge.** `prompting`, `limits` and `data-rules` in an item or a folder file are refused with a message that says they belong in the install policy (`agent-policy/1`); policy *text* is a guardrail item.
- The JSON Schema of the front matter is `schemas/knowledge.schema.json` (embedded; `KnowledgeItemLoader.Schema()`). The reader is the authority and a test keeps the two equal.

## Subjects and orphans (`SubjectResolver`)

A host says what exists through `ISubjectCatalog` (`SubjectCatalog` is one held in memory). A subject the catalog has **never** heard of is an error when the item is saved, at the subject's own line (`CheckOnSave`, `DBD-KNW-010`). A subject that **was** there and is gone leaves the item **orphaned** when none of its subjects is left: it reaches nobody (`ScopeMatcher` leaves it out) and `Orphans` lists it for its owner. `product` and `install` resolve with no catalog.

## Scope matching (`ScopeMatcher`)

Given what is in play (`InPlay`: a subject, with `Whole` when everything inside it counts, as a workspace on a Build project) and who asks (`ScopeAudience`), `Match` returns the instructions and the guardrails that apply, each from the broadest scope to the narrowest (install, product, project, folder, model, member), ties by path. An item applies when something in play is one of its subjects or inside it; references are never given by scope. The same input gives the same order.

## Chunking (`Chunker`)

Structure first (Markdown headings; a section too long splits at paragraphs, then sentences; a table or fenced code block is never cut), size from the embedding model (`ChunkerOptions.ForModel`: half its largest input, at most 500 tokens), one sentence of overlap between the pieces of one section. A chunk's id comes from its item and heading path; its **hash is of the embedded text** (the item's title and heading path, then the text), so only a changed chunk is embedded again (`Chunker.Changed`). Instructions, guardrails and short references are one chunk.

## The install knowledge project (`InstallKnowledge`)

`InstallKnowledge.Write(folder)` makes the template of plan 27 section 3.5c: `shared/` (the whole install), `build/` and `sync-and-build/` (scoped by their `_knowledge.yml`) and `policy/` (guardrails owned by `administrators`; `CheckPolicyFolder` refuses anything else there).

## What a host shows (`KnowledgeInsight`, `Worklists`)

- **Chunk preview** (`KnowledgeInsight.Chunks`): an item as typed, read strictly (diagnostics with line and column), split as it would be embedded, each chunk with its heading path and tokens; given the published text, the chunks whose hash differs are marked changed, so editing one section changes one chunk.
- **Scope preview** (`KnowledgeInsight.Scope`): what `ScopeMatcher` gives for what is in play and who asks, in order with the level each applies at, and the items left out with the reason (a reference is found by search, the subject is gone, not for this audience, what it is about is not in play).
- **Re-pointing** (`KnowledgeInsight.Repoint`): rewrites the name of a subject where it is written (the subject carries its line); the rest of the file is untouched, and it refuses when the file is not as it was read.
- **Worklists** (`Worklists.Build`): per owner, the items that are orphaned (with the subject that is gone), over a budget or marked for another look.
