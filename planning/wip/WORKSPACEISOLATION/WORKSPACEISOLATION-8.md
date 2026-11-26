# WORKSPACEISOLATION-8 (work in progress)

## Built so far

- Routes removed, answering 410 with the console's call (`WorkspaceManagement.RemovedRoutes`); publishing classes, `WorkspaceOrigin`,
  `publishes.jsonl`, the `publisher` command (RemovedCommands), the `publisher_key` table (migration 2), the store methods; the keys deleted at start.
- Screens: KeysPage, its API, the Builds tab's server records. The Scripts tab's Test stages in `prod` after a warning (owner's word).
- Relay table rows gone; its refusal names the console's calls. Tests and stories updated; docs and release notes.

## Not built yet

- Whole-solution build and the console, Gateway, Deploy and State suites (running).

## Open questions

none

## Defaults taken

- The Scripts tab starts on the channel of the draft's newest deploy to a Runner, `prod` included (folded into WORKSPACEISOLATION-P32's text).
