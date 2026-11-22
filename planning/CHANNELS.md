# CHANNELS: channels, previews and test targets

## Goal

A test channel is usable end to end: Build writes into a test target created by an explicit command and recorded as a binding, a Sync trial run copies a
limited number of rows into an overridden target, both as Runner tasks started from the Workspace; a preview is removed when its branch is merged or
abandoned; a release is proven in a browser. How channels work is `docs/publishing.md` and `docs/channel-bindings.md`.

## What exists today

- Channels in every published version's key; promotion with dependency and slot checks; bindings per channel; releases with one approval (the console's
  Releases screen, no browser story); read slots resolve to production's binding, write slots never inherit.
- The Store's draft layer; idle previews expire with a notice; the Workspace does not remove a preview when a branch is merged or abandoned.
- The contract guard over the lineage graph and its callers.
- The Runner's connection copies and the console's Bindings page; a back end cannot put a connection on a Runner by itself (the console only).
- No Build test target command; no Sync trial run.
- Lineage does not show a dataset reached through a channel override as its own node; the override's table is not a node of its own.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| CHANNELS-1 | A client library to put a connection copy on a Runner, send its login and bind it for a channel, used by the control service as the caller (WORKSPACEISOLATION) | INSTALLER-16 | M | a test through the control service puts a copy on a Runner and binds the write slot of `test`; the Runner's Bindings show it |
| CHANNELS-2 | Build's channel targets: `channel target create\| CHANNELS-1, AUTHORING-1, WORKSPACEISOLATION-5 |list` from a Workspace build project's screens, run through the control service, the override recorded as a binding; a staged or test-channel version runs on the Runner with that channel's binding; production deferral (only changed models built) | CHANNELS-1, AUTHORING-1 | L | Build conformance on SQL Server and PostgreSQL: `create` for `test` makes `dw_test`, a test-channel refresh writes there and reads unchanged models from production |
| CHANNELS-3 | Sync trial run: a one-off run limited to N rows into an overridden target in a test channel, started from the Workspace screens through the control service, the run record says `trial`, never scheduled, excluded from "last success" | CHANNELS-1, WORKSPACEISOLATION-5 | M | Sync conformance: a trial writes at most N rows into the override; production's watermark is unchanged; the run is marked `trial` |
| CHANNELS-4 | The table of a channel override as a lineage node of its own, marked with the channel | CHANNELS-2 | S | the console's graph shows `dw_test.orders` beside `dw.orders` with `channel: test` |
| CHANNELS-5 | A preview and its draft are removed when the Workspace merges or abandons the branch | none | S | a Workspace test merges a branch and the Gateway reports the preview and its draft gone |
| CHANNELS-6 | A Playwright story of the Releases screen | none | S | the story makes a release of two items, approves it, runs it and resumes after a failure |

## Needs from other subjects

- INSTALLER-16: console-made trust between the Workspace and a Runner.
- AUTHORING-1: the operations package published from the Workspace.

## Proposed decisions

None.

## Shared areas touched

- `DbDataKit.Runner.Client`, `DbDataKit.Authoring.Server`, Build's and Sync's runtime packages, `DbDataKit.Lineage`, `Shared/web/console` (Releases).
- `docs/channel-bindings.md`, `docs/ui-editors.md`.
