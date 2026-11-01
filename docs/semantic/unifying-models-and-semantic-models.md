# Unifying models and semantic models: a proposal (J30)

Asked for by the owner on 2026-10-31 as the follow-up to D12. It is about the two concepts and the two formats as a whole, not only relationships. No code. It builds on `models-and-semantic-models.md` (what overlaps today) and on J29, which has put `foreign_keys:` on built models. **The plan says to decide only after J29 has run on real projects; this is the proposal to decide with, not a decision.**

## 1. The question

Today a column is described twice: once in `models/**/<name>.yml` (physical name, SQL type, nullability, `title`, `description`, `tags`) and once, in another file, by the attribute or measure that binds to it (logical name, `label`, `synonyms`, aggregation). A person adding a column edits two files in two places and the compiler (DDB-611 to 617) says when they disagree. Should there be one definition?

## 2. Three ways to unify

| | What it is | Gains | Costs |
| --- | --- | --- | --- |
| **U1. Leave two formats, tighten the seam** | Keep both files. Build already generates the catalog's physical layer from the models. Add only what is cheap: copy a column's `title`/`description` into the matching attribute as the default `label`/documentation | No clean break; nothing to migrate | The two-place edit stays; bindings still repeat `table:` and `column:` |
| **U2. Column-level semantics in the model, cross-model semantics in the semantic file** | A model column may carry an optional `semantic:` block (`attribute` or `measure`, logical `name`, `synonyms`, `label`, aggregation and semantics for a measure, `is_time`). The semantic file keeps what spans models: shared dimensions, hierarchies, calendars, segments, role-playing relationships, saved queries, per-source `native` expressions | One place for a column's physical and logical facts; a rename moves both; a model with no `semantic:` still works exactly as now | A second way to write an attribute (in the model or the file) needs a rule for which wins; the model schema grows; a semantic reader must now read models |
| **U3. One definition** | A single file kind holds the table, its columns and the whole vocabulary; the semantic files go away | One concept | Shared dimensions (one `customers` over several sources), measures across models, hierarchies and calendars have no single table to live in. Mapped and native models, which the tool does not build, would still need a semantic home. Biggest clean break |

## 3. The questions the plan asked

* **Where a column says it is an attribute or a measure.** U2: `semantic:` on the column. U1/U3 do not change where it is said.
* **A physical name and a logical name or synonyms together.** The physical name stays the column's `name` (it is the same on every connection); the logical name and `synonyms` sit in `semantic:` beside it. The catalog already keeps these apart ("physical names stay in models and bindings, logical names everywhere else"); U2 only moves where they are written.
* **Semantic files that span several models.** They stay. Dimensions shared across sources, measures over more than one model, hierarchies, calendars and shifts, segments and saved queries belong to no single model. U3 would have to invent a home for them, which is the argument against it.
* **Roles and relationships.** J29's `foreign_keys:` is the join. A role (`order_date`, `ship_date` reaching one dimension) is semantic and stays in the semantic file as `x_role` does now; a relationship's name is derived (the rule J29 chose) or given there.
* **Sources and connections.** A semantic source *is* a connection of the project and takes its `kind` from it (D13). Nothing to unify; it already is one thing.
* **Authority when both say it.** One rule for U2: the model's `semantic:` is the default and a semantic-file entry for the same column replaces it, with a diagnostic when they contradict (not a silent override).

## 4. What a clean break would cost

* **U1: nothing.** Additive.
* **U2: additive too.** `semantic:` is optional, so every existing model and semantic file stays valid. Cost is the schema, loaders, JSON Schema, goldens and the semantic compiler reading models; the retail template could then move one dimension over as the worked example. A clean break only if the old binding form is later removed (every `bindings:` entry in the templates and in users' files), which this proposal does not ask for.
* **U3: a clean break.** Every template's `semantic/*.yml`, every user's semantic files, DDB-610 to 617, `ProjectPhysicalLayer`, the catalog loader's input, the Proxy's semantic data source, and the docs. A changed file format must fail with a message naming the file (repository rule), so a migration command would also be needed.

## 5. Recommendation

Take **U1 now** (it is a few lines) and **U2 only when real projects show the two-place edit costing something**, which is the same trigger the earlier note gave for relationships. Do not take U3: the semantic layer's value is that it spans models, and a single per-table definition has nowhere to put what spans them. Mapped and native models, which the tool does not build, are the other reason to keep a separate home for the vocabulary.

## 6. Open for the owner

1. Is U2 wanted at all, or is U1 enough until projects exist? (Recommended: U1 now; revisit U2 after J29 has run on a real project.)
2. If U2: which wins when the model and the semantic file both name a column, the file (recommended, with a diagnostic on contradiction) or the model?
