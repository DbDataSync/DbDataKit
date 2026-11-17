# Pages: one document format, `page/1`

The design `page/1` is being built to; the work is [planning/PAGEFORMAT.md](../planning/PAGEFORMAT.md). Until it is built, `docs/views.md` describes
`dashboard/1`, `report/1` and the shared core as they are, and `docs/apps.md` describes `view-spec/1` and the portal home.

## One format with a kind

`page/1` replaces `dashboard/1`, `report/1` and `view-spec/1`. A page has a `kind`:

- `dashboard` and `report`, as today's two kinds;
- `screen`, data entry and maintenance of Store entities (the long-term screen for Store rows);
- `content`, help and landing pages.

There is no `home` kind: a portal's home is any page mounted at the root of the portal. The authoring screens offer "New: Dashboard / Report / Data
screen / Content page"; each makes the same document with the right `kind`, default layout and starter widgets. The kind sets the default layout, the
template and the keys it owns (`refresh`, `kiosk`, `limits`); it does not restrict widgets. The rename of the definition kinds (registry, routes, catalog,
audit verbs, lineage, subscriptions, exports, app `views` lists) is one scripted change; an old file is refused with a message naming it.

Example: a sales team's landing page is `kind: content` with a `text` widget, a `tiles` widget of the apps each person may open and a small chart; a
search for Reports does not list it.

## The kind is searchable

The catalog's item type is the kind, so a search or browse for Reports never returns a landing page; lineage, subscriptions and exports use it too.
External reports (SSRS, Power BI Report Server) are listed with our reports, as type `report`, each marked by its catalog and provider; an external report
opens framed from an approved origin and otherwise as a link.

## Widgets and layouts

A page composes widgets of every family (data visuals, text and images, controls, entity widgets, custom widgets) in one layout (`reflow`, `canvas`,
`flow`), with the selection and placement rules of `docs/views.md`. There is no new layout element; an unpaged `flow` (`page: {size: screen}`) is the
reading layout for content.

- **Entity widgets** `grid`, `form` and `detail` carry `view-spec/1`'s keys (columns, filters, sort, `pageSize`, fields, `key`, sections). A composite key
  is supported, one parameter per key attribute. A grid asks the Store with filter, sort, limit and offset, so it searches the whole entity, not the first
  2000 rows it loaded.
- **Every kind may place every widget**, except `external`. Printing is what is restricted: an editable `grid` prints as a plain table without its actions,
  a `form` is hidden in print, and each widget may say how it prints (`print: show | table | hide`).
- **`external`** (formerly `embed`) frames an https address from an approved origin, otherwise shows a link; it has no query and is never cached. It is
  allowed on screen in `dashboard`, `screen` and `content` pages and refused in `report` pages and in any print or export layout, because a frame cannot be
  paged or exported.
- **`tiles`** lists the apps the person may open, with options for how it is drawn; a custom widget may read the same data. A page shows the same widgets
  to everyone who may open it (no visibility by role), and opens another mounted app's page through a `links` or `tiles` entry rather than framing it.

## Text

The `text` widget renders the full GFM dialect the shell's renderer has (tables, task lists, strikethrough, nested lists, blockquotes, code blocks). The
safety rules stay: no raw HTML; links only to a path on the host, to a link-vocabulary target or to an approved `https` origin; images only by hash or from
the package; `{{params.x}}` substituted as text. The WYSIWYG editor is a reused one (Milkdown, TipTap or MDXEditor, chosen by a spike for UX, license and
size), locked to that dialect.

## Custom pages

A custom page (app code, served at `/apps/<instance>/_views/<name>`, exported to PDF and PNG) is a `page/1` document with one `custom` widget in an
unpaged `flow` layout: it scrolls on screen as the frame grows to the height the code reports, and paginates for print and export. `kind: page` leaves
`app.yaml`; only `kind: widget` custom views remain there.

## Data entry

Data entry inside dashboards and reports is wanted, within limits until its specification is written: `change` and `task` link targets with a
confirmation popup and an `ask` form, and batches of changes staged, reviewed once and submitted together. Inline cell edits in the standard widgets wait
for that specification.
