# ACTIONS: scripts that apps and agents call to do something

## Goal

An action is a script (C# or SQL) with parameters, which an app, an agent or a person calls to do something, such as approve an order. It is published
as the kind `action`; declarative actions are answered by the Gateway, code actions run on a Runner. Elevated actions, which run with more rights
than the caller, wait for the security model.

## What exists today

- `action/1` and the action editor are planned (`docs/ui-editors.md`); nothing of actions is built.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| ACTIONS-1 | `action/1` and callable actions: one script format (C# or SQL) with parameters and traits, a spec like a command, published as the kind `action` with the administrator's approval of the exact version | AUTHORING-32 | L | an action is published, refused unapproved, approved, and offered as a command spec |
| ACTIONS-2 | The action host: declarative actions answered in Serving; code actions as a Runner run through path 1 (the caller waits 10 seconds, then gets the run's link); apps and agents call actions through it | ACTIONS-1, TRUSTZONES-4 | L | an app's call of a code action returns its result within the wait or the run's link; a declarative action writes a Store row as the person |
| ACTIONS-3 | Elevated actions: an action principal per channel, grants to it approved by two people, run grants to people, the declarative form (`where`, `from_state`, `to_state`, `fields`), audit and limits | ACTIONS-2 | L | a person with run but no data grant changes a row only through the action; the audit names the action, its version, the principal and the person |
| ACTIONS-4 | The action editor's run with a sample input, and the console's approvals and grants of actions | ACTIONS-3, AUTHORING-32 | M | Playwright: run an action with a sample; approve an exact version; grant run to a group |

## Open designs

### Actions and elevated actions

Confirmed by the owner on 2026-11-03, who approves grants decided on 2026-11-04.

- **An action is one typed operation:** an input schema, an output schema, an effect class and a body; it may have side effects. Actions are agents'
  custom tools and apps' custom server-side logic. One script format, `action/1`, generalises Sync's script manifest (a name, a kind, C# or SQL, declared
  parameters, beside its code and lock file), adding the schemas, traits and the caller's context; Sync's slots stay kinds of it, and an old manifest is
  refused naming the file. A body is a `.csx` script, SQL against a suite connection, a call to a command, or, for an elevated action, the declarative
  form. A callable action has a spec like a command, so it is an MCP tool, an agent tool and an endpoint apps call, under the same access rules. Python
  for actions is left out for now.
- **Where it runs:** declarative actions in Serving, in process; code actions as a Runner run through path 1 (ACTIONS-2). An action that needs an
  outside system or a long job enqueues a task and returns a status. A published version of an action is trusted code and needs the administrator's
  approval of that exact version.
- **Elevated actions: the permission is held by the action, not the person.** Some workflows let a person make one specific change to Store data they
  may not write directly. Two grants: (1) to the action, through an **action principal** of its own in the identity home **per channel** (no password or
  token a person can obtain): the entities and operations (read, create, update, change request) it may use, declared in the action's file
  (`grants:`, versioned with the code) and effective only when **an administrator and the data's owner** (the Store model's owner) both approve that
  exact version, with an audit; in the first version the grants carry entity and operation, and the field and row limits are the action's own
  declarative checks; (2) to people: **run** on the action, given to people and groups in the identity home like any role assignment, by the action's
  owner or an administrator. An action not marked elevated has no grants and runs as the person. It is not tied to an app: an app's manifest names the
  actions it calls and grants nothing. This keeps "an app's token is never wider than the person": elevation exists only inside server-side trusted
  code, the pattern of a stored procedure granted `EXECUTE`.
- **The action decides: validate, then apply or reject.** The caller's identity and attributes are part of the input; the action reads the current
  state with its own grants and checks the request (the row exists, is in the right state, the caller may make this transition, the values are
  acceptable); it rejects with a reason the person or agent can act on and changes nothing, or applies through the Store's normal write path in one
  transaction, so the model's own validation still runs after the action's checks. The declarative form makes the common checks one line each
  (`where: owner = caller.id`, `from_state: Draft`, `to_state: Submitted`, `fields: [status, comment]`); more needs a `.csx` body.
- **The four worked examples** (each a test): a requester moves their own expense claim from Draft to Submitted, and may not edit it afterwards or write
  to the claims entity at all; an approver sets the status and a comment on a claim, no other field, only on claims of their own team; a manager adds a
  new starter to an HR entity through an onboarding app without general write access to it; anyone sees how many tickets are open per team without
  being able to read the tickets.
- **Rules that keep it safe:** a person receives the action's result shaped by its output schema, never its grants, a token or rows it was not written
  to return; no escalation by default (it cannot change grants, roles, definitions or structure, or call another elevated action its definition does
  not name; an administrator can lift this for one action as a recorded choice); the audit always names the action and its version, its principal, the
  person and the app or agent it came through ("Alice ran submit-claim v3, from the Expense app"), and that is fixed, not a setting; an agent acting for
  a person may call an elevated action only if that person may run it, and elevated actions ask the person first by default; grants are per channel,
  and a write in a non-production channel goes to that channel's own data or the preview's draft layer, never production (refused with a message
  until the draft layer exists, except on a channel bound to its own copy of the model's storage); per-caller rate limits, and every rejection logged
  with its reason.
- **When not to use it:** "a person proposes, someone with the role approves" is the Store's change requests. Elevated reads of semantic models (an
  aggregate without row access) follow the same pattern later; the first version covers Store entities.

**Open questions.** Where the approval records live if access grants move to the identity home; whether grants gain field and row conditions after the
first version.

## Shared areas touched

- a new `DbDataKit.Actions`, the Gateway's serving modules, the Runner's path 1, `docs/formats.json` (`action/1`), `docs/ui-editors.md`.
