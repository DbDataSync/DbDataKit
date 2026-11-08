# Recorded widget results

The results the fixture source (`fixtureClient`, plan 32 DBR22) gives for the sample documents of the retail template (`DbDataBuild/templates/retail/dashboards` and `reports`): one file per widget,
`<kind>/<document name>/<widget id>.json`, in the `rendered-widgets` form (`DbDataKit/web/visuals/src/views/rendered.ts`; a matrix or a chart is the shape of `rendered-view.schema.json`).

They are written by hand, deterministic, and **not rows of the retail data**: the names of the fields are the model's (`retail_sales`), the numbers are made up. A text widget has no file (it is drawn from the
document); a control that lists values has one. DBR04 regenerates these files from the real renderer over the retail template, and the console's M4 story shows real rows instead.
