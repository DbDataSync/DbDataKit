# DbDataKit.Views.Hosting

The serving module `views` (plan 32 DBR11): published dashboards and reports over REST, rendered as the caller.
`DbDataKit.Views` holds the documents, the loaders, the registry kinds and the resolution; this package hosts them.

```csharp
var kinds = ViewsPublications.Register(new DefinitionKinds(), store, payloads);          // the registry of the publisher: dashboard, report
var publisher = new DefinitionPublisher(state, kinds, payloads, "serving");
var published = new ViewsPublications(store, publisher, payloads);
var module = new ViewsModule(new ViewsOptions(), published, callers, new StateViewsAudit(new AuditLog(state, "views")));
await module.StartAsync(services);                                                      // follows the group's definitions, listens
```

- **Routes** (`ViewsModule`): `GET /api/v1/dashboards` and `/reports` (the list), `GET .../{name}` (text, document, layouts, version, `as_of`), `POST .../{name}/render` with `{parameters, layout, only, viewport, medium}` (the `rendered-widgets` document), `POST /api/v1/views/preview` (an unsaved document or widget as the caller, stored nowhere, refused as a publish would refuse: DBR28), `GET .../{name}/export` (501 `export_unavailable` until DBR15 to DBR17), `/health`.
- **A person's own documents** (DBR12). `ViewsOptions.UserViews` (a `UserViewStore` over the state module `UserViewsModule`) turns on `GET /api/v1/my/views` and `/api/v1/my/dashboards|reports/{name}` (open, `PUT` save with `expected_version`, `DELETE`, `render`, `history`, `restore`, `shares`, `promote`): owned by the caller's id, shared to a person, a group or everyone, resolved as whoever opens it (their rows), audited as `view.my.*`. Without a store they answer 404 in words. `ViewsPrincipal.Groups` is where a host whose callers have no roles (the Gateway) gives the person's groups. See `docs/views.md`.
- **As the caller.** Each widget runs through the serving core's `IServingQueries` with the caller's `ServingContext`; `IViewsCallers` turns a request's token into the caller (the host supplies it: the identity home's tokens). An app's token narrows (`ViewsAppScope`): a document that queries a model outside the app's `semantic` list is refused 403.
- **Audited.** `view.list`, `view.open`, `view.render`, `view.export` and `view.preview` in the install's audit (who, through what, document and version, outcome; never a value). An entry that cannot be written stops the answer.
- **Draining and health.** `DrainAsync` refuses new requests (503) and waits for the ones in flight; `Health()` is `Ok`, `Degraded` (draining, or a version of the group this instance cannot load) or `Down`; `/health` adds what is served, the cache, and `export: {available: false}`.
- **Result cache.** `WidgetResultCache`: the rows of a request for `CacheTtl`, under `WidgetCacheKey` (channel, caller with roles and attributes, the request) and the definition's dependencies.
- **Hosting.** A host that runs one definition follower adds `ViewsPublications.Subscribers` to it and sets `ViewsOptions.Follow` false; one that mounts routes sets `Listen` to null and calls `Map`. The apps host calls `ViewsApi` in process with a narrowed `ViewsPrincipal`.
- **Export** (DBR15). `ViewsOptions.Exporter` (a `DbDataKit.Views.Export.IReportExporter`) and `PageAssets` turn on `GET .../{name}/export?format=pdf&layout=&p.name=` (a PDF of the layout the print medium selects, resolved as the caller and drawn by the exporter) and `GET .../{name}/print` (the print page as HTML); without them the route answers 501 `export_unavailable` naming the setting and `/health` says so. The same route gives CSV and xlsx of a widget's rows (DBR17, `format=csv|xlsx&widget=`; no browser, so they work without an exporter; `ViewsOptions.MaxExportRows`) and a chart's SVG (`format=svg&widget=`, the browser's drawing). See `docs/views.md`.
