# AUTHORING-19: Set up for development, finished

## Built so far

Nothing yet.

## Plan

- `POST /api/workspace-dev-logins/{workspace}/{project}` takes `draft` (the draft's workspace id). With it, the console reads the draft's connection
  file from the Workspace server as the person, writes the copy (user as plain text, password a reference to the stored secret), and runs
  `ConsoleChannelConnections.SetUpAsync` (login, copy, bind). Without it, the route stores the password only, as before.
- The screen sends the draft; the line that an administrator must still bind goes.

## Defaults taken

- The password keeps the `dev-login` name (WORKSPACEISOLATION-P28's option A); the copy is named `<package>-<slot>-<channel>`.

## Open questions

None yet.
