# Plan 34: pages, one document format for dashboards, reports, home pages, data screens and content

Status: **proposed** (owner's direction 2026-11-12, one conversation; the model below is the coordinator's reading of it, the open questions are marked). **Nothing here is started, by the owner's order:** the baseline work has to be on `main` first (see "Before this starts") and the owner has to review this plan. Slice ids `PG01`... (`PG` is free in every plan).

## What the owner decided (2026-11-12)

1. **`view-spec/1` is retired.** Its grid, form and detail screens become widgets of the unified format (below). `view-spec/1` is unreleased and open: a removal needs no version bump, and a file in the old format fails with a message naming the file.
2. **One format, `page/1`, with a `kind`**, replaces `dashboard/1` and `report/1` (which share one core today: `docs/views.md`, "A document: the shared core and the two kinds"). The authoring UI offers "New: Dashboard / Report / Home page / ..." and each choice makes the same document with the right `kind`, layout type and starter widgets. The name is `page` (not `screen`).
3. **Kinds:** `dashboard`, `report`, `home` (a portal home), `screen` (data entry and maintenance of Store entities), `content` (help and landing pages). The kind sets the allowed layouts, the allowed widgets, the template and the validation rules.
4. **The kind is searchable.** The catalog item type is the kind, so a search or a browse for Reports never returns a home page; lineage consumers, subscriptions and exports use it too.
5. **No new layout element.** A page composes widgets from every family (data visuals, text and images, controls, entity widgets, custom widgets) in one layout (`reflow`, `canvas`, `flow`), with the selection and placement rules of `docs/views.md` ("Resolving a layout", "Choosing a layout"). An unpaged `flow` (`page: {size: screen}`) is the reading layout for content.
6. **Markdown is the full GFM dialect the shell's renderer already has** (tables, task lists, strikethrough, nested lists, blockquotes, code blocks), not the small subset of today's `text` widget; one renderer, no new element. The safety rules stay: no raw HTML, links only to a path on the host, to a link-vocabulary target (DBR35) or to an approved `https` origin, images only by hash or from the package, never an arbitrary URL, `{{params.x}}` substituted as text.
7. **A WYSIWYG Markdown editor is reused, not written**, picked for UX, license and size, with its schema locked to the dialect above (PG05 spike: Milkdown, TipTap, MDXEditor).
8. **The Store read API gets a server-side query** (filter, sort, limit and offset), so a grid does not search only the first 2000 rows it loaded (PG06).
9. **A composite key is supported** in entity widgets (one parameter per key attribute; `docs/apps.md` said otherwise by mistake and is fixed).
10. **Data entry inside dashboards and reports is wanted, with limits for now and a spec later** (PG10). What exists today: `change` and `task` link targets with a confirmation popup and an `ask` form (DBR53), and batches of changes staged, reviewed once and submitted as one (DBR53b). Not built and out of scope until PG10: inline cell edits in the standard widgets.
11. **Custom pages fold into `page/1` (owner, 2026-11-13).** A custom page (app code, `kind: page` in `app.yaml`, served at `/apps/<instance>/_views/<name>`, exported to PDF and PNG) becomes a `page/1` document with one `custom` widget in an unpaged `flow` layout (`page: {size: screen}`), so the page scrolls as it does today (the frame grows to the height the code reports) and paginates for print and export as it does today; `kind: page` leaves `app.yaml` and only `kind: widget` custom views remain. To check in PG08: a flow block whose height follows the widget's reported size (`views:size`); if a block cannot, that is one small addition to the flow layout.

## Open (the owner has not decided)

- **The `embed` widget** shows an external report server's report in a frame (https only, framed only for approved origins, otherwise a link, no query, never cached). Today it is allowed in a `dashboard` and refused in a `report` (`DBD-VIEW-038`) because a frame cannot be paged or exported. The owner finds calling that a "dashboard" rule odd. Proposal: rename the widget (`external`), and make the rule depend on the medium (a layout used for print or export may not place it) instead of on the kind; allowed in `dashboard`, `home`, `screen` and `content`, refused in `report`. To decide with the rename.
- **Per-kind widget table** (proposal, to confirm): `dashboard` all data, content, control, custom, embed, entity grid and detail; `report` data, content, custom, entity grid and detail, no embed, no `refresh`, no entity form; `home` content, control, the reference widget; `screen` all entity widgets, control, text, data widgets; `content` text, image, control, any widget.
- **A reference widget `page`** (render another page's default layout at the tile's size; replaces the portal home's dashboard block of DBR13; one level deep, cycle check).
- **The names:** a rename of `dashboard` and `report` as definition kinds touches the registry (plan 30 PB01), routes, catalog, audit verbs, lineage, subscriptions, exports and the app `views` lists; done as one scripted slice.

## Before this starts (the owner's condition)

All baseline work is on `main`, built and green, and the specs here are incorporated: DBR13 (the drawing of `dashboard/1` and `report/1` in the app runtime, the portal home block, the shell dialog and panel) merged and published; the access rules (plan 22a GP01, GP02, GP11 to GP15) built by session B; session B's chromium pin task for exports; the Runner connection store slices of plan 28c are independent and do not wait. Then the owner reviews this plan and names who starts: session B owns plan 32 and the views module and is the natural owner of PG02 to PG09.

## Slices

| Slice | What | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| PG01 | The owner reviews and decides the open questions above; this plan is rewritten as decided and `docs/views.md`, `docs/apps.md` and the formats registry rows follow in the first build slice | baseline | S | the plan has no open question |
| PG02 | `page/1` replaces `dashboard/1` and `report/1`: one loader, `kind`, per-kind rules, a scripted rename of the registry kinds, routes, catalog types, audit verbs, lineage, subscriptions, exports and app `views` lists; old files refused naming the file | PG01 | L | the conformance goldens pass for every kind; the catalog finds a Report and not a home page; nothing references the old names (a source scan) |
| PG03 | Entity widgets `grid`, `form`, `detail` with `view-spec/1`'s keys (columns, filters, sort, `pageSize`, fields, `key`, sections, composite keys); `view-spec/1` and `DashboardBoard` removed (DBR13 part 4 folds in) | PG02 | L | the apps' screens run on pages; `app validate` checks them; the territory sample and template are pages |
| PG04 | Kind rules, templates and the authoring choices (New: Dashboard, Report, Home page, Data screen, Content page); validation per kind | PG02 | M | each choice makes a valid document of its kind; a widget a kind refuses is refused in words |
| PG05 | One Markdown renderer for the `text` widget (the GFM dialect, the safety rules above); the WYSIWYG editor spike and integration | PG02 | M | a table and a task list render; every rule above has a refusing test; the editor writes only the dialect |
| PG06 | The Store read API takes filter, sort, limit and offset; the grid asks the Store instead of loading 2000 rows | none | M | a grid over 50,000 rows filters and pages through the whole entity; the note about more rows goes away |
| PG07 | The `page` reference widget replaces the portal home's dashboard block | PG02 | S | a home page shows another page at the block's size |
| PG08 | Custom pages fold into a page with one custom widget in an unpaged flow layout (decision 11) | PG02 | M | a custom page exports to PDF and PNG as before; `kind: page` is refused in `app.yaml` with a message |
| PG09 | The catalog's kind facet, in search, browse, lineage, subscriptions and exports | PG02 | S | a search for Reports lists no home page |
| PG10 | The spec of data entry in dashboards and reports (inline edits, forms in a tile, validation, versions and conflicts); no build | PG03 | S | a document in `docs/` the owner approves |
