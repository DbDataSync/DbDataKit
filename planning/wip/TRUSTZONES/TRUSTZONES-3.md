# TRUSTZONES-3 work in progress

## Built so far

Nothing yet (started).

## Plan

- A task that declares the capability `ddl` is a DDL task. It must be manual-only.
- The Runner runs it only with a control-signed approval (header `X-DbDataKit-Control-Approval`), signed with the console's key; every other start is refused.
- The console signs it after the person approves the exact statements.
- The Runner's status says `ddl: true`; the Runner screens show no run button.
