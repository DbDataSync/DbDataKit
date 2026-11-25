# SYNCAUTHORING-21 (work in progress)

## Built so far

- Nothing yet. Plan: `DriverCapabilities.KeepsStateTable` (Sync's driver registry and the built-in table), answered by `sync/capabilities`
  as `keepsStateTable`; the Tracking card disables `target` and `both` when the target's driver keeps none, with the reason.

## Defaults taken

- A driver the draft carries (`driver.yaml`) or a compiled one answers `keepsStateTable: false`: the save-time check
  (`TrackingResolution.Validate`) refuses `target` and `both` for any driver id other than SQL Server's and PostgreSQL's, so the answer
  follows the same rule. Example: a draft with `drivers/legacy/driver.yaml` and a target connection on it; the Tracking card disables Target
  and Both and says the driver keeps no state table.

## Open questions

- none
