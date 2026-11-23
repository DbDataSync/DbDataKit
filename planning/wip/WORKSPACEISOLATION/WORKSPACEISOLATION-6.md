# WORKSPACEISOLATION-6: catalog refresh through the control service (work in progress)

## Built so far

- nothing yet; plan: a console route `POST api/workspace-catalog/{workspace}/{project}/gateway-read` (`ConsoleCatalogRead.GatewayAsync`) that asks the
  Workspace server for the project as the person, reads the data source's schema from a registered Gateway with `GatewayCalls.ReadSchemaAsync` over
  `ConsolePromotions.LinkFor`, and answers one connection of `catalog-cache/2`; the Catalog tab's Gateway form calls it and shows the difference before
  Accept, as the Runner read does.

## Open questions

## Defaults taken
