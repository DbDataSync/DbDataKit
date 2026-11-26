/** The workspace a version was published from (plan 33 SC20o, SC22l): the console's server of the Workspace, the project and the workspace (the draft). */
export interface WorkspaceOrigin { instance: string; project: string; workspace: string }

/** The token a Workspace puts at the end of a version's note (`DeployOrigin.Token`, `DbDataKit.Deploy`): `[workspace:<instance>/<project>/<workspace>]`, each part escaped as a URL path segment. */
const Token = /\[workspace:([^/\]\s]+)\/([^/\]\s]+)\/([^/\]\s]+)\]/

function decode(part: string): string | null {
  try { return decodeURIComponent(part) } catch { return null }
}

/** The origin a note names, or null when it names none (a version published by a command line, or by hand). */
export function workspaceOriginOf(note: string | null | undefined): WorkspaceOrigin | null {
  const m = note ? Token.exec(note) : null
  if (!m) return null
  const [instance, project, workspace] = [decode(m[1]), decode(m[2]), decode(m[3])]
  return instance && project && workspace ? { instance, project, workspace } : null
}

/** The note without the token, for reading. */
export const noteText = (note: string | null | undefined): string => (note ?? '').replace(Token, '').trim()

/** The console's address of the workspace's Runs tab. */
export const workspaceRunsLink = (o: WorkspaceOrigin): string =>
  `/servers/${encodeURIComponent(o.instance)}/${encodeURIComponent(o.project)}/${encodeURIComponent(o.workspace)}?tab=runs`
