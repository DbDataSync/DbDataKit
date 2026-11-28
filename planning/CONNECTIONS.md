# CONNECTIONS: connection kinds, their forms and their tests

## Goal

Every kind of connection the suite can use (databases, files, HTTP, and the kinds that come later) has a form in the console to make and edit it, a
**Test** that signs in and tries what the binding allows, and a channel rule that never lets a non-production channel reach production's connection.
Each kind is proven against a real system before it is called supported.

## What exists today

- Connection kinds in `connection/2` (`DbDataKit.Connections.*`): the databases, `http`, `model-provider`, SFTP, FTPS, SCP, SMB, S3 and Azure Blob
  (`docs/integrations.md`).
- A binding names the connection a slot uses in a channel; a Runner task in a non-production channel uses that channel's connection or is refused
  (`docs/integrations.md`, section 8).
- SANDBOX holds the designs for hosted connections, connection kinds as packages and their conformance suites.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| CONNECTIONS-1 | The console's connection form per kind, with **Test**: the fields of the kind (an SFTP connection's host, user, key and host-key pin), and a Test that signs in and lists, reads or queries what the binding allows | none | M | Playwright: the SFTP form's Test signs in and lists a folder; a wrong key shows the classified failure |
| CONNECTIONS-2 | A delivery channel of the console (a webhook, an email) in a non-production channel uses that channel's connection or is refused, also when its file is edited by hand, as Runner tasks already are | none | S | a delivery in `test` with no binding for `test` is refused naming the slot; it never uses `prod`'s connection |
| CONNECTIONS-3 | SMB with Windows sign-in (Kerberos) proven on a Windows host in a domain: the `smb` connection kind lists, reads and writes a real share, and a ticket failure, a clock skew and an unreachable share are each classified as on Linux | none | S | by hand, because no domain is in CI: on a domain-joined Windows host, list, read, write and a failing ticket against a real share, with SMB signing on; the steps and the result in `docs/integrations.md`; anything found fixed with a unit test |
| CONNECTIONS-4 | The secrets overview of a Workspace project's connections (decided 2026-11-12, not built): every secret reference of the project, where it is bound, whether the target holds it (read by the control service from the Runner, never by the Workspace service); the Sync parameter form picks a `Secret` parameter's reference from it (today the person types `secret:<name>`) | none | S | vitest and a Playwright story: a project with three references shows each with its binding and state |

## Needs from other subjects

- SANDBOX: hosted connections and connection kinds as packages; this subject's kinds become hosted connections there.

## Shared areas touched

- `DbDataKit.Connections.*`, the console's connection screens (`Shared/web/console`), `DbDataKit.Delivery`, `docs/integrations.md`.
