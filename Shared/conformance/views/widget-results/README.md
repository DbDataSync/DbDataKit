# Recorded widget results

The results the fixture source (`fixtureClient`, plan 32 DBR22) gives for the sample documents of the retail template (`DbDataBuild/templates/retail/dashboards` and `reports`): one file per widget,
`<kind>/<document name>/<widget id>.json`, in the `rendered-widgets` form (`Shared/web/visuals/schemas/rendered-widgets.schema.json`, typed in `src/views/rendered.ts`; a matrix or a chart is the shape of
`rendered-view.schema.json` with the widget's id).

Since DBR04 they are **written by the resolver**, not by hand: `ResolveTests` (`DbDataKit.Views.Tests`) resolves each sample document with `WidgetResolver` over a fake serving core holding a small retail sample
(`RetailServing`: the names of the model `retail_sales`, 960 generated sales lines, the answers in the semantic module's form) and compares what it gives with these files. The numbers are therefore
**computed from the sample's lines, not rows of the template's real data** (the retail seeds are built by `DbDataBuild`, which this suite does not run). The files are checked against the schema by the .NET
suite and by `rendered.test.ts` in `@dbdatakit/visuals`, so the two languages agree on one shape.

A text widget has no file (the resolver also gives its substituted text, but the fixture client draws it from the document); a control that lists values has one. To write the files again after a change to the
resolver or the sample, from `Shared/`:

```
DBDATAKIT_TEST_REGENERATE_WIDGET_RESULTS=1 dotnet test --project tests/DbDataKit.Views.Tests --filter-class DbDataKit.Views.Tests.ResolveTests
```

then run the `@dbdatakit/visuals` tests (`npx vitest run -u` rewrites the snapshots that draw them) and commit the files and the snapshots together. The console's M4 story shows real rows instead.
