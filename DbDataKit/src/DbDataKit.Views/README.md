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

## Tests

`DbDataKit.Views.Tests` runs the shared documents in `DbDataKit/conformance/views` (the `@dbdatakit/visuals` suite runs the same files) and keeps the loader and the JSON Schemas equal.
