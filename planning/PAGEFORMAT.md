# PAGEFORMAT: one document format, `page/1`

## Goal

`page/1` with a `kind` (`dashboard`, `report`, `screen`, `content`) replaces `dashboard/1`, `report/1` and `view-spec/1`. Entity widgets, the GFM text widget
with a reused WYSIWYG editor, a server-side Store query, the portal home and custom pages are pages. The design is in
[docs/pages.md](../docs/pages.md).

## What exists today

- `dashboard/1` and `report/1` on one shared core (`docs/views.md`), `view-spec/1` for an app's grid, form, detail and dashboard screens
  (`DashboardBoard`), the portal home's `views/home.yaml` blocks (`HomeBlock`: `dashboard`, `image`, `table`, `roles`).
- Custom views: widgets and pages as app code (`kind: page` in `app.yaml`), drawn in a sandboxed frame; built on our side; the apps host's half and the
  browser stories are not (old 32 DBR31, DBR32, DBR33, DBR36).
- The `embed` widget (a framed report from an approved origin, else a link).
- Actions: `change` and `task` link targets with a confirmation popup; batches of changes staged and submitted once; no inline cell edits.
- The nested axis is drawn on screen; its print has not been checked in a browser.
- The export component (pinned Chromium, fonts) is a release asset; no real release run has proved its zips and image.
- The Store read API returns at most the first 2000 rows to a grid; no server-side filter or sort.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| PAGEFORMAT-1 | `page/1` replaces `dashboard/1` and `report/1`: one loader, `kind`, a scripted rename of the registry kinds, routes, catalog types, audit verbs, lineage, subscriptions, exports and app `views` lists; `embed` renamed `external`; old files refused naming the file | none | L | the loader and rename tests; an old `dashboard/1` file is refused naming it; `check_formats.py` passes with the new entry |
| PAGEFORMAT-2 | Entity widgets `grid`, `form`, `detail` with `view-spec/1`'s keys (composite keys included); `view-spec/1` and `DashboardBoard` removed; an app draws a page in its runtime | PAGEFORMAT-1 | L | an app's screen as a page edits a Store entity with a composite key; a `view-spec/1` file is refused naming it |
| PAGEFORMAT-3 | Kind rules, templates and New (Dashboard, Report, Data screen, Content page); validation per kind; `print: show \| table \| hide` per widget | PAGEFORMAT-1 | M | each choice makes a valid document; `external` is refused in a report and in a print layout |
| PAGEFORMAT-4 | One Markdown renderer for `text` (the GFM dialect and the safety rules); the WYSIWYG editor spike (Milkdown, TipTap, MDXEditor) and its integration | PAGEFORMAT-1 | M | a table and a task list render; every safety rule has a refusing test; the editor round-trips a document unchanged |
| PAGEFORMAT-5 | The Store read API takes filter, sort, limit and offset; the grid asks the Store | none | M | a grid over 50,000 rows filters and pages through the whole entity |
| PAGEFORMAT-6 | The portal home is an ordinary page mounted at the portal's root: the `tiles` widget; `views/home.yaml` and `HomeBlock` removed | PAGEFORMAT-2 | M | a portal's home renders tiles of the apps the person may open; an old `home.yaml` is refused naming it |
| PAGEFORMAT-7 | Custom pages fold into a page with one custom widget in an unpaged flow layout; `kind: page` leaves `app.yaml`; the apps host serves custom widgets with the viewer's rows, their print page, PNG export, dialog and panel | PAGEFORMAT-2 | L | a custom page exports to PDF and PNG as before; Playwright: a custom widget's selection re-runs a standard widget; a link opens a dialog prefilled and saving refreshes |
| PAGEFORMAT-8 | The catalog's kind facet in search, browse, lineage, subscriptions and exports; external reports listed as `report`, marked by catalog | PAGEFORMAT-1 | S | a search for Reports lists our reports and an external one, and no content page |
| PAGEFORMAT-9 | The spec of data entry in dashboards and reports (inline edits, forms in a tile, validation, versions and conflicts, a table's selection as a batch); no build | PAGEFORMAT-2 | S | a document in `docs/` the owner approves (checked by hand: it is a spec) |
| PAGEFORMAT-10 | The nested axis in print: a year-then-month axis in a PDF page | PAGEFORMAT-1 | S | a PDF golden by structure shows both levels; the print preview checked once in a browser (recorded in `docs/views-testing.md`) |
| PAGEFORMAT-11 | The console's viewing pages: kiosk, print preview and export for pages | PAGEFORMAT-1 | M | Playwright: open a report, print preview, export to PDF |
| PAGEFORMAT-12 | The export component proved on a real release: the zips and the `-export` image of a snapshot release, and `dbdatagateway export install` against it | none | S | a dispatched run installs the component from a snapshot release and exports a page to PDF |
| PAGEFORMAT-13 | The `external` widget with a Windows-sign-in report server (SSRS, Power BI Report Server): framed from an approved origin, a link otherwise | PAGEFORMAT-1 | S | checked by hand on a Windows host with a report server (none in CI): the steps and result in `docs/views-testing.md` |

## Needs from other subjects

None.

## Proposed decisions

None open: the design is in `docs/pages.md`.

## Shared areas touched

- `DbDataKit.Views*`, `Shared/web/visuals`, `Shared/web/shell`, `DbDataKit.Apps.*`, `DbDataKit.Store` (read API), the catalog and lineage emitters.
- `docs/formats.json` (`page/1` added; `dashboard`, `report`, `view-spec` and the home blocks leave), `docs/ui-editors.md`, `docs/views.md`, `docs/apps.md`.
