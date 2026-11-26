# WORKSPACEISOLATION-13 (work in progress)

## Built so far

- nothing yet

## Plan

- Gateway: an administrator hands what one client owns to another client (`PUT /api/v1/settings/handovers/{from}` `{"to"}`), kept in the
  group's settings; every owner check of the Gateway reads the owner through it; audited per definition.
- `DbDataKit.Deploy`: `GatewayCalls.HandOverAsync` (finds the Workspace's clients, `workspace-...`, asks the Gateway to hand each to the console).
- Console: `POST /api/deploys/handover` `{"gateway"}` as the person, one recorded step (`gateway.handover`) per Gateway.

## Open questions

## Defaults taken
