# INSTALLER-8: work in progress

Task: `--skip-folder-permissions-check` and `startup.skip_folder_permissions_check` (planning/INSTALLER.md).

## Audit against the code (2026-11-20)

Nothing of the task existed: no folder step checked a folder's owner or access list, no server, `service install` or `service check` knew the flag or
the setting, and the console's posture list held only the posture choices of bindings.

## Built so far

- The startup library's check and switch, the four servers' folder steps, `service install` and `service check`, `/version`'s `loosened`, the
  console's posture list, docs (install.md, ui-editors.md row, release note, ci-lessons row, startup README), tests.

## Not built yet

- nothing; the remaining step is resolving this file.

## Open questions and defaults

- written as INSTALLER-P32 to INSTALLER-P35 in planning/INSTALLER.md.
