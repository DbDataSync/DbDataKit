# DbDataKit.ReportCatalogs

The provider of an external report server (plan 32 RC01): `ReportServerCatalogProvider` implements `IReportCatalogProvider` (`DbDataKit.Views`) over the REST API of SQL Server Reporting Services and Power BI Report Server (`<portal>/api/v2.0`).

- **Browse**: the folders and reports (paginated, linked and Power BI reports included; data sources, datasets and resources are left out), by folder or by search.
- **Parameters**: the report's parameter definitions (type, multi value, default, valid values with their labels, dependencies).
- **Open**: the report server's URL-access address with the parameter values filled in and the toolbar hidden, offered as a `frame` (the merged catalog, `ReportCatalogs`, hands it on as a `link` unless its origin is approved).
- **Exports**: the formats the server renders for the item's kind (`PDF`, `EXCELOPENXML`, ...).
- **Permissions**: the item's policies (`GroupUserName` with its roles); a server that does not answer them gives `Known: false`.

The client is the caller's `HttpClient`, already signed in as the catalog's service account (`DbDataKit.Connections.Http`); the provider adds nothing to a request that is not an address of the report server. A service account the server refuses (401, 403) or a server that does not answer is `unavailable` naming the address. The provider reads as the service account and lists what that account sees: the filtering per person is the snapshot's and the serving layer's (RC03).
