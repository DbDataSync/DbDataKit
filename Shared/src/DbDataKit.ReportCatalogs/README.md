# DbDataKit.ReportCatalogs

The provider of an external report server (plan 32 RC01): `ReportServerCatalogProvider` implements `IReportCatalogProvider` (`DbDataKit.Views`) over the REST API of SQL Server Reporting Services and Power BI Report Server (`<portal>/api/v2.0`).

- **Browse**: the folders and reports (paginated, linked and Power BI reports included; data sources, datasets and resources are left out), by folder or by search.
- **Parameters**: the report's parameter definitions (type, multi value, default, valid values with their labels, dependencies).
- **Open**: the report server's URL-access address with the parameter values filled in and the toolbar hidden, offered as a `frame` (the merged catalog, `ReportCatalogs`, hands it on as a `link` unless its origin is approved).
- **Exports**: the formats the server renders for the item's kind (`PDF`, `EXCELOPENXML`, ...).
- **Permissions**: the item's policies (`GroupUserName` with its roles); a server that does not answer them gives `Known: false`.

The client is the caller's `HttpClient`, already signed in as the catalog's service account (`DbDataKit.Connections.Http`); the provider adds nothing to a request that is not an address of the report server. A service account the server refuses (401, 403) or a server that does not answer is `unavailable` naming the address. The provider reads as the service account and lists what that account sees: the filtering per person is the snapshot's and the serving layer's (RC03).

## The connection, the snapshot, the filter and the routes (plan 32 RC01 to RC04)

- **The connection.** A `connection/2` of kind `report-catalog`: `endpoint` is the portal, `report_catalog: {provider: ssrs|pbirs, name?, report_server_address?}`, the service account as a sign-in (`basic`, `api_key`, `oauth2_client_credentials`, `client_certificate` or none). `ReportCatalogConnections.Create(definition, secrets)` makes the provider (dispose it: it owns the signed-in client). The answers the tests use are written from the API's documentation, not recorded (`tests/DbDataKit.ReportCatalogs.Tests/answers/README.md`); a report server that wants Windows authentication only is not reachable yet.
- **The snapshot.** `ReportCatalogSnapshotJob` reads the catalog and publishes `report-catalog/1` only when it changed; `DefinitionStoreSnapshotSink` puts it in plan 30's registry (`ReportCatalogDefinitions.Register`, kind `report-catalog`, name the catalog's), `DefinitionStoreSnapshotSource` reads what a channel serves. The runner's task is `DbDataKit.ReportCatalogs.Runner`.
- **Per person.** `ReportCatalogNames.For(home.ExternalGroupsOf(userId), ...)` gives the person's names on the report server, `SnapshotCatalogProvider` lists, finds and describes only what `ReportCatalogAccess` shows them and opens through the report server's provider; add it to `ReportCatalogs` (`DbDataKit.Views`) beside `ViewsCatalog`, and the views module's `/api/v1/catalog...` routes serve the merged view.
- **Knowledge.** `ReportCatalogKnowledge.Render` makes the generated items of a snapshot (`DbDataKit.Knowledge`), `MayRead` is their access.
