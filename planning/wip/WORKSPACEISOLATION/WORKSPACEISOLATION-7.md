# WORKSPACEISOLATION-7: development logins (work in progress)

## Built so far

Nothing yet.

## Plan

- The console's route `POST api/workspace-dev-logins/{workspace}/{project}` takes `{runner, channel, connection, user?, password}` from the screen,
  asks the Workspace server (as the person) only which package the project is, and puts the login in the Runner's secret store through the
  relay as the person (`PUT api/v1/secrets/{name}`), under a name of the package, the channel and the slot.
- The screen's client (`@dbdatakit/workspace`) posts to that console route, never to the Workspace server's relay.
- Tests: a console test whose stand-in Workspace server fails on any request carrying the login; a test with the real Runner and Workspace
  programs: the Runner's store holds the value, and no file of the Workspace server's directory does.

## Defaults taken (each becomes a proposal)

- Production is refused: Ana asks to set up `crm` for `prod`; the console answers 409 naming the rule and the Runner's Bindings page.
- The Runner decides who may store the login (its admin role, as for every secret today): Ana, an author of the project but a viewer on the Runner,
  is refused by the Runner with its words.
- The secret names: `dbdatakit:runner:dev-login:<package>.<channel>.<slot>:password` and `...:user`. Ana sets up `crm` of `crm-to-dw` in `dev`:
  the Runner holds `dbdatakit:runner:dev-login:crm-to-dw.dev.crm:password`.
- The Workspace server records nothing: it learns that the channel binds the slot from the channel picker's read through the console.

## Open questions

None yet.
