# INSTALLER-13: the console's Add a server screen (work in progress)

## Audit against the code (before building)

- Built by INSTALLER-12: `dbdatakit setup invite`, `ConsoleJoins` (Mint, Command, the join route), `StateJoinTokenStore`, `POST /api/v1/join`.
- Not built: no HTTP route makes, lists or cancels a join token; no screen; `ConsoleJoins` has no cancel.

## Built so far

- nothing yet

## Not built yet

- the console's routes for join tokens (make, list waiting, cancel with an audit entry)
- the screen `/add-server` under Servers, its vitest, the Playwright story

## Open questions

## Defaults taken
