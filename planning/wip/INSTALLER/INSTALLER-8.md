# INSTALLER-8: work in progress

Task: `--skip-folder-permissions-check` and `startup.skip_folder_permissions_check` (planning/INSTALLER.md).

## Audit against the code (2026-11-20)

Nothing of the task existed: no folder step checked a folder's owner or access list (each server's folder step reads its settings and makes the
folder), no server, `service install` or `service check` knew the flag or the setting, and the console's posture list held only the posture choices
of bindings (the console's table and the Runners').

## Built so far

- (in progress)

## Not built yet

- everything above

## Open questions and defaults

- (written as they are taken)
