# Testing dashboards and reports end to end

A script for a tester (plan 32 DBR11b): publish the retail model and its dashboard to a Gateway, open them from the console as two people whose access differs, resize, switch layout and edit. It needs a Gateway, a console paired to it, the retail project's data on an engine the Gateway can read, and a Build server paired to the Gateway for the model.

What is real today and what is not: the **model** is published by Build (it has the physical layer); the **dashboard** is published by the Gateway's own verb (`dbdatagateway views publish`) or from the console's editor; the dashboard is read through the Gateway's `views` module. **Different access** today is the data source's home groups (a person outside them gets a `denied` entry in every widget that needs the data): row-level rules are plan 22's and are not built, so two people never see two row sets yet. The report is listed but not drawn (DBR14).

## 1. A Gateway with the module on

1. In the Gateway's `server.yaml` list the module (the identity home too, if this Gateway is the home):

   ```yaml
   modules: [flightsql, identity, views]
   views: { address: 0.0.0.0, port: 8820 }
   ```

   Start it. The readiness component `views` is in `/health/ready`. Through the console the module's status says it is on:
   `GET /relay/<gateway>/api/v1/views/status` gives `{"enabled": true, "state": "ok", ...}`.
2. Pair the console with the Gateway as usual. A person signs in to the console **through the identity home** (a person who is only the console's has no home groups, so every widget of theirs is refused).

## 2. Publish the model, then the dashboard

1. **The model.** Build the retail project and publish its semantic model from the Build server (the Semantic screen's **Publish** to the paired Gateway, or `PUT /api/v1/semantic-models/retail_sales`). Its data source is created read-only on the Gateway. Give the data source a **home group** (`groups: [analysts]` under `extensions.gateway` in its connection file, or the data source screen): only that group may read it.
2. **The dashboard**, from this repository:

   ```
   dbdatagateway views publish --file DbDataBuild/templates/retail/dashboards/retail-sales-today.yaml --repo <data directory>
   dbdatagateway views list --repo <data directory>
   ```

   The first prints `Published dashboard retail-sales-today version 1; it depends on semantic-model:retail_sales@<n>`. A model that is not published says `DBD-VIEW-060` and names it; a field the model lacks says `unknown_member` with the line and column; run it again after fixing and nothing was stored in between.
   The same document can be sent to a running Gateway with `PUT /relay/<gateway>/api/v1/dashboards/retail-sales-today` (`{"text": "<the file>"}`, the author role), which is what the console's editor does on **Save** for a published source.
3. The module serves it within the Gateway's poll (`DefinitionPollSeconds`, 5 seconds by default); every instance of a group does.

## 3. Open it as two people

1. Make two home users: **Ada** in the group the data source names, **Bob** in none (`identity` verbs, or the Identities screen). Sign in to the console as Ada.
2. **Views > Dashboards.** The **Source** says `<Gateway> (published)` and "every widget is read as you"; the row `retail-sales-today` is listed (there is no *Open file* here: a file is opened in the fixtures only). Open it: the board is drawn from the Gateway's rows (the numbers differ from the recorded fixtures).
   Switch the source to *Fixtures* and back (it is kept in the address as `?source=`); an address with an unknown `?source=` says so and shows what is available.
3. Sign out, sign in as Bob and open the same address: the page is drawn, and each widget that needs the data source shows an error tile with the Gateway's words ("Not authorized for data source ... it is for the home groups analysts"); the text widget still draws. Ada's page is not changed by Bob's visit (the cache is keyed by who asks).
4. Direct, with a token: `curl -k -H "Authorization: Bearer <personal access token>" https://<gateway>:8820/api/v1/dashboards/retail-sales-today` and `POST .../render` with `{"parameters": {}, "layout": "desk"}`. No token is a 401; a token of Bob's is the same 200 with error entries. Every call is in the audit (`view.open`, `view.render`: who, name@version, the parameter names).

## 4. Resize, switch layout, parameters

1. Resize the window (1920 x 1080, 1366 x 768, a phone width): the layout the window selects fills the page; nothing scrolls. `?layout=wall` (a fixed canvas, scaled) is honoured; **Layout** in the toolbar switches and keeps it in the address. Switching to a layout whose widgets were already resolved asks the Gateway for nothing more.
2. Choose a region in the **Region** control: the address gains `?p.region=West`, and only the widgets that use the parameter are asked of the Gateway again (the toolbar's "Last request" line names them). Reload: the same address is the same board.

## 5. Edit and publish a new version

1. **Edit** on the board. Change the title or move a tile, **Save**. On a published source, saving publishes a new version through the Gateway (the author role; a viewer's console gets a refusal in words). `dbdatagateway views list` shows version 2; the board shows the change within the poll.
2. Open the editor in two browser tabs, save in one, save in the other: the second is a **conflict** (`expected_version` is stale) and offers a reload.
3. Break it: name an unknown measure (`sales.no_such_measure`) in a widget and Save: the editor shows the registry's refusal with the position, and version 2 is still what is served.
4. A report: **Reports**, **New report**. Add a text, two metrics and a table; place them (a row of two metrics with spans 6 and 6; the table in a second section that breaks before and repeats its head); set the page (Letter, landscape), a header with `{title}` and a footer with `Page {page} of {pages}`. Type `{nope}` in a header field: the loader refuses it at that field. The **Preview** below the form shows the draft with guides where pages start, before anything is saved; choosing a smaller page gives more guides. On the module's own listener `POST /api/v1/views/preview` with `{"kind": "report", "text": "<the draft>"}` and a token gives the rendered widgets of an unsaved draft as that person (a draft the registry would refuse is a 400 with the reason, and the install's audit has a `view.preview` row); the console's relay does not carry that route yet, so the editor on a Gateway source says the preview was refused (404) until the Gateway's management API does.

## 6. Get a PDF (plan 32 DBR15)

Export is off until the host is told which browser to use.
1. Get the pieces once, from the repository root: `scripts/fetch-chromium.sh` (the pinned Chrome for Testing, about 200 MB, under `.chromium/linux-x64/`; `.ps1` on Windows), `scripts/fetch-fonts.sh` (the bundled Noto fonts, 1.5 MB, under `.fonts/`), and the print page: `cd DbDataKit/web && npm ci --workspace @dbdatakit/visuals --include-workspace-root && npm run build:print -w @dbdatakit/visuals` (writes `visuals/dist/print/print.js` and `print.css`).
2. In the Gateway's `server.yaml` (the `views` module is on, as in section 1) add
   ```yaml
   views:
     export:
       mode: pinned                      # or: system, with path: /usr/bin/google-chrome (an installed Chrome or Edge; its version is recorded)
       pageAssets: /path/to/DbDataKit/web/visuals/dist/print
       # noSandbox: true                 # only as root or in a container
   ```
   (`--Gateway:Views:Export:Mode=pinned --Gateway:Views:Export:PageAssets=...` on the command line.) Restart. `curl -k https://<gateway>:8820/health` says `export: {available: true, browser: "HeadlessChrome/156...."}`; if not, `reason` says which setting or file.
3. Take the PDF with a token of the identity home: `curl -k -H "Authorization: Bearer <token>" -o sales.pdf "https://<gateway>:8820/api/v1/dashboards/retail-sales-today/export?format=pdf"`; `?layout=print` or `?layout=wall` names a layout, `&p.region=West` sets a parameter. A report: `/api/v1/reports/<name>/export`. Open it: one page for a dashboard (A4 landscape, or the canvas's own size), the report's pages with its header and footer ("Page n of N").
4. A person whose groups lack the data source gets a PDF with an error where the rows would be (the same as the screen). `GET .../print` is the same page as HTML for the browser's own Print. Every download is in the audit (`view.export`, with the pages and the browser version).
5. Break it: a wrong `path` or a pin of another version (`export unavailable: ...` in the health and a 501 naming `views.export.path`), no `pageAssets`, `fonts.required: [No Such Font]` (unavailable, naming the font).

## What a tester may not find (known gaps)

* Rows that differ by person: plan 22. Today only the data source's groups differ.
* An app's narrowed token over REST, and a dashboard inside an app (the apps host does not call the module yet).
* A PDF comes only from the module's own listener (a curl or an app with a token), not from the console: the relay carries JSON, so the console's Print preview stays disabled; use the browser's Print on the report, or `.../print` on the module. No CSV, xlsx or SVG (DBR17), no scheduled delivery (DBR18), no way to remove a published document.
* Images in a dashboard (files in the version): the Gateway's publish takes the document only, so a dashboard with an `image` widget is refused (`DBD-VIEW-063`).
