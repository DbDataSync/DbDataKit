import { request } from '@dbdatakit/shell'

// A development login, as a Workspace screen sends it: to the console's own route, which puts its password in a Runner's secret store for the project's
// package, the channel and the connection's slot. The user is not a secret and comes back as plain text. The Workspace server never receives the login
// (the route is the host's, whatever server the module talks to).
// Design: docs/authoring-metadata.md#how-a-screen-sends-a-development-login

/** What a screen sends: the Runner (its id in the console), the channel the person picked, the project's connection, and the login the person typed. */
export interface DevelopmentLoginRequest { runner: string; channel: string; connection: string; user?: string; password: string }

/** What the console answers: where the login went, the name of the secret that holds its password on the Runner (never its value), and the user as sent. */
export interface DevelopmentLoginAnswer {
  package: string
  connection: string
  slot: string
  channel: string
  production: boolean
  runner: string
  runner_name: string
  user?: string
  secrets: { password: string }
}

const enc = encodeURIComponent

/** The console's route of a development login. */
export const developmentLoginApi = {
  setUp: (workspace: string, project: string, body: DevelopmentLoginRequest) =>
    request<DevelopmentLoginAnswer>(`/api/workspace-dev-logins/${enc(workspace)}/${enc(project)}`, { method: 'POST', body: JSON.stringify(body) }),
}

/** The sentence a screen shows above the login's fields: where the login goes and where it never goes. */
export const developmentLoginWarning = (connection: string, channel: string, runner: string) =>
  `The login you type for ${connection} goes to ${runner}, which keeps it for the ${channel} channel only. It is not saved in the project, its history or the Workspace.`

/** The sentence a screen shows when the login is stored. */
export const developmentLoginLine = (answer: DevelopmentLoginAnswer) =>
  `${answer.runner_name} holds the login of ${answer.connection} for ${answer.channel}.`
