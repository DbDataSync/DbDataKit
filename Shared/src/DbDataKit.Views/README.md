# DbDataKit.Views

Dashboards and reports (plan 32): one set of **widgets**, several **layouts** over them. This library holds the vocabulary, the strict loaders, the pure selection of a layout and the resolution of a layout into rendered widgets (it opens no connection: the requests go through the serving core). It draws nothing: the renderers (`@dbdatakit/visuals`), the layout engines and the module that serves documents are other slices. The file format is in [`docs/views.md`](../../../docs/views.md).

## Widgets

```csharp
var result = ViewsLoader.LoadWidgets(yamlOrJson, "views/sales.yaml", parameters: ["region", "quarter"]);
if (!result.Ok) foreach (var d in result.Diagnostics) Console.WriteLine(d);   // views/sales.yaml:12:5: error DBD-VIEW-003: ...
foreach (var w in result.Widgets!) { /* w.Id, w.Type, w.Fields (channel, field, path, line), w.Query (form, explicit, model, raw) */ }
```

A widget is a type, its encodings and a query. The loader checks the closed Vega-Lite subset of a chart (no computation in the chart spec), the safe text and the image held by hash, the query form and its
mode, the parameters a marker, control, cross-filter or drill names, and the **binding**: an explicit query must output every field the widget encodes (a derived query is made from the encodings).
`ViewWidget.Fields` lists the encoded fields in the order written, each with its path and position, for the code that derives the request (DBR03).

A document's **links** (`links`, a widget's `on_click`; plan 32 DBR35) are read with the document: `ViewDocument.Links` gives each by name (`ViewLink`: the target kind and name, the values under `with`, `new` or `edit`, `as`, `then`, `lock`, `print`, `image`).
Pass `targets` (`dashboard:name`, `report:name`, `view:name`, `page:name`, `form:name`, each with its parameters or fields and their types) to `ViewsLoader.Load` and a link to a target the app lacks, a name the target lacks, a value of the wrong type
(`ViewsLoader.LinkTypeFits`) and a required parameter not given are refused with the code and the place; `docs/views.md` has the vocabulary.

## Deriving the request, and the catalog check

```csharp
var roles = new CatalogRoles("sales", measures: ["orders.net_revenue"], attributes: ["customers.region"]);   // or CatalogRoles(model, name => role)
var derived = DeriveRequest.Derive(widget, roles, "views/sales.yaml");
if (derived.Request is { } request) { /* {spec_version, kind: "query", query: {measures, attributes, filters, order, limit, totals...}} */ }
// derived.Bindings: the {param} markers left to bind; derived.DocumentPath("$.query.measures[0]") -> "widgets.by_region.encodings.y"
var diagnostics = CatalogCheck.Check(document.Widgets, "views/sales.yaml", new Dictionary<string, CatalogBinding> { ["sales"] = new(roles, validatorOverTheEngine) });
```

`DeriveRequest` is pure (the TypeScript twin in `@dbdatakit/visuals` gives the same request from the same goldens, `Shared/conformance/views/derive`). `CatalogCheck` sends every widget's request through the engine's
validator (`IRequestValidator`, an adapter over the semantic library's `Validator`) and reports the engine's codes at the document's file, line and path. See `docs/views.md`.

## Resolving a layout as the viewer

```csharp
var resolver = new WidgetResolver(servingQueries, model => roles.For(model), new ResolverOptions { MaxRows = 100_000, WidgetTimeout = TimeSpan.FromSeconds(30) }, servingEntities);
RenderedWidgets rendered = await resolver.ResolveAsync(context, document, "desk", parameters: values, only: ["net"]);   // context: the viewer's ServingContext
JsonObject json = rendered.ToJson();        // {"kind":"rendered_widgets","layout","as_of","widgets":{id: metric | table | matrix | chart | text | image | control | error}}
```

For the widgets a layout places (and the ones `only` names) it binds the viewer's parameter values, makes each widget's request (`DeriveRequest`, or the form that was written), runs it through the serving core's
`IServingQueries` **under the viewer's `ServingContext`** and shapes the answer: server cell text, alignment and total rows for a table, a metric with its comparison's change, a matrix or chart through the engine's
`ViewRenderer`, a text with its placeholders substituted, an image as a `data:` URI, the values a control lists. A widget that is denied, over a limit, refused by the engine or not drawable is an **error entry** with the
core's code and words; the others render. Nothing here applies an access rule. See [`docs/views.md`](../../../docs/views.md#resolving-a-layout) for the rules (unset parameters, limits, what each query form does).

## Documents

```csharp
var dashboard = DashboardLoader.Load(text, "views/sales.yaml");        // refuses a report, saying it is one
var report    = ReportLoader.Load(text, "reports/regional.yaml");
var either    = ViewsLoader.Load(text, file);                          // ViewDocument.Kind says which
if (either.Document is { } doc) { /* doc.Widgets, doc.Layouts, doc.DefaultLayout, doc.Parameters ... */ }
```

One core (widgets, parameters, layouts, selectors) and the rules of each kind on top of it: a dashboard's default layout is `reflow` or `canvas`, a report's is `flow`; `refresh` and `kiosk` are a
dashboard's, `strict`, `limits` and `cover` a report's. Every problem is a `ConfigDiagnostic` with the file, line, column, a code (`ViewsCodes`) and the key path; warnings do not stop a load. `ViewsFormat.Current`
is the one number of both kinds; the JSON Schemas (`ViewsLoader.Schema("core")`, `"dashboard"`, `"report"`) are the files of `Shared/web/visuals/schemas`, embedded.

## Choosing a layout

```csharp
var selection = LayoutSelector.Select(doc, new LayoutContext(Width: 390, Height: 844, Medium: "screen"));
// selection.Layout, selection.Choice (Named, Matched, Default) or selection.Refusal for an unknown name
```

A pure function: a named layout wins; otherwise the layouts whose `when` matches, the most specific first, ties by document order; otherwise the default. The goldens are shared with the TypeScript twin.

## Tests

`DbDataKit.Views.Tests` runs the shared documents in `Shared/conformance/views` (the `@dbdatakit/visuals` suite runs the same files) and keeps the loader and the JSON Schemas equal.
