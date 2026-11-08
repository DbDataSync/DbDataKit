# DbDataKit.Views

Dashboards and reports (plan 32): one set of **widgets**, several **layouts** over them. This library holds the vocabulary, the strict loaders and the pure selection of a layout. It renders nothing and runs no
query: the renderers (`@dbdatakit/visuals`), the layout engines and the module that serves documents are later slices. The file format is in [`docs/views.md`](../../../docs/views.md).

## Widgets

```csharp
var result = ViewsLoader.LoadWidgets(yamlOrJson, "views/sales.yaml", parameters: ["region", "quarter"]);
if (!result.Ok) foreach (var d in result.Diagnostics) Console.WriteLine(d);   // views/sales.yaml:12:5: error DBD-VIEW-003: ...
foreach (var w in result.Widgets!) { /* w.Id, w.Type, w.Fields (channel, field, path, line), w.Query (form, explicit, model, raw) */ }
```

A widget is a type, its encodings and a query. The loader checks the closed Vega-Lite subset of a chart (no computation in the chart spec), the safe text and the image held by hash, the query form and its
mode, the parameters a marker, control, cross-filter or drill names, and the **binding**: an explicit query must output every field the widget encodes (a derived query is made from the encodings).
`ViewWidget.Fields` lists the encoded fields in the order written, each with its path and position, for the code that derives the request (DBR03).

## Deriving the request, and the catalog check

```csharp
var roles = new CatalogRoles("sales", measures: ["orders.net_revenue"], attributes: ["customers.region"]);   // or CatalogRoles(model, name => role)
var derived = DeriveRequest.Derive(widget, roles, "views/sales.yaml");
if (derived.Request is { } request) { /* {spec_version, kind: "query", query: {measures, attributes, filters, order, limit, totals...}} */ }
// derived.Bindings: the {param} markers left to bind; derived.DocumentPath("$.query.measures[0]") -> "widgets.by_region.encodings.y"
var diagnostics = CatalogCheck.Check(document.Widgets, "views/sales.yaml", new Dictionary<string, CatalogBinding> { ["sales"] = new(roles, validatorOverTheEngine) });
```

`DeriveRequest` is pure (the TypeScript twin in `@dbdatakit/visuals` gives the same request from the same goldens, `DbDataKit/conformance/views/derive`). `CatalogCheck` sends every widget's request through the engine's
validator (`IRequestValidator`, an adapter over the semantic library's `Validator`) and reports the engine's codes at the document's file, line and path. See `docs/views.md`.

## Documents

```csharp
var dashboard = DashboardLoader.Load(text, "views/sales.yaml");        // refuses a report, saying it is one
var report    = ReportLoader.Load(text, "reports/regional.yaml");
var either    = ViewsLoader.Load(text, file);                          // ViewDocument.Kind says which
if (either.Document is { } doc) { /* doc.Widgets, doc.Layouts, doc.DefaultLayout, doc.Parameters ... */ }
```

One core (widgets, parameters, layouts, selectors) and the rules of each kind on top of it: a dashboard's default layout is `reflow` or `canvas`, a report's is `flow`; `refresh` and `kiosk` are a
dashboard's, `strict`, `limits` and `cover` a report's. Every problem is a `ConfigDiagnostic` with the file, line, column, a code (`ViewsCodes`) and the key path; warnings do not stop a load. `ViewsFormat.Current`
is the one number of both kinds; the JSON Schemas (`ViewsLoader.Schema("core")`, `"dashboard"`, `"report"`) are the files of `DbDataKit/web/visuals/schemas`, embedded.

## Choosing a layout

```csharp
var selection = LayoutSelector.Select(doc, new LayoutContext(Width: 390, Height: 844, Medium: "screen"));
// selection.Layout, selection.Choice (Named, Matched, Default) or selection.Refusal for an unknown name
```

A pure function: a named layout wins; otherwise the layouts whose `when` matches, the most specific first, ties by document order; otherwise the default. The goldens are shared with the TypeScript twin.

## Tests

`DbDataKit.Views.Tests` runs the shared documents in `DbDataKit/conformance/views` (the `@dbdatakit/visuals` suite runs the same files) and keeps the loader and the JSON Schemas equal.
