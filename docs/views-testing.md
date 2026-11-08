# Testing dashboards and reports end to end

A script for a tester (plan 32 DBR11b): publish the retail model and its dashboard to a Proxy, open them from the console as two people whose access differs, resize, switch layout and edit. It needs a Proxy, a console paired to it, the retail project's data on an engine the Proxy can read, and a Build server paired to the Proxy for the model.

What is real today and what is not: the **model** is published by Build (it has the physical layer); the **dashboard** is published by the Proxy's own verb (`dbdataproxy views publish`) or from the console's editor; the dashboard is read through the Proxy's `views` module. **Different access** today is the data source's home groups (a person outside them gets a `denied` entry in every widget that needs the data): row-level rules are plan 22's and are not built, so two people never see two row sets yet. The report is listed but not drawn (DBR14).

## 1. A Proxy with the module on

1. In the Proxy's `server.yaml` list the module (the identity home too, if this Proxy is the home):

   ```yaml
   modules: [flightsql, identity, views]
   views: { address: 0.0.0.0, port: 8820 }
   ```

   Start it. The readiness component `views` is in `/health/ready`. Through the console the module's status says it is on:
   `GET /relay/<proxy>/api/v1/views/status` gives `{"enabled": true, "state": "ok", ...}`.
2. Pair the console with the Proxy as usual. A person signs in to the console **through the identity home** (a person who is only the console's has no home groups, so every widget of theirs is refused).

## 2. Publish the model, then the dashboard

1. **The model.** Build the retail project and publish its semantic model from the Build server (the Semantic screen's **Publish** to the paired Proxy, or `PUT /api/v1/semantic-models/retail_sales`). Its data source is created read-only on the Proxy. Give the data source a **home group** (`groups: [analysts]` under `extensions.proxy` in its connection file, or the data source screen): only that group may read it.
2. **The dashboard**, from this repository:

   ```
   dbdataproxy views publish --file DbDataBuild/templates/retail/dashboards/retail-sales-today.yaml --repo <data directory>
   dbdataproxy views list --repo <data directory>
   ```

   The first prints `Published dashboard retail-sales-today version 1; it depends on semantic-model:retail_sales@<n>`. A model that is not published says `DBD-VIEW-060` and names it; a field the model lacks says `unknown_member` with the line and column; run it again after fixing and nothing was stored in between.
   The same document can be sent to a running Proxy with `PUT /relay/<proxy>/api/v1/dashboards/retail-sales-today` (`{"text": "<the file>"}`, the author role), which is what the console's editor does on **Save** for a published source.
3. The module serves it within the Proxy's poll (`DefinitionPollSeconds`, 5 seconds by default); every instance of a group does.

## 3. Open it as two people

1. Make two home users: **Ada** in the group the data source names, **Bob** in none (`identity` verbs, or the Identities screen). Sign in to the console as Ada.
2. **Views > Dashboards.** The **Source** says `<Proxy> (published)` and "every widget is read as you"; the row `retail-sales-today` is listed (there is no *Open file* here: a file is opened in the fixtures only). Open it: the board is drawn from the Proxy's rows (the numbers differ from the recorded fixtures).
   Switch the source to *Fixtures* and back (it is kept in the address as `?source=`); an address with an unknown `?source=` says so and shows what is available.
3. Sign out, sign in as Bob and open the same address: the page is drawn, and each widget that needs the data source shows an error tile with the Proxy's words ("Not authorized for data source ... it is for the home groups analysts"); the text widget still draws. Ada's page is not changed by Bob's visit (the cache is keyed by who asks).
4. Direct, with a token: `curl -k -H "Authorization: Bearer <personal access token>" https://<proxy>:8820/api/v1/dashboards/retail-sales-today` and `POST .../render` with `{"parameters": {}, "layout": "desk"}`. No token is a 401; a token of Bob's is the same 200 with error entries. Every call is in the audit (`view.open`, `view.render`: who, name@version, the parameter names).

## 4. Resize, switch layout, parameters

1. Resize the window (1920 x 1080, 1366 x 768, a phone width): the layout the window selects fills the page; nothing scrolls. `?layout=wall` (a fixed canvas, scaled) is honoured; **Layout** in the toolbar switches and keeps it in the address. Switching to a layout whose widgets were already resolved asks the Proxy for nothing more.
2. Choose a region in the **Region** control: the address gains `?p.region=West`, and only the widgets that use the parameter are asked of the Proxy again (the toolbar's "Last request" line names them). Reload: the same address is the same board.

## 5. Edit and publish a new version

1. **Edit** on the board. Change the title or move a tile, **Save**. On a published source, saving publishes a new version through the Proxy (the author role; a viewer's console gets a refusal in words). `dbdataproxy views list` shows version 2; the board shows the change within the poll.
2. Open the editor in two browser tabs, save in one, save in the other: the second is a **conflict** (`expected_version` is stale) and offers a reload.
3. Break it: name an unknown measure (`sales.no_such_measure`) in a widget and Save: the editor shows the registry's refusal with the position, and version 2 is still what is served.

## What a tester may not find (known gaps)

* Rows that differ by person: plan 22. Today only the data source's groups differ.
* An app's narrowed token over REST, and a dashboard inside an app (the apps host does not call the module yet).
* Reports are not drawn (DBR14); there is no export (the module answers 501 `export_unavailable`) and no way to remove a published document.
* Images in a dashboard (files in the version): the Proxy's publish takes the document only, so a dashboard with an `image` widget is refused (`DBD-VIEW-063`).
