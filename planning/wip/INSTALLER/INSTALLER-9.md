# INSTALLER-9: ports of the console, Runner and Workspace from the suite range

## Built so far

- Nothing yet. Audit: on the base branch the ports are fixed defaults (console 8480, identity home 8482, Runner 8495, Workspace 8471), `--listen`
  is a flag only (no settings key), `SuiteServices` has no port sub-blocks, `check_default_ports.py` only checks that no two defaults are shared.

## Not built yet

- Sub-blocks in `SuiteServices`, the choice of a free port and its pin, the three servers using it, the check, docs, release notes.

## Open questions and defaults

(filled in as they come up)
