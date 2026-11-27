# INSTALLER-15: groups through the token (work in progress)

## Built so far

- Join protocol (`DbDataKit.Web`): request carries the server's listeners and the key its group holds; answer carries the group key, the first
  instance's listeners and a link slot; `IJoinGroups` in `JoinService`; `ServerJoin.RunAsync(keep:)`.
- Console: `ConsoleJoinGroups`, `ConsoleGroupKeyRing`; registry keeps a group's first entry.
- Gateway: awaiting-join key mode, group certificate waits for the key, `GatewayListeners.TakeGroup`, start again in-process.
- Runner: keeps the key from the answer.
- Tests: `GroupJoinByTokenTests` (both engines pass), `ConsoleJoinGroupsTests`, `JoinGroupTests`, `ServerJoinStepTests`, `GroupListenersTests`.
- Docs: `docs/install.md`, `DbDataGateway/README.md`, `Shared/src/DbDataKit.Web/README.md`, release notes, ci-lessons row.
- Planning: row removed, references cleared in TRUSTZONES, BACKUPRESTORE, ENDPOINTS; proposals INSTALLER-P66 to P70 written.

## Left

- Full suites and checks before reporting ready.

## Open questions

- INSTALLER-P70 (identity home's signing-key secret still copied by hand).

## Defaults taken

- INSTALLER-P66 to INSTALLER-P69 (in the subject file).
