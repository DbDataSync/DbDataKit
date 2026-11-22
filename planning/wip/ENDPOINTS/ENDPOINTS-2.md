# ENDPOINTS-2 work in progress

Built: `ListenUrl` and the list form of `ListenPorts` (Startup), `ScalarOrList` in the config binder, console/Runner/Workspace read a list, Gateway options and validator on `listen`, retired `port`/`address` refused, the three default listeners chosen in the block and pinned, settings form, compose files, docs.

Defaults taken (to become proposals):
- Flight SQL's setting is the top-level `listen` (no `flightsql` section).
- The public listener always starts (so a default Gateway has three ports).
- The container image sets `listen` for 8815 and `management.listen` for 8816.
- The 7820 to 7839 range is not validated for a written port; a written port is never moved.
